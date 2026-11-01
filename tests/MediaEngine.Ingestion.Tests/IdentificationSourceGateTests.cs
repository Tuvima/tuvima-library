using MediaEngine.Domain.Configuration;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Ingestion.Tests;

public sealed class IdentificationSourceGateTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "tuvima-gate-" + Guid.NewGuid().ToString("N"));
    private readonly ConfigurationDirectoryLoader _loader;
    private readonly SpyResolver _folders = new();
    private readonly Clock _clock = new();

    public IdentificationSourceGateTests() => _loader = new ConfigurationDirectoryLoader(_config);

    public void Dispose()
    {
        _loader.Dispose();
        Directory.Delete(_config, recursive: true);
    }

    private IdentificationSourceGate Gate() => new(_folders, _loader, NullLogger<IdentificationSourceGate>.Instance, _clock);

    private void Provider(string name, bool enabled, bool requiresKey, string? key)
    {
        _loader.SaveProvider(new ProviderConfiguration
        {
            Name = name,
            Enabled = enabled,
            RequiresApiKey = requiresKey,
            HttpClient = key is null ? null : new HttpClientConfig { ApiKey = key },
        });
    }

    [Fact]
    public void AMoviesFile_IsHeld_WhileNeitherTmdbNorTheTvdbWorks()
    {
        _folders.Kind = "movies";
        Provider("tmdb", enabled: true, requiresKey: true, key: null);
        Provider("tvdb", enabled: true, requiresKey: true, key: null);

        Assert.True(Gate().IsHeld("/media/movies/Alien.mkv"));
    }

    [Fact]
    public void AMoviesFile_IsNotHeld_OnceOneOfTheSourcesHasAKey_WithoutARestart()
    {
        _folders.Kind = "movies";
        Provider("tmdb", enabled: true, requiresKey: true, key: null);
        var gate = Gate();
        Assert.True(gate.IsHeld("/media/movies/Alien.mkv"));

        Provider("tmdb", enabled: true, requiresKey: true, key: "abc");
        _clock.Advance(TimeSpan.FromSeconds(30));

        Assert.False(gate.IsHeld("/media/movies/Alien.mkv"));
    }

    [Fact]
    public void ASwitchedOffSourceWithAKey_DoesNotCount()
    {
        _folders.Kind = "tv";
        Provider("tmdb", enabled: false, requiresKey: true, key: "abc");

        Assert.True(Gate().IsHeld("/media/tv/Show/S01E01.mkv"));
    }

    [Theory]
    [InlineData("music")]
    [InlineData("books")]
    [InlineData("audiobooks")]
    [InlineData("photos")]
    [InlineData("other_videos")]
    public void KindsThatNeedNoOutsideSource_AreNeverHeld(string kind)
    {
        _folders.Kind = kind;

        Assert.False(Gate().IsHeld("/media/any/file.bin"));
    }

    [Fact]
    public void AComicsFile_NeedsComicVine()
    {
        _folders.Kind = "comics";
        Provider("comicvine", enabled: true, requiresKey: true, key: null);

        Assert.True(Gate().IsHeld("/media/comics/Saga 01.cbz"));
    }

    [Fact]
    public void AFileOutsideEveryLibrary_IsNotHeld()
    {
        _folders.Kind = null;

        Assert.False(Gate().IsHeld("/elsewhere/file.mkv"));
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class SpyResolver : ILibraryFolderResolver
    {
        public string? Kind { get; set; }

        public LibraryFolderEntry? ResolveById(string libraryId) => null;

        public LibraryFolderEntry? ResolveForPath(string absolutePath) =>
            Kind is null ? null : new LibraryFolderEntry { Id = "lib", Name = "Library", KindKey = Kind };

        public ResolvedLibrarySource? ResolveSourceForPath(string absolutePath) => null;

        public string? ResolveSourcePath(string absolutePath) => null;
    }
}
