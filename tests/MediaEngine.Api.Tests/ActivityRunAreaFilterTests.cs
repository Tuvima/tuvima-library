using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using ProviderConfiguration = MediaEngine.Domain.Configuration.ProviderConfiguration;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Spec library-activity D31 (WP10, #863): the area chips (Watch, Listen, Read) and a library's own Activity tab narrow every batch to
/// that area: counts, covers, the item total and the opened tiles. A batch with nothing in the area is hidden.
/// </summary>
public sealed class ActivityRunAreaFilterTests : IDisposable
{
    private static readonly string[] Watch = ["Movies", "TV", "Videos"];
    private static readonly string[] Read = ["Books", "Comics"];
    private const string Library = "11111111-1111-1111-1111-111111111111";
    private const string OtherLibrary = "22222222-2222-2222-2222-222222222222";
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly ActivityRunRepository _runs;
    private readonly ActivityRunReadService _service;

    public ActivityRunAreaFilterTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_run_area_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _runs = new ActivityRunRepository(_db);
        _service = new ActivityRunReadService(_runs, new LibrariesLoader());
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(path); } catch (IOException) { /* temp database cleanup is best effort */ }
        }
    }

    [Fact]
    public async Task ListRuns_WithWatchPicked_ShowsOnlyTheMovieCountCoversAndTotalOfAMixedBatch()
    {
        var run = await OpenRunAsync(Library, Start);
        await AddAsync(run, "Movies", "Dune");
        await AddAsync(run, "Movies", "Arrival");
        await AddAsync(run, "Books", "Hyperion");
        await AddAsync(run, "Books", "Emma");
        await AddAsync(run, "Books", "Persuasion");

        var watch = Assert.Single((await _service.ListRunsAsync(new ActivityRunQuery(MediaTypes: Watch))).Runs);
        var count = Assert.Single(watch.Counts);
        Assert.Equal(("Movies", 2), (count.MediaType, count.Count));
        Assert.Equal(2, watch.ItemCount);
        Assert.Equal(2, watch.Covers.Count);
        Assert.All(watch.Covers, cover => Assert.Equal("Movies", cover.MediaType));

        var all = Assert.Single((await _service.ListRunsAsync(new ActivityRunQuery())).Runs);
        Assert.Equal(5, all.ItemCount);
        Assert.Equal(2, all.Counts.Count);
    }

    [Fact]
    public async Task ListRuns_WithReadPicked_ShowsOnlyTheBooksOfAMixedBatch()
    {
        var run = await OpenRunAsync(Library, Start);
        await AddAsync(run, "Movies", "Dune");
        await AddAsync(run, "Books", "Hyperion");
        await AddAsync(run, "Books", "Emma");

        var read = Assert.Single((await _service.ListRunsAsync(new ActivityRunQuery(MediaTypes: Read))).Runs);
        var count = Assert.Single(read.Counts);
        Assert.Equal(("Books", 2), (count.MediaType, count.Count));
        Assert.Equal(2, read.ItemCount);
        Assert.All(read.Covers, cover => Assert.Equal("Books", cover.MediaType));
        Assert.Equal(2, read.Covers.Count);
    }

    [Fact]
    public async Task ListGroups_WithWatchPicked_OpensOnlyTheMovieTilesOfAMixedBatch()
    {
        var run = await OpenRunAsync(Library, Start);
        await AddAsync(run, "Movies", "Dune");
        await AddAsync(run, "Books", "Hyperion");

        var watch = await _service.ListGroupsAsync(run.EntryId, new ActivityRunScope(MediaTypes: Watch), 0, 24);
        Assert.Equal("Dune", Assert.Single(watch!.Groups).Title);

        var read = await _service.ListGroupsAsync(run.EntryId, new ActivityRunScope(MediaTypes: Read), 0, 24);
        Assert.Equal("Hyperion", Assert.Single(read!.Groups).Title);
    }

    [Fact]
    public async Task ListRuns_WithWatchPicked_HidesABatchOfMusicOnly()
    {
        var music = await OpenRunAsync(Library, Start);
        await AddAsync(music, "Music", "Track one");
        var movies = await OpenRunAsync(Library, Start.AddDays(1));
        await AddAsync(movies, "Movies", "Dune");

        var watch = await _service.ListRunsAsync(new ActivityRunQuery(MediaTypes: Watch));

        Assert.Equal(movies.Id, Assert.Single(watch.Runs).Id);
        Assert.Equal(2, (await _service.ListRunsAsync(new ActivityRunQuery())).Runs.Count);
    }

    [Fact]
    public async Task SearchTitles_WithWatchPicked_FindsOnlyTitlesInThatArea()
    {
        var run = await OpenRunAsync(Library, Start);
        await AddAsync(run, "Movies", "Dune");
        await AddAsync(run, "Books", "Dune Messiah");

        var found = await _service.SearchTitlesAsync(new ActivityRunQuery(Search: "dune", MediaTypes: Watch));

        Assert.Equal("Dune", Assert.Single(found.Titles).Title);
    }

    [Fact]
    public async Task ListRuns_ForOneLibrary_ShowsOnlyThatLibrarysCoversOfABatchSharedWithAnotherLibrary()
    {
        var scan = Guid.NewGuid();
        var films = await OpenRunAsync(Library, Start, scan);
        var books = await OpenRunAsync(OtherLibrary, Start, scan);
        await AddAsync(films, "Movies", "Dune");
        await AddAsync(books, "Books", "Hyperion");

        var tab = Assert.Single((await _service.ListRunsAsync(new ActivityRunQuery(LibraryIds: [Library]))).Runs);
        Assert.Equal("Movies", Assert.Single(tab.Covers).MediaType);

        var tiles = await _service.ListGroupsAsync(scan, new ActivityRunScope(LibraryIds: [Library]), 0, 24);
        Assert.Equal("Dune", Assert.Single(tiles!.Groups).Title);
    }

    private async Task<ActivityRunRecord> OpenRunAsync(string library, DateTimeOffset at, Guid? scanId = null)
    {
        var run = new ActivityRunRecord(Guid.NewGuid(), library, ActivityRunKinds.Added, ActivityRunStatuses.Active, false, at, at, null, 0, scanId);
        await _runs.InsertRunAsync(run);
        return run;
    }

    private Task AddAsync(ActivityRunRecord run, string mediaType, string title)
        => _runs.AddItemAsync(
            new ActivityRunItemRecord(Guid.NewGuid(), run.Id, Guid.NewGuid(), null, mediaType, title, $"item|{Guid.NewGuid():N}", title, null, Start),
            Start);

    private sealed class LibrariesLoader : IConfigurationLoader
    {
        public LibrariesConfiguration Libraries { get; } = new()
        {
            Libraries =
            [
                new LibraryFolderConfig { Id = Library, Name = "Films" },
                new LibraryFolderConfig { Id = OtherLibrary, Name = "Books" },
            ],
        };

        public event EventHandler<ConfigurationFileChangedEventArgs>? ConfigurationChanged { add { } remove { } }

        public LibrariesConfiguration LoadLibraries() => Libraries;

        public string ConfigDirectoryPath => string.Empty;
        public CoreConfiguration LoadCore() => new();
        public void SaveCore(CoreConfiguration config) { }
        public ScoringSettings LoadScoring() => new();
        public void SaveScoring(ScoringSettings settings) { }
        public MaintenanceSettings LoadMaintenance() => new();
        public void SaveMaintenance(MaintenanceSettings settings) { }
        public HydrationSettings LoadHydration() => new();
        public void SaveHydration(HydrationSettings settings) { }
        public PipelineConfiguration LoadPipelines() => new();
        public void SavePipelines(PipelineConfiguration config) { }
        public DisambiguationSettings LoadDisambiguation() => new();
        public void SaveDisambiguation(DisambiguationSettings settings) { }
        public MediaTypeConfiguration LoadMediaTypes() => new();
        public void SaveMediaTypes(MediaTypeConfiguration config) { }
        public TranscodingSettings LoadTranscoding() => new();
        public void SaveTranscoding(TranscodingSettings settings) { }
        public FieldPriorityConfiguration LoadFieldPriorities() => new();
        public void SaveFieldPriorities(FieldPriorityConfiguration config) { }
        public ProviderConfiguration? LoadProvider(string name) => null;
        public void DeleteProvider(string name) { }
        public void SaveProvider(ProviderConfiguration config) { }
        public IReadOnlyList<ProviderConfiguration> LoadAllProviders() => [];
        public T? LoadConfig<T>(string subdirectory, string name) where T : class => null;
        public void SaveConfig<T>(string subdirectory, string name, T config) where T : class { }
        public T? LoadAi<T>() where T : class => default;
        public void SaveAi<T>(T settings) where T : class { }
        public PaletteConfiguration LoadPalette() => new();
        public void SavePalette(PaletteConfiguration palette) { }
    }
}
