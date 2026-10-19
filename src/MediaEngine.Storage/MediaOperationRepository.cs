using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Jobs;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

public sealed class MediaOperationRepository : IMediaOperationRepository
{
    private static readonly string[] LeaseableStatuses =
    [
        MediaOperationStatus.Queued,
        MediaOperationStatus.RetryWaiting,
        MediaOperationStatus.Interrupted
    ];

    private static readonly string[] TerminalStatuses =
    [
        MediaOperationStatus.Succeeded,
        MediaOperationStatus.NoResult,
        MediaOperationStatus.MissingConfirmed,
        MediaOperationStatus.NotApplicable,
        MediaOperationStatus.Blocked,
        MediaOperationStatus.FailedTerminal,
        MediaOperationStatus.DeadLettered,
        MediaOperationStatus.Cancelled,
        MediaOperationStatus.Skipped
    ];

    private readonly IDatabaseConnection _db;

    public MediaOperationRepository(IDatabaseConnection db) => _db = db;

    public Task<MediaOperation> EnsureAsync(MediaOperation operation, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(operation.IdempotencyKey))
        {
            throw new ArgumentException("Media operation idempotency key is required.", nameof(operation));
        }

        var now = DateTimeOffset.UtcNow;
        var id = operation.Id == Guid.Empty ? Guid.NewGuid() : operation.Id;
        var createdAt = operation.CreatedAt == default ? now : operation.CreatedAt;
        var updatedAt = operation.UpdatedAt == default ? now : operation.UpdatedAt;
        var positionKey = operation.PositionKey == 0 ? now.ToUnixTimeMilliseconds() : operation.PositionKey;

        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT OR IGNORE INTO media_operations
                (id, operation_type, operation_kind, entity_id, entity_kind, batch_id,
                 source_path, content_hash, capability_id, capability_version, sub_key,
                 plugin_id, plugin_version, provider_id, model_id, status, stage, priority,
                 queue_name, position_key, attempt_count, poison_attempt_count, lease_owner, lease_expires_at,
                 heartbeat_at, next_retry_at, progress_percent, items_total, items_completed,
                 items_failed, result_summary, last_error, last_outcome_category, missing_reason, created_at,
                 started_at, updated_at, completed_at, idempotency_key)
            VALUES
                (@Id, @OperationType, @OperationKind, @EntityId, @EntityKind, @BatchId,
                 @SourcePath, @ContentHash, @CapabilityId, @CapabilityVersion, @SubKey,
                 @PluginId, @PluginVersion, @ProviderId, @ModelId, @Status, @Stage, @Priority,
                 @QueueName, @PositionKey, @AttemptCount, @PoisonAttemptCount, @LeaseOwner, @LeaseExpiresAt,
                 @HeartbeatAt, @NextRetryAt, @ProgressPercent, @ItemsTotal, @ItemsCompleted,
                 @ItemsFailed, @ResultSummary, @LastError, @LastOutcomeCategory, @MissingReason, @CreatedAt,
                 @StartedAt, @UpdatedAt, @CompletedAt, @IdempotencyKey);
            """,
            ToParams(operation, id, createdAt, updatedAt, positionKey));

        conn.Execute("""
            UPDATE media_operations
            SET batch_id = COALESCE(batch_id, @BatchId),
                updated_at = CASE WHEN @BatchId IS NULL OR batch_id IS NOT NULL THEN updated_at ELSE @UpdatedAt END
            WHERE idempotency_key = @IdempotencyKey;
            """,
            new
            {
                operation.BatchId,
                UpdatedAt = updatedAt.ToString("O"),
                operation.IdempotencyKey
            });

        var row = conn.QueryFirst<MediaOperationRow>(
            SelectSql + " WHERE idempotency_key = @idempotencyKey LIMIT 1;",
            new { idempotencyKey = operation.IdempotencyKey });
        return Task.FromResult(Map(row));
    }

    public Task<MediaOperation?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var row = conn.QueryFirstOrDefault<MediaOperationRow>(
            SelectSql + " WHERE id = @id LIMIT 1;",
            new { id });
        return Task.FromResult(row is null ? null : Map(row));
    }

    public Task<MediaOperation?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Task.FromResult<MediaOperation?>(null);
        }

        using var conn = _db.CreateConnection();
        var row = conn.QueryFirstOrDefault<MediaOperationRow>(
            SelectSql + " WHERE idempotency_key = @idempotencyKey LIMIT 1;",
            new { idempotencyKey });
        return Task.FromResult(row is null ? null : Map(row));
    }

    public Task<MediaOperation?> GetActiveBySourcePathAsync(string sourcePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return Task.FromResult<MediaOperation?>(null);
        }

        using var conn = _db.CreateConnection();
        var row = conn.QueryFirstOrDefault<MediaOperationRow>(
            SelectSql + """
             WHERE operation_type = @operationType
               AND source_path = @sourcePath
               AND status NOT IN @terminalStatuses
             ORDER BY updated_at DESC, created_at DESC
             LIMIT 1;
            """,
            new
            {
                operationType = MediaOperationType.IngestionFile,
                sourcePath,
                terminalStatuses = TerminalStatuses
            });
        return Task.FromResult(row is null ? null : Map(row));
    }

    public Task<MediaOperation?> GetLatestBySourcePathAsync(string sourcePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return Task.FromResult<MediaOperation?>(null);
        }

        using var conn = _db.CreateConnection();
        var row = conn.QueryFirstOrDefault<MediaOperationRow>(
            SelectSql + """
             WHERE operation_type = @operationType
               AND source_path = @sourcePath
             ORDER BY updated_at DESC, created_at DESC
             LIMIT 1;
            """,
            new
            {
                operationType = MediaOperationType.IngestionFile,
                sourcePath
            });
        return Task.FromResult(row is null ? null : Map(row));
    }

    public Task<IReadOnlyList<MediaOperation>> GetByEntityAsync(Guid entityId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<MediaOperationRow>(
            SelectSql + " WHERE entity_id = @entityId ORDER BY created_at DESC;",
            new { entityId });
        return Task.FromResult<IReadOnlyList<MediaOperation>>(rows.Select(Map).ToList());
    }

    public Task<IReadOnlyList<MediaOperation>> GetByBatchAsync(Guid batchId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<MediaOperationRow>(
            SelectSql + " WHERE batch_id = @batchId ORDER BY priority ASC, position_key ASC;",
            new { batchId });
        return Task.FromResult<IReadOnlyList<MediaOperation>>(rows.Select(Map).ToList());
    }

    public Task<IReadOnlyList<MediaOperation>> GetByPluginAsync(string pluginId, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<MediaOperationRow>(
            SelectSql + """
             WHERE plugin_id = @pluginId
             ORDER BY created_at DESC
             LIMIT @limit;
            """,
            new { pluginId, limit = Math.Clamp(limit, 1, 1000) });
        return Task.FromResult<IReadOnlyList<MediaOperation>>(rows.Select(Map).ToList());
    }

    public Task<IReadOnlyList<MediaOperation>> GetQueueAsync(string? queueName, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<MediaOperationRow>(
            SelectSql + """
             WHERE (@queueName IS NULL OR queue_name = @queueName)
             ORDER BY CASE
                        WHEN status IN ('queued','retry_waiting','interrupted','leased','running','paused') THEN 0
                        ELSE 1
                      END,
                      priority ASC,
                      position_key ASC
             LIMIT @limit;
            """,
            new { queueName, limit = Math.Clamp(limit, 1, 1000) });
        return Task.FromResult<IReadOnlyList<MediaOperation>>(rows.Select(Map).ToList());
    }

    public Task<IReadOnlyList<MediaOperation>> LeaseNextAsync(
        string workerName,
        IReadOnlyList<string> operationTypes,
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var typeClause = operationTypes.Count > 0 ? "AND operation_type IN @OperationTypes" : "";
        var now = DateTimeOffset.UtcNow;
        var leaseExpiresAt = now.Add(leaseDuration);
        var sql = $"""
            UPDATE media_operations
            SET    status = 'leased',
                   lease_owner = @workerName,
                   lease_expires_at = @leaseExpiresAt,
                   heartbeat_at = @now,
                   started_at = COALESCE(started_at, @now),
                   updated_at = @now,
                   attempt_count = attempt_count + 1
            WHERE id IN (
                SELECT id
                FROM   media_operations
                WHERE  status IN @LeaseableStatuses
                  AND  (next_retry_at IS NULL OR next_retry_at <= @now)
                  AND  (lease_expires_at IS NULL OR lease_expires_at <= @now)
                  {typeClause}
                ORDER BY priority ASC, position_key ASC
                LIMIT @batchSize
            )
            RETURNING {ReturningColumns};
            """;

        using var conn = _db.CreateConnection();
        var rows = conn.Query<MediaOperationRow>(sql, new
        {
            workerName,
            leaseExpiresAt = leaseExpiresAt.ToString("O"),
            now = now.ToString("O"),
            LeaseableStatuses,
            OperationTypes = operationTypes,
            batchSize = Math.Clamp(batchSize, 1, 100)
        });
        return Task.FromResult<IReadOnlyList<MediaOperation>>(rows.Select(Map).ToList());
    }

    public Task UpdateStageAsync(Guid id, string stage, int? progressPercent = null, CancellationToken ct = default)
        => Execute("""
            UPDATE media_operations
            SET stage = @stage,
                status = CASE WHEN status IN ('pending','queued','retry_waiting','interrupted','leased') THEN 'running' ELSE status END,
                progress_percent = COALESCE(@progressPercent, progress_percent),
                heartbeat_at = @now,
                started_at = COALESCE(started_at, @now),
                updated_at = @now
            WHERE id = @id;
            """, id, ct, new { stage, progressPercent = ClampPercent(progressPercent) });

    public Task AttachEntityAsync(Guid id, Guid entityId, string entityKind, CancellationToken ct = default)
        => Execute("""
            UPDATE media_operations
            SET entity_id = @entityId,
                entity_kind = @entityKind
            WHERE id = @id;
            """, id, ct, new { entityId, entityKind });

    public Task HeartbeatAsync(Guid id, int? progressPercent = null, CancellationToken ct = default)
        => Execute("""
            UPDATE media_operations
            SET heartbeat_at = @now,
                progress_percent = COALESCE(@progressPercent, progress_percent),
                updated_at = @now
            WHERE id = @id;
            """, id, ct, new { progressPercent = ClampPercent(progressPercent) });

    public Task MarkSucceededAsync(Guid id, string? resultSummary = null, CancellationToken ct = default)
        => MarkTerminalAsync(id, MediaOperationStatus.Succeeded, MediaOperationStage.Completed, resultSummary, null, null, ct);

    public Task MarkNoResultAsync(Guid id, string? missingReason = null, string? resultSummary = null, CancellationToken ct = default)
        => MarkTerminalAsync(id, MediaOperationStatus.NoResult, MediaOperationStage.NoResult, resultSummary, null, missingReason, ct);

    public Task MarkMissingConfirmedAsync(Guid id, string? missingReason = null, CancellationToken ct = default)
        => MarkTerminalAsync(id, MediaOperationStatus.MissingConfirmed, MediaOperationStage.Failed, null, null, missingReason, ct);

    public Task MarkBlockedAsync(Guid id, string reason, CancellationToken ct = default)
        => MarkTerminalAsync(id, MediaOperationStatus.Blocked, MediaOperationStage.Blocked, null, reason, reason, ct);

    public Task MarkFailedRetryableAsync(Guid id, string error, DateTimeOffset nextRetryAt, CancellationToken ct = default)
        => MarkFailedRetryableForOutcomeAsync(
            id,
            error,
            nextRetryAt,
            BackgroundJobOutcomeCategory.ContentFailure,
            ct);

    public Task MarkFailedRetryableForOutcomeAsync(
        Guid id,
        string error,
        DateTimeOffset nextRetryAt,
        BackgroundJobOutcomeCategory category,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var policy = BackgroundJobOutcomePolicy.For(category);
        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE media_operations
            SET status = 'retry_waiting',
                stage = 'failed',
                last_error = @error,
                next_retry_at = @nextRetryAt,
                attempt_count = attempt_count + 1,
                poison_attempt_count = poison_attempt_count + @poisonIncrement,
                last_outcome_category = @category,
                lease_owner = NULL,
                lease_expires_at = NULL,
                updated_at = @now
            WHERE id = @id;
            """,
            new
            {
                id,
                error,
                nextRetryAt = nextRetryAt.ToString("O"),
                poisonIncrement = policy.ConsumesPoisonBudget ? 1 : 0,
                category = category.ToString(),
                now = DateTimeOffset.UtcNow.ToString("O"),
            });
        return Task.CompletedTask;
    }

    public Task MarkFailedTerminalAsync(Guid id, string error, CancellationToken ct = default)
        => MarkTerminalAsync(id, MediaOperationStatus.FailedTerminal, MediaOperationStage.Failed, null, error, null, ct);

    public Task MarkFailedTerminalForOutcomeAsync(
        Guid id,
        string error,
        BackgroundJobOutcomeCategory category,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var policy = BackgroundJobOutcomePolicy.For(category);
        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE media_operations
            SET status = 'failed_terminal',
                stage = 'failed',
                last_error = @error,
                last_outcome_category = @category,
                poison_attempt_count = poison_attempt_count + @poisonIncrement,
                lease_owner = NULL,
                lease_expires_at = NULL,
                next_retry_at = NULL,
                completed_at = @now,
                updated_at = @now
            WHERE id = @id;
            """, new
        {
            id,
            error,
            category = category.ToString(),
            poisonIncrement = policy.ConsumesPoisonBudget ? 1 : 0,
            now = DateTimeOffset.UtcNow.ToString("O"),
        });
        return Task.CompletedTask;
    }

    public Task MarkDeadLetteredAsync(Guid id, string error, CancellationToken ct = default)
        => MarkTerminalAsync(id, MediaOperationStatus.DeadLettered, MediaOperationStage.Failed, null, error, null, ct);

    public Task MarkCancelledAsync(Guid id, string? reason = null, CancellationToken ct = default)
        => MarkTerminalAsync(id, MediaOperationStatus.Cancelled, null, reason, reason, null, ct);

    public Task MarkInterruptedAsync(Guid id, string? reason = null, CancellationToken ct = default)
        => MarkTerminalAsync(id, MediaOperationStatus.Interrupted, null, reason, reason, null, ct);

    public Task RequeueAsync(Guid id, CancellationToken ct = default)
        => Execute("""
            UPDATE media_operations
            SET status = 'queued',
                lease_owner = NULL,
                lease_expires_at = NULL,
                next_retry_at = NULL,
                completed_at = NULL,
                last_error = NULL,
                missing_reason = NULL,
                updated_at = @now
            WHERE id = @id;
            """, id, ct);

    public Task RecordProgressAsync(
        Guid id,
        int itemsTotal,
        int itemsCompleted,
        int itemsFailed,
        int progressPercent,
        TimeSpan? extendLease = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE media_operations
            SET items_total = @itemsTotal,
                items_completed = @itemsCompleted,
                items_failed = @itemsFailed,
                progress_percent = @progressPercent,
                heartbeat_at = @now,
                lease_expires_at = CASE WHEN @leaseExpiresAt IS NOT NULL AND status IN ('leased','running')
                                        THEN @leaseExpiresAt ELSE lease_expires_at END,
                updated_at = @now
            WHERE id = @id;
            """,
            new
            {
                id,
                itemsTotal = Math.Max(0, itemsTotal),
                itemsCompleted = Math.Max(0, itemsCompleted),
                itemsFailed = Math.Max(0, itemsFailed),
                progressPercent = ClampPercent(progressPercent) ?? 0,
                now = now.ToString("O"),
                leaseExpiresAt = extendLease is { } lease ? now.Add(lease).ToString("O") : null,
            });
        return Task.CompletedTask;
    }

    public Task<bool> PauseAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var changed = conn.Execute("""
            UPDATE media_operations
            SET status = 'paused',
                stage = 'paused',
                lease_owner = NULL,
                lease_expires_at = NULL,
                updated_at = @now
            WHERE id = @id
              AND status IN ('pending','queued','leased','running','retry_waiting','interrupted');
            """, new { id, now = DateTimeOffset.UtcNow.ToString("O") });
        return Task.FromResult(changed > 0);
    }

    public Task<bool> InterruptIfRunningAsync(Guid id, string? reason = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var changed = conn.Execute("""
            UPDATE media_operations
            SET status = 'interrupted',
                lease_owner = NULL,
                lease_expires_at = NULL,
                last_error = COALESCE(@reason, last_error),
                updated_at = @now
            WHERE id = @id
              AND status IN ('leased','running');
            """, new { id, reason, now = DateTimeOffset.UtcNow.ToString("O") });
        return Task.FromResult(changed > 0);
    }

    public Task<bool> ResumePausedAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var changed = conn.Execute("""
            UPDATE media_operations
            SET status = 'queued',
                stage = 'queued',
                lease_owner = NULL,
                lease_expires_at = NULL,
                next_retry_at = NULL,
                updated_at = @now
            WHERE id = @id
              AND status = 'paused';
            """, new { id, now = DateTimeOffset.UtcNow.ToString("O") });
        return Task.FromResult(changed > 0);
    }

    public Task<int> ReclaimStuckAsync(TimeSpan stuckThreshold, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.Subtract(stuckThreshold);
        using var conn = _db.CreateConnection();
        var reclaimed = conn.Execute("""
            UPDATE media_operations
            SET status = 'interrupted',
                lease_owner = NULL,
                lease_expires_at = NULL,
                last_error = COALESCE(last_error, 'Interrupted operation reclaimed after startup.'),
                updated_at = @now
            WHERE status IN ('leased','running')
              AND (
                    lease_expires_at IS NULL
                 OR lease_expires_at <= @now
                 OR heartbeat_at IS NULL
                 OR heartbeat_at <= @cutoff
              );
            """,
            new { now = now.ToString("O"), cutoff = cutoff.ToString("O") });
        return Task.FromResult(reclaimed);
    }

    public Task<IReadOnlyList<(Guid Id, string SourcePath)>> ListWaitingIngestionFilesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<WaitingIngestionFileRow>("""
            SELECT id AS Id, source_path AS SourcePath
            FROM media_operations
            WHERE operation_type = 'ingestion.file'
              AND status IN ('pending', 'queued')
              AND source_path IS NOT NULL;
            """);
        IReadOnlyList<(Guid Id, string SourcePath)> result = rows.Select(row => (row.Id, row.SourcePath)).ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyDictionary<string, int>> GetSummaryAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query("""
            SELECT status AS Status, COUNT(*) AS Count
            FROM media_operations
            GROUP BY status;
            """);
        IReadOnlyDictionary<string, int> summary = rows.ToDictionary(
            r => (string)r.Status,
            r => (int)r.Count,
            StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(summary);
    }

    private Task MarkTerminalAsync(
        Guid id,
        string status,
        string? stage,
        string? resultSummary,
        string? lastError,
        string? missingReason,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE media_operations
            SET status = @status,
                stage = COALESCE(@stage, stage),
                progress_percent = CASE WHEN @status = 'succeeded' THEN 100 ELSE progress_percent END,
                result_summary = @resultSummary,
                last_error = @lastError,
                missing_reason = @missingReason,
                lease_owner = NULL,
                lease_expires_at = NULL,
                heartbeat_at = @now,
                updated_at = @now,
                completed_at = @now
            WHERE id = @id;
            """,
            new { id, status, stage, resultSummary, lastError, missingReason, now = DateTimeOffset.UtcNow.ToString("O") });
        return Task.CompletedTask;
    }

    private Task Execute(string sql, Guid id, CancellationToken ct, object? extra = null)
    {
        ct.ThrowIfCancellationRequested();
        var args = extra is null
            ? new DynamicParameters()
            : new DynamicParameters(extra);
        args.Add("id", id);
        args.Add("now", DateTimeOffset.UtcNow.ToString("O"));

        using var conn = _db.CreateConnection();
        conn.Execute(sql, args);
        return Task.CompletedTask;
    }

    private static object ToParams(MediaOperation operation, Guid id, DateTimeOffset createdAt, DateTimeOffset updatedAt, long positionKey) => new
    {
        Id = id,
        operation.OperationType,
        operation.OperationKind,
        operation.EntityId,
        operation.EntityKind,
        operation.BatchId,
        operation.SourcePath,
        operation.ContentHash,
        operation.CapabilityId,
        operation.CapabilityVersion,
        operation.SubKey,
        operation.PluginId,
        operation.PluginVersion,
        operation.ProviderId,
        operation.ModelId,
        operation.Status,
        operation.Stage,
        operation.Priority,
        operation.QueueName,
        PositionKey = positionKey,
        operation.AttemptCount,
        operation.PoisonAttemptCount,
        operation.LeaseOwner,
        LeaseExpiresAt = operation.LeaseExpiresAt?.ToString("O"),
        HeartbeatAt = operation.HeartbeatAt?.ToString("O"),
        NextRetryAt = operation.NextRetryAt?.ToString("O"),
        operation.ProgressPercent,
        operation.ItemsTotal,
        operation.ItemsCompleted,
        operation.ItemsFailed,
        operation.ResultSummary,
        operation.LastError,
        operation.LastOutcomeCategory,
        operation.MissingReason,
        CreatedAt = createdAt.ToString("O"),
        StartedAt = operation.StartedAt?.ToString("O"),
        UpdatedAt = updatedAt.ToString("O"),
        CompletedAt = operation.CompletedAt?.ToString("O"),
        operation.IdempotencyKey
    };

    private static int? ClampPercent(int? progressPercent)
        => progressPercent is null ? null : Math.Clamp(progressPercent.Value, 0, 100);

    private const string ReturningColumns = """
        id AS Id,
        operation_type AS OperationType,
        operation_kind AS OperationKind,
        entity_id AS EntityId,
        entity_kind AS EntityKind,
        batch_id AS BatchId,
        source_path AS SourcePath,
        content_hash AS ContentHash,
        capability_id AS CapabilityId,
        capability_version AS CapabilityVersion,
        sub_key AS SubKey,
        plugin_id AS PluginId,
        plugin_version AS PluginVersion,
        provider_id AS ProviderId,
        model_id AS ModelId,
        status AS Status,
        stage AS Stage,
        priority AS Priority,
        queue_name AS QueueName,
        position_key AS PositionKey,
        attempt_count AS AttemptCount,
        poison_attempt_count AS PoisonAttemptCount,
        lease_owner AS LeaseOwner,
        lease_expires_at AS LeaseExpiresAt,
        heartbeat_at AS HeartbeatAt,
        next_retry_at AS NextRetryAt,
        progress_percent AS ProgressPercent,
        items_total AS ItemsTotal,
        items_completed AS ItemsCompleted,
        items_failed AS ItemsFailed,
        result_summary AS ResultSummary,
        last_error AS LastError,
        last_outcome_category AS LastOutcomeCategory,
        missing_reason AS MissingReason,
        created_at AS CreatedAt,
        started_at AS StartedAt,
        updated_at AS UpdatedAt,
        completed_at AS CompletedAt,
        idempotency_key AS IdempotencyKey
        """;

    private const string SelectSql = $"SELECT {ReturningColumns} FROM media_operations";

    private sealed class WaitingIngestionFileRow
    {
        public Guid Id { get; set; }
        public string SourcePath { get; set; } = "";
    }

    private sealed class MediaOperationRow
    {
        public Guid Id { get; set; }
        public string OperationType { get; set; } = "";
        public string OperationKind { get; set; } = "";
        public Guid? EntityId { get; set; }
        public string? EntityKind { get; set; }
        public Guid? BatchId { get; set; }
        public string? SourcePath { get; set; }
        public string? ContentHash { get; set; }
        public string? CapabilityId { get; set; }
        public string? CapabilityVersion { get; set; }
        public string? SubKey { get; set; }
        public string? PluginId { get; set; }
        public string? PluginVersion { get; set; }
        public string? ProviderId { get; set; }
        public string? ModelId { get; set; }
        public string Status { get; set; } = "";
        public string? Stage { get; set; }
        public int Priority { get; set; }
        public string QueueName { get; set; } = "";
        public long PositionKey { get; set; }
        public int AttemptCount { get; set; }
        public int PoisonAttemptCount { get; set; }
        public string? LeaseOwner { get; set; }
        public string? LeaseExpiresAt { get; set; }
        public string? HeartbeatAt { get; set; }
        public string? NextRetryAt { get; set; }
        public int ProgressPercent { get; set; }
        public int ItemsTotal { get; set; }
        public int ItemsCompleted { get; set; }
        public int ItemsFailed { get; set; }
        public string? ResultSummary { get; set; }
        public string? LastError { get; set; }
        public string? LastOutcomeCategory { get; set; }
        public string? MissingReason { get; set; }
        public string CreatedAt { get; set; } = "";
        public string? StartedAt { get; set; }
        public string UpdatedAt { get; set; } = "";
        public string? CompletedAt { get; set; }
        public string IdempotencyKey { get; set; } = "";
    }

    private static MediaOperation Map(MediaOperationRow row) => new()
    {
        Id = row.Id,
        OperationType = row.OperationType,
        OperationKind = row.OperationKind,
        EntityId = row.EntityId,
        EntityKind = row.EntityKind,
        BatchId = row.BatchId,
        SourcePath = row.SourcePath,
        ContentHash = row.ContentHash,
        CapabilityId = row.CapabilityId,
        CapabilityVersion = row.CapabilityVersion,
        SubKey = row.SubKey,
        PluginId = row.PluginId,
        PluginVersion = row.PluginVersion,
        ProviderId = row.ProviderId,
        ModelId = row.ModelId,
        Status = row.Status,
        Stage = row.Stage,
        Priority = row.Priority,
        QueueName = row.QueueName,
        PositionKey = row.PositionKey,
        AttemptCount = row.AttemptCount,
        PoisonAttemptCount = row.PoisonAttemptCount,
        LeaseOwner = row.LeaseOwner,
        LeaseExpiresAt = ParseDate(row.LeaseExpiresAt),
        HeartbeatAt = ParseDate(row.HeartbeatAt),
        NextRetryAt = ParseDate(row.NextRetryAt),
        ProgressPercent = row.ProgressPercent,
        ItemsTotal = row.ItemsTotal,
        ItemsCompleted = row.ItemsCompleted,
        ItemsFailed = row.ItemsFailed,
        ResultSummary = row.ResultSummary,
        LastError = row.LastError,
        LastOutcomeCategory = row.LastOutcomeCategory,
        MissingReason = row.MissingReason,
        CreatedAt = DateTimeOffset.Parse(row.CreatedAt),
        StartedAt = ParseDate(row.StartedAt),
        UpdatedAt = DateTimeOffset.Parse(row.UpdatedAt),
        CompletedAt = ParseDate(row.CompletedAt),
        IdempotencyKey = row.IdempotencyKey
    };

    private static DateTimeOffset? ParseDate(string? value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
}
