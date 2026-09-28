using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Models;

// ── Universe Search ───────────────────────────────────────────────────────────

/// <summary>Request to search Wikidata for identity candidates.</summary>
public sealed record SearchUniverseRequest(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("media_type")] string MediaType,
    [property: JsonPropertyName("max_candidates")] int MaxCandidates = 5,
    [property: JsonPropertyName("local_title")] string? LocalTitle = null,
    [property: JsonPropertyName("local_author")] string? LocalAuthor = null,
    [property: JsonPropertyName("local_year")] string? LocalYear = null);

/// <summary>A single enriched Wikidata candidate with cover art and description chained from retail.</summary>
public sealed class UniverseCandidate
{
    [JsonPropertyName("qid")]
    public required string Qid { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("instance_of")]
    public string? InstanceOf { get; init; }

    [JsonPropertyName("year")]
    public string? Year { get; init; }

    [JsonPropertyName("author")]
    public string? Author { get; init; }

    [JsonPropertyName("director")]
    public string? Director { get; init; }

    /// <summary>Cover art URL sourced from the chained retail provider (Apple Books, TMDB, etc.).</summary>
    [JsonPropertyName("cover_url")]
    public string? CoverUrl { get; init; }

    /// <summary>Wikipedia extract (first paragraph) fetched via QID sitelink.</summary>
    [JsonPropertyName("wikipedia_extract")]
    public string? WikipediaExtract { get; init; }

    /// <summary>How this candidate was resolved: "bridge", "structured_sparql", or "title_search".</summary>
    [JsonPropertyName("resolution_tier")]
    public string? ResolutionTier { get; init; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; init; }

    /// <summary>Bridge IDs harvested from Wikidata SPARQL (e.g. tmdb_id, isbn, imdb_id).</summary>
    [JsonPropertyName("bridge_ids")]
    public IReadOnlyDictionary<string, string> BridgeIds { get; init; } = new Dictionary<string, string>();

    /// <summary>Detected or confirmed media type for this candidate (e.g. "Epub", "Movies").</summary>
    [JsonPropertyName("media_type")]
    public string? MediaType { get; init; }

    /// <summary>Additional media-type-specific metadata (e.g. author for books, director for films).</summary>
    [JsonPropertyName("media_type_metadata")]
    public IReadOnlyDictionary<string, string>? MediaTypeMetadata { get; init; }

    /// <summary>Per-field match scores when compared against local file metadata. Null if no local context provided.</summary>
    [JsonPropertyName("match_scores")]
    public FieldMatchResult? MatchScores { get; set; }
}

/// <summary>Result of a universe search — list of enriched candidates.</summary>
public sealed record SearchUniverseResult(
    [property: JsonPropertyName("candidates")] IReadOnlyList<UniverseCandidate> Candidates,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("media_type")] string MediaType);

// ── Retail Search ─────────────────────────────────────────────────────────────

/// <summary>
/// Request to search retail providers for a media item.
/// When <c>FileHints</c> is provided (narrator, series, publisher, etc.), the Engine scores
/// candidates against file-embedded hints and blends the result into
/// <c>RetailCandidate.CompositeScore</c> for improved ranking.
/// </summary>
public sealed record SearchRetailRequest(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("media_type")] string MediaType,
    [property: JsonPropertyName("max_candidates")] int MaxCandidates = 5,
    [property: JsonPropertyName("local_title")] string? LocalTitle = null,
    [property: JsonPropertyName("local_author")] string? LocalAuthor = null,
    [property: JsonPropertyName("local_year")] string? LocalYear = null,
    [property: JsonPropertyName("file_hints")] IReadOnlyDictionary<string, string>? FileHints = null,
    [property: JsonPropertyName("search_fields")] IReadOnlyDictionary<string, string>? SearchFields = null);

/// <summary>A single retail provider candidate with cover art and basic metadata.</summary>
public sealed class RetailCandidate
{
    [JsonPropertyName("provider_id")]
    public required string ProviderId { get; init; }

    [JsonPropertyName("provider_name")]
    public required string ProviderName { get; init; }

    [JsonPropertyName("provider_item_id")]
    public string? ProviderItemId { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("year")]
    public string? Year { get; init; }

    [JsonPropertyName("author")]
    public string? Author { get; init; }

    [JsonPropertyName("director")]
    public string? Director { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("cover_url")]
    public string? CoverUrl { get; init; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; init; }

    /// <summary>Additional media-type-specific fields not covered by typed properties (e.g. album, track_number, duration).</summary>
    [JsonPropertyName("extra_fields")]
    public IReadOnlyDictionary<string, string> ExtraFields { get; init; } = new Dictionary<string, string>();

