using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// The Match tab's verdict (universal editor v5, D8 and D34): when a search result reads as a Strong, Possible or Weak match, how close a
/// measured length has to be to count as "consistent", and which facts each kind of item compares. Lives in <c>config/scoring.json</c>
/// under <c>match_verdict</c>. The verdict is advice only: Apply match is always available.
/// </summary>
public sealed class MatchVerdictSettings
{
    /// <summary>A stored confidence (0 to 1) at or above this reads as Strong in the "Why this match" table.</summary>
    [JsonPropertyName("strong_min_score")]
    public double StrongMinScore { get; set; } = 0.85;

    /// <summary>A stored confidence at or above this (and below the Strong band) reads as Possible; below it reads as Weak.</summary>
    [JsonPropertyName("possible_min_score")]
    public double PossibleMinScore { get; set; } = 0.60;

    /// <summary>How many compared facts must agree (and none differ) for a result to read as Strong.</summary>
    [JsonPropertyName("strong_min_agreeing_facts")]
    public int StrongMinAgreeingFacts { get; set; } = 2;

    /// <summary>
    /// The facts that say which record this is. When one of them differs the result reads as Weak; when only another fact differs
    /// (a runtime, a year) it reads as Possible.
    /// </summary>
    [JsonPropertyName("identity_facts")]
    public List<string> IdentityFacts { get; set; } = ["title", "number", "identifier"];

    /// <summary>A provider runtime is rounded to whole minutes, so it is judged consistent with the measured length within this many minutes.</summary>
    [JsonPropertyName("runtime_tolerance_minutes")]
    public double RuntimeToleranceMinutes { get; set; } = 2;

    /// <summary>A music track's length is judged consistent with the measured length within this many seconds.</summary>
    [JsonPropertyName("track_length_tolerance_seconds")]
    public double TrackLengthToleranceSeconds { get; set; } = 3;

    /// <summary>The facts each kind of item compares, in the order the verdict lists them. Keyed by the editor's kind (movie, book and so on).</summary>
    [JsonPropertyName("evidence_facts")]
    public Dictionary<string, List<string>> EvidenceFacts { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["movie"] = ["title", "year", "runtime", "identifier"],
        ["tv_show"] = ["title", "year"],
        ["tv_season"] = ["title", "year", "number"],
        ["tv_episode"] = ["show", "number", "title", "runtime"],
        ["album"] = ["title", "creator", "year"],
        ["song"] = ["creator", "album", "title", "track_length", "identifier"],
        ["book"] = ["title", "creator", "identifier", "year"],
        ["audiobook"] = ["title", "creator", "year", "runtime"],
        ["comic"] = ["title", "number", "year"],
        ["video"] = ["identifier"],
    };

    /// <summary>
    /// Facts a kind judges but does not draw as a row of the compare table (universal editor v6, D9: a song's table is Artist, Album, Title and Length). They
    /// still count toward the verdict, so an agreeing ISRC or MusicBrainz id keeps its effect. Keyed by the editor's kind.
    /// </summary>
    [JsonPropertyName("verdict_only_facts")]
    public Dictionary<string, List<string>> VerdictOnlyFacts { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["song"] = ["identifier"],
    };

    /// <summary>
    /// Words that mark a result as a collection, box set or omnibus rather than one title (universal editor v6, D6): the list row then says
    /// "Collection". A provider that says so itself (an <c>is_collection</c> field) does not need them. Matched as whole words, any case.
    /// </summary>
    [JsonPropertyName("collection_title_markers")]
    public List<string> CollectionTitleMarkers { get; set; } = ["box set", "boxed set", "boxset", "collection", "complete series", "omnibus", "trilogy"];

    /// <summary>True when a fact is drawn as a row of the compare table for the kind (false for a fact that only counts toward the verdict).</summary>
    public bool IsDrawn(string? kind, string fact) =>
        string.IsNullOrWhiteSpace(kind) || !VerdictOnlyFacts.TryGetValue(kind, out var hidden) || !hidden.Contains(fact, StringComparer.OrdinalIgnoreCase);

    /// <summary>The facts compared for a kind; the title and year when the kind is not listed.</summary>
    public IReadOnlyList<string> FactsFor(string? kind) =>
        !string.IsNullOrWhiteSpace(kind) && EvidenceFacts.TryGetValue(kind, out var facts) && facts.Count > 0
            ? facts
            : ["title", "year"];
}
