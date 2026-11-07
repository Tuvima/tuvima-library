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

    /// <summary>One of <see cref="EditorMatchMessageCodes"/> when the item does not line up; the Dashboard words it. Null otherwise.</summary>
    [JsonPropertyName("note_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NoteCode { get; init; }

    /// <summary>What the note speaks of (the number the target lacks, "3" or "Disc 2, track 4"); null when it needs none.</summary>
    [JsonPropertyName("note_value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NoteValue { get; init; }

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

    /// <summary>One of <see cref="EditorMatchMessageCodes"/> when nothing could be read (the provider did not answer); the Dashboard words it. Null otherwise.</summary>
    [JsonPropertyName("message_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessageCode { get; init; }

    /// <summary>The media type the lined-up items are matched as; the Dashboard stages each match with it.</summary>
    [JsonPropertyName("subject_media_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SubjectMediaType { get; init; }

    /// <summary>The kind of item inside that type (episode, track).</summary>
    [JsonPropertyName("subject_scope_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SubjectScopeId { get; init; }

    /// <summary>The group of record each match is searched and staged as.</summary>
    [JsonPropertyName("subject_target_group")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SubjectTargetGroup { get; init; }
}

/// <summary>
/// The reasons the Match tab's reads give instead of a sentence (universal editor v5): the Dashboard turns each into its own wording, so the
/// Engine never sends English prose or provider names to be shown.
/// </summary>
public static class EditorMatchMessageCodes
{
    /// <summary>Nothing was typed to search for.</summary>
    public const string EnterWords = "enter_words";

    /// <summary>The request named nothing that can be lined up (no items, or no show, season or release).</summary>
    public const string InvalidRequest = "invalid_request";

    /// <summary>The provider did not answer.</summary>
    public const string ProviderSilent = "provider_silent";

    /// <summary>The provider answered with nothing.</summary>
    public const string NothingFound = "nothing_found";

    /// <summary>The provider is not connected.</summary>
    public const string ProviderNotConnected = "provider_not_connected";

    /// <summary>The show, season or release is not on the provider.</summary>
    public const string NotOnProvider = "not_on_provider";

    /// <summary>The person may not change this item.</summary>
    public const string CannotChange = "cannot_change";

    /// <summary>The item is not of the kind the group matches.</summary>
    public const string WrongKind = "wrong_kind";

    /// <summary>The item could not be read.</summary>
    public const string CouldNotRead = "could_not_read";

    /// <summary>The item's name and tags carry no number to line up by.</summary>
    public const string NoNumber = "no_number";

    /// <summary>The chosen season or release has nothing with the item's number (note value: the number).</summary>
    public const string NoSuchNumber = "no_such_number";

    /// <summary>The item's file name speaks of a different season than the chosen one (note value: the file's season).</summary>
    public const string OtherSeason = "other_season";

    /// <summary>Another ticked item already lines up with the same episode or track (note value: the number).</summary>
    public const string Duplicate = "duplicate";
}
