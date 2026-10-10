using System.Text.Json.Serialization;

namespace MediaEngine.Web.Models.ViewDTOs;

/// <summary>
/// A single review queue item as displayed in the Needs Review tab.
/// Maps from <c>GET /review/pending</c> and <c>GET /review/{id}</c>.
/// </summary>
public sealed class ReviewItemViewModel
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("entity_id")]
    public Guid EntityId { get; set; }

    [JsonPropertyName("entity_type")]
    public string EntityType { get; set; } = string.Empty;

    [JsonPropertyName("trigger")]
    public string Trigger { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "Pending";

    [JsonPropertyName("confidence_score")]
    public double? ConfidenceScore { get; set; }

    [JsonPropertyName("proposed_collection_id")]
    public Guid? ProposedCollectionId { get; set; }

    [JsonPropertyName("candidates_json")]
    public string? CandidatesJson { get; set; }

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("resolved_at")]
    public DateTimeOffset? ResolvedAt { get; set; }

    [JsonPropertyName("resolved_by")]
    public string? ResolvedBy { get; set; }

    [JsonPropertyName("source_operation_id")]
    public Guid? SourceOperationId { get; set; }

    [JsonPropertyName("source_capability_id")]
    public string? SourceCapabilityId { get; set; }

    [JsonPropertyName("source_capability_sub_key")]
    public string? SourceCapabilitySubKey { get; set; }

    /// <summary>Entity title (best-available), populated from canonical values.</summary>
    [JsonPropertyName("entity_title")]
    public string? EntityTitle { get; set; }

    /// <summary>The media type of the entity (e.g. "Epub", "Audiobook").</summary>
    [JsonPropertyName("media_type")]
    public string? MediaType { get; set; }

    /// <summary>Cover art URL from canonical "cover" value, if available.</summary>
    [JsonPropertyName("cover_url")]
    public string? CoverUrl { get; set; }

    /// <summary>
    /// Bridge identifiers extracted from canonical values (isbn_13, isbn_10, isbn,
    /// asin, apple_books_id, wikidata_qid, etc.). ISBN keys are included regardless
    /// of whether they came from Wikidata or retail providers.
    /// </summary>
    [JsonPropertyName("bridge_identifiers")]
    public Dictionary<string, string> BridgeIdentifiers { get; set; } = [];

    /// <summary>Local/file facts used to make the matching decision.</summary>
    [JsonPropertyName("detected_facts")]
    public Dictionary<string, string> DetectedFacts { get; set; } = [];
}

/// <summary>Request body for resolving a review queue item.</summary>
public sealed class ReviewResolveRequestDto
{
    [JsonPropertyName("selected_qid")]
    public string? SelectedQid { get; set; }

    [JsonPropertyName("field_overrides")]
    public List<FieldOverrideDto>? FieldOverrides { get; set; }

    /// <summary>
    /// When resolving via search results, the provider that produced the
    /// selected match (e.g. "apple_books").
    /// </summary>
    [JsonPropertyName("provider_name")]
    public string? ProviderName { get; set; }

    /// <summary>
    /// The provider-specific item identifier for the selected match.
    /// </summary>
    [JsonPropertyName("provider_item_id")]
    public string? ProviderItemId { get; set; }
}

/// <summary>A single field override applied when resolving a review item.</summary>
public sealed class FieldOverrideDto
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    [JsonPropertyName("provider_id")]
    public string? ProviderId { get; set; }
}

/// <summary>
/// Outcome of the Move to TV action: the Engine's confirmation, or the reason it failed. Dashboard-only;
/// the confirmation itself is the Contracts <c>ReviewMoveToTvResponse</c>.
/// </summary>
public sealed record ReviewMoveToTvAttempt(MediaEngine.Contracts.Review.ReviewMoveToTvResponse? Response, string? Error);

/// <summary>Pending review count returned by <c>GET /review/count</c>.</summary>
public sealed record ReviewCountDto(
    [property: JsonPropertyName("pending_count")] int PendingCount);
