using System.Reflection;
using MediaEngine.Api.DevSupport;
using MediaEngine.Api.Services;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

public sealed partial class DevHarnessResetServiceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima_harness_reset_{Guid.NewGuid():N}.db");
    private readonly string _configRoot = Path.Combine(Path.GetTempPath(), $"tuvima_harness_reset_config_{Guid.NewGuid():N}");
    private readonly ConfigurationDirectoryLoader _configuration;

    public DevHarnessResetServiceTests()
    {
        Directory.CreateDirectory(_configRoot);
        _configuration = new ConfigurationDirectoryLoader(_configRoot);
    }

    public void Dispose()
    {
        _configuration.Dispose();
        if (Directory.Exists(_configRoot))
        {
            Directory.Delete(_configRoot, recursive: true);
        }

        TestTemp.ReleaseSqlitePool(_path);
        foreach (var file in new[] { _path, $"{_path}-wal", $"{_path}-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public async Task DevHarnessDatabaseReset_ReappliesTheBaseline()
    {
        using var database = StartedDatabase();
        var service = NewService(database);

        InvokeReset(service, "ResetDatabase");

        using (var conn = database.CreateConnection())
        {
            Assert.Equal(1L, Scalar(conn, "SELECT COUNT(*) FROM data_store_upgrades WHERE number = 1 AND name = 'baseline';"));
            Assert.Equal(1L, Scalar(conn, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'view' AND name = 'work_owned_assets';"));
            Assert.Equal(1L, Scalar(conn, "SELECT COUNT(*) FROM applications WHERE id = X'00000000000000000000000000000004';"));
            Assert.Equal(17L, Scalar(conn, "SELECT COUNT(*) FROM metadata_providers;"));
        }

        // The next normal start finds a current data store and applies nothing.
        database.InitializeSchema();
        database.RunStartupChecks();
        Assert.Empty(database.StartupNotes);
    }

    [Fact]
    public async Task LibraryDataReset_ClearsFullTextIndexesWithoutCorruptingThem()
    {
        using var database = StartedDatabase();
        using (var conn = database.CreateConnection())
        {
            Execute(conn, """
                INSERT INTO artwork_asset_search (artwork_asset_id, search_text) VALUES ('art-1', 'dune desert poster');
                INSERT INTO artwork_asset_search (artwork_asset_id, search_text) VALUES ('art-2', 'arrakis spice cover');
                INSERT INTO search_index (entity_id, title) VALUES ('work-1', 'Dune');
                INSERT INTO local_item_search (title, file_name) VALUES ('Beach sunset', 'IMG_0001.jpg');
                """);
        }

        InvokeReset(NewService(database), "ResetLibraryDatabase");

        using (var conn = database.CreateConnection())
        {
            using (var integrityCmd = conn.CreateCommand())
            {
                integrityCmd.CommandText = "PRAGMA integrity_check;";
                Assert.Equal("ok", integrityCmd.ExecuteScalar() as string);
            }

            Assert.Equal(0L, Scalar(conn, "SELECT COUNT(*) FROM artwork_asset_search;"));
            Assert.Equal(0L, Scalar(conn, "SELECT COUNT(*) FROM artwork_asset_search WHERE artwork_asset_search MATCH 'desert';"));
            Assert.Equal(0L, Scalar(conn, "SELECT COUNT(*) FROM search_index;"));

            // The indexes still accept and find new rows after the reset.
            Execute(conn, "INSERT INTO artwork_asset_search (artwork_asset_id, search_text) VALUES ('art-3', 'fresh backdrop');");
            Assert.Equal(1L, Scalar(conn, "SELECT COUNT(*) FROM artwork_asset_search WHERE artwork_asset_search MATCH 'backdrop';"));
            Execute(conn, "INSERT INTO artwork_asset_search (artwork_asset_search) VALUES ('integrity-check');");

            // The local item index is preserved, and still searchable, after a library-only reset.
            Assert.Equal(1L, Scalar(conn, "SELECT COUNT(*) FROM local_item_search WHERE local_item_search MATCH 'sunset';"));
            Execute(conn, "INSERT INTO local_item_search (local_item_search) VALUES ('integrity-check');");
        }
    }

    [Fact]
    public async Task Reset_WhenAnotherWriterNeverLetsGo_FailsWithinTheTimeoutAndNamesTheHolder()
    {
        using var database = StartedDatabase();
        var service = NewService(database, new EnrichmentPipelineExecutionGate(), new WatcherSpy());
        await database.AcquireWriteLockAsync(holderFile: "StuckWriterService.cs", holderMember: "RunAsync");
        try
        {
            var error = await Assert.ThrowsAsync<DevHarnessResetBlockedException>(
                () => service.HoldWriteLockAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Contains("StuckWriterService.RunAsync", error.Message, StringComparison.Ordinal);
            Assert.Contains("before anything was deleted", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            database.ReleaseWriteLock();
        }

        // Once the writer lets go the reset takes the lock, and disposing its hold releases it again.
        using (await service.HoldWriteLockAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)))
        {
            Assert.StartsWith(
                "DevHarnessResetService.HoldWriteLockAsync",
                database.DescribeWriteLockHolder(),
                StringComparison.Ordinal);
        }

        Assert.Null(database.DescribeWriteLockHolder());
    }

    [Fact]
    public async Task Reset_WhenTheCallerGivesUpWhileWaitingForTheWriteLock_StopsWaitingStraightAway()
    {
        using var database = StartedDatabase();
        var service = NewService(database, new EnrichmentPipelineExecutionGate(), new WatcherSpy());
        await database.AcquireWriteLockAsync();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var waiting = service.HoldWriteLockAsync(cancellation.Token);
            await cancellation.CancelAsync();

            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => waiting.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsNotType<DevHarnessResetBlockedException>(error);
        }
        finally
        {
            database.ReleaseWriteLock();
        }
    }

    [Fact]
    public async Task Reset_WhenAnEnrichmentWorkerNeverStops_FailsBeforeDeletingAnythingAndLetsWorkersBackIn()
    {
        using var database = StartedDatabase();
        using (var conn = database.CreateConnection())
        {
            Execute(conn, "INSERT INTO search_index (entity_id, title) VALUES ('work-1', 'Dune');");
        }

        var gate = new EnrichmentPipelineExecutionGate();
        var watcher = new WatcherSpy();
        var service = NewService(database, gate, watcher);
        var stuck = await gate.EnterAsync("StuckWorkerHostedService");

        var error = await Assert.ThrowsAsync<DevHarnessResetBlockedException>(
            () => service.PrepareForReingestAsync().WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("StuckWorkerHostedService", error.Message, StringComparison.Ordinal);
        Assert.Contains("before anything was deleted", error.Message, StringComparison.Ordinal);
        using (var conn = database.CreateConnection())
        {
            Assert.Equal(1L, Scalar(conn, "SELECT COUNT(*) FROM search_index;"));
        }

        Assert.Null(database.DescribeWriteLockHolder());
        Assert.Equal(1, watcher.Pauses);
        Assert.Equal(1, watcher.Resumes);

        // Other workers are let back in straight away rather than left behind the paused gate.
        using var next = await gate.EnterAsync("NextWorkerHostedService").AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        stuck.Dispose();
    }

    [Fact]
    public async Task Reset_WhenAnotherResetIsAlreadyRunning_IsRefusedWithoutResumingWorkUnderTheFirst()
    {
        using var database = StartedDatabase();
        var gate = new EnrichmentPipelineExecutionGate();
        var watcher = new WatcherSpy();
        var service = NewService(
            database,
            gate,
            watcher,
            new DevHarnessResetTimeouts(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)));
        var worker = await gate.EnterAsync("SlowWorkerHostedService");

        // The first reset is waiting for the worker to leave.
        var first = service.PrepareForReingestAsync();
        Assert.False(first.IsCompleted);

        var error = await Assert.ThrowsAsync<DevHarnessResetBlockedException>(
            () => service.PrepareForReingestAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("Another reset is already running", error.Message, StringComparison.Ordinal);

        // The refused reset left the first one's pause in place.
        var blockedEntry = gate.EnterAsync("NextWorkerHostedService").AsTask();
        Assert.False(blockedEntry.IsCompleted);
        Assert.Equal(0, watcher.Resumes);

        worker.Dispose();
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        var next = await blockedEntry.WaitAsync(TimeSpan.FromSeconds(5));
        next.Dispose();

        // Once the first reset has finished the next one is let in.
        await service.PrepareForReingestAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(@"C:\build\src\MediaEngine.Api\Services\StuckWriterService.cs")]
    [InlineData("/build/src/MediaEngine.Api/Services/StuckWriterService.cs")]
    public async Task WriteLockHolder_IsNamedByFileAndMember_WhicheverSeparatorTheBuildUsed(string holderFile)
    {
        using var database = StartedDatabase();
        await database.AcquireWriteLockAsync(holderFile: holderFile, holderMember: "RunAsync");
        try
        {
            Assert.StartsWith("StuckWriterService.RunAsync, held for ", database.DescribeWriteLockHolder(), StringComparison.Ordinal);
        }
        finally
        {
            database.ReleaseWriteLock();
        }
    }

    private DatabaseConnection StartedDatabase()
    {
        var database = new DatabaseConnection(_path);
        database.InitializeSchema();
        database.RunStartupChecks();
        return database;
    }

    private static DevHarnessResetService NewService(DatabaseConnection database) =>
        new(database, null!, null!, null!, null!, NullLogger<DevHarnessResetService>.Instance);

    private DevHarnessResetService NewService(
        DatabaseConnection database,
        EnrichmentPipelineExecutionGate gate,
        WatcherSpy watcher,
        DevHarnessResetTimeouts? timeouts = null) =>
        new(
            database,
            Options.Create(new IngestionOptions()),
            _configuration,
            watcher,
            gate,
            NullLogger<DevHarnessResetService>.Instance,
            timeouts ?? ShortTimeouts);

    private static readonly DevHarnessResetTimeouts ShortTimeouts =
        new(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200));

    private static void InvokeReset(DevHarnessResetService service, string methodName)
    {
        var method = typeof(DevHarnessResetService).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(service, [new List<string>(), CancellationToken.None]);
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class WatcherSpy : IIngestionEngine
    {
        public int Pauses { get; private set; }

        public Task PauseWatcherAsync(CancellationToken ct = default)
        {
            Pauses++;
            return Task.CompletedTask;
        }

        public int Resumes { get; private set; }

        public Task ResumeWatcherAsync(CancellationToken ct = default)
        {
            Resumes++;
            return Task.CompletedTask;
        }

        public Task EnqueueIntakeAsync(IntakeFileRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FileMetadataRereadResult> RereadAssetMetadataAsync(Guid assetId, CancellationToken ct = default) => throw new NotSupportedException();
        public void Start() => throw new NotSupportedException();
        public Task StopAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PendingOperation>> DryRunAsync(string rootPath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ScanDirectory(string directory, bool includeSubdirectories = true, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ScanDirectories(IReadOnlyList<IngestionScanTarget> targets, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RefreshImportFolderAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
}
