using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins.Declarative;

/// <summary>What a plugin that is only JSON describes: AI steps, channel and guide sources, metadata sources, notification channels and library kinds.</summary>
internal sealed record DeclarativeDefinition(
    IReadOnlyList<DeclarativeAiStep> AiSteps,
    IReadOnlyList<DeclarativeChannelSource> ChannelSources,
    IReadOnlyList<DeclarativeMetadataSource> MetadataSources,
    IReadOnlyList<DeclarativeNotificationChannel> NotificationChannels,
    IReadOnlyList<DeclarativeLibraryKind> LibraryKinds);

/// <summary>A step that sends a prompt (with <c>{name}</c> placeholders) to Local AI and returns the answer.</summary>
internal sealed record DeclarativeAiStep(
    string Key,
    string Name,
    string Role,
    string Prompt,
    int MaxTokens,
    string? JsonGrammar);

/// <summary>A channel list (M3U) or programme guide (XMLTV) fetched from a declared address on a schedule.</summary>
internal sealed record DeclarativeChannelSource(
    string Key,
    string Name,
    DeclarativeChannelFormat Format,
    Uri Url,
    PluginSchedule Refresh);

internal enum DeclarativeChannelFormat
{
    M3u,
    Xmltv,
}

/// <summary>
/// A way to deliver notifications written as data: one web request to a declared address. <see cref="Headers"/> and
/// <see cref="BodyTemplate"/> may use <c>{title}</c>, <c>{body}</c> and <c>{event}</c>; header values may also use
/// <c>{secret:name}</c> for a secret the manifest declares, so a login is never written into the file.
/// </summary>
internal sealed record DeclarativeNotificationChannel(
    string Key,
    string Name,
    Uri Url,
    string ContentType,
    IReadOnlyDictionary<string, string> Headers,
    string BodyTemplate);

/// <summary>A metadata source in the provider-configuration format. It is checked here, and the Library takes it in as a source of its own (see <c>PluginMetadataSourceFiles</c>).</summary>
internal sealed record DeclarativeMetadataSource(string Name, JsonElement Configuration);

/// <summary>
/// A library kind a plugin adds to the Library's list of kinds: its area, label, icon, format, lookup, and the grouping and
/// naming rules it chooses from the built-in ones. It is data only; the Library supplies all the behaviour.
/// </summary>
internal sealed record DeclarativeLibraryKind(LibraryKindDefinition Kind);
