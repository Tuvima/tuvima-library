using MediaEngine.Storage.Schema.Upgrades;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// A data store built at the step before "remove_ingestion_batches", holding batch rows and the batch / run link on other
/// tables, upgrades through that step: the batch tables and the link columns go, every other row stays.
/// </summary>
public sealed class RemoveIngestionBatchesUpgradeTests : IDisposable
{
    private const string StepName = "remove_ingestion_batches";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_batches_{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _connection;
    private readonly DataStoreUpgradeRunner _runner = new();

    public RemoveIngestionBatchesUpgradeTests()
    {
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        Execute("PRAGMA foreign_keys = ON;");
    }

    public void Dispose()
    {
        _connection.Dispose();
        TestTemp.DeleteDatabase(_dbPath);
    }

    [Fact]
    public void DataStoreWithBatchRowsAndRunLinks_UpgradesCleanly_AndKeepsEveryOtherRow()
    {
        var all = DataStoreUpgradeCatalog.ForProduct();
        var step = all.Single(candidate => candidate.Name == StepName);
        _runner.Run(_connection, all.Where(candidate => candidate.Number < step.Number).ToList(), Options());

        var batch = Guid.NewGuid();
        var asset = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        Execute($"""
            INSERT INTO ingestion_batches (id) VALUES ({Blob(batch)});
            INSERT INTO ingestion_batch_artifacts (id, batch_id, artifact_type, action)
                VALUES ({Blob(Guid.NewGuid())}, {Blob(batch)}, 'review_item', 'created');
            INSERT INTO identity_jobs (id, entity_id, entity_type, media_type, ingestion_run_id, state)
                VALUES ({Blob(jobId)}, {Blob(asset)}, 'MediaAsset', 'Books', {Blob(batch)}, 'Hydrating');
            INSERT INTO ingestion_log (id, file_path, status, ingestion_run_id)
                VALUES ({Blob(Guid.NewGuid())}, '/library/book.epub', 'queued_identity', {Blob(batch)});
            INSERT INTO system_activity (action_type, detail, ingestion_run_id)
                VALUES ('FileIngested', 'kept', {Blob(batch)});
            INSERT INTO entity_events (id, entity_id, entity_type, event_type, trigger, ingestion_run_id)
                VALUES ({Blob(Guid.NewGuid())}, {Blob(asset)}, 'Work', 'retail_matched', 'ingestion', {Blob(batch)});
            INSERT INTO media_operations (id, operation_type, operation_kind, batch_id, status, position_key, created_at, updated_at, idempotency_key)
                VALUES ({Blob(operationId)}, 'ingestion.file', 'ingestion', {Blob(batch)}, 'Running', 1, '2026-10-19T00:00:00Z', '2026-10-19T00:00:00Z', 'ingestion:file:/library/book.epub:1:1');
            INSERT INTO media_operation_events (id, operation_id, batch_id, event_type, occurred_at)
                VALUES ({Blob(Guid.NewGuid())}, {Blob(operationId)}, {Blob(batch)}, 'status_changed', '2026-10-19T00:00:00Z');
            """);

        var result = _runner.Run(_connection, all, Options());

        Assert.Contains(result.Applied, applied => applied.Number == step.Number);
        Assert.Equal(0, TableCount("ingestion_batches"));
        Assert.Equal(0, TableCount("ingestion_batch_artifacts"));
        foreach (var (table, column) in new[]
        {
            ("identity_jobs", "ingestion_run_id"),
            ("ingestion_log", "ingestion_run_id"),
            ("system_activity", "ingestion_run_id"),
            ("entity_events", "ingestion_run_id"),
            ("media_operations", "batch_id"),
            ("media_operation_events", "batch_id"),
        })
        {
            Assert.DoesNotContain(column, Columns(table));
        }

        Assert.Equal("Hydrating", Scalar<string>("SELECT state FROM identity_jobs;"));
        Assert.Equal("queued_identity", Scalar<string>("SELECT status FROM ingestion_log;"));
        Assert.Equal("kept", Scalar<string>("SELECT detail FROM system_activity;"));
        Assert.Equal("retail_matched", Scalar<string>("SELECT event_type FROM entity_events;"));
        Assert.Equal("Running", Scalar<string>("SELECT status FROM media_operations;"));
        Assert.Equal("status_changed", Scalar<string>("SELECT event_type FROM media_operation_events;"));

        var indexes = Query<string>("SELECT name FROM sqlite_master WHERE type = 'index';");
        string[] removedIndexes =
        [
            "idx_identity_jobs_ingestion_run_id",
            "idx_identity_jobs_run_entity_updated",
            "idx_ingestion_batch_artifacts_batch",
            "idx_ingestion_batches_status",
            "idx_ingestion_log_run",
            "idx_ingestion_log_run_created",
            "idx_media_operation_events_batch",
            "idx_media_operation_events_batch_entity",
            "idx_media_operations_batch",
            "idx_media_operations_batch_entity_type",
            "idx_system_activity_run_entity_action",
        ];
        Assert.Empty(removedIndexes.Intersect(indexes, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(
            ["entity_id", "updated_at", "created_at"],
            Query<string>("SELECT name FROM pragma_index_info('idx_identity_jobs_activity_latest') ORDER BY seqno;"));
    }

    private static string Blob(Guid id) => $"X'{Convert.ToHexString(id.ToByteArray())}'";

    private static DataStoreUpgradeOptions Options()
        => new() { Clock = TimeProvider.System, EngineVersion = "test" };

    private List<string> Columns(string table)
        => Query<string>($"SELECT name FROM pragma_table_info('{table}');");

    private int TableCount(string table)
        => (int)Scalar<long>($"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{table}';");

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private List<T> Query<T>(string sql)
    {
        var rows = new List<T>();
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((T)reader.GetValue(0));
        }

        return rows;
    }
}
