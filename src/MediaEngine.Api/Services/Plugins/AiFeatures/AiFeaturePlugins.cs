using System.Text.Json;
using MediaEngine.AI.Configuration;
using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Domain.Configuration;
using MediaEngine.Plugins;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Plugins.AiFeatures;

/// <summary>
/// Shared shape of the Local AI feature plugins: Official built-ins that are off until an administrator approves and
/// switches them on. A feature is on exactly when its plugin is on (see <see cref="PluginAiFeatureSwitches"/>).
/// </summary>
internal static class AiFeaturePluginManifests
{
    // One sentence each (Plugins and Integrations screens v4 D35) and what each provides (D34).
    private static string ShortFor(string id) => id switch
    {
        _ when id == AiFeaturePluginIds.SmartFiling => "Reads file names to guess titles and media type when they are unclear.",
        _ when id == AiFeaturePluginIds.SeriesOrder => "Suggests the order of works within a series.",
        _ when id == AiFeaturePluginIds.DescriptionInsights => "Adds themes, mood and tags from descriptions.",
        _ when id == AiFeaturePluginIds.TasteSummary => "Writes a short sentence about a person's taste.",
        _ => "",
    };

    private static string[] ProvidesFor(string id) => id switch
    {
        _ when id == AiFeaturePluginIds.SmartFiling => ["Identification"],
        _ when id == AiFeaturePluginIds.SeriesOrder => ["Series order"],
        _ => ["Insights"],
    };

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
        JsonElement? settingsSchema = null,
        PluginDeclaration? declaration = null,
        bool hidden = false) => new()
        {
            Id = id,
            Name = name,
            Version = "0.1.0",
            Kind = PluginKind.Official,
            MinimumLibraryVersion = "1.0.0",
            Description = description,
            ShortDescription = ShortFor(id),
            Provides = ProvidesFor(id),
            Category = id == AiFeaturePluginIds.SmartFiling ? "intake" : "enrichment",
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
            Declaration = declaration,
            Hidden = hidden,
        };

    /// <summary>
    /// The declaration the Local AI plugins share: administrators set up and see the results unless <paramref name="audience"/>
    /// says otherwise, Local AI is required, the output stays on removal, and a failure is skipped and retried quietly.
    /// </summary>
    public static PluginDeclaration Declare(string audience, string weight, string aiRole, string savedOutput, string changelog) => new()
    {
        Audience = audience,
        WeightClass = weight,
        OnFailure = "skip_and_retry",
        Notify = "quiet",
        Requires = [new PluginRequirement { Kind = "local_ai", Id = aiRole }],
        Stores = new PluginStores { SavedOutput = savedOutput, OnRemoval = "keep" },
        Changelog = changelog,
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

/// <summary>
/// Improves filenames and first guesses when files are added, and decides the media type of a file when it is unclear
/// (the ingestion-assist hook arrives with WP28 part B). It has nothing to do until that hook exists, so it stays off the
/// Plugins page until then.
/// </summary>
internal sealed class SmartFilingPlugin : ITuvimaPlugin
{
    public PluginManifest Manifest { get; } = AiFeaturePluginManifests.Create(
        AiFeaturePluginIds.SmartFiling,
        "Smart filing",
        "Reads file and folder names when files are added: suggests titles, authors, years and episode numbers, and decides the media type when it is unclear. Runs on this machine with Local AI; without it, plain filename rules are used and an unclear file goes to Needs Review.",
        new PluginCapabilityDescriptor
        {
            Kind = "ingestion-assist",
            Name = "Filename and type helper",
            Description = "Suggests titles, authors, years, episode numbers and the media type of a file from its name.",
        },
        aiRole: "text_quality",
        maxTokens: 512,
        scheduled: false,
        declaration: AiFeaturePluginManifests.Declare("administrators", "light", "text_quality", "filing suggestions", "Smart labelling and Type helper are now one plugin."),
        hidden: true);

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
        scheduled: true,
        declaration: AiFeaturePluginManifests.Declare("administrators", "medium", "text_quality", "series order suggestions", "Declares who may use it, what it needs and what it keeps."));

    public IReadOnlyList<IPluginCapability> CreateCapabilities() =>
    [
        new AiFeatureScheduledJob(
            JobKey,
            "Suggest series order",
            PluginSchedule.OnCron("0 3 * * *"),
            ct => services.GetRequiredService<SeriesAlignmentRunner>().RunAlignmentAsync(ct)),
    ];
}

/// <summary>Adds themes, setting, mood and other context from descriptions, with the short summary included.</summary>
internal sealed class DescriptionInsightsPlugin(IServiceProvider services) : ITuvimaPlugin
{
    public const string JobKey = "description-insights";
    public const string VibeJobKey = "vibe-tags";
    public const string BooksKey = "books";
    public const string MoviesTvKey = "movies_tv";
    public const string MusicKey = "music";
    public const string ComicsKey = "comics";

    public PluginManifest Manifest { get; } = AiFeaturePluginManifests.Create(
        AiFeaturePluginIds.DescriptionInsights,
        "Description insights",
        "Reads descriptions and genres and adds themes, mood, setting, period, audience, pace, content warnings and people, a short summary, and mood tags chosen from the words in this plugin's settings. Runs on this machine with Local AI; results are labelled as AI.",
        new PluginCapabilityDescriptor
        {
            Kind = PluginCapabilityKinds.ScheduledJobKind,
            Name = "Description insights pass",
            Description = "Adds structured context from descriptions in small batches, and mood tags once a day.",
        },
        aiRole: "text_scholar",
        maxTokens: 1024,
        scheduled: true,
        defaultSettings: DefaultSettings(),
        settingsSchema: SettingsSchema(),
        declaration: AiFeaturePluginManifests.Declare("administrators", "heavy", "text_scholar", "descriptive tags and summaries", "Vibe tags is now part of this plugin."));

    public IReadOnlyList<IPluginCapability> CreateCapabilities() =>
    [
        new AiFeatureScheduledJob(
            JobKey,
            "Add description insights",
            PluginSchedule.Every(TimeSpan.FromMinutes(15)),
            ct => services.GetRequiredService<DescriptionIntelligenceRunner>().ProcessBatchAsync(ct)),
        new AiFeatureScheduledJob(
            VibeJobKey,
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
        extraPermissions: [PluginPermissionIds.WatchActivity],
        declaration: AiFeaturePluginManifests.Declare("everyone", "light", "text_fast", "taste summaries", "Declares who may use it, what it needs and what it keeps."));

    public IReadOnlyList<IPluginCapability> CreateCapabilities() => [];
}
