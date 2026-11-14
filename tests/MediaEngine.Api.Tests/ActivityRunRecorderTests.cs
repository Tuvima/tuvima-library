using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

public sealed class ActivityRunRecorderTests : IDisposable
{
    private const string Library = "11111111-1111-1111-1111-111111111111";
    private const string VaultLibrary = "99999999-9999-9999-9999-999999999999";
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly ActivityRunRepository _real;
    private readonly KnownAssetStore _store;
    private readonly ClockStub _clock = new(new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero));
    private readonly ActivityRunRecorder _recorder;

    public ActivityRunRecorderTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_run_recorder_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _real = new ActivityRunRepository(_db);
        _store = new KnownAssetStore(_real);
        _recorder = new ActivityRunRecorder(_store, new VaultStub([VaultLibrary]), _clock, NullLogger<ActivityRunRecorder>.Instance);
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
    public async Task RecordAdded_FourteenFilesOneAfterAnother_JoinOneRunThatClosesAfterFiveQuietMinutes()
    {
        for (var index = 0; index < 14; index++)
        {
            await AddMovieAsync($"Movie {index}");
            _clock.Advance(TimeSpan.FromSeconds(20));
        }

        var open = Assert.Single(await _real.ListRunsAsync(new ActivityRunQuery()));
        Assert.Equal(14, open.Run.ItemCount);
        Assert.Equal(ActivityRunStatuses.Active, open.Run.Status);

        _clock.Advance(TimeSpan.FromMinutes(6));
        await _recorder.CloseQuietRunsAsync(ActivityRunRecorder.QuietWindow);

        var done = Assert.Single(await _real.ListRunsAsync(new ActivityRunQuery()));
        Assert.Equal(ActivityRunStatuses.Completed, done.Run.Status);
        Assert.NotNull(done.Run.EndedAt);
    }

    [Fact]
    public async Task RecordAdded_AfterFiveQuietMinutes_OpensASecondRunThatIsNotTheFirst()
    {
        await AddMovieAsync("Original");
        _clock.Advance(TimeSpan.FromMinutes(6));
        await AddMovieAsync("Later");

        var runs = (await _real.ListRunsAsync(new ActivityRunQuery(NewestFirst: false))).Select(entry => entry.Run).ToList();
        Assert.Equal(2, runs.Count);
        Assert.True(runs[0].IsFirstRun);
        Assert.False(runs[1].IsFirstRun);
    }

    [Fact]
    public async Task RecordAdded_ASecondWaveOfTheInitialImport_JoinsTheFirstRun_WhileTheQueueHasNotGoneQuiet()
    {
        var queue = new QueueStub(_clock.GetUtcNow().AddMinutes(-1));
        var recorder = RecorderWith(queue);
        await AddMovieAsync("Wave one", recorder);
        _clock.Advance(TimeSpan.FromMinutes(6)); // longer than the quiet window, but the Engine is still busy (D30)
        await AddMovieAsync("Wave two", recorder);

        var only = Assert.Single(await _real.ListRunsAsync(new ActivityRunQuery())).Run;
        Assert.True(only.IsFirstRun);
        Assert.Equal(2, only.ItemCount);

        // The queue goes quiet and starts a new period: the next import is a later one and can be New.
        queue.State = "idle";
        _clock.Advance(TimeSpan.FromMinutes(1));
        await recorder.CloseQuietRunsAsync(ActivityRunRecorder.QuietWindow);
        _clock.Advance(TimeSpan.FromMinutes(10));
        await recorder.CloseQuietRunsAsync(ActivityRunRecorder.QuietWindow);
        queue.PeriodStart = _clock.GetUtcNow();
        queue.State = "working";
        await AddMovieAsync("Much later", recorder);

        var runs = (await _real.ListRunsAsync(new ActivityRunQuery(NewestFirst: false))).Select(entry => entry.Run).ToList();
        Assert.Equal(2, runs.Count);
        Assert.False(runs[1].IsFirstRun);
    }

    [Fact]
    public async Task RecordAdded_TwoLibrariesInOneScan_ShareOneHistoryEntry()
    {
        const string other = "22222222-2222-2222-2222-222222222222";
        await AddMovieAsync("Movie in A");
        var asset = Guid.NewGuid();
        _store.Known[asset] = new ActivityAssetContext(other, "/b/book.epub");
        await _recorder.RecordAddedAsync(Guid.NewGuid(), asset, "Books", "A Book", null, null);

        var runs = (await _real.ListRunsAsync(new ActivityRunQuery())).Select(entry => entry.Run).ToList();
        Assert.Equal(2, runs.Count);
        Assert.Single(runs.Select(run => run.EntryId).Distinct());
        Assert.Equal(2, (await _real.ListGroupsAsync(runs[0].EntryId, null, 0, 10)).Sum(group => group.ItemCount));
    }

    [Fact]
    public async Task RecordAdded_ForALibraryInTheVault_RecordsNothing()
    {
        var asset = Guid.NewGuid();
        _store.Known[asset] = new ActivityAssetContext(VaultLibrary, "/vault/secret.mkv");

        await _recorder.RecordAddedAsync(Guid.NewGuid(), asset, "Movies", "Secret", null, null);

        Assert.Empty(await _real.ListRunsAsync(new ActivityRunQuery()));
    }

    [Fact]
    public async Task RecordAdded_ForAFileWithNoLibrary_RecordsNothing()
    {
        var asset = Guid.NewGuid();
        _store.Known[asset] = new ActivityAssetContext(null, "/loose/file.mkv");

        await _recorder.RecordAddedAsync(Guid.NewGuid(), asset, "Movies", "Loose", null, null);

        Assert.Empty(await _real.ListRunsAsync(new ActivityRunQuery()));
    }

    [Fact]
    public async Task RecordRemoved_ThreeFiles_FormOneRemovedRun()
    {
        for (var index = 0; index < 3; index++)
        {
            await _recorder.RecordRemovedAsync(Guid.NewGuid(), Guid.Parse(Library), $"/media/file{index}.mkv");
        }

        var only = Assert.Single(await _real.ListRunsAsync(new ActivityRunQuery()));
        Assert.Equal(ActivityRunKinds.Removed, only.Run.Kind);
        Assert.Equal(3, only.Run.ItemCount);
        Assert.False(only.Run.IsFirstRun);
    }

    [Fact]
    public async Task RecordAdded_Episodes_StackIntoOneTilePerShow()
    {
        var show = Guid.NewGuid();
        for (var index = 0; index < 4; index++)
        {
            var asset = Guid.NewGuid();
            _store.Known[asset] = new ActivityAssetContext(Library, $"/tv/show/e{index}.mkv");
            await _recorder.RecordAddedAsync(Guid.NewGuid(), asset, "TV", $"Episode {index}", "The Expanse", show);
        }

        var run = Assert.Single(await _real.ListRunsAsync(new ActivityRunQuery())).Run;
        var group = Assert.Single(await _real.ListGroupsAsync(run.EntryId, null, 0, 10));
        Assert.Equal(4, group.ItemCount);
        Assert.Equal("The Expanse", group.GroupTitle);
    }

    [Fact]
    public void Stack_OtherVideosShareOneTilePerRootFolder()
    {
        var first = ActivityRunRecorder.Stack("Videos", Guid.NewGuid(), null, "Lesson 1", null, "/learn/course/lesson1.mp4");
        var second = ActivityRunRecorder.Stack("Videos", Guid.NewGuid(), null, "Lesson 2", null, "/learn/course/lesson2.mp4");

        Assert.Equal(first.Key, second.Key);
        Assert.Equal("course", first.Title);
    }

    [Fact]
    public async Task RecordAdded_TwoTitlesTwelveMinutesApartWhileTheEngineIsBusy_JoinOneBatchThatStaysOpen()
    {
        var queue = new QueueStub(_clock.GetUtcNow()) { State = "working" };
        var recorder = RecorderWith(queue);
        await AddMovieAsync("Big video one", recorder);

        for (var minute = 0; minute < 12; minute++)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(0, await recorder.CloseQuietRunsAsync(ActivityRunRecorder.QuietWindow)); // the sweeper's minute tick
        }

        await AddMovieAsync("Big video two", recorder);

        var only = Assert.Single(await _real.ListRunsAsync(new ActivityRunQuery())).Run;
        Assert.Equal(2, only.ItemCount);
        Assert.Equal(ActivityRunStatuses.Active, only.Status);
    }

    [Fact]
    public async Task CloseQuietRuns_TheFiveQuietMinutesOnlyCountFromWhenTheEngineGoesIdle()
    {
        var queue = new QueueStub(_clock.GetUtcNow()) { State = "working" };
        var recorder = RecorderWith(queue);
        await AddMovieAsync("Only title", recorder);
        _clock.Advance(TimeSpan.FromMinutes(20));
        await recorder.CloseQuietRunsAsync(ActivityRunRecorder.QuietWindow); // still busy: nothing closes

        queue.State = "idle";
        _clock.Advance(TimeSpan.FromMinutes(1));
        await recorder.CloseQuietRunsAsync(ActivityRunRecorder.QuietWindow); // first idle look
        _clock.Advance(TimeSpan.FromMinutes(4));
        await recorder.CloseQuietRunsAsync(ActivityRunRecorder.QuietWindow); // four idle minutes: still open
        Assert.Equal(ActivityRunStatuses.Active, Assert.Single(await _real.ListRunsAsync(new ActivityRunQuery())).Run.Status);

        _clock.Advance(TimeSpan.FromMinutes(2));
        await recorder.CloseQuietRunsAsync(ActivityRunRecorder.QuietWindow);

        var done = Assert.Single(await _real.ListRunsAsync(new ActivityRunQuery())).Run;
        Assert.Equal(ActivityRunStatuses.Completed, done.Status);
    }

    [Fact]
    public async Task RecordAdded_AFileAddedAfterFiveIdleMinutes_OpensASecondBatch()
    {
        var queue = new QueueStub(_clock.GetUtcNow()) { State = "working" };
        var recorder = RecorderWith(queue);
        await AddMovieAsync("Import", recorder);
        _clock.Advance(TimeSpan.FromMinutes(3));
        queue.State = "idle";
        await recorder.CloseQuietRunsAsync(ActivityRunRecorder.QuietWindow); // the Engine is seen idle
        _clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(30));
        await recorder.CloseQuietRunsAsync(ActivityRunRecorder.QuietWindow);

        await AddMovieAsync("Dropped in later", recorder);

        var runs = (await _real.ListRunsAsync(new ActivityRunQuery(NewestFirst: false))).Select(entry => entry.Run).ToList();
        Assert.Equal(2, runs.Count);
        Assert.Equal(ActivityRunStatuses.Completed, runs[0].Status);
        Assert.Equal(1, runs[0].ItemCount);
    }

    [Fact]
    public async Task RecordAdded_AfterTheEngineWasStoppedMidImport_ContinuesTheBatchThatWasCutOff()
    {
        var first = RecorderWith(new QueueStub(_clock.GetUtcNow()) { State = "working" });
        await AddMovieAsync("Before the stop", first);

        // The Engine stops, is down for a minute, and starts again; the new sweeper closes what the last session left open.
        _clock.Advance(TimeSpan.FromMinutes(1));
        var queue = new QueueStub(_clock.GetUtcNow()) { State = "idle" };
        var second = RecorderWith(queue);
        Assert.Equal(1, await second.CloseLeftOpenRunsAsync());
        Assert.Equal(ActivityRunStatuses.Completed, Assert.Single(await _real.ListRunsAsync(new ActivityRunQuery())).Run.Status);

        // The same work is picked up again.
        _clock.Advance(TimeSpan.FromSeconds(20));
        queue.State = "working";
        await AddMovieAsync("After the restart", second);

        var only = Assert.Single(await _real.ListRunsAsync(new ActivityRunQuery())).Run;
        Assert.Equal(2, only.ItemCount);
        Assert.Equal(ActivityRunStatuses.Active, only.Status);
        Assert.Null(only.EndedAt);
    }

    [Fact]
    public async Task RecordAdded_AFileDroppedInLongAfterARestart_OpensANewBatchInsteadOfReopeningTheOldOne()
    {
        var first = RecorderWith(new QueueStub(_clock.GetUtcNow()) { State = "working" });
        await AddMovieAsync("Before the stop", first);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var queue = new QueueStub(_clock.GetUtcNow()) { State = "idle" };
        var second = RecorderWith(queue);
        await second.CloseLeftOpenRunsAsync();

        _clock.Advance(TimeSpan.FromMinutes(6)); // the Engine stayed idle after the restart
        await second.CloseQuietRunsAsync(ActivityRunRecorder.QuietWindow);
        await AddMovieAsync("A new drop", second);

        var runs = (await _real.ListRunsAsync(new ActivityRunQuery(NewestFirst: false))).Select(entry => entry.Run).ToList();
        Assert.Equal(2, runs.Count);
        Assert.Equal(1, runs[0].ItemCount);
        Assert.Equal(ActivityRunStatuses.Completed, runs[0].Status);
    }

    private ActivityRunRecorder RecorderWith(QueueStub queue) => new(
        _store, new VaultStub([VaultLibrary]), _clock, NullLogger<ActivityRunRecorder>.Instance,
        new ServiceCollection().AddSingleton<MediaEngine.Api.Services.IIngestionQueueProgressService>(queue).BuildServiceProvider());

    private async Task<Guid> AddMovieAsync(string title, ActivityRunRecorder? recorder = null)
    {
        var work = Guid.NewGuid();
        var asset = Guid.NewGuid();
        _store.Known[asset] = new ActivityAssetContext(Library, $"/movies/{title}.mkv");
        await (recorder ?? _recorder).RecordAddedAsync(work, asset, "Movies", title, null, null);
        return work;
    }

    [Fact]
    public async Task NewTag_ItemsFromALibrarysFirstImport_AreNeverNew()
    {
        var first = await AddMovieAsync("First");

        var page = await new NewTagRule(_real, _clock).MarkAsync(GridOf(first), CancellationToken.None);

        Assert.False(Assert.Single(page.Items).IsNew);
    }

    [Fact]
    public async Task NewTag_AnItemFromALaterImport_IsNewForFourteenDaysAndThenNot()
    {
        await AddMovieAsync("First");
        _clock.Advance(TimeSpan.FromMinutes(10));
        var later = await AddMovieAsync("Later");
        _clock.Advance(TimeSpan.FromMinutes(10));
        await _recorder.CloseQuietRunsAsync(TimeSpan.Zero);
        var rule = new NewTagRule(_real, _clock);

        _clock.Advance(TimeSpan.FromDays(13));
        Assert.True(Assert.Single((await rule.MarkAsync(GridOf(later), CancellationToken.None)).Items).IsNew);

        _clock.Advance(TimeSpan.FromDays(2));
        Assert.False(Assert.Single((await rule.MarkAsync(GridOf(later), CancellationToken.None)).Items).IsNew);
    }

    [Fact]
    public async Task NewTag_ACardNoImportRecorded_IsNotNew()
    {
        var page = await new NewTagRule(_real, _clock).MarkAsync(GridOf(Guid.NewGuid()), CancellationToken.None);

        Assert.False(Assert.Single(page.Items).IsNew);
    }

    private static DisplayGridPageDto GridOf(Guid workId) => new(
        "watch", "recent", "Recent", null, [CardFor(workId)], 1, 0, 10, false, null, "added", "added", null, [], []);

    private static DisplayCardDto CardFor(Guid workId) => new(
        Id: Guid.NewGuid(), WorkId: workId, AssetId: null, CollectionId: null, MediaType: "Movies", GroupingType: "work",
        Title: "Card", Subtitle: null, Facts: [],
        Artwork: new DisplayArtworkDto(null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null,
            null, null, null, null, null, null, null, null, null),
        PreferredShape: "portrait", Presentation: "default", TileTextMode: "coverOnly", PreviewPlacement: "bottom",
        Progress: null, Actions: [], Flags: new DisplayCardFlagsDto(true, false, false, false), SortTimestamp: DateTimeOffset.UtcNow);

    private sealed class ClockStub(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class QueueStub(DateTimeOffset periodStart) : MediaEngine.Api.Services.IIngestionQueueProgressService
    {
        public DateTimeOffset PeriodStart { get; set; } = periodStart;
        public string State { get; set; } = "working";

        public MediaEngine.Contracts.Ingestion.IngestionQueueProgressDto Current
            => new() { State = State, StartedAt = PeriodStart };
    }

    private sealed class VaultStub(IEnumerable<string> ids) : IVaultLibraryRepository
    {
        public Task<IReadOnlySet<string>> GetIdsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<string>>(ids.ToHashSet(StringComparer.OrdinalIgnoreCase));

        public Task<IReadOnlyList<VaultLibrary>> GetAllAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<VaultLibrary?> GetAsync(string libraryId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> AnyAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> IsInVaultAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> MayPersonBeVaultOnlyAsync(Guid personId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> HoldsOrdinaryFilesAsync(string folderPath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AddAsync(VaultLibrary library, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> UpdateLookupAsync(string libraryId, string lookupSource, string? lookupServerId, DateTimeOffset now, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RemoveAsync(string libraryId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RemoveAsync(string libraryId, IReadOnlyCollection<string> sourcePaths, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>The real run storage, with the asset-to-library lookup answered from a table the test fills in (no media files needed).</summary>
    private sealed class KnownAssetStore(ActivityRunRepository inner) : IActivityRunStore
    {
        public Dictionary<Guid, ActivityAssetContext> Known { get; } = [];

        public Task<ActivityAssetContext?> GetAssetContextAsync(Guid assetId, CancellationToken ct = default)
            => Task.FromResult(Known.GetValueOrDefault(assetId));

        public Task<ActivityRunRecord?> FindOpenRunAsync(string libraryId, string kind, DateTimeOffset notBefore, CancellationToken ct = default) => inner.FindOpenRunAsync(libraryId, kind, notBefore, ct);
        public Task<bool> LibraryHasRunsAsync(string libraryId, CancellationToken ct = default) => inner.LibraryHasRunsAsync(libraryId, ct);
        public Task<Guid?> FindOpenScanIdAsync(string kind, DateTimeOffset notBefore, CancellationToken ct = default) => inner.FindOpenScanIdAsync(kind, notBefore, ct);
        public Task<DateTimeOffset?> GetInitialImportStartAsync(string libraryId, CancellationToken ct = default) => inner.GetInitialImportStartAsync(libraryId, ct);
        public Task InsertRunAsync(ActivityRunRecord run, CancellationToken ct = default) => inner.InsertRunAsync(run, ct);
        public Task AddItemAsync(ActivityRunItemRecord item, DateTimeOffset at, CancellationToken ct = default) => inner.AddItemAsync(item, at, ct);
        public Task<int> CompleteQuietRunsAsync(DateTimeOffset quietBefore, CancellationToken ct = default) => inner.CompleteQuietRunsAsync(quietBefore, ct);
        public Task<int> CompleteInterruptedRunsAsync(CancellationToken ct = default) => inner.CompleteInterruptedRunsAsync(ct);
        public Task<int> ReopenInterruptedRunsAsync(string kind, CancellationToken ct = default) => inner.ReopenInterruptedRunsAsync(kind, ct);
        public Task<int> ClearInterruptedAsync(CancellationToken ct = default) => inner.ClearInterruptedAsync(ct);
        public Task<IReadOnlyList<ActivityRunSummary>> ListRunsAsync(ActivityRunQuery query, CancellationToken ct = default) => inner.ListRunsAsync(query, ct);
        public Task<ActivityRunRecord?> GetRunAsync(Guid runId, CancellationToken ct = default) => inner.GetRunAsync(runId, ct);
        public Task<IReadOnlyList<ActivityRunGroup>> ListGroupsAsync(Guid runId, ActivityRunScope? scope, int offset, int limit, CancellationToken ct = default) => inner.ListGroupsAsync(runId, scope, offset, limit, ct);
        public Task<IReadOnlyList<ActivityTitleHit>> SearchTitlesAsync(ActivityRunQuery query, CancellationToken ct = default) => inner.SearchTitlesAsync(query, ct);
        public Task<DateTimeOffset?> GetLastCompletedAsync(IReadOnlyCollection<string>? libraryIds, CancellationToken ct = default) => inner.GetLastCompletedAsync(libraryIds, ct);
        public Task<int> PurgeAsync(DateTimeOffset olderThan, CancellationToken ct = default) => inner.PurgeAsync(olderThan, ct);
        public Task<IReadOnlySet<Guid>> GetNewWorkIdsAsync(IReadOnlyCollection<Guid> workIds, DateTimeOffset newSince, CancellationToken ct = default) => inner.GetNewWorkIdsAsync(workIds, newSince, ct);
        public Task<IReadOnlySet<Guid>> ListNewWorkIdsAsync(DateTimeOffset newSince, int limit, CancellationToken ct = default) => inner.ListNewWorkIdsAsync(newSince, limit, ct);
    }
}
