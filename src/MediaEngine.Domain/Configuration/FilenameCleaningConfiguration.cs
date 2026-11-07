using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// What the Match tab's pre-fill, the Videos title cleaner and the scan read from a file or folder name (universal editor v5, D33),
/// loaded from <c>config/filename_cleaning.json</c>. Every list here is plain data: the noise words by family, the separators, the year
/// rule, the season-and-episode patterns, the track and disc patterns and the edition markers. The file shipped with the Engine states
/// every key; a person's copy may leave any out and may add words of their own under <see cref="CustomNoiseWords"/>.
/// </summary>
public sealed class FilenameCleaningConfiguration
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1";

    /// <summary>Release noise by family (resolution, source, codec, audio, release flags, container). The family names are free; only the words matter.</summary>
    [JsonPropertyName("noise_words")]
    public Dictionary<string, List<string>> NoiseWords { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Words a person adds of their own; they count like the shipped noise words.</summary>
    [JsonPropertyName("custom_noise_words")]
    public List<string> CustomNoiseWords { get; set; } = [];

    /// <summary>Noise words that are also ordinary title words; they only count after " - " or an opening bracket.</summary>
    [JsonPropertyName("ambiguous_words")]
    public List<string> AmbiguousWords { get; set; } = [];

    /// <summary>Regular expressions for noise a word list cannot spell, such as any resolution.</summary>
    [JsonPropertyName("noise_patterns")]
    public List<string> NoisePatterns { get; set; } = [];

    /// <summary>Characters read as spaces in a release-style name.</summary>
    [JsonPropertyName("separators")]
    public List<string> Separators { get; set; } = [];

    [JsonPropertyName("year")]
    public FilenameYearRule Year { get; set; } = new();

    /// <summary>Regular expressions with the named groups <c>s</c> (season) and <c>e</c> (episode).</summary>
    [JsonPropertyName("season_episode_patterns")]
    public List<string> SeasonEpisodePatterns { get; set; } = [];

    /// <summary>A regular expression for a folder that names a season.</summary>
    [JsonPropertyName("season_folder_pattern")]
    public string SeasonFolderPattern { get; set; } = string.Empty;

    /// <summary>Regular expressions with the named groups <c>track</c> and, where they carry one, <c>disc</c>.</summary>
    [JsonPropertyName("track_patterns")]
    public List<string> TrackPatterns { get; set; } = [];

    /// <summary>Words that name an edition (Extended, Director's Cut, Remastered, Live).</summary>
    [JsonPropertyName("edition_markers")]
    public List<string> EditionMarkers { get; set; } = [];
}

/// <summary>How a year is recognised in a name.</summary>
public sealed class FilenameYearRule
{
    /// <summary>A regular expression for the year itself (no brackets or separators around it).</summary>
    [JsonPropertyName("pattern")]
    public string Pattern { get; set; } = string.Empty;
}
