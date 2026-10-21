using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// How Videos libraries read file and folder names. Loaded from <c>config/videos.json</c>; the defaults below apply
/// when the file is missing so a fresh install behaves the same.
/// </summary>
public sealed class VideoNamingConfiguration
{
    /// <summary>The order identification sources are tried; the first match wins.</summary>
    [JsonPropertyName("identification_order")]
    public List<string> IdentificationOrder { get; set; } = [VideoIdentificationMethods.YoutubeId, VideoIdentificationMethods.InfoJson, VideoIdentificationMethods.Nfo, VideoIdentificationMethods.FileName];

    [JsonPropertyName("title_cleaning")]
    public VideoTitleCleaningConfiguration TitleCleaning { get; set; } = new();

    [JsonPropertyName("phone_footage")]
    public VideoPhoneFootageConfiguration PhoneFootage { get; set; } = new();
}

/// <summary>The identification methods stored on a video, in the words used for the "How we identified this" note.</summary>
public static class VideoIdentificationMethods
{
    public const string YoutubeId = "youtube_id";
    public const string InfoJson = "info_json";
    public const string Nfo = "nfo";
    public const string FileName = "file_name";

    public static bool IsValid(string? value) => value is YoutubeId or InfoJson or Nfo or FileName;
}

public sealed class VideoTitleCleaningConfiguration
{
    /// <summary>Drop a number at the start of a name ("01 - Intro" becomes "Intro").</summary>
    [JsonPropertyName("strip_leading_number")]
    public bool StripLeadingNumber { get; set; } = true;

    /// <summary>Characters read as spaces.</summary>
    [JsonPropertyName("word_separators")]
    public List<string> WordSeparators { get; set; } = ["_", "."];

    /// <summary>Release tags removed from the end of a name, such as resolution and codec.</summary>
    [JsonPropertyName("remove_tags")]
    public List<string> RemoveTags { get; set; } =
    [
        "2160p", "1440p", "1080p", "720p", "480p", "360p", "4k", "uhd", "hdr", "web-dl", "webdl", "webrip", "bluray", "bdrip", "dvdrip", "hdtv",
        "x264", "x265", "h264", "h265", "hevc", "avc", "aac", "ac3", "dts", "mp4", "mkv", "av1", "vp9", "opus",
    ];

    /// <summary>Drop a trailing <c>[youtubeid]</c> from the visible title.</summary>
    [JsonPropertyName("strip_youtube_id_suffix")]
    public bool StripYoutubeIdSuffix { get; set; } = true;
}

public sealed class VideoPhoneFootageConfiguration
{
    /// <summary>Camera-style file name patterns (regular expressions, case-insensitive), such as IMG_1234 or MOV_0042.</summary>
    [JsonPropertyName("name_patterns")]
    public List<string> NamePatterns { get; set; } =
    [
        @"^(IMG|MOV|VID|PXL|DSC|DSCN|MVI|GOPR|GX\d*|PANO|WP|SAM)[_\-]?\d{3,}",
        @"^\d{8}[_\-]\d{6}",
        @"^(VID|MOV|IMG)[_\-]\d{8}",
        @"^PXL_\d{8}_\d{9}",
        @"^Screen Recording \d{4}",
    ];

    /// <summary>Folder names phones and cameras create.</summary>
    [JsonPropertyName("folder_names")]
    public List<string> FolderNames { get; set; } = ["DCIM", "Camera", "100APPLE", "100ANDRO", "100MEDIA", "Camera Roll"];

    /// <summary>The share of clips that must look like camera files before the hint appears.</summary>
    [JsonPropertyName("min_share")]
    public double MinShare { get; set; } = 0.6;

    /// <summary>Clips shorter than this many minutes count as short clips.</summary>
    [JsonPropertyName("short_clip_minutes")]
    public double ShortClipMinutes { get; set; } = 5;
}
