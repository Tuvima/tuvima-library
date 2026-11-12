using MediaEngine.Api.Services.Display;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Caching.Memory;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The shared work lists are expensive to build (1 to 3 s on a real library) and expired every 30 s, so a reader that arrived just
/// after the expiry waited for a reload. These checks pin the new behaviour: readers that arrive together share one load, and a
/// list that is getting old is served at once while one background reload replaces it.
/// </summary>
public sealed class DisplayProjectionSharedListTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly LoadCounter _counter;
    private readonly ManualTime _time = new();

    public DisplayProjectionSharedListTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_shared_lists_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _counter = new LoadCounter(_db);
    }

    [Fact]
    public async Task VisibleWorkRows_ReadersThatArriveTogetherShareOneLoad()
    {
        var service = CreateService();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.LoadVisibleWorkRowsAsync(CancellationToken.None)));

        Assert.Equal(1, _counter.Opened);
    }

    [Fact]
    public async Task VisibleWorkRows_AFreshListIsServedWithoutAnotherLoad()
    {
        var service = CreateService();
        await service.LoadVisibleWorkRowsAsync(CancellationToken.None);

        _time.Advance(TimeSpan.FromSeconds(5));
        await service.LoadVisibleWorkRowsAsync(CancellationToken.None);

        Assert.Equal(1, _counter.Opened);
    }

    [Fact]
    public async Task VisibleWorkRows_AnAgingListIsServedAtOnceWhileOneBackgroundLoadReplacesIt()
    {
        var service = CreateService();
        var first = await service.LoadVisibleWorkRowsAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(25));

        var second = await service.LoadVisibleWorkRowsAsync(CancellationToken.None);
        var third = await service.LoadVisibleWorkRowsAsync(CancellationToken.None);

        // The old list came back immediately (same instance) and the reload ran, once, behind the readers.
        Assert.Same(first, second);
        Assert.NotNull(third);
        await WaitUntilAsync(() => _counter.Opened >= 2);
        await Task.Delay(100);
        Assert.Equal(2, _counter.Opened);

        // After the reload, readers get the new list.
        var fourth = await service.LoadVisibleWorkRowsAsync(CancellationToken.None);
        Assert.NotSame(first, fourth);
    }

    [Fact]
    public async Task ASharedListThatFailedToLoad_DoesNotBlockTheNextReader()
    {
        var cache = new SharedListCache(new MemoryCache(new MemoryCacheOptions()), null, _time);
        var calls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync<string>(
            "list", () => ++calls == 1 ? throw new InvalidOperationException("first load fails") : Task.FromResult("ok"), CancellationToken.None));
        var next = await cache.GetAsync("list", () => Task.FromResult("ok"), CancellationToken.None);

        Assert.Equal("ok", next);
    }

    [Fact]
    public async Task OneReaderCancelling_LeavesTheSharedLoadRunningForTheOthers()
    {
        var cache = new SharedListCache(new MemoryCache(new MemoryCacheOptions()), null, _time);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();

        var cancelled = cache.GetAsync("list", () => release.Task, cancel.Token);
        var patient = cache.GetAsync("list", () => release.Task, CancellationToken.None);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        release.SetResult("loaded");

        Assert.Equal("loaded", await patient);
    }

    private DisplayProjectionReadService CreateService() =>
        new(
            new DisplayWorkProjectionReader(_counter),
            new DisplayVisibleWorkReader(_counter),
            new DisplayJourneyProjectionReader(_counter),
            new DisplayFavoriteProjectionReader(_counter),
            null!,
            new MemoryCache(new MemoryCacheOptions()),
            logger: null,
            time: _time);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(condition(), "The background reload did not run.");
    }

    public void Dispose()
    {
        _db.Dispose();
        MediaEngine.TestSupport.TestTemp.ReleaseSqlitePool(_dbPath);
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    private sealed class LoadCounter(IDatabaseConnection inner) : IDatabaseConnection
    {
        private int _opened;

        public int Opened => Volatile.Read(ref _opened);

        public Microsoft.Data.Sqlite.SqliteConnection CreateConnection()
        {
            Interlocked.Increment(ref _opened);
            return inner.CreateConnection();
        }

        public Microsoft.Data.Sqlite.SqliteConnection Open() => inner.Open();
        public Task<T> ExecuteReadAsync<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction, CancellationToken, T> body, CancellationToken ct = default) => inner.ExecuteReadAsync(body, ct);
        public void InitializeSchema() => inner.InitializeSchema();
        public void RunStartupChecks() => inner.RunStartupChecks();
        public Task AcquireWriteLockAsync(CancellationToken ct = default, string holderFile = "", string holderMember = "") => inner.AcquireWriteLockAsync(ct, holderFile, holderMember);
        public void ReleaseWriteLock() => inner.ReleaseWriteLock();
        public string? DescribeWriteLockHolder() => inner.DescribeWriteLockHolder();
        public Task<T> ExecuteWriteAsync<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction, CancellationToken, T> body, CancellationToken ct = default, string holderFile = "", string holderMember = "") => inner.ExecuteWriteAsync(body, ct, holderFile, holderMember);
        public Task ExecuteWriteAsync(Action<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction, CancellationToken> body, CancellationToken ct = default, string holderFile = "", string holderMember = "") => inner.ExecuteWriteAsync(body, ct, holderFile, holderMember);
        public void Dispose() { }
    }
}
