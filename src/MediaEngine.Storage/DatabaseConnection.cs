using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

/// <summary>
/// Facade for the SQLite database lifecycle.
/// Owns the shared startup connection and delegates focused lifecycle work to
/// connection, schema, current-startup-task, integrity, and maintenance collaborators.
/// </summary>
public sealed class DatabaseConnection : IDatabaseConnection
{
    private readonly string _databasePath;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SchemaInitializer _schemaInitializer;
    private readonly SchemaMigrator _schemaMigrator;
    private readonly DatabaseIntegrityChecker _integrityChecker;
    private readonly SqliteMaintenanceService _maintenanceService;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private SqliteConnection? _connection;

    /// <param name="databasePath">
    /// Absolute or relative path to the <c>.db</c> file.
    /// Typically sourced from <c>config/core.json</c>.
    /// </param>
    public DatabaseConnection(string databasePath)
        : this(
            databasePath,
            new SqliteConnectionFactory(databasePath),
            new SchemaInitializer(),
            new SchemaMigrator(),
            new DatabaseIntegrityChecker(),
            new SqliteMaintenanceService())
    {
    }

    internal DatabaseConnection(
        string databasePath,
        SqliteConnectionFactory connectionFactory,
        SchemaInitializer schemaInitializer,
        SchemaMigrator schemaMigrator,
        DatabaseIntegrityChecker integrityChecker,
        SqliteMaintenanceService maintenanceService)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DapperConfiguration.Configure();
        _databasePath = databasePath;
        _connectionFactory = connectionFactory;
        _schemaInitializer = schemaInitializer;
        _schemaMigrator = schemaMigrator;
        _integrityChecker = integrityChecker;
        _maintenanceService = maintenanceService;
    }

    /// <summary>
    /// Lines describing data a startup migration changed (such as profiles moved between accounts), for the host to log.
    /// </summary>
    public IReadOnlyList<string> StartupNotes => _schemaMigrator.Notes;

    /// <inheritdoc/>
    public SqliteConnection Open()
    {
        if (_connection is not null)
        {
            return _connection;
        }

        _connection = _connectionFactory.OpenSharedConnection();
        return _connection;
    }

    /// <inheritdoc/>
    public SqliteConnection CreateConnection()
        => _connectionFactory.CreateOperationConnection();

    /// <inheritdoc/>
    public void InitializeSchema()
    {
        // The epoch guard may need to move an incompatible pre-beta database.
        // Release the shared connection first so Windows does not keep the file locked.
        _connection?.Dispose();
        _connection = null;
        StorageEpochGuard.EnsureCurrentOrReset(_databasePath);
        var conn = Open();
        _integrityChecker.EnsureWritable(conn, _databasePath);
        _schemaInitializer.Initialize(conn);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <c>PRAGMA integrity_check</c> returns anything other than "ok".
    /// </exception>
    public void RunStartupChecks()
    {
        var conn = Open();
        _integrityChecker.RunStartupChecks(conn, _databasePath);
        _schemaMigrator.RunStartupTasks(conn);
    }

    /// <summary>
    /// Executes a VACUUM to reclaim unused pages.
    /// Spec: "SHOULD perform a VACUUM during low-activity maintenance windows."
    /// Call when <c>MaintenanceSettings.VacuumOnStartup</c> is <c>true</c>.
    /// </summary>
    public void Vacuum()
        => _maintenanceService.Vacuum(Open());

    /// <inheritdoc/>
    public Task AcquireWriteLockAsync(CancellationToken ct = default)
        => _writeLock.WaitAsync(ct);

    /// <inheritdoc/>
    public void ReleaseWriteLock()
        => _writeLock.Release();

    internal static void ExecuteStartupTransaction(SqliteConnection conn, Action<SqliteTransaction> body)
    {
        using var transaction = conn.BeginTransaction();
        body(transaction);
        transaction.Commit();
    }

    /// <inheritdoc/>
    public Task<T> ExecuteReadAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, T> body,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        ct.ThrowIfCancellationRequested();
        using var conn = CreateConnection();
        using var transaction = conn.BeginTransaction();
        var result = body(conn, transaction, ct);
        ct.ThrowIfCancellationRequested();
        transaction.Commit();
        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public async Task<T> ExecuteWriteAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, T> body,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        await AcquireWriteLockAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();
            var result = body(conn, tx, ct);
            ct.ThrowIfCancellationRequested();
            tx.Commit();
            return result;
        }
        finally
        {
            ReleaseWriteLock();
        }
    }

    /// <inheritdoc/>
    public Task ExecuteWriteAsync(
        Action<SqliteConnection, SqliteTransaction, CancellationToken> body,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return ExecuteWriteAsync<object?>(
            (conn, tx, token) =>
            {
                body(conn, tx, token);
                return null;
            },
            ct);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
        // Release pooled handles so the data file can be moved or deleted after shutdown.
        _connectionFactory.ClearPool();
        _writeLock.Dispose();
    }
}
