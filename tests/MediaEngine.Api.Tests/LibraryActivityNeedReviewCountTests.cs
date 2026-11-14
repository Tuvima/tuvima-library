using Dapper;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using ProviderConfiguration = MediaEngine.Domain.Configuration.ProviderConfiguration;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Library Activity (spec library-activity, WP7, D28, issue #866): there is one "need review" number. The Activity card, the Needs Review tab
/// and the administrator banner all show it, and it is the number the Needs Review list holds, not the count of import titles that look unsure.
/// </summary>
public sealed class LibraryActivityNeedReviewCountTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly ReviewQueueRepository _reviews;
    private readonly UnmatchedAssetRepository _unmatched;
    private readonly NeedsReviewListReadService _list;

    public LibraryActivityNeedReviewCountTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_activity_need_review_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _reviews = new ReviewQueueRepository(_db);
        _unmatched = new UnmatchedAssetRepository(_db);
        _list = new NeedsReviewListReadService(
            new ReviewQueueReadService(_db),
            _unmatched,
            new LibrariesLoader(new LibraryFolderConfig { Id = "movies", Name = "Movies" }),
            new VaultLibraryRepository(_db),
            new NeedsReviewRowContextReadService(_db));
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task TheCardTheTabAndTheBannerAllShowEight_WhenEightWaitInNeedsReviewAndFiveMoreTitlesAreStillBeingMatched()
    {
        var reviewIds = Enumerable.Range(1, 8).Select(n => SeedTitleWithReview($"Waiting {n}")).ToList();
        for (var n = 1; n <= 5; n++)
        {
            SeedTitleStillBeingMatched($"Matching {n}");
        }

        var (card, tab, banner) = await ReadAllThreeAsync();

        Assert.Equal(8, card);
        Assert.Equal(8, tab);
        Assert.Equal(8, banner);

        await _reviews.UpdateStatusAsync(reviewIds[0], ReviewStatus.Resolved, null);

        (card, tab, banner) = await ReadAllThreeAsync();
        Assert.Equal(7, card);
        Assert.Equal(7, tab);
        Assert.Equal(7, banner);
    }

    [Fact]
    public async Task TheCardNumber_IsTheNeedsReviewListsCount_NotTheNumberOfImportTitlesThatLookUnsure()
    {
        SeedTitleWithReview("Waiting");
        for (var n = 1; n <= 5; n++)
        {
            SeedTitleStillBeingMatched($"Matching {n}");
        }

        var snapshot = await Presentation().GetSnapshotAsync(includeUnmatchedFiles: true);

        Assert.Equal(1, snapshot.NeedReviewCount);
        Assert.Equal(1, (await _list.CountAsync(includeFiles: true)).PendingCount);
    }

    [Fact]
    public async Task ACallerWhoMayNotSeeUnmatchedFiles_GetsTheSameNumberTheirNeedsReviewCountGives()
    {
        SeedTitleWithReview("Waiting");
        var file = SeedTitleStillBeingMatched("Never matched");
        await _unmatched.MarkUnmatchedAsync(file, "movies", UnmatchedReasons.NoMatch, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7));

        var administrator = await Presentation().GetSnapshotAsync(includeUnmatchedFiles: true);
        var everyoneElse = await Presentation().GetSnapshotAsync(includeUnmatchedFiles: false);

        Assert.Equal((await _list.CountAsync(includeFiles: true)).PendingCount, administrator.NeedReviewCount);
        Assert.Equal(2, administrator.NeedReviewCount);
        Assert.Equal((await _list.CountAsync(includeFiles: false)).PendingCount, everyoneElse.NeedReviewCount);
        Assert.Equal(1, everyoneElse.NeedReviewCount);
    }

    private IngestionPresentationReadService Presentation() => new(
        _db,
        new FixedQueueProgress(new IngestionQueueProgressDto { State = "working", StartedAt = DateTimeOffset.UtcNow.AddHours(-1) }),
        needsReview: _list);

    // The card (the Activity snapshot), the tab (the Needs Review list's own total) and the banner (GET /review/count) as an administrator reads them.
    private async Task<(int Card, int Tab, int Banner)> ReadAllThreeAsync()
    {
        var card = (await Presentation().GetSnapshotAsync(includeUnmatchedFiles: true)).NeedReviewCount;
        var tab = (await _list.ListAsync(null, includeFiles: true, 0, 100)).Total;
        var banner = (await _list.CountAsync(includeFiles: true)).PendingCount;
        return (card, tab, banner);
    }

    // A title the Engine is unsure about: it has an entry waiting in Needs Review.
    private Guid SeedTitleWithReview(string title)
    {
        var asset = SeedTitle(title, identityState: null);
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        _reviews.InsertAsync(new ReviewQueueEntry
        {
            Id = id,
            EntityId = asset,
            EntityType = "MediaAsset",
            Trigger = ReviewTrigger.MetadataConflict,
            Status = ReviewStatus.Pending,
            Detail = "needs a look",
            CreatedAt = now,
            ReviewReadyAt = now,
            AutomationCompletedAt = now,
        }).GetAwaiter().GetResult();
        return id;
    }

    // A title whose match is still undecided: the import card used to count it as "review", yet nothing waits for a person in Needs Review.
    private Guid SeedTitleStillBeingMatched(string title) => SeedTitle(title, identityState: "RetailMatchedNeedsReview");

    private Guid SeedTitle(string title, string? identityState)
    {
        var (workId, editionId, assetId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var conn = _db.CreateConnection();
        conn.Execute("INSERT INTO works (id, media_type, work_kind) VALUES (@workId, 'Movies', 'standalone');", new { workId });
        conn.Execute("INSERT INTO editions (id, work_id, format_label) VALUES (@editionId, @workId, 'Test');", new { editionId, workId });
        conn.Execute(
            "INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, library_id) VALUES (@assetId, @editionId, @hash, @path, 'movies');",
            new { assetId, editionId, hash = $"hash-{assetId:N}", path = $"C:/watch/{assetId:N}.mkv" });
        conn.Execute("INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@workId, 'title', @title, @now);", new { workId, title, now });
        conn.Execute("""
            INSERT INTO ingestion_log (id, file_path, media_asset_id, content_hash, status, media_type, detected_title, normalized_title, created_at, updated_at)
            VALUES (@id, @path, @assetId, @hash, 'registered', 'Movies', @title, @title, @now, @now);
            """, new { id = Guid.NewGuid(), path = $"C:/watch/{assetId:N}.mkv", assetId, hash = $"hash-{assetId:N}", title, now });
        if (identityState is not null)
        {
            conn.Execute("""
                INSERT INTO identity_jobs (id, entity_id, entity_type, media_type, state, pass, created_at, updated_at)
                VALUES (@id, @assetId, 'MediaAsset', 'Movies', @state, 'Quick', @now, @now);
                """, new { id = Guid.NewGuid(), assetId, state = identityState, now });
        }

        return assetId;
    }

    private sealed class FixedQueueProgress(IngestionQueueProgressDto current) : MediaEngine.Api.Services.IIngestionQueueProgressService
    {
        public IngestionQueueProgressDto Current { get; } = current;
    }

    private sealed class LibrariesLoader : IConfigurationLoader
    {
        public LibrariesLoader(params LibraryFolderConfig[] libraries) =>
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
