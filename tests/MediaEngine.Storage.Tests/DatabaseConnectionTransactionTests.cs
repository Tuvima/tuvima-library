using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// Covers <see cref="DatabaseConnection.ExecuteWriteAsync{T}"/> and its
/// non-generic overload: commit correctness, rollback-on-throw, write-lock
/// release after a failed body, write serialization under concurrent callers,
/// and cancellation before the transaction body ever runs.
/// </summary>
public sealed class DatabaseConnectionTransactionTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;

    public DatabaseConnectionTransactionTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_tx_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        TryDelete(_dbPath);
        TryDelete($"{_dbPath}-wal");
        TryDelete($"{_dbPath}-shm");
    }

    [Fact]
    public async Task ExecuteWriteAsync_CommitsWrittenRowOnSuccess()
    {
        const string path = "C:/library/commit-test.mkv";

        await _db.ExecuteWriteAsync((conn, tx, _) =>
            InsertHashRow(conn, tx, path, "hash-commit"));

        Assert.Equal("hash-commit", ReadHash(path));
    }

    [Fact]
    public async Task ExecuteWriteAsync_RollsBackWhenBodyThrows()
    {
        const string path = "C:/library/rollback-test.mkv";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _db.ExecuteWriteAsync((conn, tx, _) =>
            {
                InsertHashRow(conn, tx, path, "hash-rollback");
                throw new InvalidOperationException("Simulated failure after write.");
            }));

        Assert.Null(ReadHash(path));
    }

    [Fact]
    public async Task ExecuteWriteAsync_ReleasesWriteLockAfterFailureSoNextCallSucceeds()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _db.ExecuteWriteAsync((conn, tx, _) =>
            {
                InsertHashRow(conn, tx, "C:/library/lock-release.mkv", "will-not-persist");
                throw new InvalidOperationException("Simulated failure after write.");
            }));

        // If the semaphore had leaked when the body above threw, this call would
        // block until the timeout token fires and surface OperationCanceledException
        // instead of completing — proving the lock was actually released in `finally`.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _db.ExecuteWriteAsync(
            (conn, tx, _) => InsertHashRow(conn, tx, "C:/library/after-failure.mkv", "hash-after"),
            timeout.Token);

        Assert.Equal("hash-after", ReadHash("C:/library/after-failure.mkv"));
    }

    [Fact]
    public async Task ExecuteWriteAsync_SerializesConcurrentWritersWithoutDatabaseLockedErrors()
    {
        const int writerCount = 8;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writers = Enumerable.Range(0, writerCount)
            .Select(i => Task.Run(async () =>
            {
                await start.Task;
                await _db.ExecuteWriteAsync((conn, tx, _) =>
                    InsertHashRow(conn, tx, $"C:/library/concurrent-{i}.mkv", $"hash-{i}"));
            }))
            .ToArray();
        start.SetResult();

        // Task.WhenAll surfaces the first failure; a "database is locked" SqliteException
        // here would mean write serialization was not actually enforced.
        await Task.WhenAll(writers);

        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM file_hash_cache WHERE absolute_path LIKE 'C:/library/concurrent-%';";
        var count = Convert.ToInt32(cmd.ExecuteScalar());

        Assert.Equal(writerCount, count);
    }

    [Fact]
    public async Task ExecuteWriteAsync_ThrowsBeforeInvokingBodyWhenTokenAlreadyCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var bodyInvoked = false;

        // ThrowsAnyAsync: SemaphoreSlim.WaitAsync surfaces a pre-cancelled token
        // as TaskCanceledException, a subclass of OperationCanceledException.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _db.ExecuteWriteAsync(
                (_, _, _) =>
                {
                    bodyInvoked = true;
                },
                cts.Token));

        Assert.False(bodyInvoked);
    }

    private static void InsertHashRow(SqliteConnection conn, SqliteTransaction tx, string path, string hash)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO file_hash_cache (absolute_path, size_bytes, mtime_utc, sha256, cached_at)
            VALUES ($path, $size, $mtime, $hash, $cachedAt);
            """;
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$size", 1024L);
        cmd.Parameters.AddWithValue("$mtime", now);
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$cachedAt", now);
        cmd.ExecuteNonQuery();
    }

    private string? ReadHash(string path)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sha256 FROM file_hash_cache WHERE absolute_path = $path;";
        cmd.Parameters.AddWithValue("$path", path);
        return cmd.ExecuteScalar() as string;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Test cleanup is best effort; failure should not hide the test assertion result.
        }
    }
}

public sealed class DatabaseConnectionDisposeTests
{
    [Fact]
    public void Dispose_ReleasesPooledHandlesSoDatabaseFileCanBeDeleted()
    {
        DapperConfiguration.Configure();
        var path = Path.Combine(Path.GetTempPath(), $"tuvima_dispose_{Guid.NewGuid():N}.db");
        try
        {
            var db = new DatabaseConnection(path);
            db.InitializeSchema();
            db.RunStartupChecks();
            using (var conn = db.CreateConnection())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT 1";
                cmd.ExecuteScalar();
            }

            db.Dispose();

            File.Delete(path);
            File.Delete($"{path}-wal");
            File.Delete($"{path}-shm");
            Assert.False(File.Exists(path));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists($"{path}-wal")) File.Delete($"{path}-wal");
            if (File.Exists($"{path}-shm")) File.Delete($"{path}-shm");
        }
    }
}
