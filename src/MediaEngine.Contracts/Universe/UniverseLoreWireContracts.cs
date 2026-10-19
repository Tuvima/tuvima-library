using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Universe;

/// <summary>
/// Response item for <c>GET /universe/{qid}/lore-delta</c>: one entity whose cached Wikidata
/// revision was compared with the current one. Property names are snake_case, matching the
/// other Universe responses.
/// </summary>
public sealed record UniverseLoreDeltaResponse(
    string entity_qid,
    string label,
    long cached_revision,
    long current_revision,
    bool has_changed);

/// <summary>Request body for <c>POST /universe/{qid}/lore-sources/manual</c>.</summary>
public sealed record UniverseLoreManualSourceRequest(
    [property: JsonPropertyName("source_name")] string? SourceName,
    [property: JsonPropertyName("base_url")] string BaseUrl,
    [property: JsonPropertyName("api_url")] string? ApiUrl = null);
