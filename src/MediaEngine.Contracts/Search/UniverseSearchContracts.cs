using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Search;

/// <summary>Request sent to <c>POST /search/universe</c> (Wikidata identity candidates).</summary>
public sealed class SearchUniverseRequestDto
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
}

/// <summary>A Wikidata identity candidate enriched with retail cover art.</summary>
public sealed class UniverseCandidateDto
{
    [JsonPropertyName("qid")]
    public string Qid { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("instance_of")]
    public string? InstanceOf { get; set; }

    [JsonPropertyName("year")]
    public string? Year { get; set; }

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("director")]
    public string? Director { get; set; }

    [JsonPropertyName("cover_url")]
    public string? CoverUrl { get; set; }

    [JsonPropertyName("wikipedia_extract")]
    public string? WikipediaExtract { get; set; }

    /// <summary>How the candidate was resolved: "bridge", "structured_sparql" or "title_search".</summary>
    [JsonPropertyName("resolution_tier")]
    public string? ResolutionTier { get; set; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("bridge_ids")]
    public Dictionary<string, string> BridgeIds { get; set; } = [];

    [JsonPropertyName("media_type")]
    public string? MediaType { get; set; }

    [JsonPropertyName("media_type_metadata")]
    public Dictionary<string, string>? MediaTypeMetadata { get; set; }

    [JsonPropertyName("match_scores")]
    public FieldMatchScoresDto? MatchScores { get; set; }
}

/// <summary>Response from <c>POST /search/universe</c>.</summary>
public sealed class SearchUniverseResponseDto
{
    [JsonPropertyName("candidates")]
    public List<UniverseCandidateDto> Candidates { get; set; } = [];

    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;

    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = string.Empty;
}
