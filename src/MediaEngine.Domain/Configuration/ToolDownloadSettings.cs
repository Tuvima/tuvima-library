using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// The exact builds of FFmpeg, Tesseract and ExifTool the Library may fetch when the person asks, loaded from
/// <c>config/tool_downloads.json</c>. Every entry is a fixed address and the SHA-256 value the download (and, for
/// FFmpeg, each program inside it) must match. A missing file means the pinned values below. FFmpeg is fetched by the
/// Library on its own only while it is missing and <see cref="AutoInstallRequired"/> is on, and so is ExifTool when its
/// own <see cref="ExifToolDownload.AutoInstall"/> is on; everything else is fetched only when the person asks.
/// </summary>
public sealed class ToolDownloadSettings
{
    /// <summary>
    /// When true (the default), a Required tool the Library can fetch here (today FFmpeg) is downloaded by itself at
    /// first-run setup and at Engine start-up whenever it is missing. Turn it off to keep the older "offer only" behaviour.
    /// </summary>
    [JsonPropertyName("auto_install_required")]
    public bool AutoInstallRequired { get; set; } = true;

    /// <summary>The only host a download may come from.</summary>
    public const string AllowedHost = "github.com";

    /// <summary>The platform label of the builds pinned below.</summary>
    public const string WindowsX64 = "win-x64";

    /// <summary>The platform label of ExifTool's Perl distribution on 64-bit Linux.</summary>
    public const string LinuxX64 = "linux-x64";

    /// <summary>The platform label of ExifTool's Perl distribution on 64-bit ARM Linux.</summary>
    public const string LinuxArm64 = "linux-arm64";

    /// <summary>FFmpeg and FFprobe as one zip, or <c>null</c> when none is offered.</summary>
    [JsonPropertyName("ffmpeg")]
    public FfmpegDownload? Ffmpeg { get; set; } = new();

    /// <summary>Tesseract's setup program and the English and page-orientation language data, or <c>null</c> when none is offered.</summary>
    [JsonPropertyName("tesseract")]
    public TesseractDownload? Tesseract { get; set; } = new();

    /// <summary>ExifTool (writes the profile name into View files), one build per platform, or <c>null</c> when none is offered.</summary>
    [JsonPropertyName("exiftool")]
    public ExifToolDownload? ExifTool { get; set; } = new();

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

    /// <summary>
    /// The characters a Windows file name cannot hold. Checked on every platform, because the downloaded tools land on
    /// Windows too and <see cref="Path.GetInvalidFileNameChars"/> only lists <c>/</c> and NUL on Linux.
    /// </summary>
    private static readonly char[] ForbiddenFileNameChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    /// <summary>A plain file name: not empty, no folders, no drive, none of the characters a file name cannot hold.</summary>
    public static bool IsPlainFileName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name is not ("." or "..")
        && name.IndexOfAny(ForbiddenFileNameChars) < 0
        && !name.Any(char.IsControl)
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

        if (ExifTool is not null && !ExifTool.IsValid())
        {
            ExifTool = null;
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
    public string Version { get; set; } = "9.0.2";

    [JsonPropertyName("url")]
    public string Url { get; set; } =
        "https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip";

    /// <summary>SHA-256 of the whole archive.</summary>
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "60F467265B1E312373DBCD92200C2618A74850F98D3D078E94296BB3FA2047BA";

    /// <summary>The programs taken out of the archive by file name, each with the SHA-256 it must have once extracted.</summary>
    [JsonPropertyName("files")]
    public List<ExtractedFile> Files { get; set; } =
    [
        new() { Name = "ffmpeg.exe", Sha256 = "3256173F3F8BFFD7DF12227C68ADF68025EDB1832273A9530688A7BB1ED8EDEC" },
        new() { Name = "ffprobe.exe", Sha256 = "F0D36ECBBDD3BCFAC3EFA078C96C7271C2E68B3810595552AC3B7F17E9A65C52" },
    ];

    /// <summary>File names, in order of preference, of the licence inside the archive. The first found is saved as <c>LICENSE.txt</c>.</summary>
    [JsonPropertyName("license_names")]
    public List<string> LicenseNames { get; set; } = ["LICENSE.txt", "COPYING.GPLv3", "LICENSE"];

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

/// <summary>
/// A file inside an archive that must match a SHA-256 once extracted. <see cref="Path"/> is relative to the archive's
/// single top folder (which is dropped), uses <c>/</c>, and holds plain file names only.
/// </summary>
public sealed class ArchiveFile
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    internal bool IsValid() =>
        ToolDownloadSettings.IsSha256(Sha256)
        && !string.IsNullOrWhiteSpace(Path)
        && Path.Split('/').All(ToolDownloadSettings.IsPlainFileName);
}

/// <summary>
/// The pinned ExifTool builds: Windows gets the official 64-bit zip (it brings its own Perl), 64-bit Linux gets the
/// official Perl distribution (it needs Perl on the machine). Both are unmodified copies kept as a release of the
/// Library's own GitHub project, so every address stays on github.com.
/// </summary>
public sealed class ExifToolDownload
{
    /// <summary>The version label shown to the person and written to the activity log.</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "13.59";

