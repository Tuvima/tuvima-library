using System.Text.Json;
using MediaEngine.Domain.Configuration;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// The pinned FFmpeg and Tesseract downloads (<c>config/tool_downloads.json</c>) and the tools banner's remembered
/// "Not now" answers.
/// </summary>
public sealed class ToolDownloadConfigAndPreferenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-tool-download-config-" + Guid.NewGuid().ToString("N"));

    public ToolDownloadConfigAndPreferenceTests()
    {
        DapperConfiguration.Configure();
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
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

    [Fact]
    public void AGithubAddressWithValidChecksums_IsKept()
    {
        var settings = LoadWith(ffmpegUrl: "https://github.com/BtbN/FFmpeg-Builds/releases/download/another/ffmpeg.zip");

        Assert.Equal("https://github.com/BtbN/FFmpeg-Builds/releases/download/another/ffmpeg.zip", settings.Ffmpeg?.Url);
    }

    // ── "Not now" on the banner ──────────────────────────────────────────────

    [Fact]
    public async Task ADeclinedTool_IsStillDeclinedForANewRepositoryOnTheSameDataStore()
    {
        var path = Path.Combine(_root, "store.db");
        using (var first = new DatabaseConnection(path))
        {
            first.InitializeSchema();
            first.RunStartupChecks();
            await new ToolPromptPreferenceRepository(first).DeclineAsync("ffmpeg");
        }

        using var second = new DatabaseConnection(path);
        second.InitializeSchema();
        second.RunStartupChecks();
        var declined = await new ToolPromptPreferenceRepository(second).GetDeclinedAsync();

        Assert.Equal(["ffmpeg"], declined);
    }

    [Fact]
    public async Task ClearingADecline_ForgetsOnlyThatTool_AndDecliningTwiceIsHarmless()
    {
        using var db = new DatabaseConnection(Path.Combine(_root, "clear.db"));
        db.InitializeSchema();
        db.RunStartupChecks();
        var repository = new ToolPromptPreferenceRepository(db);

        await repository.DeclineAsync("ffmpeg");
        await repository.DeclineAsync("ffmpeg");
        await repository.DeclineAsync("image_engine");
        await repository.ClearDeclinedAsync("ffmpeg");

        Assert.Equal(["image_engine"], await repository.GetDeclinedAsync());
    }

    [Fact]
    public async Task TheRepositoryDoesNotMistakeTheDataStoresOwnMetadataForDeclines()
    {
        using var db = new DatabaseConnection(Path.Combine(_root, "other.db"));
        db.InitializeSchema();
        db.RunStartupChecks();

        // The data store already keeps its epoch in the same table.
        Assert.Empty(await new ToolPromptPreferenceRepository(db).GetDeclinedAsync());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private ToolDownloadSettings LoadWith(
        string? ffmpegUrl = null, string? ffmpegSha256 = null, string? ffmpegFileName = null,
        string? tesseractInstallerUrl = null, string? tesseractLanguageUrl = null, string? tesseractLanguageName = null)
    {
        var defaults = new ToolDownloadSettings();
        defaults.Ffmpeg!.Url = ffmpegUrl ?? defaults.Ffmpeg.Url;
        defaults.Ffmpeg.Sha256 = ffmpegSha256 ?? defaults.Ffmpeg.Sha256;
        defaults.Ffmpeg.Files[0].Name = ffmpegFileName ?? defaults.Ffmpeg.Files[0].Name;
        defaults.Tesseract!.Installer.Url = tesseractInstallerUrl ?? defaults.Tesseract.Installer.Url;
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
