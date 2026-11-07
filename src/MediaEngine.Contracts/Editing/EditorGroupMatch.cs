using System.Text.Json.Serialization;
using MediaEngine.Contracts.Matching;

namespace MediaEngine.Contracts.Editing;

/// <summary>The kinds of group the Match tab can line up (universal editor v5, D36).</summary>
public static class EditorGroupMatchKinds
{
    /// <summary>Several episodes of one season, lined up against a season of one show by their episode numbers.</summary>
    public const string Episodes = "episodes";

    /// <summary>Several songs of one album, lined up against one release by their disc and track numbers.</summary>
    public const string Songs = "songs";
}

/// <summary>
/// Match on several ticked episodes or songs (universal editor v5, D36): one show and season, or one release, picked once. Every ticked
/// item is lined up by its own number against it.
/// </summary>
public sealed class EditorGroupMatchRequestDto
{
    /// <summary>One of <see cref="EditorGroupMatchKinds"/>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>The ticked items.</summary>
    [JsonPropertyName("entity_ids")]
    public List<Guid> EntityIds { get; set; } = [];

    /// <summary>For episodes: the TheTVDB show.</summary>
    [JsonPropertyName("series_id")]
    public string? SeriesId { get; set; }

    /// <summary>For episodes: the season of that show (0 is Specials).</summary>
    [JsonPropertyName("season_number")]
    public int? SeasonNumber { get; set; }

    /// <summary>For songs: the MusicBrainz release.</summary>
    [JsonPropertyName("release_id")]
    public string? ReleaseId { get; set; }
}

/// <summary>One ticked item and the episode or track it lines up with.</summary>
public sealed class EditorGroupMatchLineDto
{
    [JsonPropertyName("entity_id")]
    public Guid EntityId { get; init; }

    /// <summary>What the item is called now.</summary>
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    /// <summary>The number the item carries, as shown ("Episode 3", "Disc 1, track 4"); null when it carries none.</summary>
    [JsonPropertyName("number_text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NumberText { get; init; }

    /// <summary>The record it lines up with; null when the show or release has none with this item's number.</summary>
    [JsonPropertyName("target_title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetTitle { get; init; }

    /// <summary>One of <see cref="EditorMatchVerdicts"/>; null when nothing lined up.</summary>
    [JsonPropertyName("verdict")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Verdict { get; init; }

    [JsonPropertyName("reasons")]
    public IReadOnlyList<EditorMatchReasonDto> Reasons { get; init; } = [];

    /// <summary>True when the item lines up and its verdict is not Check match: it is ticked by default.</summary>
    [JsonPropertyName("fits")]
    public bool Fits { get; init; }

    /// <summary>A plain sentence when the item does not line up ("Season 2 has no episode 14").</summary>
    [JsonPropertyName("note")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }

    /// <summary>The record exactly as the match is applied from; null when nothing lined up.</summary>
    [JsonPropertyName("candidate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ItemCanonicalRetailCandidateDto? Candidate { get; init; }

    /// <summary>Where applying the match puts the item and its file; null when nothing lined up.</summary>
    [JsonPropertyName("placement")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EditorMatchPlacementDto? Placement { get; init; }
}

/// <summary>The answer to a group Match: one line per ticked item, in the order they were ticked.</summary>
public sealed class EditorGroupMatchDto
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    /// <summary>The show and season, or the release, the items were lined up with, as shown.</summary>
    [JsonPropertyName("target_label")]
    public string TargetLabel { get; init; } = string.Empty;

    [JsonPropertyName("lines")]
    public IReadOnlyList<EditorGroupMatchLineDto> Lines { get; init; } = [];

    /// <summary>The plain sentence shown when nothing could be read (the provider did not answer); null otherwise.</summary>
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }
}
