using Dapper;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Ingestion;

namespace MediaEngine.Storage.Tests;

public sealed class IngestionQueueProgressRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly MediaOperationRepository _operations;
    private readonly IdentityJobRepository _jobs;
    private readonly IngestionQueueProgressRepository _repository;

    public IngestionQueueProgressRepositoryTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima-queue-progress-{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _operations = new MediaOperationRepository(_db);
        _jobs = new IdentityJobRepository(_db);
        _repository = new IngestionQueueProgressRepository(_db);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch (ObjectDisposedException) { }
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    [Fact]
    public async Task FileInTheMiddleOfIdentity_ShowsItsAssetItsIdentityStateAndTheJobsMediaType()
    {
        var (_, assetId) = BuildAsset("Movies");
        var operation = await AddOperationAsync("/watch/a.mkv", MediaOperationStage.QueuedIdentity, MediaOperationStatus.Running, Now.AddMinutes(-1));
        await _operations.AttachEntityAsync(operation.Id, assetId, "MediaAsset");
        await AddJobAsync(assetId, "Hydrating", "Movies");

        var row = Assert.Single(await _repository.ReadAsync(Now.AddHours(-1)));

        Assert.Equal(operation.Id, row.OperationId);
        Assert.Equal(assetId, row.AssetId);
        Assert.Equal("Hydrating", row.IdentityState);
        Assert.Equal("Movies", row.MediaType);
        Assert.Equal(MediaOperationStage.QueuedIdentity, row.Stage);
        Assert.Equal(MediaOperationStatus.Running, row.Status);
        Assert.Null(row.HeldToolId);
        Assert.False(row.HasPendingReview);
        Assert.Equal(Now.AddMinutes(-1), row.CreatedAt);
        Assert.NotNull(row.IdentityUpdatedAt);
    }

    [Fact]
    public async Task FileNotYetRegistered_HasNoAssetNoIdentityAndNoMediaType()
    {
        await AddOperationAsync("/watch/b.mkv", MediaOperationStage.Queued, MediaOperationStatus.Queued, Now.AddSeconds(-5));

        var row = Assert.Single(await _repository.ReadAsync(Now.AddHours(-1)));

        Assert.Null(row.AssetId);
        Assert.Null(row.IdentityState);
        Assert.Null(row.MediaType);
        Assert.Equal(MediaOperationStage.Queued, row.Stage);
    }

    [Fact]
    public async Task MediaType_FallsBackToTheIngestionLogWhenThereIsNoIdentityJob()
    {
        var (_, assetId) = BuildAsset("Books");
        var operation = await AddOperationAsync("/watch/c.epub", MediaOperationStage.Registered, MediaOperationStatus.Running, Now.AddSeconds(-5));
        await _operations.AttachEntityAsync(operation.Id, assetId, "MediaAsset");
        await InsertLogAsync("/watch/c.epub", assetId, "Books");

        var row = Assert.Single(await _repository.ReadAsync(Now.AddHours(-1)));

        Assert.Equal("Books", row.MediaType);
    }

    [Fact]
    public async Task HeldAsset_ShowsTheToolItIsWaitingFor()
    {
        var (_, assetId) = BuildAsset("Movies");
        var operation = await AddOperationAsync("/watch/d.mkv", MediaOperationStage.Completed, MediaOperationStatus.Succeeded, Now.AddMinutes(-2));
        await _operations.AttachEntityAsync(operation.Id, assetId, "MediaAsset");
        await AddJobAsync(assetId, "Ready", "Movies");
        await new MediaInspectionHoldRepository(_db).UpsertAsync(assetId, "ffmpeg", Now.AddMinutes(-1));

        var row = Assert.Single(await _repository.ReadAsync(Now.AddHours(-1)));

        Assert.Equal("ffmpeg", row.HeldToolId);
    }

    [Fact]
    public async Task PendingReview_IsFlaggedWhetherItIsOnTheAssetOrItsWork_ButAFailedWriteBackIsNot()
    {
        var (workA, assetA) = BuildAsset("Movies");
        var opA = await AddOperationAsync("/watch/e1.mkv", MediaOperationStage.Completed, MediaOperationStatus.Succeeded, Now.AddMinutes(-2));
        await _operations.AttachEntityAsync(opA.Id, assetA, "MediaAsset");
        await AddReviewAsync(workA, "LowConfidence", "Pending");

        var (_, assetB) = BuildAsset("Movies");
        var opB = await AddOperationAsync("/watch/e2.mkv", MediaOperationStage.Completed, MediaOperationStatus.Succeeded, Now.AddMinutes(-2));
        await _operations.AttachEntityAsync(opB.Id, assetB, "MediaAsset");
        await AddReviewAsync(assetB, "AmbiguousMatch", "Pending");

        var (workC, assetC) = BuildAsset("Movies");
        var opC = await AddOperationAsync("/watch/e3.mkv", MediaOperationStage.Completed, MediaOperationStatus.Succeeded, Now.AddMinutes(-2));
        await _operations.AttachEntityAsync(opC.Id, assetC, "MediaAsset");
        await AddReviewAsync(workC, "WritebackFailed", "Pending");

        var (_, assetD) = BuildAsset("Movies");
        var opD = await AddOperationAsync("/watch/e4.mkv", MediaOperationStage.Completed, MediaOperationStatus.Succeeded, Now.AddMinutes(-2));
        await _operations.AttachEntityAsync(opD.Id, assetD, "MediaAsset");
        await AddReviewAsync(assetD, "LowConfidence", "Resolved");

        var rows = (await _repository.ReadAsync(Now.AddHours(-1))).ToDictionary(row => row.OperationId);

        Assert.True(rows[opA.Id].HasPendingReview);
        Assert.True(rows[opB.Id].HasPendingReview);
        Assert.False(rows[opC.Id].HasPendingReview);
        Assert.False(rows[opD.Id].HasPendingReview);
    }

    [Fact]
    public async Task OldFinishedFile_IsLeftOutOfARecentPeriod_ButAnOldFileStillBeingWorkedOnIsKept()
    {
        var (_, finishedAsset) = BuildAsset("Movies");
        var finished = await AddOperationAsync("/watch/old-done.mkv", MediaOperationStage.Completed, MediaOperationStatus.Succeeded, Now.AddDays(-2));
        await _operations.AttachEntityAsync(finished.Id, finishedAsset, "MediaAsset");
        await AddJobAsync(finishedAsset, "Ready", "Movies");

        var (_, busyAsset) = BuildAsset("Movies");
        var busy = await AddOperationAsync("/watch/old-busy.mkv", MediaOperationStage.Completed, MediaOperationStatus.Succeeded, Now.AddDays(-2));
        await _operations.AttachEntityAsync(busy.Id, busyAsset, "MediaAsset");
        await AddJobAsync(busyAsset, "UniverseEnriching", "Movies");

        var retrying = await AddOperationAsync("/watch/old-retry.mkv", MediaOperationStage.Failed, MediaOperationStatus.RetryWaiting, Now.AddDays(-2));

        var recent = (await _repository.ReadAsync(Now.AddHours(-1))).Select(row => row.OperationId).ToList();
        var all = (await _repository.ReadAsync(Now.AddDays(-3))).Select(row => row.OperationId).ToList();

        Assert.Equal(new[] { busy.Id, retrying.Id }.Order(), recent.Order());
        Assert.Contains(finished.Id, all);
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public async Task WithNoPeriodYet_OnlyFilesStillBeingWorkedOnOrHeldAreReturned()
    {
        var (_, doneAsset) = BuildAsset("Movies");
        var done = await AddOperationAsync("/watch/done.mkv", MediaOperationStage.Completed, MediaOperationStatus.Succeeded, Now.AddSeconds(-30));
        await _operations.AttachEntityAsync(done.Id, doneAsset, "MediaAsset");
        await AddJobAsync(doneAsset, "Ready", "Movies");

        var (_, heldAsset) = BuildAsset("Movies");
        var held = await AddOperationAsync("/watch/held.mkv", MediaOperationStage.Completed, MediaOperationStatus.Succeeded, Now.AddSeconds(-30));
        await _operations.AttachEntityAsync(held.Id, heldAsset, "MediaAsset");
        await AddJobAsync(heldAsset, "Ready", "Movies");
        await new MediaInspectionHoldRepository(_db).UpsertAsync(heldAsset, "ffmpeg", Now);

        var queued = await AddOperationAsync("/watch/queued.mkv", MediaOperationStage.Discovered, MediaOperationStatus.Pending, Now.AddSeconds(-5));

        var rows = await _repository.ReadAsync(null);

        Assert.Equal(new[] { held.Id, queued.Id }.Order(), rows.Select(row => row.OperationId).Order());
    }

    [Fact]
    public async Task OtherKindsOfOperation_AreNotPartOfTheIngestionQueue()
    {
        await _operations.EnsureAsync(new MediaOperation
        {
            OperationType = MediaOperationType.EnrichmentCoverArt,
            OperationKind = MediaOperationKind.Enrichment,
            Status = MediaOperationStatus.Running,
            Stage = MediaOperationStage.ProviderLookup,
            IdempotencyKey = $"enrich:{Guid.NewGuid():N}",
            CreatedAt = Now.AddSeconds(-5),
            UpdatedAt = Now.AddSeconds(-5),
        });

        Assert.Empty(await _repository.ReadAsync(Now.AddHours(-1)));
    }

    [Fact]
    public async Task AttachingTheAsset_RecordsTheEntityWithoutChangingStatusOrStage()
    {
        var (_, assetId) = BuildAsset("Movies");
        var operation = await AddOperationAsync("/watch/f.mkv", MediaOperationStage.Scoring, MediaOperationStatus.Running, Now);

        await _operations.AttachEntityAsync(operation.Id, assetId, "MediaAsset");

        var stored = await _operations.GetByIdAsync(operation.Id);
        Assert.NotNull(stored);
        Assert.Equal(assetId, stored.EntityId);
        Assert.Equal("MediaAsset", stored.EntityKind);
        Assert.Equal(MediaOperationStatus.Running, stored.Status);
        Assert.Equal(MediaOperationStage.Scoring, stored.Stage);
    }

    [Fact]
    public async Task TheQueryUsesIndexesInsteadOfScanningMediaOperations()
    {
        using var conn = _db.CreateConnection();
        var plan = (await conn.QueryAsync(
                "EXPLAIN QUERY PLAN " + IngestionQueueProgressRepository.Sql,
                new { since = Now.AddHours(-1).ToString("O") }))
            .Select(row => (string)((IDictionary<string, object>)row)["detail"])
            .ToList();

        Assert.DoesNotContain(plan, line => line.StartsWith("SCAN mo", StringComparison.Ordinal)
            || line.StartsWith("SCAN media_operations", StringComparison.Ordinal));
        Assert.Contains(plan, line => line.Contains("idx_media_operations_type_created", StringComparison.Ordinal));
        Assert.Contains(plan, line => line.Contains("idx_media_operations_type_status", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, line => line.EndsWith("(operation_type=?)", StringComparison.Ordinal));
    }

    private async Task<MediaOperation> AddOperationAsync(string path, string stage, string status, DateTimeOffset createdAt) =>
        await _operations.EnsureAsync(new MediaOperation
        {
            OperationType = MediaOperationType.IngestionFile,
            OperationKind = MediaOperationKind.Ingestion,
            SourcePath = path,
            Status = status,
            Stage = stage,
            QueueName = "ingestion",
            IdempotencyKey = $"ingestion:file:{path}",
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        });

    private async Task AddJobAsync(Guid assetId, string state, string mediaType) =>
        await _jobs.CreateAsync(new IdentityJob
        {
            EntityId = assetId,
            EntityType = "MediaAsset",
            MediaType = mediaType,
            State = state,
            CreatedAt = Now.AddMinutes(-1),
            UpdatedAt = Now.AddSeconds(-10),
        });

    private async Task InsertLogAsync(string path, Guid assetId, string mediaType) =>
        await new IngestionLogRepository(_db).InsertAsync(new IngestionLogEntry
        {
            FilePath = path,
            MediaAssetId = assetId,
            MediaType = mediaType,
            Status = "registered",
        });

    private async Task AddReviewAsync(Guid entityId, string trigger, string status)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO review_queue (id, entity_id, entity_type, trigger, status)
            VALUES (@id, @entityId, 'MediaAsset', @trigger, @status);
            """,
            new { id = Guid.NewGuid(), entityId, trigger, status });
    }

    private (Guid WorkId, Guid AssetId) BuildAsset(string mediaType)
    {
        using var conn = _db.CreateConnection();
        var collectionId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        conn.Execute(
            """
            INSERT INTO collections (id, created_at) VALUES (@collectionId, datetime('now'));
            INSERT INTO works (id, collection_id, media_type) VALUES (@workId, @collectionId, @mediaType);
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
                VALUES (@assetId, @editionId, @hash, @filePath, 'Normal');
            """,
            new
            {
                collectionId,
                workId,
                editionId,
                assetId,
                mediaType,
                hash = $"hash_{assetId:N}",
                filePath = $"/library/{assetId:N}.bin",
            });
        return (workId, assetId);
    }
}
