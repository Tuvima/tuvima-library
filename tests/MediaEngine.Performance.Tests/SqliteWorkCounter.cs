using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace MediaEngine.Performance.Tests;

/// <summary>
/// Wraps a data store so every connection it hands out counts the work SQLite does for it.
/// SQLite calls a connection's progress handler once for every <see cref="InstructionsPerTick"/> virtual-machine
/// instructions, so the number of calls measures how much a query did. Unlike elapsed time it does not change when other
/// tests, builds or processes compete for the machine, which is what a "does this query stay cheap" check needs.
/// </summary>
internal sealed class SqliteWorkCounter : IDatabaseConnection
{
    public const int InstructionsPerTick = 1_000;

    private readonly IDatabaseConnection _inner;
    private long _ticks;

    public SqliteWorkCounter(IDatabaseConnection inner) => _inner = inner;

    /// <summary>Work done so far, in thousands of SQLite virtual-machine instructions.</summary>
    public long KiloInstructions => Interlocked.Read(ref _ticks);

    public void Reset() => Interlocked.Exchange(ref _ticks, 0);

    public SqliteConnection CreateConnection()
    {
        var connection = _inner.CreateConnection();
        raw.sqlite3_progress_handler(connection.Handle, InstructionsPerTick, Tick, null);
        return connection;
    }

    private int Tick(object? state)
    {
        Interlocked.Increment(ref _ticks);
        return 0;
    }

    public SqliteConnection Open() => _inner.Open();

    public Task<T> ExecuteReadAsync<T>(Func<SqliteConnection, SqliteTransaction, CancellationToken, T> body, CancellationToken ct = default) =>
        _inner.ExecuteReadAsync(body, ct);

    public void InitializeSchema() => _inner.InitializeSchema();

    public void RunStartupChecks() => _inner.RunStartupChecks();

    public Task AcquireWriteLockAsync(CancellationToken ct = default, string holderFile = "", string holderMember = "") => _inner.AcquireWriteLockAsync(ct, holderFile, holderMember);

    public void ReleaseWriteLock() => _inner.ReleaseWriteLock();
    public string? DescribeWriteLockHolder() => _inner.DescribeWriteLockHolder();

    public Task<T> ExecuteWriteAsync<T>(Func<SqliteConnection, SqliteTransaction, CancellationToken, T> body, CancellationToken ct = default, string holderFile = "", string holderMember = "") =>
        _inner.ExecuteWriteAsync(body, ct, holderFile, holderMember);

    public Task ExecuteWriteAsync(Action<SqliteConnection, SqliteTransaction, CancellationToken> body, CancellationToken ct = default, string holderFile = "", string holderMember = "") =>
        _inner.ExecuteWriteAsync(body, ct, holderFile, holderMember);

    // The fixture owns the wrapped data store.
    public void Dispose()
    {
    }
}
