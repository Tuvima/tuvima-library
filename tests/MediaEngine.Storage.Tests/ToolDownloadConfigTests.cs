using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// The pinned FFmpeg, Tesseract and ExifTool downloads (<c>config/tool_downloads.json</c>).
/// </summary>
public sealed class ToolDownloadConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-tool-download-config-" + Guid.NewGuid().ToString("N"));

    public ToolDownloadConfigTests()
    {
        DapperConfiguration.Configure();
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        TestTemp.ReleaseSqlitePoolsIn(_root);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ── The config file ──────────────────────────────────────────────────────

    [Fact]
    public void AMissingFile_MeansTheSamePinnedBuildsTheScriptsUse()
    {
        var settings = new ConfigurationDirectoryLoader(Path.Combine(_root, "config")).LoadToolDownloads();

        Assert.NotNull(settings.Ffmpeg);
        Assert.Equal("60F467265B1E312373DBCD92200C2618A74850F98D3D078E94296BB3FA2047BA", settings.Ffmpeg.Sha256);
        Assert.Equal(["ffmpeg.exe", "ffprobe.exe"], settings.Ffmpeg.Files.Select(file => file.Name));
        Assert.NotNull(settings.Tesseract);
        Assert.Equal(["eng.traineddata", "osd.traineddata"], settings.Tesseract.LanguageFiles.Select(file => file.Name));
        Assert.NotNull(settings.ExifTool);
        Assert.True(settings.ExifTool.AutoInstall);
        Assert.Equal("13.59", settings.ExifTool.Version);
    }

    [Fact]
    public void TheExifToolEntry_HasAWindowsAndALinuxBuild_BothOnGithub()
    {
        var exiftool = new ConfigurationDirectoryLoader(Path.Combine(FindRepositoryRoot(), "config")).LoadToolDownloads().ExifTool;

        Assert.NotNull(exiftool);
        var windows = exiftool.FindBuild(ToolDownloadSettings.WindowsX64);
        var linux = exiftool.FindBuild(ToolDownloadSettings.LinuxArm64);
        Assert.NotNull(windows);
        Assert.NotNull(linux);
        Assert.Same(linux, exiftool.FindBuild(ToolDownloadSettings.LinuxX64));
        Assert.False(windows.IsTarGz);
        Assert.True(linux.IsTarGz);
        Assert.All([windows.Url, linux.Url], url => Assert.True(ToolDownloadSettings.IsAllowedAddress(url)));
        Assert.Contains("exiftool_files/perl.exe", windows.Files.Select(file => file.Path));
        Assert.Contains("exiftool_files/exiftool.pl", windows.Files.Select(file => file.Path));
        Assert.Equal(["exiftool"], linux.Files.Select(file => file.Path));
        Assert.Null(exiftool.FindBuild("osx-arm64"));
    }

    [Theory]
    [InlineData("http://github.com/Tuvima/tuvima-library/releases/download/exiftool-13.59/exiftool-13.59_64.zip")]
    [InlineData("https://exiftool.org/exiftool-13.59_64.zip")]
    [InlineData("https://sourceforge.net/projects/exiftool/files/exiftool-13.59_64.zip/download")]
    [InlineData("https://github.com.example.com/exiftool-13.59_64.zip")]
    [InlineData("https://user:secret@github.com/Tuvima/tuvima-library/releases/download/x/exiftool.zip")]
    [InlineData("https://github.com/Tuvima/tuvima-library/releases/download/x/exiftool.exe")]
    [InlineData("")]
    public void AnExifToolAddressThatIsNotHttpsOnGithubOrNotAnArchive_IsRefusedWhenLoaded(string address)
    {
        var settings = LoadWith(exifToolUrl: address);

        Assert.Null(settings.ExifTool);
        Assert.NotNull(settings.Ffmpeg);
        Assert.NotNull(settings.Tesseract);
    }

    [Theory]
    [InlineData("not-a-checksum")]
    [InlineData("44B512B25AF500724BA579D0A53C8FC5851628B692DD5E5D94AE4A15C2CBA9E")]
    [InlineData("")]
    public void AnExifToolArchiveWithAMalformedChecksum_IsRefusedWhenLoaded(string checksum)
    {
        Assert.Null(LoadWith(exifToolSha256: checksum).ExifTool);
    }

    [Theory]
    [InlineData("../exiftool")]
    [InlineData("exiftool_files/../../perl.exe")]
    [InlineData("C:/perl.exe")]
    [InlineData("")]
    public void AnExifToolFilePathThatLeavesTheArchiveFolder_IsRefusedWhenLoaded(string path)
    {
        Assert.Null(LoadWith(exifToolFilePath: path).ExifTool);
    }

    [Fact]
    public void AnExifToolPlatformListedTwice_OrUnknown_IsRefusedWhenLoaded()
    {
        var twice = new ToolDownloadSettings();
        twice.ExifTool!.Builds[1].Platforms.Add(ToolDownloadSettings.WindowsX64);
        var unknown = new ToolDownloadSettings();
        unknown.ExifTool!.Builds[0].Platforms = ["plan9-mips"];

        Assert.Null(Load(twice).ExifTool);
        Assert.Null(Load(unknown).ExifTool);
    }

    [Fact]
    public void TheShippedFile_ParsesToTheSameValuesAsTheBuiltInDefaults()
    {
        var shipped = Path.Combine(FindRepositoryRoot(), "config");
        var settings = new ConfigurationDirectoryLoader(shipped).LoadToolDownloads();
        var defaults = new ToolDownloadSettings();

        Assert.Equal(JsonSerializer.Serialize(defaults), JsonSerializer.Serialize(settings));
    }

    [Theory]
    [InlineData("http://github.com/BtbN/FFmpeg-Builds/releases/download/x/ffmpeg.zip")]
    [InlineData("https://example.com/ffmpeg.zip")]
    [InlineData("https://github.com.example.com/ffmpeg.zip")]
    [InlineData("https://user:secret@github.com/BtbN/ffmpeg.zip")]
    [InlineData("https://github.com:8443/BtbN/ffmpeg.zip")]
    [InlineData("ftp://github.com/BtbN/ffmpeg.zip")]
    [InlineData("/relative/ffmpeg.zip")]
    [InlineData("")]
    public void AnFfmpegAddressThatIsNotHttpsOnGithub_IsRefusedWhenLoaded(string address)
    {
        var settings = LoadWith(ffmpegUrl: address);

        Assert.Null(settings.Ffmpeg);
        Assert.NotNull(settings.Tesseract);
    }

    [Fact]
    public void ATesseractInstallerOrLanguageFileOnAnotherHost_RefusesTheWholeTesseractEntry()
    {
        var installer = LoadWith(tesseractInstallerUrl: "https://downloads.example.org/tesseract-setup.exe");
        var language = LoadWith(tesseractLanguageUrl: "http://github.com/tesseract-ocr/tessdata_fast/raw/abc/eng.traineddata");

        Assert.Null(installer.Tesseract);
        Assert.NotNull(installer.Ffmpeg);
        Assert.Null(language.Tesseract);
    }

    [Fact]
    public void AMalformedChecksum_OrAFileNameWithAFolderInIt_IsRefused()
    {
        Assert.Null(LoadWith(ffmpegSha256: "not-a-checksum").Ffmpeg);
        Assert.Null(LoadWith(ffmpegFileName: "../ffmpeg.exe").Ffmpeg);
        Assert.Null(LoadWith(tesseractLanguageName: "tessdata/eng.traineddata").Tesseract);
    }

    [Theory]
    [InlineData("ffmpeg/../ffmpeg.exe")]
    [InlineData("..\\ffmpeg.exe")]
    [InlineData("C:ffmpeg.exe")]
    [InlineData("ffmpeg?.exe")]
    [InlineData("ffm|peg.exe")]
    [InlineData("")]
    [InlineData("..")]
    public void AFileNameThatIsNotAPlainName_IsRefused_ForFfmpegAndForTesseractLanguageData(string name)
    {
        Assert.Null(LoadWith(ffmpegFileName: name).Ffmpeg);
        Assert.Null(LoadWith(tesseractLanguageName: name).Tesseract);
    }

    [Theory]
    [InlineData("tesseract-setup.msi")]
    [InlineData("tesseract-setup.zip")]
    [InlineData("tesseract-setup")]
    [InlineData("tesseract-setup.exe.txt")]
    public void ATesseractInstallerThatIsNotAnExe_IsRefused(string name)
    {
        Assert.Null(LoadWith(tesseractInstallerName: name).Tesseract);
        Assert.NotNull(LoadWith(tesseractInstallerName: "Tesseract-Setup.EXE").Tesseract);
    }

    [Fact]
    public void AGithubAddressWithValidChecksums_IsKept()
    {
        var settings = LoadWith(ffmpegUrl: "https://github.com/BtbN/FFmpeg-Builds/releases/download/another/ffmpeg.zip");

        Assert.Equal("https://github.com/BtbN/FFmpeg-Builds/releases/download/another/ffmpeg.zip", settings.Ffmpeg?.Url);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private ToolDownloadSettings LoadWith(
        string? ffmpegUrl = null, string? ffmpegSha256 = null, string? ffmpegFileName = null,
        string? tesseractInstallerUrl = null, string? tesseractLanguageUrl = null, string? tesseractLanguageName = null,
        string? tesseractInstallerName = null, string? exifToolUrl = null, string? exifToolSha256 = null,
        string? exifToolFilePath = null)
    {
        var defaults = new ToolDownloadSettings();
        defaults.Ffmpeg!.Url = ffmpegUrl ?? defaults.Ffmpeg.Url;
        defaults.Ffmpeg.Sha256 = ffmpegSha256 ?? defaults.Ffmpeg.Sha256;
        defaults.Ffmpeg.Files[0].Name = ffmpegFileName ?? defaults.Ffmpeg.Files[0].Name;
        defaults.Tesseract!.Installer.Url = tesseractInstallerUrl ?? defaults.Tesseract.Installer.Url;
        defaults.Tesseract.Installer.Name = tesseractInstallerName ?? defaults.Tesseract.Installer.Name;
        defaults.Tesseract.LanguageFiles[0].Url = tesseractLanguageUrl ?? defaults.Tesseract.LanguageFiles[0].Url;
        defaults.Tesseract.LanguageFiles[0].Name = tesseractLanguageName ?? defaults.Tesseract.LanguageFiles[0].Name;
        defaults.ExifTool!.Builds[0].Url = exifToolUrl ?? defaults.ExifTool.Builds[0].Url;
        defaults.ExifTool.Builds[0].Sha256 = exifToolSha256 ?? defaults.ExifTool.Builds[0].Sha256;
        defaults.ExifTool.Builds[0].Files[0].Path = exifToolFilePath ?? defaults.ExifTool.Builds[0].Files[0].Path;
        return Load(defaults);
    }

    private ToolDownloadSettings Load(ToolDownloadSettings written)
    {
        var config = Path.Combine(_root, "config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "tool_downloads.json"), JsonSerializer.Serialize(written));
        return new ConfigurationDirectoryLoader(config).LoadToolDownloads();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "config", "tool_downloads.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("config/tool_downloads.json was not found above the test folder.");
    }
}
