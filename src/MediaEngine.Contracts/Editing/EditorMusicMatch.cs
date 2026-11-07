using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Editing;

/// <summary>
/// One release of an artist's album in the Match tab's "Albums and releases" list (universal editor v5, 3c "Music, step by step"): a
/// MusicBrainz release with the facts that tell the pressings apart (year, label, format, country, number of tracks).
/// </summary>
public sealed class EditorMusicReleaseDto
{
    /// <summary>The provider's id for the release (a MusicBrainz release id).</summary>
    [JsonPropertyName("release_id")]
    public string ReleaseId { get; init; } = string.Empty;

    /// <summary>The provider's id for the album the release belongs to; null when the provider gave none.</summary>
    [JsonPropertyName("release_group_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReleaseGroupId { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("artist")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Artist { get; init; }

    [JsonPropertyName("year")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Year { get; init; }

    [JsonPropertyName("label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; init; }

    /// <summary>The medium: "CD", "2 × CD", "Digital", "Vinyl".</summary>
    [JsonPropertyName("format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Format { get; init; }

    [JsonPropertyName("country")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Country { get; init; }

    [JsonPropertyName("track_count")]
    public int TrackCount { get; init; }

    /// <summary>The cover for the row, through the Engine's own preview connection point; null when there is none.</summary>
    /// <summary>True when the release has a front cover on the Cover Art Archive; the chosen track then carries that cover address.</summary>
    [JsonPropertyName("has_cover")]
    public bool HasCover { get; init; }

    [JsonPropertyName("image_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageUrl { get; init; }

    /// <summary>The release's page on the provider's own site ("Open source").</summary>
    [JsonPropertyName("source_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceUrl { get; init; }

    /// <summary>True for the release the song or album is matched to now (marked "current").</summary>
    [JsonPropertyName("is_current")]
    public bool IsCurrent { get; init; }

    /// <summary>True for the release whose track count and title fit the file best.</summary>
    [JsonPropertyName("is_suggested")]
    public bool IsSuggested { get; init; }
}

/// <summary>The answer to "which releases of this album are there": the list for the Match tab's first column.</summary>
public sealed class EditorMusicReleasesDto
{
    [JsonPropertyName("entity_id")]
    public Guid EntityId { get; init; }

    /// <summary>The words that were searched.</summary>
    [JsonPropertyName("query")]
    public string Query { get; init; } = string.Empty;

    [JsonPropertyName("releases")]
    public IReadOnlyList<EditorMusicReleaseDto> Releases { get; init; } = [];

    /// <summary>The plain sentence shown when there are no releases or the provider did not answer; null otherwise.</summary>
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }
}

/// <summary>One track of a release: its disc and number, its length, and which recording it is.</summary>
public sealed class EditorMusicTrackDto
{
    /// <summary>The provider's id for the recording (a MusicBrainz recording id).</summary>
    [JsonPropertyName("recording_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RecordingId { get; init; }

    /// <summary>The release's own id for this track on this release (a MusicBrainz track id).</summary>
    [JsonPropertyName("release_track_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReleaseTrackId { get; init; }

    [JsonPropertyName("disc")]
    public int Disc { get; init; } = 1;

    [JsonPropertyName("track")]
    public int Track { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("artist")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Artist { get; init; }

    [JsonPropertyName("length_seconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LengthSeconds { get; init; }

    /// <summary>The length as shown ("4:14"); null when the provider gave none.</summary>
    [JsonPropertyName("length_text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LengthText { get; init; }

    /// <summary>The provider's note that tells recordings of one song apart ("live, 2023", "radio edit", "remastered"); null when it gave none.</summary>
    [JsonPropertyName("disambiguation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Disambiguation { get; init; }

    /// <summary>True for the track whose number, title and length fit the file best.</summary>
    [JsonPropertyName("is_suggested")]
    public bool IsSuggested { get; init; }
}

/// <summary>One disc (medium) of a release with its tracks.</summary>
public sealed class EditorMusicDiscDto
{
    [JsonPropertyName("number")]
    public int Number { get; init; } = 1;

    /// <summary>The disc's own title when it has one ("Live at Bergen"); null otherwise.</summary>
    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    [JsonPropertyName("tracks")]
    public IReadOnlyList<EditorMusicTrackDto> Tracks { get; init; } = [];
}

/// <summary>A release opened: its discs and tracks, with the track that fits the file marked.</summary>
public sealed class EditorMusicReleaseTracksDto
{
    [JsonPropertyName("entity_id")]
    public Guid EntityId { get; init; }

    [JsonPropertyName("release")]
    public EditorMusicReleaseDto Release { get; init; } = new();

    [JsonPropertyName("discs")]
    public IReadOnlyList<EditorMusicDiscDto> Discs { get; init; } = [];
}