    /// <summary>
    /// When true, the Library fetches ExifTool by itself whenever it is missing (and <see cref="ToolDownloadSettings.AutoInstallRequired"/>
    /// is on). ExifTool is an Optional tool, so this is the only way an Optional tool is fetched without a button press.
    /// </summary>
    [JsonPropertyName("auto_install")]
    public bool AutoInstall { get; set; } = true;

    [JsonPropertyName("builds")]
    public List<ExifToolBuild> Builds { get; set; } =
    [
        new()
        {
            Platforms = [ToolDownloadSettings.WindowsX64],
            Url = "https://github.com/Tuvima/tuvima-library/releases/download/exiftool-13.59/exiftool-13.59_64.zip",
            Sha256 = "44B512B25AF500724BA579D0A53C8FC5851628B692DD5E5D94AE4A15C2CBA9EC",
            Files =
            [
                new() { Path = "exiftool_files/perl.exe", Sha256 = "4D61EBE19311DBF7B9710AC2C6C402E3CBA3E23B63E8B82BE88E471343BED52D" },
                new() { Path = "exiftool_files/exiftool.pl", Sha256 = "9E8E817FAB9FDC2E37F7CD023AA34E76D1EF561834DFD791A48A7E327CC8F4B1" },
            ],
        },
        new()
        {
            Platforms = [ToolDownloadSettings.LinuxX64, ToolDownloadSettings.LinuxArm64],
            Url = "https://github.com/Tuvima/tuvima-library/releases/download/exiftool-13.59/Image-ExifTool-13.59.tar.gz",
            Sha256 = "668EA3ACECECB7235FBD0F4900E72D5F12C9B07E5C778FD36CB1E9B5828FD65A",
            Files = [new() { Path = "exiftool", Sha256 = "A6B8018093845AC93011307BAC4AB630D6604B2365FC7F66BB88C88D7DEDB5ED" }],
        },
    ];

    /// <summary>The build for a platform label such as <c>win-x64</c>, or <c>null</c> when none is pinned.</summary>
    public ExifToolBuild? FindBuild(string? platform) =>
        platform is null
            ? null
            : Builds?.FirstOrDefault(build => build?.Platforms?.Contains(platform, StringComparer.OrdinalIgnoreCase) == true);

    internal bool IsValid() =>
        Builds is { Count: > 0 }
        && Builds.All(build => build?.IsValid() == true)
        && Builds.SelectMany(build => build.Platforms).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            == Builds.Sum(build => build.Platforms.Count);
}

/// <summary>One ExifTool archive: where from, which platforms it is for, and the files inside it that must match.</summary>
public sealed class ExifToolBuild
{
    private static readonly string[] KnownPlatforms =
        [ToolDownloadSettings.WindowsX64, ToolDownloadSettings.LinuxX64, ToolDownloadSettings.LinuxArm64];

    [JsonPropertyName("platforms")]
    public List<string> Platforms { get; set; } = [];

    /// <summary>A <c>.zip</c> or <c>.tar.gz</c> address.</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    /// <summary>SHA-256 of the whole archive.</summary>
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>The program files that must match their SHA-256 once extracted. The rest of the archive (licences, readme, libraries) is kept beside them.</summary>
    [JsonPropertyName("files")]
    public List<ArchiveFile> Files { get; set; } = [];

    /// <summary>True for a <c>.tar.gz</c> archive, false for a <c>.zip</c>.</summary>
    [JsonIgnore]
    public bool IsTarGz => Url.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase);

    internal bool IsValid() =>
        Platforms is { Count: > 0 }
        && Platforms.All(platform => KnownPlatforms.Contains(platform, StringComparer.OrdinalIgnoreCase))
        && ToolDownloadSettings.IsAllowedAddress(Url)
        && (Url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || IsTarGz)
        && ToolDownloadSettings.IsSha256(Sha256)
        && Files is { Count: > 0 }
        && Files.All(file => file?.IsValid() == true);
}
