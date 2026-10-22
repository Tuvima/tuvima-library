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

    private WaitingStateReattachHostedService NewWorker() =>
        new(new WaitingPersonalStateRepository(_database), new IdleIngestion(), () => 90, _logger);

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

    private sealed class IdleIngestion : IIngestionQueueProgressService
    {
        public IngestionQueueProgressDto Current { get; } = new() { State = "idle" };
    }

    private sealed class RecordingLogger : ILogger<WaitingStateReattachHostedService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // Debug lines are not counted: only what an administrator would see in the log.
            if (logLevel >= LogLevel.Information)
            {
                Messages.Add(formatter(state, exception));
            }
        }
    }
}
