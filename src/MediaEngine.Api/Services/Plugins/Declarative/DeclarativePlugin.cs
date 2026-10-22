using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins.Declarative;

/// <summary>
/// A plugin that is only JSON (kind <c>declarative</c>). The Library reads the description and supplies all the behaviour:
/// there is no code to load, so what such a plugin can do is exactly what its manifest says and what was approved.
/// </summary>
internal sealed class DeclarativePlugin(PluginManifest manifest, DeclarativeDefinition definition) : ITuvimaPlugin
{
    public PluginManifest Manifest { get; } = manifest;

    public DeclarativeDefinition Definition { get; } = definition;

    public IReadOnlyList<IPluginCapability> CreateCapabilities() =>
    [
        .. Definition.AiSteps.Select(step => new DeclarativeAiStepCapability(step)),
        .. Definition.ChannelSources.Select(source => new DeclarativeChannelSourceJob(source)),
        .. Definition.MetadataSources.Select(source => new DeclarativeMetadataSourceCapability(source)),
        .. Definition.NotificationChannels.Select(channel => new DeclarativeNotificationChannelCapability(channel)),
        .. Definition.LibraryKinds.Select(kind => new DeclarativeLibraryKindCapability(kind)),
    ];

    /// <summary>
    /// Builds the plugin for a manifest, or returns the plain reason it cannot be built. <paramref name="knowledge"/> is what the
    /// plugin's library kinds may refer to outside the plugin (the Library's metadata sources and naming templates).
    /// </summary>
    public static (ITuvimaPlugin? Plugin, string? Error) TryCreate(PluginManifest manifest, LibraryKindKnowledge? knowledge = null)
    {
        var parsed = DeclarativePluginParser.Parse(manifest, knowledge);
        return parsed.Definition is null
            ? (null, parsed.Error)
            : (new DeclarativePlugin(manifest, parsed.Definition), null);
    }
}

internal static class DeclarativeCapabilityKinds
{
    public const string AiStep = "declarative-ai-step";
    public const string MetadataSource = "declarative-metadata-source";
    public const string NotificationChannel = "declarative-notification-channel";
    public const string LibraryKind = "declarative-library-kind";

    /// <summary>The collection a channel source keeps its latest fetch in, one record per source key.</summary>
    public const string ChannelRecords = "channels";
}

internal sealed class DeclarativeAiStepCapability(DeclarativeAiStep step) : IPluginCapability
{
    public string Kind => DeclarativeCapabilityKinds.AiStep;

    public DeclarativeAiStep Step { get; } = step;
}

internal sealed class DeclarativeNotificationChannelCapability(DeclarativeNotificationChannel channel) : IPluginCapability
{
    public string Kind => DeclarativeCapabilityKinds.NotificationChannel;

    public DeclarativeNotificationChannel Channel { get; } = channel;
}

internal sealed class DeclarativeLibraryKindCapability(DeclarativeLibraryKind kind) : IPluginCapability
{
    public string Kind => DeclarativeCapabilityKinds.LibraryKind;

    public DeclarativeLibraryKind LibraryKind { get; } = kind;
}

internal sealed class DeclarativeMetadataSourceCapability(DeclarativeMetadataSource source) : IPluginCapability
{
    public string Kind => DeclarativeCapabilityKinds.MetadataSource;

    public DeclarativeMetadataSource Source { get; } = source;
}

/// <summary>Fetches a channel list or guide on its schedule and keeps the latest result as the plugin's own record.</summary>
internal sealed class DeclarativeChannelSourceJob(DeclarativeChannelSource source) : IPluginScheduledJob
{
    private const long MaxDownloadBytes = 20 * 1024 * 1024;
    private const int MaxGuideProgrammes = 2000;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public string Kind => PluginCapabilityKinds.ScheduledJobKind;

    public string Key => $"channels-{source.Key}";

    public string DisplayName => source.Name.Length > 80 ? source.Name[..80] : source.Name;

    public PluginSchedule DefaultSchedule => source.Refresh;

    public async Task RunAsync(IPluginExecutionContext context, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
        using var response = await context.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{source.Url.Host} answered {(int)response.StatusCode} instead of the list.");
        }

        if (response.Content.Headers.ContentLength > MaxDownloadBytes)
        {
            throw new InvalidOperationException($"The list from {source.Url.Host} is larger than {MaxDownloadBytes / (1024 * 1024)} MB.");
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var stored = Build(text);
        await context.Records.PutAsync(
            DeclarativeCapabilityKinds.ChannelRecords,
            source.Key,
            JsonSerializer.Serialize(stored, Json),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the downloaded text into what is kept; throws with a plain message when it is not the declared format.</summary>
    internal ChannelSourceSnapshot Build(string text)
    {
        if (source.Format == DeclarativeChannelFormat.M3u)
        {
            var channels = ChannelListParsers.ParseM3u(text, out var error)
                ?? throw new InvalidOperationException(error);
            return new ChannelSourceSnapshot(DateTimeOffset.UtcNow, source.Format.ToString().ToLowerInvariant(), channels, []);
        }

        if (!ChannelListParsers.TryParseXmltv(text, out var guideChannels, out var programmes, out var guideError))
        {
            throw new InvalidOperationException(guideError);
        }

        return new ChannelSourceSnapshot(
            DateTimeOffset.UtcNow,
            source.Format.ToString().ToLowerInvariant(),
            guideChannels,
            programmes.OrderBy(programme => programme.Start).Take(MaxGuideProgrammes).ToList());
    }
}

/// <summary>What a channel source keeps: when it was fetched and what was in it.</summary>
internal sealed record ChannelSourceSnapshot(
    DateTimeOffset FetchedAt,
    string Format,
    IReadOnlyList<ChannelEntry> Channels,
    IReadOnlyList<GuideProgramme> Programmes);
