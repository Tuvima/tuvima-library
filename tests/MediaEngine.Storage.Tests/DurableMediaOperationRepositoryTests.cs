using Dapper;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Storage.Tests;

public sealed class DurableMediaOperationRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;

    public DurableMediaOperationRepositoryTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_operations_{Guid.NewGuid():N}.db");
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
    public async Task EnsureAsync_IsIdempotentByIdempotencyKey()
    {
        var repo = new MediaOperationRepository(_db);
        var key = $"ingestion:file:C:/watch/movie.mkv:{Guid.NewGuid():N}";

        var first = await repo.EnsureAsync(NewOperation(key, sourcePath: "C:/watch/movie.mkv"));
        var second = await repo.EnsureAsync(NewOperation(key, sourcePath: "C:/watch/movie-renamed.mkv"));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("C:/watch/movie.mkv", second.SourcePath);
        Assert.Single(await repo.GetQueueAsync("ingestion", 10));
    }

    [Fact]
    public async Task GetByIdempotencyKeyAsync_ReturnsTrackedOperation()
    {
        var repo = new MediaOperationRepository(_db);
        var key = $"ingestion:file:C:/watch/restart.epub:{Guid.NewGuid():N}";
        var created = await repo.EnsureAsync(NewOperation(
            key,
            sourcePath: "C:/watch/restart.epub",
            batchId: Guid.NewGuid()));

        var found = await repo.GetByIdempotencyKeyAsync(key);
        var missing = await repo.GetByIdempotencyKeyAsync($"missing:{Guid.NewGuid():N}");

        Assert.NotNull(found);
        Assert.Equal(created.Id, found.Id);
        Assert.Equal(created.BatchId, found.BatchId);
        Assert.Null(missing);
    }

    [Fact]
    public async Task GetActiveBySourcePathAsync_ReturnsInFlightOperationAcrossFingerprintChanges()
    {
        var repo = new MediaOperationRepository(_db);
        var sourcePath = Path.GetFullPath("C:/watch/restart.epub");
        var batchId = Guid.NewGuid();
        var active = await repo.EnsureAsync(NewOperation(
            $"ingestion:file:{sourcePath}:100:1",
            sourcePath: sourcePath,
            batchId: batchId,
            status: MediaOperationStatus.Running));
        await repo.EnsureAsync(NewOperation(
            $"ingestion:file:{sourcePath}:100:0",
            sourcePath: sourcePath,
            status: MediaOperationStatus.Succeeded));

        var found = await repo.GetActiveBySourcePathAsync(sourcePath);

        Assert.NotNull(found);
        Assert.Equal(active.Id, found.Id);
        Assert.Equal(batchId, found.BatchId);
    }

    [Fact]
    public async Task GetActiveBySourcePathAsync_IgnoresOnlyTerminalOperations()
    {
        var repo = new MediaOperationRepository(_db);
        var sourcePath = Path.GetFullPath("C:/watch/completed.epub");
        await repo.EnsureAsync(NewOperation(
            $"ingestion:file:{sourcePath}:100:1",
            sourcePath: sourcePath,
            status: MediaOperationStatus.Succeeded));

        var found = await repo.GetActiveBySourcePathAsync(sourcePath);

        Assert.Null(found);
    }

    [Fact]
    public async Task GetLatestBySourcePathAsync_ReturnsTerminalOperationForTrackedPath()
    {
        var repo = new MediaOperationRepository(_db);
        var sourcePath = Path.GetFullPath("C:/watch/completed.epub");
        var terminal = await repo.EnsureAsync(NewOperation(
            $"ingestion:file:{sourcePath}:100:1",
            sourcePath: sourcePath,
            status: MediaOperationStatus.Succeeded));

        var found = await repo.GetLatestBySourcePathAsync(sourcePath);

        Assert.NotNull(found);
        Assert.Equal(terminal.Id, found.Id);
        Assert.Equal(MediaOperationStatus.Succeeded, found.Status);
    }

    [Fact]
    public async Task LeaseNextAsync_LeasesQueuedRowsAndIgnoresActiveLease()
    {
        var repo = new MediaOperationRepository(_db);
        await repo.EnsureAsync(NewOperation($"ingestion:file:{Guid.NewGuid():N}"));

        var leased = await repo.LeaseNextAsync(
            "worker-1",
            [MediaOperationType.IngestionFile],
            batchSize: 1,
            leaseDuration: TimeSpan.FromMinutes(5));

        var secondLease = await repo.LeaseNextAsync(
            "worker-2",
            [MediaOperationType.IngestionFile],
            batchSize: 1,
            leaseDuration: TimeSpan.FromMinutes(5));

        Assert.Single(leased);
        Assert.Equal(MediaOperationStatus.Leased, leased[0].Status);
        Assert.Equal("worker-1", leased[0].LeaseOwner);
        Assert.Empty(secondLease);
    }

    [Fact]
    public async Task EnsureAsync_StoresOperationIdsAsGuidBlobs()
    {
        var repo = new MediaOperationRepository(_db);
        var entityId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var operation = await repo.EnsureAsync(NewOperation(
            $"ingestion:file:{Guid.NewGuid():N}",
            entityId: entityId,
            batchId: batchId));

        using var conn = _db.CreateConnection();
        var storageTypes = conn.QuerySingle<(string IdType, string EntityIdType, string BatchIdType)>("""
            SELECT typeof(id) AS IdType,
                   typeof(entity_id) AS EntityIdType,
                   typeof(batch_id) AS BatchIdType
            FROM media_operations
            WHERE idempotency_key = @key;
            """, new { key = operation.IdempotencyKey });
        var byBatch = await repo.GetByBatchAsync(batchId);

        Assert.Equal("blob", storageTypes.IdType);
        Assert.Equal("blob", storageTypes.EntityIdType);
        Assert.Equal("blob", storageTypes.BatchIdType);
        Assert.Single(byBatch);
        Assert.Equal(entityId, byBatch[0].EntityId);
        Assert.Equal(batchId, byBatch[0].BatchId);
    }

    [Fact]
    public async Task TerminalMarkers_SetExpectedStatusAndCompletion()
    {
        var repo = new MediaOperationRepository(_db);
        var operation = await repo.EnsureAsync(NewOperation($"identity:{Guid.NewGuid():N}"));

        await repo.MarkSucceededAsync(operation.Id, "completed");
        var updated = await repo.GetByIdAsync(operation.Id);

        Assert.NotNull(updated);
        Assert.Equal(MediaOperationStatus.Succeeded, updated.Status);
        Assert.Equal(100, updated.ProgressPercent);
        Assert.NotNull(updated.CompletedAt);
    }

    [Fact]
    public async Task ReclaimStuckAsync_MarksExpiredRunningWorkInterrupted()
    {
        var repo = new MediaOperationRepository(_db);
        var operation = await repo.EnsureAsync(NewOperation(
            $"plugin:{Guid.NewGuid():N}",
            status: MediaOperationStatus.Running,
            heartbeatAt: DateTimeOffset.UtcNow.AddHours(-1)));

        var reclaimed = await repo.ReclaimStuckAsync(TimeSpan.FromMinutes(10));
        var updated = await repo.GetByIdAsync(operation.Id);

        Assert.Equal(1, reclaimed);
        Assert.NotNull(updated);
        Assert.Equal(MediaOperationStatus.Interrupted, updated.Status);
        Assert.Null(updated.LeaseOwner);
    }

    [Fact]
    public async Task UpdateStageAsync_RevivesInterruptedOperationAsRunning()
    {
        var repo = new MediaOperationRepository(_db);
        var operation = await repo.EnsureAsync(NewOperation(
            $"identity:wikidata:{Guid.NewGuid():N}",
            status: MediaOperationStatus.Interrupted));

        await repo.UpdateStageAsync(operation.Id, MediaOperationStage.ProviderLookup, 35);
        var updated = await repo.GetByIdAsync(operation.Id);

        Assert.NotNull(updated);
        Assert.Equal(MediaOperationStatus.Running, updated.Status);
        Assert.Equal(MediaOperationStage.ProviderLookup, updated.Stage);
        Assert.Equal(35, updated.ProgressPercent);
        Assert.NotNull(updated.StartedAt);
    }

    [Fact]
    public async Task MarkFailedRetryableAsync_IncrementsAttemptCount()
    {
        var repo = new MediaOperationRepository(_db);
        var operation = await repo.EnsureAsync(NewOperation($"ingestion:file:{Guid.NewGuid():N}"));
        var nextRetryAt = DateTimeOffset.UtcNow.AddMinutes(5);

        await repo.MarkFailedRetryableAsync(operation.Id, "locked", nextRetryAt);
        var updated = await repo.GetByIdAsync(operation.Id);

        Assert.NotNull(updated);
        Assert.Equal(MediaOperationStatus.RetryWaiting, updated.Status);
        Assert.Equal(1, updated.AttemptCount);
        Assert.Equal("locked", updated.LastError);
        Assert.NotNull(updated.NextRetryAt);
    }

    [Fact]
    public async Task EntityCapabilityState_UsesSingleKeyForNullSubKey()
    {
        var repo = new EntityCapabilityStateRepository(_db);
        var entityId = Guid.NewGuid();

        var first = await repo.EnsureAsync(NewCapability(entityId, subKey: null));
        var second = await repo.EnsureAsync(NewCapability(entityId, subKey: null));

        var states = await repo.GetByEntityAsync(entityId);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(states);
    }

    [Fact]
    public async Task EntityCapabilityState_StoresGuidColumnsAsBlobs()
    {
        var repo = new EntityCapabilityStateRepository(_db);
        var entityId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        await repo.EnsureAsync(NewCapability(entityId));
        await repo.MarkQueuedAsync(entityId, CapabilityId.IdentityWikidataBridge, null, operationId);

        using var conn = _db.CreateConnection();
        var storageTypes = conn.QuerySingle<(string IdType, string EntityIdType, string LastOperationIdType)>("""
            SELECT typeof(id) AS IdType,
                   typeof(entity_id) AS EntityIdType,
                   typeof(last_operation_id) AS LastOperationIdType
            FROM entity_capability_states
            WHERE entity_id = @entityId;
            """, new { entityId });
        var state = await repo.GetAsync(entityId, CapabilityId.IdentityWikidataBridge);

        Assert.Equal("blob", storageTypes.IdType);
        Assert.Equal("blob", storageTypes.EntityIdType);
        Assert.Equal("blob", storageTypes.LastOperationIdType);
        Assert.Equal(operationId, state!.LastOperationId);
    }

    [Fact]
    public async Task MediaOperationEvent_StoresGuidColumnsAsBlobs()
    {
        var repo = new MediaOperationEventRepository(_db);
        var operationId = Guid.NewGuid();
        var entityId = Guid.NewGuid();
        var batchId = Guid.NewGuid();

        await repo.AddAsync(new MediaOperationEvent
        {
            OperationId = operationId,
            EntityId = entityId,
            BatchId = batchId,
            EventType = "status_changed",
            NewStatus = MediaOperationStatus.Running,
        });

        using var conn = _db.CreateConnection();
        var storageTypes = conn.QuerySingle<(string IdType, string OperationIdType, string EntityIdType, string BatchIdType)>("""
            SELECT typeof(id) AS IdType,
                   typeof(operation_id) AS OperationIdType,
                   typeof(entity_id) AS EntityIdType,
                   typeof(batch_id) AS BatchIdType
            FROM media_operation_events
            WHERE operation_id = @operationId;
            """, new { operationId });
        var events = await repo.GetByOperationAsync(operationId);

        Assert.Equal("blob", storageTypes.IdType);
        Assert.Equal("blob", storageTypes.OperationIdType);
        Assert.Equal("blob", storageTypes.EntityIdType);
        Assert.Equal("blob", storageTypes.BatchIdType);
        Assert.Single(events);
        Assert.Equal(entityId, events[0].EntityId);
        Assert.Equal(batchId, events[0].BatchId);
    }

    [Fact]
    public async Task InvalidateForCapabilityVersionAsync_MarksOlderVersionStale()
    {
        var repo = new EntityCapabilityStateRepository(_db);
        var entityId = Guid.NewGuid();
        await repo.EnsureAsync(NewCapability(entityId, version: "1.0"));

        await repo.InvalidateForCapabilityVersionAsync(CapabilityId.IdentityWikidataBridge, "2.0");
        var updated = await repo.GetAsync(entityId, CapabilityId.IdentityWikidataBridge);

        Assert.NotNull(updated);
        Assert.Equal(EntityCapabilityStatus.Stale, updated.Status);
        Assert.True(updated.Stale);
        Assert.True(updated.NeedsRerun);
    }

    [Fact]
    public async Task EnsureAndNoResult_CompleteVersionedCapabilityRerun()
    {
        var repo = new EntityCapabilityStateRepository(_db);
        var entityId = Guid.NewGuid();
        await repo.EnsureAsync(NewCapability(entityId, version: "1.0"));
        await repo.InvalidateForCapabilityVersionAsync(CapabilityId.IdentityWikidataBridge, "2.0");

        await repo.EnsureAsync(NewCapability(entityId, version: "2.0"));
        await repo.MarkNoResultAsync(entityId, CapabilityId.IdentityWikidataBridge, null, "No statement");
        var updated = await repo.GetAsync(entityId, CapabilityId.IdentityWikidataBridge);

        Assert.NotNull(updated);
        Assert.Equal("2.0", updated.CapabilityVersion);
        Assert.Equal(EntityCapabilityStatus.NoResult, updated.Status);
        Assert.False(updated.Stale);
        Assert.False(updated.NeedsRerun);
    }

    private static MediaOperation NewOperation(
        string idempotencyKey,
        string? sourcePath = null,
        string status = MediaOperationStatus.Queued,
        DateTimeOffset? heartbeatAt = null,
        Guid? entityId = null,
        Guid? batchId = null) => new()
        {
            OperationType = MediaOperationType.IngestionFile,
            OperationKind = MediaOperationKind.Ingestion,
            EntityId = entityId,
            BatchId = batchId,
            SourcePath = sourcePath,
            Status = status,
            Stage = MediaOperationStage.Queued,
            QueueName = "ingestion",
            Priority = 100,
            PositionKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            HeartbeatAt = heartbeatAt,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            IdempotencyKey = idempotencyKey
        };

    private static EntityCapabilityState NewCapability(
        Guid entityId,
        string? subKey = null,
        string version = "1.0") => new()
        {
            EntityId = entityId,
            EntityKind = "asset",
            CapabilityId = CapabilityId.IdentityWikidataBridge,
            CapabilityKind = MediaOperationKind.Identity,
            CapabilityVersion = version,
            SubKey = subKey,
            Status = EntityCapabilityStatus.Succeeded,
            Requiredness = CapabilityRequiredness.Optional,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
}
