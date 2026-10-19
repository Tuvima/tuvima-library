using Dapper;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Performance.Tests;

public sealed class LargeLibraryFixture : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"tuvima_performance_{Guid.NewGuid():N}.db");

    public LargeLibraryFixture()
    {
        Database = new DatabaseConnection(_path);
        Database.InitializeSchema();
        Database.RunStartupChecks();
    }

    public DatabaseConnection Database { get; }

    public Guid SeedCollectionItems(int workCount)
    {
        var collectionId = Guid.NewGuid();
        using var connection = Database.CreateConnection();
        using var transaction = connection.BeginTransaction();
        connection.Execute(
            """
            INSERT INTO collections (id, display_name, collection_type)
            VALUES (@collectionId, 'Performance fixture', 'Custom');

            WITH RECURSIVE sequence(value) AS (
                SELECT 1
                UNION ALL
                SELECT value + 1 FROM sequence WHERE value < @workCount
            )
            INSERT INTO works (id, media_type, curator_state)
            SELECT randomblob(16),
                   CASE value % 4
                       WHEN 0 THEN 'Books'
                       WHEN 1 THEN 'Movies'
                       WHEN 2 THEN 'Music'
                       ELSE 'Audiobooks'
                   END,
                   'accepted'
            FROM sequence;

            INSERT INTO collection_items (id, collection_id, work_id, sort_order)
            SELECT randomblob(16), @collectionId, id, rowid
            FROM works;
            """,
            new { collectionId, workCount },
            transaction,
            commandTimeout: 120);
        transaction.Commit();
        return collectionId;
    }

    public Guid SeedIngestionBatch(int groupCount)
    {
        var batchId = Guid.NewGuid();
        var occurredAt = DateTimeOffset.UtcNow.ToString("O");
        using var connection = Database.CreateConnection();
        using var transaction = connection.BeginTransaction();
        connection.Execute(
            """
            INSERT INTO ingestion_batches (
                id, status, category, files_total, files_processed, files_registered,
                started_at, completed_at, created_at, updated_at)
            VALUES (
                @batchId, 'completed', 'Performance', @groupCount, @groupCount, @groupCount,
                @occurredAt, @occurredAt, @occurredAt, @occurredAt);

            CREATE TEMP TABLE performance_ingestion_seed (
                sequence INTEGER PRIMARY KEY,
                work_id BLOB NOT NULL,
                edition_id BLOB NOT NULL,
                asset_id BLOB NOT NULL,
                log_id BLOB NOT NULL);

            WITH RECURSIVE sequence(value) AS (
                SELECT 1
                UNION ALL
                SELECT value + 1 FROM sequence WHERE value < @groupCount)
            INSERT INTO performance_ingestion_seed
            SELECT value, randomblob(16), randomblob(16), randomblob(16), randomblob(16)
            FROM sequence;

            INSERT INTO works (id, media_type, work_kind)
            SELECT work_id, 'Movies', 'standalone'
            FROM performance_ingestion_seed;

            INSERT INTO editions (id, work_id, format_label)
            SELECT edition_id, work_id, 'Performance fixture'
            FROM performance_ingestion_seed;

            INSERT INTO media_assets (
                id, edition_id, content_hash, file_path_root, presented_at)
            SELECT asset_id,
                   edition_id,
                   lower(hex(asset_id)),
                   'C:/performance/' || sequence || '.mkv',
                   @occurredAt
            FROM performance_ingestion_seed;

            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            SELECT work_id, 'title', 'Performance Movie ' || sequence, @occurredAt
            FROM performance_ingestion_seed;

            INSERT INTO ingestion_log (
                id, file_path, media_asset_id, content_hash, status, media_type,
                detected_title, normalized_title, ingestion_run_id, created_at, updated_at)
            SELECT log_id,
                   'C:/performance/' || sequence || '.mkv',
                   asset_id,
                   lower(hex(asset_id)),
                   'registered',
                   'Movies',
                   'Performance Movie ' || sequence,
                   'performance movie ' || sequence,
                   @batchId,
                   @occurredAt,
                   @occurredAt
            FROM performance_ingestion_seed;

            DROP TABLE performance_ingestion_seed;
            """,
            new { batchId, groupCount, occurredAt },
            transaction,
            commandTimeout: 120);
        transaction.Commit();
        return batchId;
    }

    public void Dispose()
    {
        Database.Dispose();
        TestTemp.ReleaseSqlitePool(_path);
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
