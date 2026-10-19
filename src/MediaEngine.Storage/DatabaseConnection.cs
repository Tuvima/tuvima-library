using MediaEngine.Storage.Contracts;
using MediaEngine.Storage.Schema;
using MediaEngine.Storage.Schema.Upgrades;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

/// <summary>
/// Facade for the SQLite database lifecycle.
/// Owns the shared startup connection and delegates focused lifecycle work to
/// connection, upgrade-step runner, reference-data top-up, integrity, and maintenance collaborators.
/// </summary>
public sealed class DatabaseConnection : IDatabaseConnection
{
    private readonly string _databasePath;
    private readonly DataStoreStartupOptions _startupOptions;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ReferenceDataTopUp _referenceDataTopUp;
    private readonly DatabaseIntegrityChecker _integrityChecker;
    private readonly SqliteMaintenanceService _maintenanceService;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly List<string> _startupNotes = [];
    private readonly IReadOnlyList<DataStoreUpgradeStep>? _upgradeSteps;
    private readonly Func<string, long>? _freeSpaceLookup;
    private SqliteConnection? _connection;

    /// <param name="databasePath">
    /// Absolute or relative path to the <c>.db</c> file.
    /// Typically sourced from <c>config/core.json</c>.
    /// </param>
    public DatabaseConnection(string databasePath)
        : this(databasePath, new DataStoreStartupOptions())
    {
    }

    /// <param name="databasePath">Absolute or relative path to the <c>.db</c> file.</param>
    /// <param name="options">Host settings such as the backups folder named in upgrade messages.</param>
    public DatabaseConnection(string databasePath, DataStoreStartupOptions options)
        : this(
            databasePath,
            options,
            new SqliteConnectionFactory(databasePath),
            new ReferenceDataTopUp(),
            new DatabaseIntegrityChecker(),
            new SqliteMaintenanceService())
    {
    }

    internal DatabaseConnection(
        string databasePath,
        DataStoreStartupOptions options,
        SqliteConnectionFactory connectionFactory,
        ReferenceDataTopUp referenceDataTopUp,
        DatabaseIntegrityChecker integrityChecker,
        SqliteMaintenanceService maintenanceService,
        IReadOnlyList<DataStoreUpgradeStep>? upgradeSteps = null,
        Func<string, long>? freeSpaceLookup = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(options);
        DapperConfiguration.Configure();
        _databasePath = databasePath;
        _startupOptions = options;
        _connectionFactory = connectionFactory;
        _referenceDataTopUp = referenceDataTopUp;
        _integrityChecker = integrityChecker;
        _maintenanceService = maintenanceService;
        _upgradeSteps = upgradeSteps;
        _freeSpaceLookup = freeSpaceLookup;
    }

    /// <summary>
    /// Lines describing the data store upgrade steps the last <see cref="InitializeSchema"/> applied (and where the
    /// safety copy is, when there is one), for the host to log. Empty when nothing was applied.
    /// </summary>
    public IReadOnlyList<string> StartupNotes => _startupNotes;

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
    /// <exception cref="DataStoreUpgradeException">
    /// Thrown when an upgrade step fails, or the data store is newer than this Engine or no longer matches the recorded steps.
    /// </exception>
    public void InitializeSchema()
    {
        // The epoch guard may need to move an incompatible pre-beta database.
        // Release the shared connection first so Windows does not keep the file locked.
        _connection?.Dispose();
        _connection = null;
        _startupNotes.Clear();
        StorageEpochGuard.EnsureCurrentOrReset(_databasePath);
        var conn = Open();
        _integrityChecker.EnsureWritable(conn, _databasePath);
        if (StorageStructure.HasUserTables(conn))
        {
            // Before any step runs, so a damaged file is reported as damaged rather than as a failed step.
            _integrityChecker.EnsureIntact(conn, _databasePath);
        }

        var hasBackupFolder = !string.IsNullOrWhiteSpace(_startupOptions.BackupDirectory);
        var safetyCopy = hasBackupFolder
            ? new DataStoreSafetyCopy(_databasePath, _startupOptions.BackupDirectory!, freeSpaceLookup: _freeSpaceLookup)
            : null;
        var upgradeOptions = new DataStoreUpgradeOptions
        {
            BackupLocationHint = hasBackupFolder
                ? $"the backups folder '{Path.GetFullPath(_startupOptions.BackupDirectory!)}' "
                  + $"or the safety copies the Engine makes before an upgrade in '{DataStoreSafetyCopy.FolderFor(_startupOptions.BackupDirectory!)}'"
                : "your backups folder",
            BeforeFirstPendingStep = safetyCopy?.AsHook(conn),
        };

        var result = new DataStoreUpgradeRunner().Run(conn, _upgradeSteps ?? DataStoreUpgradeCatalog.ForProduct(), upgradeOptions);
        _startupNotes.AddRange(result.DescribeForLog());
        if (safetyCopy is not null)
        {
            _startupNotes.AddRange(safetyCopy.Warnings);
        }
        else if (result.FromNumber > 0 && result.Applied.Count > 0)
        {
            _startupNotes.Add("No backup folder configured, so no safety copy was made.");
        }
    }

    /// <inheritdoc/>
    public void RunStartupChecks()
    {
        var conn = Open();
        _integrityChecker.EnsureWritable(conn, _databasePath);
        _referenceDataTopUp.Run(conn);
        _integrityChecker.Optimize(conn);
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

    /// <summary>
    /// Closes the idle pooled connections to one data file so it can be replaced or moved
    /// before the Engine opens it. Only that file's pool is cleared: clearing every pool in the
    /// process can dispose a connection another thread is opening at that moment.
    /// </summary>
    public static void ReleasePooledConnections(string databasePath) =>
        SqliteConnectionFactory.ClearPool(databasePath);

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
