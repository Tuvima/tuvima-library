using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// The Match tab's music flow (universal editor v5, D34): how many releases one search lists and how the release and the track to suggest
/// are chosen. Lives in <c>config/scoring.json</c> under <c>music_match</c>. The suggestion is advice: the person can choose any release or track.
/// </summary>
public sealed class MusicMatchSettings
{
    /// <summary>How many releases one search lists.</summary>
    [JsonPropertyName("max_releases")]
    public int MaxReleases { get; set; } = 10;

    /// <summary>Points a release earns when its title is the album's title.</summary>
    [JsonPropertyName("release_title_points")]
    public int ReleaseTitlePoints { get; set; } = 4;

    /// <summary>Points a release earns when its track count is exactly the number of tracks the Library holds for the album.</summary>
    [JsonPropertyName("release_track_count_exact_points")]
    public int ReleaseTrackCountExactPoints { get; set; } = 3;

    /// <summary>Points a release earns when it has more tracks than the Library holds (the Library holds part of the album).</summary>
    [JsonPropertyName("release_track_count_more_points")]
    public int ReleaseTrackCountMorePoints { get; set; } = 1;

    /// <summary>Points a track earns when its title is the file's title.</summary>
    [JsonPropertyName("track_title_points")]
    public int TrackTitlePoints { get; set; } = 4;

    /// <summary>Points a track earns when its number (and disc, when the file names one) is the file's.</summary>
    [JsonPropertyName("track_number_points")]
    public int TrackNumberPoints { get; set; } = 2;

    /// <summary>Points a track earns when its length agrees with the measured length.</summary>
    [JsonPropertyName("track_length_points")]
    public int TrackLengthPoints { get; set; } = 2;

    /// <summary>A track is suggested only when it earns at least this many points.</summary>
    [JsonPropertyName("track_min_points")]
    public int TrackMinPoints { get; set; } = 4;
}
