using System.Text.Json;
using MediaEngine.AI.Configuration;
using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Plugins;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Plugins.AiFeatures;

/// <summary>
/// Shared shape of the Local AI feature plugins: Official built-ins that are off until an administrator approves and
/// switches them on. A feature is on exactly when its plugin is on (see <see cref="PluginAiFeatureSwitches"/>).
/// </summary>
internal static class AiFeaturePluginManifests
{
    public static PluginManifest Create(
        string id,
        string name,
        string description,
        PluginCapabilityDescriptor capability,
        string aiRole,
        int maxTokens,
        bool scheduled,
        string[]? extraPermissions = null,
        Dictionary<string, JsonElement>? defaultSettings = null,
        JsonElement? settingsSchema = null) => new()
        {
            Id = id,
            Name = name,
            Version = "0.1.0",
            Kind = PluginKind.Official,
            MinimumLibraryVersion = "1.0.0",
            Description = description,
            Capabilities = [capability],
            Permissions = [
                PluginPermissionIds.AiInfer,
                .. (scheduled ? new[] { PluginPermissionIds.ScheduledWork } : []),
                .. (extraPermissions ?? []),
            ],
            AiPermissions =
        [
            new PluginAiPermission
            {
                Role = aiRole,
                MaxTokens = maxTokens,
                Schedule = scheduled ? "scheduled" : "on-demand",
                ResourceClass = "ai",
            },
        ],
            SupportedPlatforms = [],
            DefaultSettings = defaultSettings ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase),
            SettingsSchema = settingsSchema,
        };
}

