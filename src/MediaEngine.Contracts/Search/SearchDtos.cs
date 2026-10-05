using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Search;

/// <summary>A work returned by the local collection search endpoint.</summary>
public sealed class SearchResultDto
{
    [JsonPropertyName("work_id")]
    public Guid WorkId { get; init; }

    [JsonPropertyName("collection_id")]
    public Guid? CollectionId { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("author")]
    public string? Author { get; init; }

    [JsonPropertyName("media_type")]
    public string MediaType { get; init; } = string.Empty;

    [JsonPropertyName("collection_display_name")]
    public string CollectionDisplayName { get; init; } = string.Empty;

    [JsonPropertyName("series")]
    public string? Series { get; init; }

    [JsonPropertyName("series_position")]
    public string? SeriesPosition { get; init; }

    [JsonPropertyName("show_name")]
    public string? ShowName { get; init; }

    [JsonPropertyName("season_number")]
    public string? SeasonNumber { get; init; }

    [JsonPropertyName("episode_number")]
    public string? EpisodeNumber { get; init; }

    [JsonPropertyName("cover_url")]
    public string? CoverUrl { get; init; }

    [JsonPropertyName("year")]
    public string? Year { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("rating")]
    public string? Rating { get; init; }
}

/// <summary>Request sent to the retail-provider search endpoint.</summary>
public sealed class SearchRetailRequestDto
{
    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;

    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = string.Empty;

    [JsonPropertyName("max_candidates")]
    public int MaxCandidates { get; set; } = 5;

    [JsonPropertyName("local_title")]
    public string? LocalTitle { get; set; }

    [JsonPropertyName("local_author")]
    public string? LocalAuthor { get; set; }

    [JsonPropertyName("local_year")]
    public string? LocalYear { get; set; }

    [JsonPropertyName("file_hints")]
    public Dictionary<string, string>? FileHints { get; set; }

    [JsonPropertyName("search_fields")]
    public Dictionary<string, string>? SearchFields { get; set; }
}

/// <summary>A provider candidate returned by retail search.</summary>
public sealed class SearchRetailCandidateDto
{
    [JsonPropertyName("provider_id")]
    public string ProviderId { get; set; } = string.Empty;

    [JsonPropertyName("provider_name")]
    public string ProviderName { get; set; } = string.Empty;

    [JsonPropertyName("provider_item_id")]
    public string? ProviderItemId { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("year")]
    public string? Year { get; set; }

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("director")]
    public string? Director { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("cover_url")]
    public string? CoverUrl { get; set; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("extra_fields")]
    public Dictionary<string, string> ExtraFields { get; set; } = [];

    [JsonPropertyName("match_scores")]
    public FieldMatchScoresDto? MatchScores { get; set; }

    [JsonPropertyName("composite_score")]
    public double CompositeScore { get; set; }
}

/// <summary>Response from retail-provider search.</summary>
public sealed class SearchRetailResponseDto
{
    [JsonPropertyName("candidates")]
    public List<SearchRetailCandidateDto> Candidates { get; set; } = [];

    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;

    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = string.Empty;
}

/// <summary>Requests provider-specific evidence for a retail candidate.</summary>
public sealed class RetailCandidateDetailRequestDto
{
    [JsonPropertyName("provider_name")]
    public string ProviderName { get; set; } = string.Empty;

    [JsonPropertyName("provider_item_id")]
    public string? ProviderItemId { get; set; }

    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = string.Empty;

    [JsonPropertyName("extra_fields")]
    public Dictionary<string, string> ExtraFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Rich evidence loaded after a retail candidate is selected.</summary>
public sealed class RetailCandidateDetailDto
{
    [JsonPropertyName("provider_name")]
    public string ProviderName { get; set; } = string.Empty;

    [JsonPropertyName("detail_kind")]
    public string DetailKind { get; set; } = "facts";

    [JsonPropertyName("heading")]
    public string Heading { get; set; } = "Provider details";

    [JsonPropertyName("source_label")]
    public string SourceLabel { get; set; } = string.Empty;

    [JsonPropertyName("facts")]
    public Dictionary<string, string> Facts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("items")]
    public List<RetailCandidateDetailItemDto> Items { get; set; } = [];

    [JsonPropertyName("unavailable_message")]
    public string? UnavailableMessage { get; set; }
}

public sealed class RetailCandidateDetailItemDto
{
    [JsonPropertyName("ordinal")]
    public int Ordinal { get; set; }

    [JsonPropertyName("disc_number")]
    public int? DiscNumber { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("duration_seconds")]
    public double? DurationSeconds { get; set; }

