using Dapper;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="IReviewQueueRepository"/>.
///
/// All methods are async-safe and non-blocking.
/// </summary>
public sealed class ReviewQueueRepository : IReviewQueueRepository
{
    private readonly IDatabaseConnection _db;

    public ReviewQueueRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc/>
    public Task<Guid> InsertAsync(ReviewQueueEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            var parameters = new
            {
                id = entry.Id,
                entityId = entry.EntityId,
                entityType = entry.EntityType,
                trigger = entry.Trigger,
                status = entry.Status,
                proposedCollectionId = entry.ProposedCollectionId,
                confidence = entry.ConfidenceScore,
                candidates = entry.CandidatesJson,
                detail = entry.Detail,
                createdAt = entry.CreatedAt.ToString("O"),
                resolvedAt = entry.ResolvedAt.HasValue ? (object)entry.ResolvedAt.Value.ToString("O") : null,
                resolvedBy = entry.ResolvedBy,
                sourceOperationId = entry.SourceOperationId,
                sourceCapabilityId = entry.SourceCapabilityId,
                sourceCapabilitySubKey = entry.SourceCapabilitySubKey,
                reviewReadyAt = entry.ReviewReadyAt?.ToString("O"),
                automationCompletedAt = entry.AutomationCompletedAt?.ToString("O"),
                pending = ReviewStatus.Pending,
            };

            var existingId = conn.QueryFirstOrDefault<Guid?>("""
                SELECT id
                FROM review_queue
                WHERE entity_id = @entityId
                  AND trigger = @trigger
                  AND status = @pending
                ORDER BY created_at ASC
                LIMIT 1;
                """, parameters, tx);

            if (existingId.HasValue)
            {
                conn.Execute("""
                    UPDATE review_queue
                    SET entity_type = @entityType,
                        proposed_collection_id = COALESCE(@proposedCollectionId, proposed_collection_id),
                        confidence_score = COALESCE(@confidence, confidence_score),
                        candidates_json = COALESCE(@candidates, candidates_json),
                        detail = COALESCE(@detail, detail),
                        source_operation_id = COALESCE(@sourceOperationId, source_operation_id),
                        source_capability_id = COALESCE(@sourceCapabilityId, source_capability_id),
                        source_capability_sub_key = COALESCE(@sourceCapabilitySubKey, source_capability_sub_key),
                        review_ready_at = COALESCE(@reviewReadyAt, review_ready_at),
                        automation_completed_at = COALESCE(@automationCompletedAt, automation_completed_at)
                    WHERE id = @existingId;
                    """, new
                {
                    existingId = existingId.Value,
                    parameters.entityType,
                    parameters.proposedCollectionId,
                    parameters.confidence,
                    parameters.candidates,
                    parameters.detail,
                    parameters.sourceOperationId,
                    parameters.sourceCapabilityId,
                    parameters.sourceCapabilitySubKey,
                    parameters.reviewReadyAt,
                    parameters.automationCompletedAt,
                }, tx);

                return existingId.Value;
            }

            conn.Execute("""
                INSERT OR IGNORE INTO review_queue
                    (id, entity_id, entity_type, trigger, status,
                     proposed_collection_id, confidence_score, candidates_json,
                     detail, created_at, resolved_at, resolved_by,
                     source_operation_id, source_capability_id, source_capability_sub_key,
                     review_ready_at, automation_completed_at)
                VALUES
                    (@id, @entityId, @entityType, @trigger, @status,
                     @proposedCollectionId, @confidence, @candidates,
                     @detail, @createdAt, @resolvedAt, @resolvedBy,
                     @sourceOperationId, @sourceCapabilityId, @sourceCapabilitySubKey,
                     @reviewReadyAt, @automationCompletedAt)
                """, parameters, tx);

            var inserted = conn.ExecuteScalar<long>("SELECT changes();", transaction: tx) > 0;
            if (inserted)
            {
                return entry.Id;
            }

            existingId = conn.QueryFirstOrDefault<Guid?>("""
                SELECT id
                FROM review_queue
                WHERE entity_id = @entityId
                  AND trigger = @trigger
                  AND status = @pending
                ORDER BY created_at ASC
                LIMIT 1;
                """, parameters, tx);

            return existingId ?? entry.Id;
        }, ct);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ReviewQueueEntry>> GetPendingAsync(
        int limit = 50,
        CancellationToken ct = default)
    {
        using var conn = _db.CreateConnection();
        var rows = conn.Query<ReviewQueueRow>("""
            SELECT id, entity_id AS EntityId, entity_type AS EntityType, trigger, status,
                   proposed_collection_id AS ProposedCollectionId, confidence_score AS ConfidenceScore,
                   candidates_json AS CandidatesJson, detail, created_at AS CreatedAt,
                   resolved_at AS ResolvedAt, resolved_by AS ResolvedBy,
                   source_operation_id AS SourceOperationId,
                   source_capability_id AS SourceCapabilityId,
                   source_capability_sub_key AS SourceCapabilitySubKey,
                   review_ready_at AS ReviewReadyAt,
                   automation_completed_at AS AutomationCompletedAt
            FROM   review_queue
            WHERE  status = @status
              AND  review_ready_at IS NOT NULL
            ORDER BY created_at DESC
            LIMIT  @limit
            """, new { status = ReviewStatus.Pending, limit }).AsList();

        return Task.FromResult<IReadOnlyList<ReviewQueueEntry>>(rows.Select(MapRow).ToList());
    }

    /// <inheritdoc/>
    public Task<ReviewQueueEntry?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = _db.CreateConnection();
        var row = conn.QueryFirstOrDefault<ReviewQueueRow>("""
            SELECT id, entity_id AS EntityId, entity_type AS EntityType, trigger, status,
                   proposed_collection_id AS ProposedCollectionId, confidence_score AS ConfidenceScore,
                   candidates_json AS CandidatesJson, detail, created_at AS CreatedAt,
                   resolved_at AS ResolvedAt, resolved_by AS ResolvedBy,
                   source_operation_id AS SourceOperationId,
                   source_capability_id AS SourceCapabilityId,
                   source_capability_sub_key AS SourceCapabilitySubKey,
                   review_ready_at AS ReviewReadyAt,
                   automation_completed_at AS AutomationCompletedAt
            FROM   review_queue
            WHERE  id = @id
            """, new { id });

        return Task.FromResult(row is null ? null : (ReviewQueueEntry?)MapRow(row));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ReviewQueueEntry>> GetByEntityAsync(
        Guid entityId,
        CancellationToken ct = default)
    {
        using var conn = _db.CreateConnection();
        var rows = conn.Query<ReviewQueueRow>("""
            SELECT id, entity_id AS EntityId, entity_type AS EntityType, trigger, status,
                   proposed_collection_id AS ProposedCollectionId, confidence_score AS ConfidenceScore,
                   candidates_json AS CandidatesJson, detail, created_at AS CreatedAt,
                   resolved_at AS ResolvedAt, resolved_by AS ResolvedBy,
                   source_operation_id AS SourceOperationId,
                   source_capability_id AS SourceCapabilityId,
                   source_capability_sub_key AS SourceCapabilitySubKey,
                   review_ready_at AS ReviewReadyAt,
                   automation_completed_at AS AutomationCompletedAt
            FROM   review_queue
            WHERE  entity_id = @entityId
            ORDER BY created_at DESC
            """, new { entityId }).AsList();

        return Task.FromResult<IReadOnlyList<ReviewQueueEntry>>(rows.Select(MapRow).ToList());
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ReviewQueueEntry>> GetPendingByEntityAsync(
        Guid entityId,
        CancellationToken ct = default)
    {
        using var conn = _db.CreateConnection();
        var rows = conn.Query<ReviewQueueRow>("""
            SELECT id, entity_id AS EntityId, entity_type AS EntityType, trigger, status,
                   proposed_collection_id AS ProposedCollectionId, confidence_score AS ConfidenceScore,
                   candidates_json AS CandidatesJson, detail, created_at AS CreatedAt,
                   resolved_at AS ResolvedAt, resolved_by AS ResolvedBy,
                   source_operation_id AS SourceOperationId,
                   source_capability_id AS SourceCapabilityId,
                   source_capability_sub_key AS SourceCapabilitySubKey,
                   review_ready_at AS ReviewReadyAt,
                   automation_completed_at AS AutomationCompletedAt
            FROM   review_queue
            WHERE  entity_id = @entityId AND status = @status
            ORDER BY created_at DESC
            """, new { entityId, status = ReviewStatus.Pending }).AsList();

        return Task.FromResult<IReadOnlyList<ReviewQueueEntry>>(rows.Select(MapRow).ToList());
    }

    /// <inheritdoc/>
    public Task UpdateStatusAsync(
        Guid id,
        string status,
        string? resolvedBy = null,
        CancellationToken ct = default)
    {
        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE review_queue
            SET    status      = @status,
                   resolved_at = @resolvedAt,
                   resolved_by = @resolvedBy
            WHERE  id = @id
            """, new
        {
            id,
            status,
            resolvedAt = DateTimeOffset.UtcNow.ToString("O"),
            resolvedBy,
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Moves a still-pending review item to <paramref name="status"/> on a caller-owned write
    /// transaction. Returns false when the item is no longer pending (already resolved or dismissed).
    /// </summary>
    internal static bool ResolvePendingInTransaction(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction tx,
        Guid id,
        string status,
        string? resolvedBy) =>
        conn.Execute("""
            UPDATE review_queue
            SET    status      = @status,
                   resolved_at = @resolvedAt,
                   resolved_by = @resolvedBy
            WHERE  id = @id
              AND  status = @pending
            """, new
        {
            id,
            status,
            resolvedAt = DateTimeOffset.UtcNow.ToString("O"),
            resolvedBy,
            pending = ReviewStatus.Pending,
        }, tx) == 1;

    /// <inheritdoc/>
    public Task<int> MarkPendingReadyByEntityAsync(Guid entityId, CancellationToken ct = default)
    {
        using var conn = _db.CreateConnection();
        var now = DateTimeOffset.UtcNow.ToString("O");
        var rows = conn.Execute("""
            UPDATE review_queue
            SET    review_ready_at = COALESCE(review_ready_at, @now),
                   automation_completed_at = COALESCE(automation_completed_at, @now)
            WHERE  entity_id = @entityId
              AND  status = @status
              AND  review_ready_at IS NULL
            """, new
        {
            entityId,
            status = ReviewStatus.Pending,
            now,
        });

        return Task.FromResult(rows);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ReviewQueueEntry>> PromotePendingReadyByEntityAsync(
        Guid entityId,
        CancellationToken ct = default) =>
        _db.ExecuteWriteAsync<IReadOnlyList<ReviewQueueEntry>>((conn, tx, innerCt) =>
        {
            var now = DateTimeOffset.UtcNow.ToString("O");

            var ids = conn.Query<Guid>("""
                SELECT id
                FROM review_queue
                WHERE entity_id = @entityId
                  AND status = @status
                  AND review_ready_at IS NULL
                ORDER BY created_at ASC;
                """, new
            {
                entityId,
                status = ReviewStatus.Pending,
            }, tx).AsList();

            if (ids.Count == 0)
            {
                return [];
            }

            conn.Execute("""
                UPDATE review_queue
                SET    review_ready_at = @now,
                       automation_completed_at = COALESCE(automation_completed_at, @now)
                WHERE  entity_id = @entityId
                  AND  status = @status
                  AND  review_ready_at IS NULL;
                """, new
            {
                now,
                entityId,
                status = ReviewStatus.Pending,
            }, tx);

            var rows = conn.Query<ReviewQueueRow>("""
                SELECT id AS Id, entity_id AS EntityId, entity_type AS EntityType,
                       trigger AS Trigger, status AS Status,
                       proposed_collection_id AS ProposedCollectionId,
                       confidence_score AS ConfidenceScore,
                       candidates_json AS CandidatesJson, detail AS Detail,
                       created_at AS CreatedAt, resolved_at AS ResolvedAt,
                       resolved_by AS ResolvedBy,
                       source_operation_id AS SourceOperationId,
                       source_capability_id AS SourceCapabilityId,
                       source_capability_sub_key AS SourceCapabilitySubKey,
                       review_ready_at AS ReviewReadyAt,
                       automation_completed_at AS AutomationCompletedAt
                FROM review_queue
                WHERE entity_id = @entityId
                  AND status = @status
                  AND review_ready_at = @now
                ORDER BY created_at ASC;
                """, new
            {
                entityId,
                status = ReviewStatus.Pending,
                now,
            }, tx).AsList();

            return rows.Select(MapRow).ToList();
        }, ct);

    /// <inheritdoc/>
    public Task<int> GetPendingCountAsync(CancellationToken ct = default)
    {
        using var conn = _db.CreateConnection();
        var count = conn.ExecuteScalar<int>("""
            SELECT COUNT(DISTINCT rq.id)
            FROM review_queue rq
            WHERE rq.status = @status
              AND rq.review_ready_at IS NOT NULL
              AND (
                    (rq.entity_type = 'MediaAsset' AND EXISTS (
                        SELECT 1 FROM media_assets ma WHERE ma.id = rq.entity_id
                    ))
                 OR (rq.entity_type = 'Work' AND EXISTS (
                        SELECT 1 FROM works w WHERE w.id = rq.entity_id
                    ))
                 OR (rq.entity_type NOT IN ('MediaAsset', 'Work'))
              )
            """,
            new { status = ReviewStatus.Pending });

        return Task.FromResult(count);
    }

    /// <inheritdoc/>
    public Task<int> DismissAllByEntityAsync(Guid entityId, CancellationToken ct = default)
    {
        using var conn = _db.CreateConnection();
        var rows = conn.Execute("""
            UPDATE review_queue
            SET    status      = @dismissed,
                   resolved_at = @now,
                   resolved_by = 'reconciliation'
            WHERE  entity_id = @entityId
              AND  status     = @pending
            """, new
        {
            dismissed = ReviewStatus.Dismissed,
            now = DateTimeOffset.UtcNow.ToString("O"),
            entityId,
            pending = ReviewStatus.Pending,
        });

        return Task.FromResult(rows);
    }

    /// <inheritdoc/>
    public Task<int> ResolveAllByEntityAsync(Guid entityId, string resolvedBy = "system:auto-organize", CancellationToken ct = default)
    {
        using var conn = _db.CreateConnection();
        var rows = conn.Execute("""
            UPDATE review_queue
            SET    status      = @resolved,
                   resolved_at = @now,
                   resolved_by = @resolvedBy
            WHERE  entity_id = @entityId
              AND  status     = @pending
            """, new
        {
            resolved = ReviewStatus.Resolved,
            now = DateTimeOffset.UtcNow.ToString("O"),
            resolvedBy,
            entityId,
            pending = ReviewStatus.Pending,
        });

        return Task.FromResult(rows);
    }

    /// <inheritdoc/>
    public Task<int> ResolvePendingByEntityAndTriggersAsync(
        Guid entityId,
        IReadOnlyCollection<string> triggers,
        string resolvedBy,
        CancellationToken ct = default)
    {
        if (triggers.Count == 0)
        {
            return Task.FromResult(0);
        }

        using var conn = _db.CreateConnection();
        var rows = conn.Execute("""
            UPDATE review_queue
            SET    status      = @resolved,
                   resolved_at = @now,
                   resolved_by = @resolvedBy
            WHERE  entity_id = @entityId
              AND  status = @pending
              AND  trigger IN @triggers
            """, new
        {
            resolved = ReviewStatus.Resolved,
            now = DateTimeOffset.UtcNow.ToString("O"),
            resolvedBy,
            entityId,
            pending = ReviewStatus.Pending,
            triggers = triggers.ToArray(),
        });

        return Task.FromResult(rows);
    }

    /// <inheritdoc/>
    public Task<int> PurgeOrphanedAsync(CancellationToken ct = default)
    {
        using var conn = _db.CreateConnection();
        var deleted = conn.Execute("""
            DELETE FROM review_queue
            WHERE entity_type = 'MediaAsset'
              AND NOT EXISTS (
                SELECT 1
                FROM media_assets ma
                WHERE ma.id = review_queue.entity_id
              );

            DELETE FROM review_queue
            WHERE entity_type = 'Work'
              AND NOT EXISTS (
                SELECT 1
                FROM works w
                WHERE w.id = review_queue.entity_id
              );
            """);

        return Task.FromResult(deleted);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>Flat data-transfer struct that Dapper populates from a SELECT row.</summary>
    private sealed class ReviewQueueRow
    {
        public Guid Id { get; set; }
        public Guid EntityId { get; set; }
        public string EntityType { get; set; } = "";
        public string Trigger { get; set; } = "";
        public string Status { get; set; } = "";
        public string? ProposedCollectionId { get; set; }
        public double? ConfidenceScore { get; set; }
        public string? CandidatesJson { get; set; }
        public string? Detail { get; set; }
        public string CreatedAt { get; set; } = "";
        public string? ResolvedAt { get; set; }
        public string? ResolvedBy { get; set; }
        public Guid? SourceOperationId { get; set; }
        public string? SourceCapabilityId { get; set; }
        public string? SourceCapabilitySubKey { get; set; }
        public string? ReviewReadyAt { get; set; }
        public string? AutomationCompletedAt { get; set; }
    }

    private static ReviewQueueEntry MapRow(ReviewQueueRow r) => new()
    {
        Id = r.Id,
        EntityId = r.EntityId,
        EntityType = r.EntityType,
        Trigger = r.Trigger,
        Status = r.Status,
        ProposedCollectionId = r.ProposedCollectionId,
        ConfidenceScore = r.ConfidenceScore,
        CandidatesJson = r.CandidatesJson,
        Detail = r.Detail,
        CreatedAt = DateTimeOffset.TryParse(r.CreatedAt, out var created) ? created : DateTimeOffset.UtcNow,
        ResolvedAt = r.ResolvedAt is not null && DateTimeOffset.TryParse(r.ResolvedAt, out var resolved)
                              ? resolved : null,
        ResolvedBy = r.ResolvedBy,
        SourceOperationId = r.SourceOperationId,
        SourceCapabilityId = r.SourceCapabilityId,
        SourceCapabilitySubKey = r.SourceCapabilitySubKey,
        ReviewReadyAt = r.ReviewReadyAt is not null && DateTimeOffset.TryParse(r.ReviewReadyAt, out var ready)
            ? ready : null,
        AutomationCompletedAt = r.AutomationCompletedAt is not null && DateTimeOffset.TryParse(r.AutomationCompletedAt, out var completed)
            ? completed : null,
    };
}
