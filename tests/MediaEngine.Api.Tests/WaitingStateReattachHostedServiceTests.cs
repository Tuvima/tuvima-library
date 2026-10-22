using Dapper;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Backups;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Storage;
using MediaEngine.Storage.Backup;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The worker that puts a restored person's waiting state back on its title: it costs nothing while nothing waits, puts state back
/// when the title is in the Library, and says what it did in one line of counts.
/// </summary>
public sealed class WaitingStateReattachHostedServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
    private const string Stamp = "2026-10-21T10:00:00Z";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima_reattach_worker_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly RecordingLogger _logger = new();
    private readonly ControlledIngestion _ingestion = new();

    public WaitingStateReattachHostedServiceTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _database.RunStartupChecks();
    }

    [Fact]
    public void RunOnce_DoesNothing_WhenNothingIsWaiting()
    {
        var worker = NewWorker();

        Assert.Null(worker.RunOnce(Now, CancellationToken.None));
        Assert.Empty(_logger.Messages);
    }

    [Fact]
    public void RunOnce_PutsWaitingStateBackWhenTheTitleIsInTheLibrary_AndLogsOneLineOfCounts()
    {
        var profile = AddProfile();
        RestoreProgressOnATitle(profile, progress: 42.5);
        var worker = NewWorker();

        // The title is not in the Library yet: the state keeps waiting and nothing is logged.
        var first = worker.RunOnce(Now.AddMinutes(1), CancellationToken.None);
        Assert.Equal(0, first!.Attached);
        Assert.Equal(1, first.StillWaiting);
        Assert.Empty(_logger.Messages);

        var asset = AddMovie("tt1160419", "returned-hash");
        var second = worker.RunOnce(Now.AddMinutes(2), CancellationToken.None);

        Assert.Equal(1, second!.Attached);
        Assert.Equal(0, second.StillWaiting);
        using var connection = _database.CreateConnection();
        Assert.Equal(42.5, connection.ExecuteScalar<double>("SELECT progress_pct FROM user_states WHERE asset_id = @asset", new { asset }));
        var line = Assert.Single(_logger.Messages);
        Assert.Contains("1 re-attached", line);
        Assert.Contains("0 still waiting", line);
        Assert.Contains("0 cleared", line);
    }

    [Fact]
    public void RunOnce_LooksAtEveryWaitingRowOnce_InBatches_WithAShortPauseBetweenThem()
    {
        var profile = AddProfile();
        RestoreProgressOnTitles(profile, count: 5);
        var pauses = new List<TimeSpan>();
        var worker = NewWorker(batchSize: 2, pause: (span, _) => pauses.Add(span));

        var result = worker.RunOnce(Now.AddMinutes(1), CancellationToken.None);

        // Two, two and one: a pause after each full batch and none after the last. Nothing is capped, so no row is left for days.
        Assert.Equal(5, result!.StillWaiting);
        Assert.Equal([WaitingStateReattachHostedService.BatchPause, WaitingStateReattachHostedService.BatchPause], pauses);
        Assert.Equal(WaitingStateReattachHostedService.BatchPause, TimeSpan.FromMilliseconds(100));
        using var connection = _database.CreateConnection();
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM waiting_personal_state WHERE last_checked_at IS NULL"));
    }

    [Fact]
    public void RunOnce_StopsEarly_WhenIngestionBecomesBusyAgain_AndLeavesTheRestUnlooked()
    {
        var profile = AddProfile();
        RestoreProgressOnTitles(profile, count: 5);
        var worker = NewWorker(batchSize: 2, pause: (_, _) => _ingestion.State = "working");

        var result = worker.RunOnce(Now.AddMinutes(1), CancellationToken.None);

        // The first batch ran, the pause let ingestion start, so the second batch was the last: one row was never looked at.
        Assert.NotNull(result);
        using var connection = _database.CreateConnection();
        Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM waiting_personal_state WHERE last_checked_at IS NULL"));
    }

    [Fact]
    public void RunOnce_LogsOneWarningWithTheCountAndTables_ForRowsThatCouldNotBeWritten_AndNeverATitle()
    {
        var profile = AddProfile();
        RestoreProgressOnATitle(profile, progress: 42.5);
        AddMovie("tt1160419", "returned-hash");
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("CREATE TRIGGER refuse_user_states BEFORE INSERT ON user_states BEGIN SELECT RAISE(ABORT, 'refused'); END");
        }

        var worker = NewWorker();
        var result = worker.RunOnce(Now.AddMinutes(1), CancellationToken.None);

        Assert.Equal(1, result!.FailedRows);
        Assert.Equal(["user_states"], result.FailedTables);
        var warning = Assert.Single(_logger.Warnings);
        Assert.Contains("1 waiting rows", warning);
        Assert.Contains("user_states", warning);
        Assert.DoesNotContain("tt1160419", warning);
    }

    private WaitingStateReattachHostedService NewWorker(int batchSize = WaitingStateReattachHostedService.BatchSize, Action<TimeSpan, CancellationToken>? pause = null) =>
        new(new WaitingPersonalStateRepository(_database), _ingestion, () => 90, _logger, batchSize, pause ?? ((_, _) => { }));

    private byte[] AddProfile()
    {
        var id = GuidSql.ToBlob(Guid.NewGuid());
        using var connection = _database.CreateConnection();
        connection.Execute("INSERT INTO profiles (id, display_name, created_at) VALUES (@id, 'Maya', @now)", new { id, now = Stamp });
        return id;
    }

    private byte[] AddMovie(string imdb, string hash)
    {
        var work = GuidSql.ToBlob(Guid.NewGuid());
        var edition = GuidSql.ToBlob(Guid.NewGuid());
        var asset = GuidSql.ToBlob(Guid.NewGuid());
        using var connection = _database.CreateConnection();
        connection.Execute("INSERT INTO works (id, media_type) VALUES (@work, 'Movies')", new { work });
        connection.Execute("INSERT INTO editions (id, work_id, format_label) VALUES (@edition, @work, '4K Remux')", new { edition, work });
        connection.Execute(
            "INSERT INTO media_assets (id, edition_id, content_hash, file_path_root) VALUES (@asset, @edition, @hash, @path)",
            new { asset, edition, hash, path = $"/media/{hash}.mkv" });
        connection.Execute(
            "INSERT INTO bridge_ids (id, entity_id, id_type, id_value) VALUES (@id, @work, 'imdb_id', @imdb)",
            new { id = GuidSql.ToBlob(Guid.NewGuid()), work, imdb });
        return asset;
    }

    /// <summary>Restores a backup that holds one progress entry for a title the Library does not have yet.</summary>
    private void RestoreProgressOnATitle(byte[] profile, double progress)
    {
        var title = BackupTitle.Empty("t1", "MediaAsset") with
        {
            Ids = new Dictionary<string, string> { ["imdb_id"] = "tt1160419" },
            FileHashes = ["original-hash"],
            Edition = "4K Remux",
        };
        var table = new BackupExportTable(
            "user_states",
            BackupDataKind.PersonalState,
            ["user_id", "asset_id", "progress_pct", "last_accessed"],
            [[BackupValue.FromBlob(profile), BackupValue.FromTitleRef("t1"), BackupValue.FromReal(progress), BackupValue.FromText(Stamp)]]);
        var export = new BackupExport(
            [table],
            [],
            [title],
            new BackupExportContents(BackupScope.WholeLibrary, 0, 0, 1, 0, 1, 0, 1, 0),
            []);

        new BackupRestoreRepository(_database).Import(export, "backup.zip", null, null, Now);
    }

    /// <summary>Restores a backup that holds one progress entry for each of <paramref name="count"/> titles the Library does not have.</summary>
    private void RestoreProgressOnTitles(byte[] profile, int count)
    {
        var titles = new List<BackupTitle>();
        var rows = new List<IReadOnlyList<BackupValue>>();
        for (var i = 0; i < count; i++)
        {
            var key = "t" + i;
            titles.Add(BackupTitle.Empty(key, "MediaAsset") with
            {
                Ids = new Dictionary<string, string> { ["imdb_id"] = "tt90000" + i },
                FileHashes = ["missing-hash-" + i],
                Edition = "4K Remux",
            });
            rows.Add([BackupValue.FromBlob(profile), BackupValue.FromTitleRef(key), BackupValue.FromReal(10 + i), BackupValue.FromText(Stamp)]);
        }

        var table = new BackupExportTable("user_states", BackupDataKind.PersonalState, ["user_id", "asset_id", "progress_pct", "last_accessed"], rows);
        var export = new BackupExport(
            [table],
            [],
            titles,
            new BackupExportContents(BackupScope.WholeLibrary, 0, 0, count, 0, count, 0, count, 0),
            []);

        new BackupRestoreRepository(_database).Import(export, "backup.zip", null, null, Now);
    }

    public void Dispose()
    {
        _database.Dispose();
        using (var pool = new SqliteConnection($"Data Source={_path}"))
        {
            SqliteConnection.ClearPool(pool);
        }

        // Best-effort cleanup of the temporary data store; a locked file must not fail the test run.
        foreach (var leftover in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try
            {
                File.Delete(leftover);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class ControlledIngestion : IIngestionQueueProgressService
    {
        public string State { get; set; } = "idle";

        public IngestionQueueProgressDto Current => new() { State = State };
    }

    private sealed class RecordingLogger : ILogger<WaitingStateReattachHostedService>
    {
        public List<string> Messages { get; } = [];

        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // Debug lines are not counted: only what an administrator would see in the log.
            if (logLevel >= LogLevel.Information)
            {
                Messages.Add(formatter(state, exception));
            }

            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
