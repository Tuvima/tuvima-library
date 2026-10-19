using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// The exact builds of FFmpeg and Tesseract the Library may fetch when the person asks, loaded from
/// <c>config/tool_downloads.json</c>. Every entry is a fixed address and the SHA-256 value the download (and, for
/// FFmpeg, each program inside it) must match. A missing file means the pinned values below. Nothing here is ever
/// fetched by itself.
/// </summary>
public sealed class ToolDownloadSettings
{
    /// <summary>The only host a download may come from.</summary>
    public const string AllowedHost = "github.com";

    /// <summary>The platform label of the builds pinned below.</summary>
    public const string WindowsX64 = "win-x64";

    /// <summary>FFmpeg and FFprobe as one zip, or <c>null</c> when none is offered.</summary>
    [JsonPropertyName("ffmpeg")]
    public FfmpegDownload? Ffmpeg { get; set; } = new();

    /// <summary>Tesseract's setup program and the English and page-orientation language data, or <c>null</c> when none is offered.</summary>
    [JsonPropertyName("tesseract")]
    public TesseractDownload? Tesseract { get; set; } = new();

    /// <summary>
    /// True for a fixed <c>https</c> address on <see cref="AllowedHost"/> with no user name, password or port. Anything
    /// else (another host, plain <c>http</c>, a relative address) is refused.
    /// </summary>
    public static bool IsAllowedAddress(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, AllowedHost, StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(uri.UserInfo)
        && uri.IsDefaultPort;

    /// <summary>
    /// The hosts a download may be handed on to. GitHub serves release and raw files from its own storage hosts, so a
    /// fixed <see cref="AllowedHost"/> address redirects to one of these; anything else is refused.
    /// </summary>
    public static readonly IReadOnlyList<string> AllowedRedirectHosts =
    [
        AllowedHost,
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "raw.githubusercontent.com",
    ];

    /// <summary>True for an <c>https</c> address on <see cref="AllowedRedirectHosts"/> with no user name, password or port.</summary>
    public static bool IsAllowedRedirectAddress(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && AllowedRedirectHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(uri.UserInfo)
        && uri.IsDefaultPort;

    /// <summary>A plain file name: not empty, no folders, no drive, none of the characters a file name cannot hold.</summary>
    public static bool IsPlainFileName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name is not ("." or "..")
        && name.IndexOfAny(['/', '\\', ':']) < 0
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal);

    /// <summary>True for a 64-character hexadecimal SHA-256 value.</summary>
    public static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    /// <summary>
    /// Drops every entry that has an address outside <see cref="IsAllowedAddress"/> or a malformed checksum, so a
    /// hand-edited file can never point the Library at another host. A dropped entry simply is not offered.
    /// </summary>
    public void RemoveDisallowedEntries()
    {
        if (Ffmpeg is not null && !Ffmpeg.IsValid())
        {
            Ffmpeg = null;
        }

        if (Tesseract is not null && !Tesseract.IsValid())
        {
            Tesseract = null;
        }
    }
}

/// <summary>One file to fetch: where from, what it is called on disk, and the SHA-256 it must match.</summary>
public sealed class DownloadFile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>A plain file name only: no folders, no traversal.</summary>
    internal bool IsValid() =>
        ToolDownloadSettings.IsAllowedAddress(Url)
        && ToolDownloadSettings.IsSha256(Sha256)
        && ToolDownloadSettings.IsPlainFileName(Name);
}

/// <summary>The pinned FFmpeg build: one archive, and the two programs and licence taken out of it.</summary>
public sealed class FfmpegDownload
{
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = ToolDownloadSettings.WindowsX64;

    /// <summary>The version label shown to the person and written to the activity log.</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "9.0.1";

    [JsonPropertyName("url")]
    public string Url { get; set; } =
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-08-28-17-08/ffmpeg-n9.0.1-11-ge47273f4d9-win64-gpl-9.0.zip";

