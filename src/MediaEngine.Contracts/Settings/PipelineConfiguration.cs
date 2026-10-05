using System.Text.Json.Serialization;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Contracts.Settings;

public sealed class PipelineProviderEntry
{
    [JsonPropertyName("rank")]
    public int Rank { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("purpose")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Purpose { get; set; }

    [JsonPropertyName("requires_identity")]
    public bool RequiresIdentity { get; set; }

    [JsonPropertyName("use_as_identity_fallback")]
    public bool UseAsIdentityFallback { get; set; }

    [JsonPropertyName("accepted_transition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AcceptedProviderTransitionConfiguration? AcceptedTransition { get; set; }

    [JsonPropertyName("accepted_actions")]
    public List<string> AcceptedActions { get; set; } = [];
}

public sealed class AcceptedProviderTransitionConfiguration
{
    [JsonPropertyName("when")]
    public string When { get; set; } = "identity-fallback-accepted";

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("max_attempts")]
    public int MaxAttempts { get; set; } = 1;

    [JsonPropertyName("hint_fields")]
    public List<string> HintFields { get; set; } = [];
}

public sealed class RetailScopeScoringConfiguration
{
    [JsonPropertyName("fields")]
    public Dictionary<string, RetailFieldScoringRule> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("gates")]
    public List<string> Gates { get; set; } = [];
    [JsonPropertyName("bonuses")]
    public Dictionary<string, double> Bonuses { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("penalties")]
    public Dictionary<string, double> Penalties { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class RetailFieldScoringRule
{
    [JsonPropertyName("weight")]
    public double Weight { get; set; }
    [JsonPropertyName("if_missing")]
    public string IfMissing { get; set; } = "redistribute";
}

public sealed class RetailScoringPolicyConfiguration
{
    [JsonPropertyName("scopes")]
    public Dictionary<string, RetailScopeScoringConfiguration> Scopes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("creator_list_mode")]
    public string CreatorListMode { get; set; } = "proportional";

    [JsonPropertyName("auto_accept_threshold")]
    public double? AutoAcceptThreshold { get; set; }

    [JsonPropertyName("ambiguous_threshold")]
    public double? AmbiguousThreshold { get; set; }
}

public sealed class MediaTypePipeline
{
    [JsonPropertyName("strategy")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ProviderStrategy Strategy { get; set; } = ProviderStrategy.Waterfall;

    [JsonPropertyName("providers")]
    public List<PipelineProviderEntry> Providers { get; set; } = [];

    [JsonPropertyName("max_provider_attempts")]
    public int MaxProviderAttempts { get; set; } = 8;

    [JsonPropertyName("scoring")]
    public RetailScoringPolicyConfiguration Scoring { get; set; } = new();

    [JsonPropertyName("field_priorities")]
    public Dictionary<string, List<string>> FieldPriorities { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PipelineConfiguration
{
    [JsonPropertyName("pipelines")]
    public Dictionary<string, MediaTypePipeline> Pipelines { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public MediaTypePipeline GetPipelineForMediaType(string mediaTypeDisplayName)
    {
        return Pipelines.TryGetValue(mediaTypeDisplayName, out var pipeline) ? pipeline : new();
    }
}