    /// <summary>Per-field match scores when compared against local file metadata. Null if no local context provided.</summary>
    [JsonPropertyName("match_scores")]
    public FieldMatchResult? MatchScores { get; set; }

    /// <summary>
    /// Composite ranking score combining retail fuzzy match (60%) and description match (40%).
    /// Used for sorting when FileHints were provided; equals Confidence otherwise.
    /// </summary>
    [JsonPropertyName("composite_score")]
    public double CompositeScore { get; set; }
}

/// <summary>Result of a retail search — list of candidates from relevant providers.</summary>
public sealed record SearchRetailResult(
    [property: JsonPropertyName("candidates")] IReadOnlyList<RetailCandidate> Candidates,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("media_type")] string MediaType)
{
    public IReadOnlyList<string> ProviderErrors { get; init; } = [];
}

// ── Apply Match ───────────────────────────────────────────────────────────────

/// <summary>Request to apply a manually selected match to a libraryItem item.</summary>
// ── Resolve Search ────────────────────────────────────────────────────────────

/// <summary>
/// Request for the unified resolve search that runs retail identification + Wikidata
/// bridge resolution for each candidate. Used by the resolve tab to show fully
/// enriched results ranked by composite score.
/// </summary>
/// <summary>A single resolve candidate with retail match + Wikidata bridge result.</summary>
internal sealed class ResolveCandidate
{
    /// <summary>Retail provider name (e.g. "apple_books", "tmdb").</summary>
    [JsonPropertyName("provider_name")]
    public string ProviderName { get; set; } = "";

    /// <summary>Provider's item ID for this candidate.</summary>
    [JsonPropertyName("provider_item_id")]
    public string ProviderItemId { get; set; } = "";

    /// <summary>Candidate title.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>Candidate author/artist.</summary>
    [JsonPropertyName("author")]
    public string? Author { get; set; }

    /// <summary>Year of publication/release.</summary>
    [JsonPropertyName("year")]
    public string? Year { get; set; }

    /// <summary>Description text (may contain basic HTML).</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>Cover art URL.</summary>
    [JsonPropertyName("cover_url")]
    public string? CoverUrl { get; set; }

    /// <summary>Rating (if available).</summary>
    [JsonPropertyName("rating")]
    public double? Rating { get; set; }

    /// <summary>Retail match confidence (0.0-1.0).</summary>
    [JsonPropertyName("retail_score")]
    public double RetailScore { get; set; }

    /// <summary>Description match score (0.0-1.0).</summary>
    [JsonPropertyName("description_score")]
    public double DescriptionScore { get; set; }

    /// <summary>Composite score combining all signals.</summary>
    [JsonPropertyName("composite_score")]
    public double CompositeScore { get; set; }

    /// <summary>Bridge IDs extracted from the retail result.</summary>
    [JsonPropertyName("bridge_ids")]
    public Dictionary<string, string> BridgeIds { get; set; } = new();

    // ── Wikidata bridge resolution results ──

    /// <summary>Whether Wikidata bridge resolution succeeded.</summary>
    [JsonPropertyName("wikidata_resolved")]
    public bool WikidataResolved { get; set; }

    /// <summary>Work QID (if resolved).</summary>
    [JsonPropertyName("work_qid")]
    public string? WorkQid { get; set; }

    /// <summary>Edition QID (if resolved to an edition).</summary>
    [JsonPropertyName("edition_qid")]
    public string? EditionQid { get; set; }

    /// <summary>Whether this is an edition match (vs work-level).</summary>
    [JsonPropertyName("is_edition")]
    public bool IsEdition { get; set; }

    /// <summary>Narrator from Wikidata (P175, if available).</summary>
    [JsonPropertyName("wikidata_narrator")]
    public string? WikidataNarrator { get; set; }

    /// <summary>Wikipedia URL (if available).</summary>
    [JsonPropertyName("wikipedia_url")]
    public string? WikipediaUrl { get; set; }

    /// <summary>Per-field match details for UI display.</summary>
    [JsonPropertyName("field_matches")]
    public List<FieldMatchDetail>? FieldMatches { get; set; }
}

/// <summary>A single field's match result for display in the resolve tab.</summary>
internal sealed class FieldMatchDetail
{
    [JsonPropertyName("field_key")]
    public string FieldKey { get; set; } = "";

    [JsonPropertyName("file_value")]
    public string FileValue { get; set; } = "";

    [JsonPropertyName("matched")]
    public bool Matched { get; set; }

    [JsonPropertyName("raw_score")]
    public int RawScore { get; set; }

    [JsonPropertyName("weight")]
    public double Weight { get; set; }
}

// ── Create Manual Entry ───────────────────────────────────────────────────────

/// <summary>Request to manually create metadata for a libraryItem item with no provider match.</summary>
