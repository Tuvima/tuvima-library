using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Review;

public sealed class ReviewItemDto
{
    [JsonPropertyName("id")] public Guid Id { get; init; }
    [JsonPropertyName("entity_id")] public Guid EntityId { get; init; }
    [JsonPropertyName("entity_type")] public string EntityType { get; init; } = string.Empty;
    [JsonPropertyName("trigger")] public string Trigger { get; init; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
    [JsonPropertyName("proposed_collection_id")] public string? ProposedCollectionId { get; init; }
    [JsonPropertyName("confidence_score")] public double? ConfidenceScore { get; init; }
    [JsonPropertyName("candidates_json")] public string? CandidatesJson { get; init; }
    [JsonPropertyName("detail")] public string? Detail { get; init; }
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("resolved_at")] public DateTimeOffset? ResolvedAt { get; init; }
    [JsonPropertyName("resolved_by")] public string? ResolvedBy { get; init; }
    [JsonPropertyName("source_operation_id")] public Guid? SourceOperationId { get; init; }
    [JsonPropertyName("source_capability_id")] public string? SourceCapabilityId { get; init; }
    [JsonPropertyName("source_capability_sub_key")] public string? SourceCapabilitySubKey { get; init; }
    [JsonPropertyName("media_type")] public string? MediaType { get; init; }
    [JsonPropertyName("entity_title")] public string? EntityTitle { get; init; }
    [JsonPropertyName("cover_url")] public string? CoverUrl { get; init; }
    [JsonPropertyName("bridge_identifiers")] public Dictionary<string, string> BridgeIdentifiers { get; init; } = [];
    [JsonPropertyName("detected_facts")] public Dictionary<string, string> DetectedFacts { get; init; } = [];
}

public sealed class ReviewResolveRequestDto
{
    [JsonPropertyName("selected_qid")] public string? SelectedQid { get; init; }
    [JsonPropertyName("field_overrides")] public List<FieldOverrideDto>? FieldOverrides { get; init; }
    [JsonPropertyName("provider_name")] public string? ProviderName { get; init; }
    [JsonPropertyName("provider_item_id")] public string? ProviderItemId { get; init; }
}

public sealed class FieldOverrideDto
{
    [JsonPropertyName("key")] public string Key { get; init; } = string.Empty;
    [JsonPropertyName("value")] public string Value { get; init; } = string.Empty;
    [JsonPropertyName("provider_id")] public string? ProviderId { get; init; }
}

public sealed class ReviewCountResponse
{
    [JsonPropertyName("pending_count")]
    public int PendingCount { get; init; }
}

/// <summary>
/// One TMDB TV-catalogue suggestion for a film file that has no movie match. The Engine serialises
/// an array of these into <see cref="ReviewItemDto.CandidatesJson"/> for review items whose trigger is
/// <c>MovieMatchedAsTv</c>; the Dashboard reads it back to render the suggestion card.
/// </summary>
public sealed class MovieTvSuggestionDto
{
    [JsonPropertyName("tmdb_tv_id")] public string TmdbTvId { get; init; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("first_air_year")] public int? FirstAirYear { get; init; }
    /// <summary>TMDB series type, for example "Miniseries" or "Scripted".</summary>
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("seasons")] public int Seasons { get; init; }
    [JsonPropertyName("episodes")] public int Episodes { get; init; }
    [JsonPropertyName("poster_url")] public string? PosterUrl { get; init; }
    [JsonPropertyName("overview")] public string? Overview { get; init; }
}
