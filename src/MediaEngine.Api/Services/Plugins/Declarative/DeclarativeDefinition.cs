using System.Text.Json;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins.Declarative;

/// <summary>What a plugin that is only JSON describes: AI steps, channel and guide sources, and metadata sources.</summary>
internal sealed record DeclarativeDefinition(
    IReadOnlyList<DeclarativeAiStep> AiSteps,
    IReadOnlyList<DeclarativeChannelSource> ChannelSources,
    IReadOnlyList<DeclarativeMetadataSource> MetadataSources);

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

/// <summary>A metadata source in the provider-configuration format. It is checked here; the Library starts using it when providers move under Plugins (WP20).</summary>
internal sealed record DeclarativeMetadataSource(string Name, JsonElement Configuration);
