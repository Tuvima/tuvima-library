using Dapper;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The pipeline's own batch progress rows, which the screens no longer read. These stay until the batch tables are
/// removed from the pipeline.
/// </summary>
public sealed class IngestionBatchProgressTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;

    public IngestionBatchProgressTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_ingestion_batch_progress_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task MultiFileBookUsesIdentityUnitsAndUnmatchedWorkIsNotACompletedAddition()
    {
        var batch = AddBatch("running", 40, 40);
        var work = AddStandalone(batch, "Audiobooks", "A forty-file book", ready: false);
        using var conn = _db.CreateConnection();
        var asset = conn.QuerySingle<Guid>("SELECT ma.id FROM media_assets ma JOIN editions e ON e.id=ma.edition_id WHERE e.work_id=@work", new { work });
        conn.Execute("""
            INSERT INTO identity_jobs (id,entity_id,entity_type,media_type,ingestion_run_id,state,pass,created_at,updated_at)
            VALUES (@id,@asset,'MediaAsset','Audiobooks',@batch,'RetailNoMatch','Quick',@now,@now);
            """, new { id = Guid.NewGuid(), asset, batch, now = DateTimeOffset.UtcNow.ToString("O") });
        var service = new MediaEngine.Providers.Services.BatchProgressService(new IngestionBatchRepository(_db), null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaEngine.Providers.Services.BatchProgressService>.Instance);
        var progress = (await service.GetProgressAsync(batch))!;
        Assert.Equal(40, progress.FilesProcessed);
        Assert.Equal(1, progress.WorkUnitsTotal);
        Assert.Equal(1, progress.WorkUnitsCompleted);
        Assert.True(progress.IsComplete);
        conn.Execute("UPDATE identity_jobs SET state='ReadyWithoutUniverse';");
        MarkReady(work);
        await new MediaOperationRepository(_db).EnsureAsync(new MediaEngine.Domain.Entities.MediaOperation
        {
            OperationType = "enrichment.people",
            OperationKind = "enrichment",
            BatchId = batch,
            EntityId = asset,
            Status = "queued",
            QueueName = "people",
            IdempotencyKey = "test:people",
        });
        var enriching = (await service.GetProgressAsync(batch))!;
        Assert.False(enriching.IsComplete);
        Assert.True(enriching.ProgressIsIndeterminate);
        Assert.Equal("Finishing enrichment", enriching.CurrentStage);
        Assert.NotEqual(99, enriching.ProgressPercent);
    }

    [Fact]
    public async Task SharedBatchProgressStaysIncompleteUntilIdentityFinishes()
    {
        var batch = AddBatch("running", 1, 1);
        var work = AddStandalone(batch, "Books", "Still identifying", ready: false);
        using var conn = _db.CreateConnection();
        var asset = conn.QuerySingle<Guid>("SELECT ma.id FROM media_assets ma JOIN editions e ON e.id=ma.edition_id WHERE e.work_id=@work", new { work });
        conn.Execute("""
            INSERT INTO identity_jobs (id,entity_id,entity_type,media_type,ingestion_run_id,state,pass,created_at,updated_at)
            VALUES (@id,@asset,'MediaAsset','Books',@batch,'Hydrating','Quick',@now,@now);
            """, new { id = Guid.NewGuid(), asset, batch, now = DateTimeOffset.UtcNow.ToString("O") });
        var service = new MediaEngine.Providers.Services.BatchProgressService(new IngestionBatchRepository(_db), null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaEngine.Providers.Services.BatchProgressService>.Instance);
        var active = await service.GetProgressAsync(batch);
        Assert.NotNull(active);
        Assert.False(active.IsComplete);
        Assert.Equal(0, active.ProgressPercent);
        conn.Execute("UPDATE identity_jobs SET state='Ready';");
        MarkReady(work);
        var complete = await service.GetProgressAsync(batch);
        Assert.True(complete!.IsComplete);
        Assert.Equal(100, complete.ProgressPercent);
        conn.Execute("UPDATE media_assets SET presented_at=NULL; UPDATE ingestion_log SET status='queued_identity'; UPDATE ingestion_batches SET status='completed';");
        conn.Execute("""
            INSERT INTO ingestion_log (id,file_path,status,ingestion_run_id) VALUES (@id,'C:/watch/duplicate.epub','duplicate',@batch);
            UPDATE ingestion_batches SET files_total=2 WHERE id=@batch;
            """, new { id = Guid.NewGuid(), batch });
        var withDuplicate = await service.GetProgressAsync(batch);
        Assert.True(withDuplicate!.IsComplete);
        Assert.Equal(2, withDuplicate.FilesProcessed);
        Assert.Equal(1, withDuplicate.FilesIdentified);

        // A different input file can resolve to an asset already handled in this
        // batch. Its duplicate outcome still settles that input in the batch total.
        conn.Execute("UPDATE ingestion_log SET media_asset_id=@asset WHERE status='duplicate';", new { asset });
        Assert.True((await service.GetProgressAsync(batch))!.IsComplete);
        var originalPath = conn.QuerySingle<string>("SELECT file_path FROM ingestion_log WHERE status='queued_identity';");
        conn.Execute("INSERT INTO ingestion_log (id,file_path,status,media_asset_id,ingestion_run_id) VALUES (@id,@originalPath,'duplicate',@asset,@batch);", new { id = Guid.NewGuid(), originalPath, asset, batch });
        Assert.Equal(1, (await new IngestionBatchRepository(_db).GetProgressSnapshotAsync(batch)).FilesSkipped);
    }

    private Guid AddBatch(string status, int total, int processed)
    {
        var id = Guid.NewGuid();
        var time = DateTimeOffset.UtcNow;
        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO ingestion_batches (
                id, status, source_path, category, files_total, files_processed,
                files_registered, files_review, files_no_match, files_failed,
                started_at, completed_at, created_at, updated_at)
            VALUES (
                @id, @status, 'C:/watch', 'Mixed', @total, @processed,
                @processed, 0, 0, 0, @time, @completedAt, @time, @time);
            """, new { id, status, total, processed, time = time.ToString("O"), completedAt = status == "completed" ? time.ToString("O") : null });
        return id;
    }

    private Guid AddStandalone(Guid batchId, string mediaType, string title, bool ready = true)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var conn = _db.CreateConnection();
        conn.Execute(
            "INSERT INTO works (id, media_type, work_kind, ready_at, first_ready_at) VALUES (@workId, @mediaType, 'standalone', @readyAt, @readyAt);",
            new { workId, mediaType, readyAt = ready ? now : null });
        conn.Execute("INSERT INTO editions (id, work_id, format_label) VALUES (@editionId, @workId, 'Test');", new { editionId, workId });
        conn.Execute("INSERT INTO media_assets (id, edition_id, content_hash, file_path_root) VALUES (@assetId, @editionId, @hash, @path);", new { assetId, editionId, hash = $"hash-{assetId:N}", path = $"C:/watch/{assetId:N}.media" });
        conn.Execute("INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@workId, 'title', @title, @now);", new { workId, title, now });
        conn.Execute("""
            INSERT INTO ingestion_log (
                id, file_path, media_asset_id, content_hash, status, media_type,
                detected_title, normalized_title, ingestion_run_id, created_at, updated_at)
            VALUES (
                @logId, @path, @assetId, @hash, 'registered', @mediaType,
                @title, @title, @batchId, @now, @now);
            """, new { logId = Guid.NewGuid(), path = $"C:/watch/{assetId:N}.media", assetId, hash = $"hash-{assetId:N}", mediaType, title, batchId, now });
        return workId;
    }

    private void MarkReady(Guid workId)
    {
        using var conn = _db.CreateConnection();
        var stamp = DateTimeOffset.UtcNow.ToString("O");
        conn.Execute("UPDATE works SET ready_at = @stamp, first_ready_at = COALESCE(first_ready_at, @stamp) WHERE id = @workId;", new { workId, stamp });
    }
}