    /// <summary>SHA-256 of the whole archive.</summary>
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "DEE63142094F79F6A50CDECE65384B7793181EAB3B6DB2EC907834981BB8AB10";

    /// <summary>The programs taken out of the archive by file name, each with the SHA-256 it must have once extracted.</summary>
    [JsonPropertyName("files")]
    public List<ExtractedFile> Files { get; set; } =
    [
        new() { Name = "ffmpeg.exe", Sha256 = "989A60089B9B1A98896A5BD99EE793AB6841724E1B2441D5EF3E5D17DB0B0938" },
        new() { Name = "ffprobe.exe", Sha256 = "001D80FDDF67BC303E91C6B8ECCDF53AF29A5F87ECF3837056B391CC3DD3F7B4" },
    ];

    /// <summary>File names, in order of preference, of the licence inside the archive. The first found is saved as <c>LICENSE.txt</c>.</summary>
    [JsonPropertyName("license_names")]
    public List<string> LicenseNames { get; set; } = ["LICENSE.txt", "COPYING.GPLv3"];

    internal bool IsValid() =>
        Platform == ToolDownloadSettings.WindowsX64
        && ToolDownloadSettings.IsAllowedAddress(Url)
        && ToolDownloadSettings.IsSha256(Sha256)
        && Files is { Count: > 0 }
        && Files.All(file => file?.IsValid() == true)
        && LicenseNames is not null;
}

/// <summary>A program taken out of an archive by its file name.</summary>
public sealed class ExtractedFile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    internal bool IsValid() =>
        ToolDownloadSettings.IsSha256(Sha256)
        && ToolDownloadSettings.IsPlainFileName(Name);
}

/// <summary>The pinned Tesseract setup program and the language data it is used with.</summary>
public sealed class TesseractDownload
{
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = ToolDownloadSettings.WindowsX64;

    [JsonPropertyName("version")]
    public string Version { get; set; } = "5.4.0";

    /// <summary>Tesseract's setup program; it is downloaded and checked, then the person runs it.</summary>
    [JsonPropertyName("installer")]
    public DownloadFile Installer { get; set; } = new()
    {
        Name = "tesseract-ocr-w64-setup-5.4.0.20240606.exe",
        Url = "https://github.com/UB-Mannheim/tesseract/releases/download/v5.4.0.20240606/tesseract-ocr-w64-setup-5.4.0.20240606.exe",
        Sha256 = "C885FFF6998E0608BA4BB8AB51436E1C6775C2BAFC2559A19B423E18678B60C9",
    };

    /// <summary>The program the setup installs; recorded for reference and not checked by the Library.</summary>
    [JsonPropertyName("executable_sha256")]
    public string ExecutableSha256 { get; set; } = "BABB405F4366B480D02CD8FF2BAC8D497170F6C1711CE6F3D5D8BF0FB7FA6ED9";

    /// <summary>English and page-orientation data, placed straight into the text recognition data folder.</summary>
    [JsonPropertyName("language_files")]
    public List<DownloadFile> LanguageFiles { get; set; } =
    [
        new()
        {
            Name = "eng.traineddata",
            Url = "https://github.com/tesseract-ocr/tessdata_fast/raw/923915d4ced2a7235221788285785a29c4a42d4a/eng.traineddata",
            Sha256 = "7D4322BD2A7749724879683FC3912CB542F19906C83BCC1A52132556427170B2",
        },
        new()
        {
            Name = "osd.traineddata",
            Url = "https://github.com/tesseract-ocr/tessdata_fast/raw/923915d4ced2a7235221788285785a29c4a42d4a/osd.traineddata",
            Sha256 = "19E9B11EE4549A15BE965B74514745FBDA70EFA791B11CFBC5D53609F86EE60A",
        },
    ];

    internal bool IsValid() =>
        Platform == ToolDownloadSettings.WindowsX64
        && Installer?.IsValid() == true
        && Installer.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && LanguageFiles is not null
        && LanguageFiles.All(file => file?.IsValid() == true);
}
