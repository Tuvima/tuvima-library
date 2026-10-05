using System.Text.Json.Serialization;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// A single provider entry in a media type's pipeline configuration.
/// Providers are executed in <see cref="Rank"/> order according to
/// the pipeline's <see cref="MediaTypePipeline.Strategy"/>.
/// </summary>
public sealed class PipelineProviderEntry
{
    /// <summary>
    /// Execution order within the pipeline. Lower numbers run first.
    /// Must be unique within a media type's provider list.
    /// </summary>
    [JsonPropertyName("rank")]
    public int Rank { get; set; }

    /// <summary>
    /// Provider config name (e.g. "apple_api", "musicbrainz", "tmdb").
    /// Must match the <c>name</c> field in the corresponding provider config file.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional purpose for this provider in the chain, such as
    /// <c>identity</c> or <c>enrichment</c>. Runtime ordering is still driven by
    /// <see cref="Rank"/>, while identity purpose can also control which accepted
    /// provider candidate owns the selected identity for the job.
    /// </summary>
    [JsonPropertyName("purpose")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Purpose { get; set; }

    /// <summary>
    /// When true, this provider is skipped unless an earlier identity provider
    /// produced an auto-accepted candidate.
    /// </summary>
    [JsonPropertyName("requires_identity")]
    public bool RequiresIdentity { get; set; }

    /// <summary>
    /// Allows this provider to supply the identity only when no earlier identity
    /// provider was auto-accepted. When an identity already exists it retains its
    /// configured enrichment role.
    /// </summary>
    [JsonPropertyName("use_as_identity_fallback")]
    public bool UseAsIdentityFallback { get; set; }

    /// <summary>
    /// Optional bounded follow-up provider attempt scheduled after this provider
    /// is accepted under the configured outcome condition.
    /// </summary>
    [JsonPropertyName("accepted_transition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AcceptedProviderTransitionConfiguration? AcceptedTransition { get; set; }

    /// <summary>Configured post-acceptance actions interpreted by the pipeline.</summary>
    [JsonPropertyName("accepted_actions")]
    public List<string> AcceptedActions { get; set; } = [];
}

public sealed class AcceptedProviderTransitionConfiguration
{
    /// <summary>Configured outcome that activates the transition.</summary>
    [JsonPropertyName("when")]
    public string When { get; set; } = "identity-fallback-accepted";

    /// <summary>Provider to run with hints from the accepted candidate.</summary>
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    /// <summary>Maximum number of attempts scheduled by this transition.</summary>
    [JsonPropertyName("max_attempts")]
    public int MaxAttempts { get; set; } = 1;

    /// <summary>Accepted claim keys copied into the follow-up request hints.</summary>
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

    /// <summary>
    /// Creator-list comparison operator. Supported values are
    /// <c>proportional</c> and <c>local-primary-containment</c>.
    /// </summary>
    [JsonPropertyName("creator_list_mode")]
    public string CreatorListMode { get; set; } = "proportional";

    /// <summary>Optional per-pipeline auto-accept threshold override.</summary>
    [JsonPropertyName("auto_accept_threshold")]
    public double? AutoAcceptThreshold { get; set; }

    /// <summary>Optional per-pipeline ambiguous threshold override.</summary>
    [JsonPropertyName("ambiguous_threshold")]
    public double? AmbiguousThreshold { get; set; }
}

/// <summary>
/// Pipeline configuration for a single media type.
/// Defines the execution strategy and ordered provider list for Stage 1
/// (Retail Identification) of the hydration pipeline.
/// </summary>
public sealed class MediaTypePipeline
{
    /// <summary>
    /// How providers collaborate: Waterfall (first match wins), Cascade (all run,
    /// claims merge), or Sequential (chained, each feeds the next).
    /// </summary>
    [JsonPropertyName("strategy")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ProviderStrategy Strategy { get; set; } = ProviderStrategy.Waterfall;

    /// <summary>
    /// Ranked list of providers for this media type. Executed in
    /// <see cref="PipelineProviderEntry.Rank"/> order.
    /// </summary>
    [JsonPropertyName("providers")]
    public List<PipelineProviderEntry> Providers { get; set; } = [];

    /// <summary>
    /// Absolute provider-attempt budget, including configured transitions.
    /// Prevents configuration cycles from producing unbounded provider calls.
    /// </summary>
    [JsonPropertyName("max_provider_attempts")]
    public int MaxProviderAttempts { get; set; } = 8;

    /// <summary>Media-pipeline retail scoring policy.</summary>
    [JsonPropertyName("scoring")]
    public RetailScoringPolicyConfiguration Scoring { get; set; } = new();

    /// <summary>
    /// Per-field provider priority overrides for this media type.
    /// Key = claim key (e.g. "cover", "description", "narrator").
    /// Value = ordered list of provider names; first provider with a claim wins.
    ///
    /// Used for fields where the media lane needs provider-specific display
    /// precedence (cover, rating, description, narrator, duration, page_count, or
    /// edition-specific audio title/author/series). Fields without an override fall
    /// back to the normal Priority Cascade.
    /// </summary>
    [JsonPropertyName("field_priorities")]
    public Dictionary<string, List<string>> FieldPriorities { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Complete pipeline configuration for all media types.
///
/// Loaded from <c>config/pipelines.json</c>. Supports unlimited ranked
/// providers per media type and three execution strategies.
/// </summary>
public sealed class PipelineConfiguration
{
    /// <summary>
    /// Maps media type display names to their pipeline configuration.
    /// Keys: "Books", "Audiobooks", "Comics", "Movies", "TV", "Music".
    /// </summary>
    [JsonPropertyName("pipelines")]
    public Dictionary<string, MediaTypePipeline> Pipelines { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the pipeline config for a given media type display name.
    /// Returns an empty Waterfall pipeline if the media type is not configured.
    /// </summary>
    public MediaTypePipeline GetPipelineForMediaType(string mediaTypeDisplayName)
    {
        return Pipelines.TryGetValue(mediaTypeDisplayName, out var pipeline) ? pipeline : new();
    }

    /// <summary>
    /// Resolves the pipeline config for a given <see cref="MediaType"/> enum value.
    /// </summary>
    public MediaTypePipeline GetPipelineForMediaType(MediaType mediaType)
    {
        return GetPipelineForMediaType(mediaType.ToString());
    }
}
