using System.Text.Json;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Review;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using Microsoft.Data.Sqlite;
using ProviderConfiguration = MediaEngine.Domain.Configuration.ProviderConfiguration;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Needs Review is one list (universal editor v3, WP7 and D9): review items and unmatched files read together. These tests use a real
/// temporary SQLite data store, and check the two promises the product makes: the library page's "N files not identified" opens a list that
/// holds exactly N, and the Settings notifier count is the number of rows the list holds.
/// </summary>
public sealed class NeedsReviewListReadServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly UnmatchedAssetRepository _unmatched;
    private readonly ReviewQueueRepository _reviews;
    private readonly NeedsReviewListReadService _service;

    public NeedsReviewListReadServiceTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_needs_review_list_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _unmatched = new UnmatchedAssetRepository(_db);
        _reviews = new ReviewQueueRepository(_db);
        var loader = new ListLoader(
            new LibraryFolderConfig { Id = "movies", Name = "Movies" },
            new LibraryFolderConfig { Id = "tv", Name = "TV" });
        _service = new NeedsReviewListReadService(new ReviewQueueReadService(_db), _unmatched, loader, new VaultLibraryRepository(_db));
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task TheListHoldsReviewItemsAndUnmatchedFilesTogether_NewestFirst_EachWithItsKind()
    {
        var oldFile = SeedFile("movies", "old.mkv", tried: At(1));
        var unsure = SeedReview("movies", ReviewTrigger.MetadataConflict, created: At(2));
        var newFile = SeedFile("tv", "new.mkv", tried: At(3));
        var failedRetail = SeedReview("movies", ReviewTrigger.RetailMatchFailed, created: At(4));

        var list = await _service.ListAsync(null, null, includeFiles: true, 0, 100);

        Assert.Equal(4, list.Total);
        Assert.Equal([$"review:{failedRetail:D}", $"file:{newFile:D}", $"review:{unsure:D}", $"file:{oldFile:D}"], list.Items.Select(row => row.Key));
        Assert.Equal(3, list.NotMatchedTotal);
        Assert.Equal(1, list.NotSureTotal);
        Assert.All(list.Items.Where(row => row.Unmatched is not null), row => Assert.Equal(NeedsReviewKinds.NotMatched, row.Kind));
        Assert.Equal(NeedsReviewKinds.NotSure, list.Items.Single(row => row.Key == $"review:{unsure:D}").Kind);
        Assert.Equal(NeedsReviewKinds.NotMatched, list.Items.Single(row => row.Key == $"review:{failedRetail:D}").Kind);
        Assert.Equal("TV", list.Items.Single(row => row.Key == $"file:{newFile:D}").LibraryName);
    }

    [Fact]
    public async Task TheKindFilterNarrowsTheRows_ButTheChipTotalsStayWhole()
    {
        SeedFile("movies", "a.mkv", tried: At(1));
        SeedFile("movies", "b.mkv", tried: At(2));
        SeedReview("movies", ReviewTrigger.MetadataConflict, created: At(3));

        var notMatched = await _service.ListAsync("not_matched", null, true, 0, 100);
        var notSure = await _service.ListAsync("not_sure", null, true, 0, 100);

        Assert.Equal(2, notMatched.Total);
        Assert.All(notMatched.Items, row => Assert.Equal(NeedsReviewKinds.NotMatched, row.Kind));
        Assert.Equal(1, notSure.Total);
        Assert.Equal((2, 1), (notSure.NotMatchedTotal, notSure.NotSureTotal));
    }

    [Fact]
    public async Task ALibrarysNotIdentifiedCountOpensAListOfExactlyThatManyRows()
    {
        for (var i = 0; i < 5; i++) SeedFile("movies", $"m{i}.mkv", tried: At(i));
        for (var i = 0; i < 2; i++) SeedFile("tv", $"t{i}.mkv", tried: At(10 + i));
        var kept = SeedFile("movies", "kept.mkv", tried: At(20));
        await _unmatched.KeepAsIsAsync(kept, DateTimeOffset.UtcNow);
        SeedReview("movies", ReviewTrigger.MetadataConflict, created: At(30));

        var counts = await _unmatched.CountByLibraryAsync();
        var list = await _service.ListAsync("not_matched", "movies", true, 0, 100);

        Assert.Equal(5, counts["movies"]);
        Assert.Equal(counts["movies"], list.Total);
        Assert.Equal(counts["movies"], list.Items.Count(row => row.Unmatched is not null));
        Assert.DoesNotContain(list.Items, row => row.Unmatched?.AssetId == kept);
        Assert.All(list.Items, row => Assert.Equal("movies", row.LibraryId, ignoreCase: true));
    }

    [Fact]
    public async Task TheNotifierCountIsTheNumberOfRowsTheListHolds()
    {
        SeedFile("movies", "a.mkv", tried: At(1));
        SeedFile("tv", "b.mkv", tried: At(2));
        SeedReview("movies", ReviewTrigger.MetadataConflict, created: At(3));
        SeedReview("tv", ReviewTrigger.RetailMatchFailed, created: At(4));
        var kept = SeedFile("tv", "kept.mkv", tried: At(5));
        await _unmatched.KeepAsIsAsync(kept, DateTimeOffset.UtcNow);

        var count = await _service.CountAsync(includeFiles: true);
        var list = await _service.ListAsync(null, null, true, 0, 100);

        Assert.Equal(list.Total, count.PendingCount);
        Assert.Equal(list.NotMatchedTotal, count.NotMatchedCount);
        Assert.Equal(list.NotSureTotal, count.NotSureCount);
        Assert.Equal(4, count.PendingCount);
    }

    [Fact]
    public async Task ACallerWhoMayNotSeeTheFilesGetsOnlyTheReviewItems_InTheListAndInTheCount()
    {
        SeedFile("movies", "a.mkv", tried: At(1));
        SeedReview("movies", ReviewTrigger.MetadataConflict, created: At(2));

        var list = await _service.ListAsync(null, null, includeFiles: false, 0, 100);
        var count = await _service.CountAsync(includeFiles: false);

        Assert.Single(list.Items);
        Assert.Null(list.Items[0].Unmatched);
        Assert.Equal(1, count.PendingCount);
    }

    [Fact]
    public async Task ALibraryThatIsNotListedHoldsNothing()
    {
        SeedFile("movies", "a.mkv", tried: At(1));

        var list = await _service.ListAsync(null, "no-such-library", true, 0, 100);

        Assert.Empty(list.Items);
        Assert.Equal(0, list.Total);
    }

    [Fact]
    public async Task PagingKeepsTheTotalAndTheOrder()
    {
        for (var i = 0; i < 5; i++) SeedFile("movies", $"m{i}.mkv", tried: At(i));

        var first = await _service.ListAsync(null, null, true, 0, 2);
        var second = await _service.ListAsync(null, null, true, 2, 2);

        Assert.Equal(5, first.Total);
        Assert.Equal(2, first.Items.Count);
        Assert.Equal(2, second.Items.Count);
        Assert.Empty(first.Items.Select(row => row.Key).Intersect(second.Items.Select(row => row.Key)));
    }

    [Fact]
    public async Task AWikidataReview_OffersTheProviderMatchAsItsBestGuess()
    {
        var id = SeedReview("movies", ReviewTrigger.WikidataBridgeFailed, created: At(1), title: "Arrival");

        var row = (await _service.ListAsync(null, null, true, 0, 10)).Items.Single(r => r.Key == $"review:{id:D}");

        Assert.NotNull(row.BestGuess);
        Assert.Equal("keep_provider_match", row.BestGuess!.Action);
        Assert.Equal("Arrival", row.BestGuess.Name);
        Assert.Equal(NeedsReviewKinds.NotSure, row.Kind);
    }

    [Fact]
    public async Task AFilmThatLooksLikeATvSeries_OffersTheSeriesAsItsBestGuess()
    {
        var suggestion = JsonSerializer.Serialize(new[] { new MovieTvSuggestionDto { TmdbTvId = "42", Name = "Chernobyl", FirstAirYear = 2019, Seasons = 1, Episodes = 5 } });
        var id = SeedReview("movies", ReviewTrigger.MovieMatchedAsTv, created: At(1), title: "Chernobyl", candidatesJson: suggestion);

        var row = (await _service.ListAsync(null, null, true, 0, 10)).Items.Single(r => r.Key == $"review:{id:D}");

        Assert.Equal("move_to_tv", row.BestGuess!.Action);
        Assert.Equal("Chernobyl", row.BestGuess.Name);
        Assert.Equal("2019", row.BestGuess.Year);
    }

    [Fact]
    public async Task AReviewWithNoSingleAnswerHasNoBestGuess_AndAnUnmatchedFileNeverDoes()
    {
        SeedReview("movies", ReviewTrigger.MetadataConflict, created: At(1));
        SeedFile("movies", "a.mkv", tried: At(2));

        var list = await _service.ListAsync(null, null, true, 0, 10);

        Assert.All(list.Items, row => Assert.Null(row.BestGuess));
    }

    private static DateTimeOffset At(int minutes) => new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

    private Guid SeedFile(string libraryId, string fileName, DateTimeOffset tried)
    {
        var (workId, editionId, assetId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        SeedAsset(workId, editionId, assetId, libraryId, $"C:/media/{libraryId}/{fileName}");
        _unmatched.MarkUnmatchedAsync(assetId, libraryId, UnmatchedReasons.NoMatch, tried, tried.AddDays(7)).GetAwaiter().GetResult();
        return assetId;
    }

    private Guid SeedReview(string libraryId, string trigger, DateTimeOffset created, string? title = null, string? candidatesJson = null)
    {
        var (workId, editionId, assetId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        SeedAsset(workId, editionId, assetId, libraryId, $"C:/media/{libraryId}/{assetId:N}.mkv");
        if (title is not null)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES ($id, 'title', $title, $now);";
            cmd.Parameters.Add("$id", SqliteType.Blob).Value = GuidSql.ToBlob(assetId);
            cmd.Parameters.AddWithValue("$title", title);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        var id = Guid.NewGuid();
        _reviews.InsertAsync(new ReviewQueueEntry
        {
            Id = id,
            EntityId = assetId,
            EntityType = "MediaAsset",
            Trigger = trigger,
            Status = ReviewStatus.Pending,
            Detail = "needs a look",
            CandidatesJson = candidatesJson,
            CreatedAt = created,
            ReviewReadyAt = created,
            AutomationCompletedAt = created,
        }).GetAwaiter().GetResult();
        return id;
    }

    private void SeedAsset(Guid workId, Guid editionId, Guid assetId, string libraryId, string path)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO works (id, media_type, work_kind) VALUES ($workId, 'Movies', 'standalone');
            INSERT INTO editions (id, work_id) VALUES ($editionId, $workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, library_id) VALUES ($assetId, $editionId, $hash, $path, $library);
            """;
        cmd.Parameters.Add("$workId", SqliteType.Blob).Value = GuidSql.ToBlob(workId);
        cmd.Parameters.Add("$editionId", SqliteType.Blob).Value = GuidSql.ToBlob(editionId);
        cmd.Parameters.Add("$assetId", SqliteType.Blob).Value = GuidSql.ToBlob(assetId);
        cmd.Parameters.AddWithValue("$hash", $"nrl-{assetId:N}");
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$library", libraryId);
        cmd.ExecuteNonQuery();
    }

    private sealed class ListLoader : IConfigurationLoader
    {
        public ListLoader(params LibraryFolderConfig[] libraries) =>
            Libraries = new LibrariesConfiguration { Libraries = [.. libraries] };

        public LibrariesConfiguration Libraries { get; }

        public event EventHandler<ConfigurationFileChangedEventArgs>? ConfigurationChanged;

        public void RaiseChanged(string relativePath, bool applied = true) =>
            ConfigurationChanged?.Invoke(this, new ConfigurationFileChangedEventArgs(relativePath, relativePath, applied, null));

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
