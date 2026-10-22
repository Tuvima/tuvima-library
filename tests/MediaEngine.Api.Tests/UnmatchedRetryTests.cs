using Dapper;
using MediaEngine.Domain;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.FileWrites;
using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Api.Services.Unmatched;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using ProviderConfiguration = MediaEngine.Domain.Configuration.ProviderConfiguration;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Files the Engine could not match are looked up again without anyone doing anything (Library setup spec D34, D40): on the schedule, after a
/// provider or plugin changes, and on Retry now. A file kept as it is is never retried until Look up again brings it back. Everything runs against
/// a real temp data store with hand-written spies.
/// </summary>
public sealed class UnmatchedRetryTests : IDisposable
{
    private const string MoviesLibrary = "cccccccc-0000-4000-8000-000000000001";
    private const string ShowsLibrary = "cccccccc-0000-4000-8000-000000000002";
    private const string YoutubeLibrary = "cccccccc-0000-4000-8000-000000000003";
    private const string HomeVideosLibrary = "cccccccc-0000-4000-8000-000000000004";

    private static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-unmatched-retry-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly UnmatchedAssetRepository _unmatched;
    private readonly IdentityJobRepository _jobs;
    private readonly CanonicalValueRepository _canonicals;
    private readonly LibrariesOnlyLoader _loader;
    private readonly SpyRereader _rereader;
    private readonly SpySignal _signal = new();
    private readonly UnmatchedAssetService _service;

