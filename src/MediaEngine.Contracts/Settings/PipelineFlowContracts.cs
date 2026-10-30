using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Settings;

/// <summary>Roles a provider can have in a media type's ingestion flow. A provider that is not listed is Off.</summary>
public static class PipelineFlowRoles
{
    public const string Primary = "primary";
    public const string Fallback = "fallback";
    public const string Enrichment = "enrichment";
}

/// <summary>One provider row of an edited ingestion flow, in the order it should run.</summary>
public sealed class PipelineFlowProviderDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; set; } = PipelineFlowRoles.Primary;
}

/// <summary>The edited flow: for each media type changed, the providers in order. Strategy, scoring and per-field priorities are never sent.</summary>
public sealed class PipelineFlowSaveRequest
{
    [JsonPropertyName("media_types")]
    public Dictionary<string, List<PipelineFlowProviderDto>> MediaTypes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PipelineFlowProblemDto
{
    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public sealed class PipelineFlowSaveResponse
{
    [JsonPropertyName("saved")]
    public bool Saved { get; set; }

    [JsonPropertyName("problems")]
    public List<PipelineFlowProblemDto> Problems { get; set; } = [];
}

/// <summary>An installed provider that can be put into a media type's flow.</summary>
public sealed class PipelineFlowOptionDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("plugin_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PluginId { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("sends_titles_online")]
    public bool SendsTitlesOnline { get; set; } = true;

    /// <summary>Media types in the flow this provider can handle.</summary>
    [JsonPropertyName("media_types")]
    public List<string> MediaTypes { get; set; } = [];

    /// <summary>Media types whose flow already lists this provider.</summary>
    [JsonPropertyName("used_in")]
    public List<string> UsedIn { get; set; } = [];
}

public sealed class PipelineFlowOptionsResponse
{
    [JsonPropertyName("providers")]
    public List<PipelineFlowOptionDto> Providers { get; set; } = [];
}
