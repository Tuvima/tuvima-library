using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Contracts;

/// <summary>
/// Manages the lifecycle of the SQLite connection and WAL-mode settings.
/// Spec: Phase 4 – Interfaces § IDatabaseConnection
/// </summary>
public interface IDatabaseConnection : IDisposable
{
    /// <summary>
    /// Opens (or returns the already-open) shared connection used only for
    /// schema initialization and startup checks.  Do NOT use for normal
    /// repository operations — use <see cref="CreateConnection"/> instead.
    /// </summary>
    SqliteConnection Open();

    /// <summary>
    /// Returns a new pooled connection configured with WAL mode, foreign keys,
    /// and a busy timeout.  Callers MUST dispose the returned connection
    /// (use <c>using var conn = _db.CreateConnection();</c>).
    /// Each thread gets its own connection, eliminating internal command-list
    /// corruption when multiple threads operate concurrently.
    /// </summary>
    SqliteConnection CreateConnection();

    /// <summary>
    /// Runs synchronous SQLite reads inside one consistent snapshot. The
    /// connection and transaction are owned by the helper; the callback must
    /// not retain either after it returns.
    /// </summary>
    Task<T> ExecuteReadAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, T> body,
        CancellationToken ct = default);

    /// <summary>
    /// Brings the data store to the latest structure: checks the storage epoch and that the file is writable,
    /// runs <c>PRAGMA integrity_check</c> on an existing data store, then applies any pending numbered upgrade
    /// steps (each exactly once, in its own transaction). A brand-new data store gets the baseline step.
    /// Safe to call on every startup; a data store that is already current is left untouched.
    /// Throws <see cref="InvalidOperationException"/> if integrity_check does not return "ok", and refuses to start
    /// when an upgrade step fails, the data store is newer than this Engine, or an applied step was edited.
    /// </summary>
    void InitializeSchema();

    /// <summary>
    /// Startup housekeeping after <see cref="InitializeSchema"/>: checks the file is writable, adds any missing
    /// built-in reference rows (metadata providers, setup steps, the default Owner profile), and runs PRAGMA optimize.
    /// Spec: "SHOULD execute on application startup."
    /// </summary>
    void RunStartupChecks();

    /// <summary>
    /// Acquires the global write-serialization lock.
    /// This exists primarily for <see cref="ExecuteWriteAsync{T}"/> to use
    /// internally, plus rare advanced scenarios that cannot express their work as a
    /// single <c>body</c> callback. Calling this directly alongside a raw
    /// <c>BeginTransaction()</c> is deprecated — use
    /// <see cref="ExecuteWriteAsync{T}"/> or
    /// <see cref="ExecuteWriteAsync(Action{SqliteConnection, SqliteTransaction, CancellationToken}, CancellationToken)"/>
    /// instead so the write-lock contract is enforced structurally.
    /// </summary>
    Task AcquireWriteLockAsync(CancellationToken ct = default);

    /// <summary>
    /// Releases the global write-serialization lock.
    /// This exists primarily for <see cref="ExecuteWriteAsync{T}"/> to use
    /// internally, plus rare advanced scenarios that cannot express their work as a
    /// single <c>body</c> callback. Must be called in a <c>finally</c> block after
    /// the transaction completes. Calling this directly alongside a raw
    /// <c>BeginTransaction()</c> is deprecated — use
    /// <see cref="ExecuteWriteAsync{T}"/> or
    /// <see cref="ExecuteWriteAsync(Action{SqliteConnection, SqliteTransaction, CancellationToken}, CancellationToken)"/>
    /// instead so the write-lock contract is enforced structurally.
    /// </summary>
    void ReleaseWriteLock();

    /// <summary>
    /// Runs synchronous SQLite work inside the global write lock and a dedicated
    /// transaction. Microsoft.Data.Sqlite does not provide asynchronous I/O, so
    /// the callback is deliberately synchronous; only waiting for the write lock
    /// is asynchronous.
    /// </summary>
    Task<T> ExecuteWriteAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, T> body,
        CancellationToken ct = default);

    /// <summary>
    /// Non-generic overload of <see cref="ExecuteWriteAsync{T}"/>.
    /// </summary>
    Task ExecuteWriteAsync(
        Action<SqliteConnection, SqliteTransaction, CancellationToken> body,
        CancellationToken ct = default);

}
