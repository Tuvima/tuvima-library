using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// The pinned FFmpeg and Tesseract downloads (<c>config/tool_downloads.json</c>).
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
        Assert.Equal("DEE63142094F79F6A50CDECE65384B7793181EAB3B6DB2EC907834981BB8AB10", settings.Ffmpeg.Sha256);
        Assert.Equal(["ffmpeg.exe", "ffprobe.exe"], settings.Ffmpeg.Files.Select(file => file.Name));
        Assert.NotNull(settings.Tesseract);
        Assert.Equal(["eng.traineddata", "osd.traineddata"], settings.Tesseract.LanguageFiles.Select(file => file.Name));
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
        string? tesseractInstallerName = null)
    {
        var defaults = new ToolDownloadSettings();
        defaults.Ffmpeg!.Url = ffmpegUrl ?? defaults.Ffmpeg.Url;
        defaults.Ffmpeg.Sha256 = ffmpegSha256 ?? defaults.Ffmpeg.Sha256;
        defaults.Ffmpeg.Files[0].Name = ffmpegFileName ?? defaults.Ffmpeg.Files[0].Name;
        defaults.Tesseract!.Installer.Url = tesseractInstallerUrl ?? defaults.Tesseract.Installer.Url;
        defaults.Tesseract.Installer.Name = tesseractInstallerName ?? defaults.Tesseract.Installer.Name;
        defaults.Tesseract.LanguageFiles[0].Url = tesseractLanguageUrl ?? defaults.Tesseract.LanguageFiles[0].Url;
        defaults.Tesseract.LanguageFiles[0].Name = tesseractLanguageName ?? defaults.Tesseract.LanguageFiles[0].Name;

        var config = Path.Combine(_root, "config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "tool_downloads.json"), JsonSerializer.Serialize(defaults));
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
