using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// How Vault file names are read (Videos spec D38a): name patterns made of parts, ignored words, characters treated as spaces,
/// product codes, and how closely a length must agree. Loaded from <c>config/vault_naming.json</c>; the defaults below apply when
/// the file is missing. These are settings, not code, so a person can adapt them to their own naming.
/// </summary>
public sealed class VaultNamingConfiguration
{
    /// <summary>
    /// Name patterns, tried in order; the first that fits the whole name wins. A pattern is plain text with parts in square brackets:
    /// <c>[studio]</c>, <c>[date]</c>, <c>[title]</c>, <c>[performer]</c> and <c>[code]</c>. A space in a pattern matches any run of spaces.
    /// </summary>
    [JsonPropertyName("patterns")]
    public List<string> Patterns { get; set; } =
    [
        "[studio] - [date] - [title]",
        "[studio] [date] [title]",
        "[date] - [title]",
        "[code] - [title]",
        "[studio] - [title]",
    ];

    /// <summary>Release words left out before a name is read (resolution, source, codec, group tags).</summary>
    [JsonPropertyName("ignored_words")]
    public List<string> IgnoredWords { get; set; } =
    [
        "2160p", "1440p", "1080p", "720p", "480p", "360p", "4k", "uhd", "hdr", "web-dl", "webdl", "webrip", "bluray", "bdrip", "dvdrip",
        "hdtv", "x264", "x265", "h264", "h265", "hevc", "avc", "aac", "ac3", "dts", "mp4", "mkv", "av1", "vp9", "opus", "xxx",
    ];

    /// <summary>Characters read as spaces.</summary>
    [JsonPropertyName("space_characters")]
    public List<string> SpaceCharacters { get; set; } = [".", "_"];

    [JsonPropertyName("product_code")]
    public VaultProductCodeConfiguration ProductCode { get; set; } = new();

    /// <summary>How closely a file's length must agree with a record's length for a code or a title to count as a strong match.</summary>
    [JsonPropertyName("duration_tolerance")]
    public VaultDurationToleranceConfiguration DurationTolerance { get; set; } = new();
}

public sealed class VaultProductCodeConfiguration
{
    /// <summary>
    /// A regular expression with the named groups <c>letters</c> and <c>digits</c>. The default reads studio codes such as ABC-123,
    /// ABCD_00123 and IPX001 when they stand alone in a name.
    /// </summary>
    [JsonPropertyName("pattern")]
    public string Pattern { get; set; } = @"(?<![A-Za-z0-9])(?<letters>[A-Za-z]{2,6})[-_]?(?<digits>\d{2,5})(?![A-Za-z0-9])";

    /// <summary>Letter groups that look like a code but are not one (video sizes, discs, volumes).</summary>
    [JsonPropertyName("ignored_prefixes")]
    public List<string> IgnoredPrefixes { get; set; } =
    [
        "hd", "sd", "mp", "cd", "dvd", "vol", "part", "ep", "fps", "hq", "fhd", "uhd", "dd", "ac", "aac", "dts", "avc", "hevc", "web",
        "bd", "scene", "disc", "cam", "mov", "mvi", "img", "vid", "dsc",
    ];
}

public sealed class VaultDurationToleranceConfiguration
{
    /// <summary>The smallest difference in seconds that still counts as the same length.</summary>
    [JsonPropertyName("seconds")]
    public double Seconds { get; set; } = 5;

    /// <summary>A share of the longer length that still counts as the same length. The larger of the two limits applies.</summary>
    [JsonPropertyName("percent")]
    public double Percent { get; set; } = 1.5;
}
