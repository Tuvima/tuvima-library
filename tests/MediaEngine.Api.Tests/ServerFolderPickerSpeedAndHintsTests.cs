using MediaEngine.Api.Services.Settings;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The folder picker must never hang on a drive that does not answer, and it should warn (never block) when the chosen folder looks
/// like another kind of library or when nothing is set up to identify the media. Real temporary folders.
/// </summary>
public sealed class ServerFolderPickerSpeedAndHintsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-picker-hint-tests", Guid.NewGuid().ToString("N"));
    private readonly string _shared;
    private readonly string _configFolder;
    private readonly ConfigurationDirectoryLoader _configuration;
    private readonly ServerFolderBrowserService _service;

    public ServerFolderPickerSpeedAndHintsTests()
    {
        _shared = Path.Combine(_root, "shared");
        _configFolder = Path.Combine(_root, "config");
        Directory.CreateDirectory(Path.Combine(_shared, "TV Shows", "Some Show", "Season 1"));
        Directory.CreateDirectory(Path.Combine(_shared, "Films"));
        Directory.CreateDirectory(Path.Combine(_shared, "Stuff", "Show B", "Season 2"));
        File.WriteAllText(Path.Combine(_shared, "Films", "A Film (2001).mkv"), "x");
        File.WriteAllText(Path.Combine(_shared, "TV Shows", "Some Show", "Season 1", "Some Show - S01E01.mkv"), "x");
        var viewRoot = Directory.CreateDirectory(Path.Combine(_root, "view")).FullName;
        _configuration = new ConfigurationDirectoryLoader(_configFolder);
        _configuration.SaveLibraries(new LibrariesConfiguration
        {
            StorageLocations =
            [
                new ServerStorageLocationConfig { Id = "shared", Label = "Shared", Path = _shared, AllowWrite = true },
                new ServerStorageLocationConfig { Id = "view", Label = "View", Path = viewRoot, AllowWrite = true },
            ],
            ViewStorage = new ViewStorageConfig { StorageLocationId = "view", RelativeRoot = "." },
        });
        _service = new ServerFolderBrowserService(_configuration, NullLogger<ServerFolderBrowserService>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Test cleanup is best effort.
        }
    }

    private ServerFolderValidationResultDto Validate(string relative, string kindKey) =>
        _service.Validate(new ValidateServerFolderRequest
        {
            StorageLocationId = "shared",
            RelativePath = relative,
            KindKey = kindKey,
            SelectionMode = ServerFolderSelectionModes.ExistingLibrary,
        });

    [Theory]
    [InlineData("TV Shows", "Movies", "TV")]
    [InlineData("My Movies", "TV", "Movies")]
    [InlineData("Music", "Books", "Music")]
    [InlineData("Audiobooks", "Books", "Audiobooks")]
    [InlineData("Movies", "Movies", null)]
    [InlineData("Stuff", "Music", null)]
    public void LooksLikeOtherFormat_UsesTheFolderName(string name, string kindFormat, string? expected) =>
        Assert.Equal(expected, FolderKindHint.LooksLikeOtherFormat(Path.Combine(_shared, name), kindFormat));

    [Fact]
    public void LooksLikeOtherFormat_SeesSeasonFoldersInsideAMoviesFolder() =>
        Assert.Equal("TV", FolderKindHint.LooksLikeOtherFormat(Path.Combine(_shared, "Stuff"), "Movies"));

    [Fact]
    public void LooksLikeOtherFormat_LeavesAFilmFolderAlone() =>
        Assert.Null(FolderKindHint.LooksLikeOtherFormat(Path.Combine(_shared, "Films"), "Movies"));

    [Fact]
    public void Validate_OfATvFolderForAMoviesLibrary_WarnsButStillAllowsIt()
    {
        var result = Validate("TV Shows", "movies");

        Assert.True(result.CanSelect);
        var warning = Assert.Single(result.Issues, issue => issue.Code == "looks_like_other_kind");
        Assert.Equal("warning", warning.Severity);
        Assert.Contains("TV folder", warning.Message);
        Assert.Contains("Movies", warning.Message);
    }

    [Fact]
    public void Validate_OfAFilmFolderForAMoviesLibrary_HasNoKindWarning() =>
        Assert.DoesNotContain(Validate("Films", "movies").Issues, issue => issue.Code == "looks_like_other_kind");

    [Fact]
    public void Validate_ForMovies_WarnsWhenTheIdentifyingPluginHasNoKey()
    {
        WriteProvider("tmdb", enabled: true, apiKey: "");

        var result = Validate("Films", "movies");

        Assert.True(result.CanSelect);
        var warning = Assert.Single(result.Issues, issue => issue.Code == "no_metadata_provider");
        Assert.Equal("warning", warning.Severity);
        Assert.Contains("TMDB", warning.Message);
    }

    [Fact]
    public void Validate_ForMovies_DoesNotWarnOnceTheIdentifyingPluginHasAKey()
    {
        WriteProvider("tmdb", enabled: true, apiKey: "secret");

        Assert.DoesNotContain(Validate("Films", "movies").Issues, issue => issue.Code == "no_metadata_provider");
    }

    [Fact]
    public void Validate_ForMovies_WarnsWhenThePluginIsSwitchedOff()
    {
        WriteProvider("tmdb", enabled: false, apiKey: "secret");

        Assert.Contains(Validate("Films", "movies").Issues, issue => issue.Code == "no_metadata_provider");
    }

    [Fact]
    public async Task ADriveThatNeverAnswers_GivesAPlainMessageInsteadOfHanging()
    {
        _service.IoTimeLimit = TimeSpan.FromMilliseconds(100);
        using var release = new ManualResetEventSlim();

        var started = DateTime.UtcNow;
        var failure = await Assert.ThrowsAsync<ServerFolderAccessException>(() =>
            _service.RunWithTimeLimitAsync(() => { release.Wait(TimeSpan.FromSeconds(10)); return 1; }, CancellationToken.None));
        release.Set();

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.Contains("isn't responding", failure.Message);
    }

    [Fact]
    public async Task AFolderThatAnswers_IsListedAsBefore()
    {
        var result = await _service.BrowseAsync(new BrowseServerFoldersRequest { StorageLocationId = "shared" }, [], CancellationToken.None);

        Assert.Contains(result.Directories, directory => directory.Name == "Films");
    }

    [Fact]
    public void TheRootsList_NeverWaitsOnTheDrivesItOffers()
    {
        var started = DateTime.UtcNow;

        var roots = _service.GetStorageLocations();

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.Contains(roots, location => location.Id == "shared");
    }

    private void WriteProvider(string name, bool enabled, string apiKey)
    {
        var folder = Path.Combine(_configFolder, "providers");
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, name + ".json"),
            $$"""
            {
              "name": "{{name}}",
              "enabled": {{enabled.ToString().ToLowerInvariant()}},
              "domain": "Video",
              "provider_capabilities": ["identity", "metadata"],
              "requires_api_key": true,
              "http_client": { "api_key": "{{apiKey}}" }
            }
            """);
    }
}