    public UnmatchedRetryTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _unmatched = new UnmatchedAssetRepository(_database);
        _jobs = new IdentityJobRepository(_database);
        _canonicals = new CanonicalValueRepository(_database);
        _rereader = new SpyRereader(_canonicals);
        _loader = new LibrariesOnlyLoader(
            Library(MoviesLibrary, "Movies", "movies"),
            Library(ShowsLibrary, "Shows", "tv"),
            Library(YoutubeLibrary, "YouTube", "youtube"),
            Library(HomeVideosLibrary, "Home videos", "other_videos"));
        var assets = new MediaAssetRepository(_database);
        _service = new UnmatchedAssetService(
            _unmatched,
            _jobs,
            assets,
            _canonicals,
            new ConfiguredExternalLookupGuard(_loader, assets, NullLogger<ConfiguredExternalLookupGuard>.Instance),
            _loader,
            _rereader,
            NullLogger<UnmatchedAssetService>.Instance,
            _signal,
            clock: new FixedClock(Now));
    }

    public void Dispose()
    {
        _database.Dispose();
        DatabaseConnection.ReleasePooledConnections(_databasePath);
        File.Delete(_databasePath);
    }

    // ── The schedule ──────────────────────────────────────────────────────────

    [Fact]
    public async Task TheSchedule_LooksADueFileUpAgain_ByQueueingANewIdentityJob()
    {
        var asset = await SeedUnmatchedAsync(MoviesLibrary, "Movies", due: Now.AddMinutes(-1));

        var handled = await _service.RetryDueAsync(CancellationToken.None);

        Assert.Equal(1, handled);
        var job = await _jobs.GetByEntityAsync(asset);
        Assert.Equal(nameof(IdentityJobState.Queued), job!.State);
        Assert.Equal("Movies", job.MediaType);
        Assert.Contains(IdentityPipelineSignalKind.Retail, _signal.Signals);
        var row = await _unmatched.GetAsync(asset);
        Assert.NotNull(row);
        Assert.Equal(Now, row.LastTriedAt);
        Assert.Equal(Now.AddDays(7), row.NextRetryAt);
    }

    [Fact]
    public async Task TheSchedule_LeavesAFileThatIsNotDueYetAlone()
    {
        var asset = await SeedUnmatchedAsync(MoviesLibrary, "Movies", due: Now.AddDays(3));

        var handled = await _service.RetryDueAsync(CancellationToken.None);

        Assert.Equal(0, handled);
        Assert.Equal(nameof(IdentityJobState.RetailNoMatch), (await _jobs.GetByEntityAsync(asset))!.State);
    }

    [Fact]
    public async Task TheSchedule_NeverRetriesAFileKeptAsItIs()
    {
        var asset = await SeedUnmatchedAsync(MoviesLibrary, "Movies", due: Now.AddMinutes(-1));
        await _service.KeepAsIsAsync(asset, CancellationToken.None);

        var handled = await _service.RetryDueAsync(CancellationToken.None);
        await _service.MakeEveryItemDueAsync(CancellationToken.None);
        handled += await _service.RetryDueAsync(CancellationToken.None);

        Assert.Equal(0, handled);
        Assert.Equal(nameof(IdentityJobState.RetailNoMatch), (await _jobs.GetByEntityAsync(asset))!.State);
        Assert.Empty(_signal.Signals);
    }

    [Fact]
    public async Task LookUpAgain_ClearsTheKeptMark_AndRetriesNow()
    {
        var asset = await SeedUnmatchedAsync(MoviesLibrary, "Movies", due: Now.AddDays(7));
        await _service.KeepAsIsAsync(asset, CancellationToken.None);

        var done = await _service.LookUpAgainAsync(asset, CancellationToken.None);

        Assert.True(done);
        var row = await _unmatched.GetAsync(asset);
        Assert.False(row!.KeptAsIs);
        Assert.Equal(nameof(IdentityJobState.Queued), (await _jobs.GetByEntityAsync(asset))!.State);
        Assert.Equal(1, (await _unmatched.CountByLibraryAsync())[MoviesLibrary]);
    }

    [Fact]
    public async Task ARetryOfAFileInALibraryThatLooksNothingUpNow_TakesItOutOfUnmatched_AndQueuesNothing()
    {
        // A library switched to Other videos never looks anything up, and a plain kind never has Unmatched files.
        var asset = await SeedUnmatchedAsync(HomeVideosLibrary, "Videos", due: Now.AddMinutes(-1));

        await _service.RetryDueAsync(CancellationToken.None);

        Assert.Null(await _unmatched.GetAsync(asset));
        Assert.Equal(nameof(IdentityJobState.RetailNoMatch), (await _jobs.GetByEntityAsync(asset))!.State);
        Assert.Empty(_signal.Signals);
    }

    // ── Retry now ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task RetryNowOnALibrary_QueuesEveryItemThatIsNotKept_AndLeavesOtherLibrariesAlone()
    {
        var first = await SeedUnmatchedAsync(MoviesLibrary, "Movies", due: Now.AddDays(5));
        var second = await SeedUnmatchedAsync(MoviesLibrary, "Movies", due: Now.AddDays(6));
        var kept = await SeedUnmatchedAsync(MoviesLibrary, "Movies", due: Now.AddDays(5));
        var otherLibrary = await SeedUnmatchedAsync(ShowsLibrary, "TV", due: Now.AddDays(5));
        await _service.KeepAsIsAsync(kept, CancellationToken.None);

        var looked = await _service.RetryLibraryNowAsync(MoviesLibrary, CancellationToken.None);

        Assert.Equal(2, looked);
        Assert.Equal(nameof(IdentityJobState.Queued), (await _jobs.GetByEntityAsync(first))!.State);
        Assert.Equal(nameof(IdentityJobState.Queued), (await _jobs.GetByEntityAsync(second))!.State);
        Assert.Equal(nameof(IdentityJobState.RetailNoMatch), (await _jobs.GetByEntityAsync(kept))!.State);
        Assert.Equal(nameof(IdentityJobState.RetailNoMatch), (await _jobs.GetByEntityAsync(otherLibrary))!.State);
    }

    [Fact]
    public async Task RetryNowOnOneFile_LooksItUpAgain_ButNotAKeptFile()
    {
        var open = await SeedUnmatchedAsync(MoviesLibrary, "Movies", due: Now.AddDays(5));
        var kept = await SeedUnmatchedAsync(MoviesLibrary, "Movies", due: Now.AddDays(5));
        await _service.KeepAsIsAsync(kept, CancellationToken.None);

        Assert.True(await _service.RetryNowAsync(open, CancellationToken.None));
        Assert.False(await _service.RetryNowAsync(kept, CancellationToken.None));

        Assert.Equal(nameof(IdentityJobState.Queued), (await _jobs.GetByEntityAsync(open))!.State);
        Assert.Equal(nameof(IdentityJobState.RetailNoMatch), (await _jobs.GetByEntityAsync(kept))!.State);
    }

    [Fact]
    public async Task KeepAll_KeepsEveryUnmatchedFileOfThatLibraryAndTheirCountGoesToZero()
    {
        await SeedUnmatchedAsync(MoviesLibrary, "Movies", due: Now.AddDays(5));
        await SeedUnmatchedAsync(MoviesLibrary, "Movies", due: Now.AddDays(5));
        await SeedUnmatchedAsync(ShowsLibrary, "TV", due: Now.AddDays(5));

        var kept = await _service.KeepAllAsIsAsync(MoviesLibrary, CancellationToken.None);

        Assert.Equal(2, kept);
        var counts = await _unmatched.CountByLibraryAsync();
        Assert.False(counts.ContainsKey(MoviesLibrary));
        Assert.Equal(1, counts[ShowsLibrary]);
    }

    // ── A provider or plugin changes ──────────────────────────────────────────

    [Fact]
    public async Task AProviderSettingsFileThatIsReloaded_MakesUnmatchedFilesDueAndTheNextPassLooksThemUp()
    {
        var asset = await SeedUnmatchedAsync(MoviesLibrary, "Movies", due: Now.AddDays(6));
        using var hosted = NewHostedService();

        _loader.RaiseChanged("providers/tmdb.json");
        var handled = await hosted.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, handled);
        Assert.Equal(nameof(IdentityJobState.Queued), (await _jobs.GetByEntityAsync(asset))!.State);
    }

    [Fact]
    public async Task APluginThatIsSwitchedOnOrUpdated_MakesUnmatchedFilesDue()
    {
        var asset = await SeedUnmatchedAsync(MoviesLibrary, "Movies", due: Now.AddDays(6));
        using var hosted = NewHostedService();

        hosted.NotifyProviderOrPluginChanged();
        var handled = await hosted.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, handled);
        Assert.Equal(nameof(IdentityJobState.Queued), (await _jobs.GetByEntityAsync(asset))!.State);
    }

    [Fact]
    public async Task AnUnrelatedSettingsFileOrARejectedReload_DoesNotMakeAnyFileDue()
    {
        var asset = await SeedUnmatchedAsync(MoviesLibrary, "Movies", due: Now.AddDays(6));
        using var hosted = NewHostedService();

        _loader.RaiseChanged("core.json");
        _loader.RaiseChanged("providers/tmdb.json", applied: false);
        var handled = await hosted.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, handled);
        Assert.Equal(nameof(IdentityJobState.RetailNoMatch), (await _jobs.GetByEntityAsync(asset))!.State);
    }

    [Fact]
    public async Task AProviderChange_NeverMakesAKeptFileDue()
    {
        var asset = await SeedUnmatchedAsync(MoviesLibrary, "Movies", due: Now.AddDays(6));
        await _service.KeepAsIsAsync(asset, CancellationToken.None);
        using var hosted = NewHostedService();

        hosted.NotifyProviderOrPluginChanged();
        var handled = await hosted.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, handled);
    }

    // ── A YouTube file with no ID ─────────────────────────────────────────────

    [Fact]
    public async Task ARetryOfAYoutubeFileWithNoId_ReadsItAgain_AndStaysUnmatchedWhileThereIsStillNoId()
    {
        var asset = await SeedUnmatchedAsync(YoutubeLibrary, "Videos", due: Now.AddMinutes(-1), reason: UnmatchedReasons.NoYoutubeId, withJob: false);

        await _service.RetryDueAsync(CancellationToken.None);

        Assert.Equal([asset], _rereader.Reread);
        Assert.NotNull(await _unmatched.GetAsync(asset));
        Assert.Null(await _jobs.GetByEntityAsync(asset));
    }

    [Fact]
    public async Task ARetryOfAYoutubeFileWhoseInfoJsonNowCarriesAnId_TakesItOutOfUnmatched()
    {
        var asset = await SeedUnmatchedAsync(YoutubeLibrary, "Videos", due: Now.AddMinutes(-1), reason: UnmatchedReasons.NoYoutubeId, withJob: false);
        _rereader.IdFoundOnReread = "dQw4w9WgXcQ";

        await _service.RetryDueAsync(CancellationToken.None);

        Assert.Null(await _unmatched.GetAsync(asset));
        Assert.Null(await _jobs.GetByEntityAsync(asset));
    }

    // ── Counts and scope ──────────────────────────────────────────────────────

    [Fact]
    public async Task TheUnmatchedCount_LeavesKeptFilesOut_AndAnAdministratorSeesEveryLibrary()
    {
        await SeedUnmatchedAsync(MoviesLibrary, "Movies", due: Now);
        var kept = await SeedUnmatchedAsync(MoviesLibrary, "Movies", due: Now);
        await SeedUnmatchedAsync(ShowsLibrary, "TV", due: Now);
        await _service.KeepAsIsAsync(kept, CancellationToken.None);

        var counts = await new UnmatchedCountService(_unmatched).CountsAsync(UnmatchedScope.For(Administrator()), CancellationToken.None);

        Assert.Equal(1, counts[MoviesLibrary]);
        Assert.Equal(1, counts[ShowsLibrary]);
    }

    [Fact]
    public async Task AProfileWithoutAccessToALibrary_GetsNoCountForIt()
    {
        await SeedUnmatchedAsync(MoviesLibrary, "Movies", due: Now);
        await SeedUnmatchedAsync(ShowsLibrary, "TV", due: Now);
        var service = new UnmatchedCountService(_unmatched);

        var ordinary = await service.CountsAsync(UnmatchedScope.For(Member()), CancellationToken.None);
        var signedOut = await service.CountsAsync(UnmatchedScope.For(new RequestAuthority(PrincipalKind.Anonymous, false)), CancellationToken.None);
        var onlyMovies = await service.CountsAsync(UnmatchedScope.Of([MoviesLibrary]), CancellationToken.None);

        Assert.Empty(ordinary);
        Assert.Empty(signedOut);
        Assert.Equal([MoviesLibrary], onlyMovies.Keys.ToArray());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private UnmatchedRetryHostedService NewHostedService() => new(
        _service,
        new ScheduledTaskRunner(new NoRunRecords(), NullLogger<ScheduledTaskRunner>.Instance),
        _loader,
        NullLogger<UnmatchedRetryHostedService>.Instance);

    private static RequestAuthority Administrator() => new(
        PrincipalKind.Human,
        IsAuthenticated: true,
        AccountId: Guid.NewGuid(),
        ActiveProfileId: Guid.NewGuid(),
        AccountEnabled: true,
        GrantEnabled: true,
        AccountIsAdministrator: true,
        GrantAdminEnabled: true);

    private static RequestAuthority Member() => new(
        PrincipalKind.Human,
        IsAuthenticated: true,
        AccountId: Guid.NewGuid(),
        ActiveProfileId: Guid.NewGuid(),
        AccountEnabled: true,
        GrantEnabled: true);

    private static LibraryFolderConfig Library(string id, string name, string kindKey) => new()
    {
        Id = id,
        Name = name,
        Kind = LibraryKinds.Catalogued,
        KindKey = kindKey,
        MetadataPolicy = LibraryMetadataPolicies.Enriched,
        YoutubeLookup = true,
    };

    private async Task<Guid> SeedUnmatchedAsync(
        string libraryId, string mediaType, DateTimeOffset due, string reason = UnmatchedReasons.NoMatch, bool withJob = true)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using (var conn = _database.CreateConnection())
        {
            conn.Execute(
                """
                INSERT INTO works (id, media_type) VALUES (@workId, @mediaType);
                INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, library_id)
                VALUES (@assetId, @editionId, @hash, @path, @libraryId);
                """,
                new { workId, editionId, assetId, mediaType, hash = Guid.NewGuid().ToString("N"), path = $"/media/{assetId:N}.mkv", libraryId });
        }

        if (withJob)
        {
            var job = new IdentityJob { EntityId = assetId, EntityType = "MediaAsset", MediaType = mediaType };
            await _jobs.CreateAsync(job);
            await _jobs.UpdateStateAsync(job.Id, IdentityJobState.RetailNoMatch);
        }

        await _unmatched.MarkUnmatchedAsync(assetId, libraryId, reason, Now.AddDays(-1), due);
        return assetId;
    }

    // ── Spies ─────────────────────────────────────────────────────────────────

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class SpySignal : IIdentityPipelineSignal
    {
        public List<IdentityPipelineSignalKind> Signals { get; } = [];

        public void Signal(IdentityPipelineSignalKind kind) => Signals.Add(kind);

        public Task WaitAsync(IdentityPipelineSignalKind kind, TimeSpan fallbackDelay, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class SpyRereader(ICanonicalValueRepository canonicals) : IAssetRereader
    {
        public List<Guid> Reread { get; } = [];

        /// <summary>When set, the re-read finds this YouTube ID (a new .info.json beside the file).</summary>
        public string? IdFoundOnReread { get; set; }

        public async Task<FileMetadataRereadResult> RereadAsync(Guid assetId, CancellationToken ct)
        {
            Reread.Add(assetId);
            if (IdFoundOnReread is not null)
            {
                await canonicals.UpsertBatchAsync(
                [
                    new CanonicalValue { EntityId = assetId, Key = MetadataFieldConstants.YoutubeVideoId, Value = IdFoundOnReread, LastScoredAt = Now },
                ], ct);
            }

            return new FileMetadataRereadResult(assetId, "Updated", true, false, "Re-read.");
        }
    }

    private sealed class NoRunRecords : IScheduledTaskRunRepository
    {
        public Task<Guid> StartAsync(string taskKey, string? pluginId, DateTimeOffset startedAt, CancellationToken ct = default) => Task.FromResult(Guid.NewGuid());

        public Task FinishAsync(Guid runId, string outcome, DateTimeOffset finishedAt, int? itemsProcessed, string? message, CancellationToken ct = default) => Task.CompletedTask;

        public Task<ScheduledTaskRun?> GetAsync(Guid runId, CancellationToken ct = default) => Task.FromResult<ScheduledTaskRun?>(null);

        public Task<IReadOnlyDictionary<string, ScheduledTaskRun>> GetLatestPerTaskAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, ScheduledTaskRun>>(new Dictionary<string, ScheduledTaskRun>());

        public Task<IReadOnlyList<ScheduledTaskRun>> ListRecentAsync(string taskKey, int limit, int skip = 0, string? pluginId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScheduledTaskRun>>([]);

        public Task<int> MarkRunningAsInterruptedAsync(DateTimeOffset now, CancellationToken ct = default) => Task.FromResult(0);

        public Task PruneAsync(string taskKey, DateTimeOffset now, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class LibrariesOnlyLoader : IConfigurationLoader
    {
        public LibrariesOnlyLoader(params LibraryFolderConfig[] libraries) =>
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
