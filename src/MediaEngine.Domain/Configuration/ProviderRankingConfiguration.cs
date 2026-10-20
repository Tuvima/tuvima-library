using System.Text.Json;
using System.Text.Json.Serialization;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// Declares how a provider picks one result from a list that the search returned: which results
/// fit the request, how well, and how ties are broken. Used when plain title matching cannot tell
/// two near-identical results apart (the same series published in several runs, for example).
/// Declared in <c>candidate_ranking</c> of a provider file.
/// </summary>
public sealed class CandidateRankingConfiguration
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Media types the ranking applies to. Empty means all.</summary>
    [JsonPropertyName("media_types")]
    public List<string> MediaTypes { get; set; } = [];

    /// <summary>Named values read from the lookup request, usable by matches, scores and URLs.</summary>
    [JsonPropertyName("facts")]
    public Dictionary<string, RankingFactConfiguration> Facts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Facts that must have a value; without them the ranking is skipped.</summary>
    [JsonPropertyName("require_facts")]
    public List<string> RequireFacts { get; set; } = [];

    /// <summary>How the results themselves are matched and scored.</summary>
    [JsonPropertyName("candidates")]
    public RankingCandidateSetConfiguration Candidates { get; set; } = new();

    /// <summary>An extra search for the best-fitting run, whose result joins the candidates.</summary>
    [JsonPropertyName("preferred_run")]
    public RankingPreferredRunConfiguration? PreferredRun { get; set; }

    /// <summary>Fills in missing candidate fields from one lookup per candidate when several fit.</summary>
    [JsonPropertyName("enrich_when_multiple")]
    public RankingEnrichmentConfiguration? EnrichWhenMultiple { get; set; }

    /// <summary>Score terms added after enrichment, using the enriched fields.</summary>
    [JsonPropertyName("final_terms")]
    public List<RankingTermConfiguration> FinalTerms { get; set; } = [];

    /// <summary>Sort keys, best first, as <c>field:desc</c> or <c>field:asc</c>; <c>score</c> is always available.</summary>
    [JsonPropertyName("order")]
    public List<string> Order { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }

    public bool AppliesTo(MediaType mediaType) =>
        MediaTypes.Count == 0
        || MediaTypes.Any(name => string.Equals(name, mediaType.ToString(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Accepted values of <see cref="RankingFactConfiguration.As"/> and <see cref="RankingFieldConfiguration.As"/>.</summary>
public static class RankingValueKinds
{
    public const string Text = "text";
    public const string Year = "year";
    public const string LeadingInt = "leading_int";
    public const string CleanTitle = "clean_title";
}

/// <summary>Accepted values of <see cref="RankingMatchConfiguration.Compare"/>.</summary>
public static class RankingComparisons
{
    public const string EquivalentText = "equivalent_text";
    public const string EquivalentOrdinal = "equivalent_ordinal";
    public const string SameText = "equals";
}

/// <summary>Accepted values of <see cref="RankingTermConfiguration.Kind"/>.</summary>
public static class RankingTermKinds
{
    public const string WordOverlap = "word_overlap";
    public const string YearBands = "year_bands";
    public const string EnglishText = "english_text";
    public const string LogScale = "log_scale";
    public const string TextAffinity = "text_affinity";
}

public sealed class RankingFactConfiguration
{
    /// <summary>
    /// Sources, first non-blank wins: <c>request:{field}</c>, <c>hint:{key}</c>, <c>fact:{name}</c>
    /// (an earlier fact) or <c>title_year</c> (a year in brackets at the end of the request title).
    /// </summary>
    [JsonPropertyName("from")]
    public List<string> From { get; set; } = [];

    /// <summary><c>text</c> (default), <c>year</c>, <c>leading_int</c> or <c>clean_title</c>.</summary>
    [JsonPropertyName("as")]
    public string As { get; set; } = RankingValueKinds.Text;

    /// <summary>Source used when none of <see cref="From"/> has a value.</summary>
    [JsonPropertyName("fallback")]
    public string? Fallback { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public sealed class RankingFieldConfiguration
{
    /// <summary>Paths tried in order; the first non-blank value wins.</summary>
    [JsonPropertyName("paths")]
    public List<string> Paths { get; set; } = [];

    [JsonPropertyName("as")]
    public string As { get; set; } = RankingValueKinds.Text;

    /// <summary>For <c>enrich_when_multiple</c>: keep the value the candidate already has.</summary>
    [JsonPropertyName("only_if_missing")]
    public bool OnlyIfMissing { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public sealed class RankingMatchConfiguration
{
    /// <summary>Fact that the candidate value is compared with.</summary>
    [JsonPropertyName("fact")]
    public string? Fact { get; set; }

    /// <summary>Alternative to <see cref="Fact"/>: a template over the surrounding values, such as <c>{id}</c>.</summary>
    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("paths")]
    public List<string> Paths { get; set; } = [];

    /// <summary><c>equivalent_text</c>, <c>equivalent_ordinal</c> or <c>equals</c> (ignoring case).</summary>
    [JsonPropertyName("compare")]
    public string Compare { get; set; } = RankingComparisons.EquivalentText;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public sealed class RankingBandConfiguration
{
    /// <summary>The band applies when the difference is at most this much; bands are tried in order.</summary>
    [JsonPropertyName("max_delta")]
    public int MaxDelta { get; set; }

    [JsonPropertyName("score")]
    public double Score { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public sealed class RankingTermConfiguration
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>Request fact the term reads.</summary>
    [JsonPropertyName("fact")]
    public string? Fact { get; set; }

    /// <summary>Candidate field the term reads.</summary>
    [JsonPropertyName("field")]
    public string? Field { get; set; }

    // word_overlap
    [JsonPropertyName("weight")]
    public double Weight { get; set; }

    // year_bands
    [JsonPropertyName("bands")]
    public List<RankingBandConfiguration>? Bands { get; set; }

    [JsonPropertyName("otherwise")]
    public double Otherwise { get; set; }

    /// <summary>When the fact has no value: fields at or above this year earn <see cref="NoFactScore"/>.</summary>
    [JsonPropertyName("no_fact_at_or_above")]
    public int? NoFactAtOrAbove { get; set; }

    [JsonPropertyName("no_fact_score")]
    public double NoFactScore { get; set; }

    // english_text
    [JsonPropertyName("foreign")]
    public double Foreign { get; set; }

    [JsonPropertyName("english")]
    public double English { get; set; }

    // log_scale
    [JsonPropertyName("factor")]
    public double Factor { get; set; }

    [JsonPropertyName("cap")]
    public double Cap { get; set; }

    /// <summary>A count below the value of this fact scores <see cref="BelowScore"/>.</summary>
    [JsonPropertyName("below_fact")]
    public string? BelowFact { get; set; }

    [JsonPropertyName("below_score")]
    public double BelowScore { get; set; }

    /// <summary>A count of at most this scores <see cref="AtMostScore"/>.</summary>
    [JsonPropertyName("at_most_count")]
    public int? AtMostCount { get; set; }

    [JsonPropertyName("at_most_score")]
    public double AtMostScore { get; set; }

    // text_affinity
    [JsonPropertyName("min")]
    public double Min { get; set; }

    [JsonPropertyName("hit")]
    public double Hit { get; set; }

    [JsonPropertyName("miss")]
    public double Miss { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public sealed class RankingScoreConfiguration
{
    [JsonPropertyName("base")]
    public double Base { get; set; }

    [JsonPropertyName("terms")]
    public List<RankingTermConfiguration> Terms { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public sealed class RankingCandidateSetConfiguration
{
    /// <summary>Tests every candidate must pass.</summary>
    [JsonPropertyName("match")]
    public List<RankingMatchConfiguration> Match { get; set; } = [];

    [JsonPropertyName("fields")]
    public Dictionary<string, RankingFieldConfiguration> Fields { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("score")]
    public RankingScoreConfiguration Score { get; set; } = new();

    /// <summary>How to tell that two candidates are the same item (a search hit and the item found in a run).</summary>
    [JsonPropertyName("identity")]
    public RankingIdentityConfiguration Identity { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

/// <summary>
/// Two candidates are the same when both have an id and the ids are equal; without ids, when both
/// have a scope and an ordinal, the scopes are equal and the ordinals are equivalent.
/// </summary>
public sealed class RankingIdentityConfiguration
{
    [JsonPropertyName("id_paths")]
    public List<string> IdPaths { get; set; } = [];

    [JsonPropertyName("scope_paths")]
    public List<string> ScopePaths { get; set; } = [];

    [JsonPropertyName("ordinal_paths")]
    public List<string> OrdinalPaths { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public sealed class RankingPreferredRunConfiguration
{
    /// <summary>Facts that must have a value for the extra search to run.</summary>
    [JsonPropertyName("when_facts")]
    public List<string> WhenFacts { get; set; } = [];

    [JsonPropertyName("search")]
    public RankingRunSearchConfiguration Search { get; set; } = new();

    [JsonPropertyName("result")]
    public RankingRunResultConfiguration Result { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

/// <summary>The search that finds the runs that could hold the requested item.</summary>
public sealed class RankingRunSearchConfiguration
{
    /// <summary>URL template over <c>{base_url}</c>, <c>{api_key}</c> and the facts.</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("items_path")]
    public string ItemsPath { get; set; } = string.Empty;

    [JsonPropertyName("match")]
    public List<RankingMatchConfiguration> Match { get; set; } = [];

    [JsonPropertyName("fields")]
    public Dictionary<string, RankingFieldConfiguration> Fields { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Field that identifies the run; runs without it are ignored.</summary>
    [JsonPropertyName("id_field")]
    public string IdField { get; set; } = string.Empty;

    /// <summary>Drops runs whose count field is below a fact (a run too short to hold the item).</summary>
    [JsonPropertyName("reject_if_below")]
    public RankingRejectConfiguration? RejectIfBelow { get; set; }

    [JsonPropertyName("score")]
    public RankingScoreConfiguration Score { get; set; } = new();

    [JsonPropertyName("order")]
    public List<string> Order { get; set; } = [];

    [JsonPropertyName("top")]
    public int Top { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public sealed class RankingRejectConfiguration
{
    [JsonPropertyName("field")]
    public string Field { get; set; } = string.Empty;

    [JsonPropertyName("fact")]
    public string Fact { get; set; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

/// <summary>Looks the requested item up inside each found run, best run first.</summary>
public sealed class RankingRunResultConfiguration
{
    /// <summary>URL template over the facts and the run's fields (for example <c>{id}</c>).</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("items_path")]
    public string ItemsPath { get; set; } = string.Empty;

    [JsonPropertyName("match")]
    public List<RankingMatchConfiguration> Match { get; set; } = [];

    /// <summary>Added to the score of the result found this way.</summary>
    [JsonPropertyName("score_bonus")]
    public double ScoreBonus { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public sealed class RankingEnrichmentConfiguration
{
    /// <summary>URL template over <c>{base_url}</c>, <c>{api_key}</c> and the candidate's fields.</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    /// <summary>Remember responses per key; a template over the candidate's fields.</summary>
    [JsonPropertyName("cache")]
    public FollowUpCacheConfiguration? Cache { get; set; }

    /// <summary>Path of the object in the response that the field paths read from.</summary>
    [JsonPropertyName("item_path")]
    public string ItemPath { get; set; } = string.Empty;

    /// <summary>Candidate field that selects the lookup; candidates without it are not enriched.</summary>
    [JsonPropertyName("key_field")]
    public string KeyField { get; set; } = string.Empty;

    [JsonPropertyName("adds")]
    public Dictionary<string, RankingFieldConfiguration> Adds { get; set; } = new(StringComparer.Ordinal);

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}