    [JsonPropertyName("provider_item_id")]
    public string? ProviderItemId { get; set; }
}

/// <summary>Request for the unified resolve search.</summary>
public sealed class SearchResolveRequestDto
{
    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;

    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = string.Empty;

    [JsonPropertyName("max_candidates")]
    public int MaxCandidates { get; set; } = 5;

    [JsonPropertyName("file_hints")]
    public Dictionary<string, string>? FileHints { get; set; }
}

/// <summary>A provider candidate returned by unified resolve search.</summary>
public sealed class SearchResolveCandidateDto
{
    [JsonPropertyName("provider_name")]
    public string ProviderName { get; set; } = string.Empty;

    [JsonPropertyName("provider_item_id")]
    public string ProviderItemId { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("year")]
    public string? Year { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("cover_url")]
    public string? CoverUrl { get; set; }

    [JsonPropertyName("rating")]
    public double? Rating { get; set; }

    [JsonPropertyName("retail_score")]
    public double RetailScore { get; set; }

    [JsonPropertyName("description_score")]
    public double DescriptionScore { get; set; }

    [JsonPropertyName("composite_score")]
    public double CompositeScore { get; set; }

    [JsonPropertyName("bridge_ids")]
    public Dictionary<string, string> BridgeIds { get; set; } = [];

    [JsonPropertyName("wikidata_resolved")]
    public bool WikidataResolved { get; set; }

    [JsonPropertyName("work_qid")]
    public string? WorkQid { get; set; }

    [JsonPropertyName("edition_qid")]
    public string? EditionQid { get; set; }

    [JsonPropertyName("is_edition")]
    public bool IsEdition { get; set; }

    [JsonPropertyName("wikidata_narrator")]
    public string? WikidataNarrator { get; set; }

    [JsonPropertyName("wikipedia_url")]
    public string? WikipediaUrl { get; set; }

    [JsonPropertyName("field_matches")]
    public List<DescriptionFieldMatchDto>? FieldMatches { get; set; }
}

/// <summary>Response from unified resolve search.</summary>
public sealed class SearchResolveResponseDto
{
    [JsonPropertyName("candidates")]
    public List<SearchResolveCandidateDto> Candidates { get; set; } = [];
}

/// <summary>A single field-level text comparison.</summary>
public sealed class DescriptionFieldMatchDto
{
    [JsonPropertyName("field_key")]
    public string FieldKey { get; set; } = string.Empty;

    [JsonPropertyName("file_value")]
    public string FileValue { get; set; } = string.Empty;

    [JsonPropertyName("matched")]
    public bool Matched { get; set; }

    [JsonPropertyName("raw_score")]
    public int RawScore { get; set; }

    [JsonPropertyName("weight")]
    public double Weight { get; set; }
}

/// <summary>Per-field fuzzy-match scores returned by provider searches.</summary>
public sealed class FieldMatchScoresDto
{
    [JsonPropertyName("field_scores")]
    public List<FieldScoreDto>? FieldScores { get; set; }

    [JsonPropertyName("title_score")]
    public double TitleScore { get; set; }

    [JsonPropertyName("author_score")]
    public double AuthorScore { get; set; }

    [JsonPropertyName("year_score")]
    public double YearScore { get; set; }

    [JsonPropertyName("format_score")]
    public double FormatScore { get; set; }

    [JsonPropertyName("composite_score")]
    public double CompositeScore { get; set; }

    [JsonPropertyName("title_verdict")]
    public int TitleVerdict { get; set; }

    [JsonPropertyName("author_verdict")]
    public int AuthorVerdict { get; set; }

    [JsonPropertyName("year_verdict")]
    public int YearVerdict { get; set; }

    [JsonPropertyName("format_verdict")]
    public int FormatVerdict { get; set; }

    [JsonPropertyName("cover_score")]
    public double CoverScore { get; set; }

    [JsonPropertyName("cover_verdict")]
    public int CoverVerdict { get; set; }
}

public sealed class FieldScoreDto
{
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
    [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
    [JsonPropertyName("score")] public double? Score { get; set; }
    [JsonPropertyName("weight")] public double Weight { get; set; }
    [JsonPropertyName("missing")] public bool Missing { get; set; }
    [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
    [JsonPropertyName("contribution")] public double Contribution { get; set; }
    [JsonPropertyName("missing_policy")] public string MissingPolicy { get; set; } = string.Empty;
    [JsonPropertyName("verdict")] public string Verdict { get; set; } = string.Empty;
    [JsonPropertyName("local_value")] public string? LocalValue { get; set; }
    [JsonPropertyName("candidate_value")] public string? CandidateValue { get; set; }
}