/// <summary>The recurring job of a scheduled Local AI feature plugin; one pass of the feature's runner per run.</summary>
internal sealed class AiFeatureScheduledJob(
    string key,
    string displayName,
    PluginSchedule defaultSchedule,
    Func<CancellationToken, Task<TaskRunResult>> runPass) : IPluginScheduledJob
{
    public string Kind => PluginCapabilityKinds.ScheduledJobKind;

    public string Key { get; } = key;

    public string DisplayName { get; } = displayName;

    public PluginSchedule DefaultSchedule { get; } = defaultSchedule;

    public async Task RunAsync(IPluginExecutionContext context, CancellationToken cancellationToken)
    {
        var result = await runPass(cancellationToken).ConfigureAwait(false);

        // A pass that handled its own error asks to be recorded as failed; the plugin job record carries that.
        if (string.Equals(result.Outcome, ScheduledTaskOutcomes.Failed, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(result.Message ?? "The pass failed.");
        }
    }
}

/// <summary>Improves filenames and first guesses when files are added (the ingestion-assist hook arrives with WP28 part B).</summary>
internal sealed class SmartLabellingPlugin : ITuvimaPlugin
{
    public PluginManifest Manifest { get; } = AiFeaturePluginManifests.Create(
        AiFeaturePluginIds.SmartLabelling,
        "Smart labelling",
        "Reads file and folder names and suggests titles, authors, years and episode numbers when files are added. Runs on this machine with Local AI; without it, plain filename rules are used.",
        new PluginCapabilityDescriptor
        {
            Kind = "ingestion-assist",
            Name = "Filename labeller",
            Description = "Suggests titles, authors, years and episode numbers from file names.",
        },
        aiRole: "text_quality",
        maxTokens: 512,
        scheduled: false);

    public IReadOnlyList<IPluginCapability> CreateCapabilities() => [];
}

/// <summary>Decides the media type of a file when it is unclear.</summary>
internal sealed class TypeHelperPlugin : ITuvimaPlugin
{
    public PluginManifest Manifest { get; } = AiFeaturePluginManifests.Create(
        AiFeaturePluginIds.TypeHelper,
        "Type helper",
        "Reads file names and basic file facts to decide the media type when it is unclear. Runs on this machine with Local AI; without it, the unclear file goes to Needs Review.",
        new PluginCapabilityDescriptor
        {
            Kind = "ingestion-assist",
            Name = "Media type helper",
            Description = "Suggests the media type of a file the Library cannot place on its own.",
        },
        aiRole: "text_quality",
        maxTokens: 256,
        scheduled: false);

    public IReadOnlyList<IPluginCapability> CreateCapabilities() => [];
}

/// <summary>Suggests the order of works within a series.</summary>
internal sealed class SeriesOrderPlugin(IServiceProvider services) : ITuvimaPlugin
{
    public const string JobKey = "series-order";

    public PluginManifest Manifest { get; } = AiFeaturePluginManifests.Create(
        AiFeaturePluginIds.SeriesOrder,
        "Series order",
        "Reads series and title details and suggests the order of works within a series, once a day. Runs on this machine with Local AI; suggestions are labelled as AI.",
        new PluginCapabilityDescriptor
        {
            Kind = PluginCapabilityKinds.ScheduledJobKind,
            Name = "Series order pass",
            Description = "Suggests missing positions within a series.",
        },
        aiRole: "text_quality",
        maxTokens: 512,
        scheduled: true);

    public IReadOnlyList<IPluginCapability> CreateCapabilities() =>
    [
        new AiFeatureScheduledJob(
            JobKey,
            "Suggest series order",
            PluginSchedule.OnCron("0 3 * * *"),
            ct => services.GetRequiredService<SeriesAlignmentRunner>().RunAlignmentAsync(ct)),
    ];
}

/// <summary>Adds mood tags from the plugin's own word lists.</summary>
internal sealed class VibeTagsPlugin(IServiceProvider services) : ITuvimaPlugin
{
    public const string JobKey = "vibe-tags";
    public const string BooksKey = "books";
    public const string MoviesTvKey = "movies_tv";
    public const string MusicKey = "music";
    public const string ComicsKey = "comics";

    public PluginManifest Manifest { get; } = AiFeaturePluginManifests.Create(
        AiFeaturePluginIds.VibeTags,
        "Vibe tags",
        "Reads descriptions and genres and adds mood tags, once a day. The words it may use are in this plugin's settings. Runs on this machine with Local AI; tags are labelled as AI.",
        new PluginCapabilityDescriptor
        {
            Kind = PluginCapabilityKinds.ScheduledJobKind,
            Name = "Vibe tag pass",
            Description = "Adds mood tags chosen from the plugin's word lists.",
        },
        aiRole: "text_quality",
        maxTokens: 512,
        scheduled: true,
        defaultSettings: DefaultSettings(),
        settingsSchema: SettingsSchema());

    public IReadOnlyList<IPluginCapability> CreateCapabilities() =>
    [
        new AiFeatureScheduledJob(
            JobKey,
            "Add vibe tags",
            PluginSchedule.OnCron("0 4 * * *"),
            ct => services.GetRequiredService<VibeBatchRunner>().RunBatchAsync(ct)),
    ];

    private static Dictionary<string, JsonElement> DefaultSettings()
    {
        var words = new AiVibeVocabulary();
        return new(StringComparer.OrdinalIgnoreCase)
        {
            [BooksKey] = JsonSerializer.SerializeToElement(words.Books),
            [MoviesTvKey] = JsonSerializer.SerializeToElement(words.MoviesTv),
            [MusicKey] = JsonSerializer.SerializeToElement(words.Music),
            [ComicsKey] = JsonSerializer.SerializeToElement(words.Comics),
        };
    }

    private static JsonElement SettingsSchema()
    {
        using var document = JsonDocument.Parse("""
        {
          "properties": {
            "books": {
              "type": "array",
              "title": "Words for books and audiobooks",
              "description": "The only words a book or audiobook may be tagged with.",
              "items": { "type": "string" }
            },
            "movies_tv": {
              "type": "array",
              "title": "Words for films and TV",
              "description": "The only words a film or TV show may be tagged with.",
              "items": { "type": "string" }
            },
            "music": {
              "type": "array",
              "title": "Words for music",
              "description": "The only words an album or track may be tagged with.",
              "items": { "type": "string" }
            },
            "comics": {
              "type": "array",
              "title": "Words for comics",
              "description": "The only words a comic may be tagged with.",
              "items": { "type": "string" }
            }
          },
          "required": ["books", "movies_tv", "music", "comics"]
        }
        """);
        return document.RootElement.Clone();
    }
}

/// <summary>Adds themes, setting, mood and other context from descriptions, with the short summary included.</summary>
internal sealed class DescriptionInsightsPlugin(IServiceProvider services) : ITuvimaPlugin
{
    public const string JobKey = "description-insights";

    public PluginManifest Manifest { get; } = AiFeaturePluginManifests.Create(
        AiFeaturePluginIds.DescriptionInsights,
        "Description insights",
        "Reads descriptions and adds themes, mood, setting, period, audience, pace, content warnings and people, plus a short summary. Runs on this machine with Local AI; results are labelled as AI.",
        new PluginCapabilityDescriptor
        {
            Kind = PluginCapabilityKinds.ScheduledJobKind,
            Name = "Description insights pass",
            Description = "Adds structured context from descriptions in small batches.",
        },
        aiRole: "text_scholar",
        maxTokens: 1024,
        scheduled: true);

    public IReadOnlyList<IPluginCapability> CreateCapabilities() =>
    [
        new AiFeatureScheduledJob(
            JobKey,
            "Add description insights",
            PluginSchedule.Every(TimeSpan.FromMinutes(15)),
            ct => services.GetRequiredService<DescriptionIntelligenceRunner>().ProcessBatchAsync(ct)),
    ];
}

/// <summary>Writes the short taste summary on a profile page from what that profile watched, read and listened to.</summary>
internal sealed class TasteSummaryPlugin : ITuvimaPlugin
{
    public PluginManifest Manifest { get; } = AiFeaturePluginManifests.Create(
        AiFeaturePluginIds.TasteSummary,
        "Taste summary",
        "Writes a sentence or two about a person's taste from what they watched, read and listened to. Runs on this machine with Local AI and never leaves it; without it, the profile page shows the numbers and a plain sentence.",
        new PluginCapabilityDescriptor
        {
            Kind = "taste-summary",
            Name = "Taste summary writer",
            Description = "Summarises a profile's taste in a sentence or two.",
        },
        aiRole: "text_fast",
        maxTokens: 160,
        scheduled: false,
        extraPermissions: [PluginPermissionIds.WatchActivity]);

    public IReadOnlyList<IPluginCapability> CreateCapabilities() => [];
}
