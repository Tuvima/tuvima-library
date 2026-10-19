using System.Globalization;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Ingestion;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="IIngestionQueueProgressRepository"/>: one set-based read over the per-file intake
/// operations (<c>media_operations</c> rows of type <c>ingestion.file</c>), joined to the newest identity job of each
/// file's media asset, its media type, any inspection hold and any Pending review.
/// </summary>
public sealed class IngestionQueueProgressRepository : IIngestionQueueProgressRepository
{
    // The states an identity job still has work to do in (the finished ones are Ready, ReadyWithoutUniverse, Failed,
    // RetailNoMatch, QidNoMatch and QidNeedsReview). They are listed so the state index can be used.
    private const string LiveIdentityStates =
        $"'{nameof(IdentityJobState.Queued)}', '{nameof(IdentityJobState.RetailSearching)}', "
        + $"'{nameof(IdentityJobState.RetailMatched)}', '{nameof(IdentityJobState.RetailMatchedNeedsReview)}', "
        + $"'{nameof(IdentityJobState.BridgeSearching)}', '{nameof(IdentityJobState.QidResolved)}', "
        + $"'{nameof(IdentityJobState.Hydrating)}', '{nameof(IdentityJobState.UniverseEnriching)}'";

    // The intake statuses that are still in the queue.
    private const string OpenOperationStatuses =
        "'pending', 'queued', 'leased', 'running', 'retry_waiting', 'failed_retryable', 'interrupted', 'paused', 'blocked'";

    internal const string Sql = $"""
        WITH candidates AS (
            SELECT id
            FROM media_operations
            WHERE operation_type = 'ingestion.file'
              AND @since IS NOT NULL
              AND created_at >= @since
            UNION
            SELECT id
            FROM media_operations
            WHERE operation_type = 'ingestion.file'
              AND status IN ({OpenOperationStatuses})
            UNION
            SELECT mo.id
            FROM identity_jobs ij
            CROSS JOIN media_operations mo INDEXED BY idx_media_operations_entity ON mo.entity_id = ij.entity_id
            WHERE ij.state IN ({LiveIdentityStates})
              AND mo.operation_type = 'ingestion.file'
            UNION
            SELECT mo.id
            FROM media_inspection_holds h
            CROSS JOIN media_operations mo INDEXED BY idx_media_operations_entity ON mo.entity_id = h.asset_id
            WHERE mo.operation_type = 'ingestion.file'
        ),
        latest_job AS (
            SELECT entity_id, state, media_type, updated_at
            FROM (
                SELECT ij.entity_id, ij.state, ij.media_type, ij.updated_at,
                       ROW_NUMBER() OVER (
                           PARTITION BY ij.entity_id
                           ORDER BY ij.updated_at DESC, ij.created_at DESC) AS rn
                FROM identity_jobs ij
                WHERE ij.entity_id IN (
                    SELECT mo.entity_id
                    FROM media_operations mo
                    WHERE mo.id IN (SELECT id FROM candidates)
                      AND mo.entity_id IS NOT NULL)
            )
            WHERE rn = 1
        )
        SELECT mo.id               AS OperationId,
               mo.created_at       AS CreatedAt,
               mo.updated_at       AS UpdatedAt,
               mo.stage            AS Stage,
               mo.status           AS Status,
               mo.result_summary   AS ResultSummary,
               mo.entity_id        AS AssetId,
               lj.state            AS IdentityState,
               lj.updated_at       AS IdentityUpdatedAt,
               COALESCE(
                   lj.media_type,
                   (SELECT il.media_type
                    FROM ingestion_log il
                    WHERE il.media_asset_id = mo.entity_id
                      AND il.media_type IS NOT NULL
                    ORDER BY il.updated_at DESC
                    LIMIT 1)) AS MediaType,
               (SELECT h.tool_id
                FROM media_inspection_holds h
                WHERE h.asset_id = mo.entity_id) AS HeldToolId,
               EXISTS (
                   SELECT 1
                   FROM review_queue rq
                   WHERE rq.status = 'Pending'
                     AND rq.trigger != 'WritebackFailed'
                     AND rq.entity_id IN (
                         mo.entity_id,
                         (SELECT e.work_id
                          FROM media_assets ma
                          JOIN editions e ON e.id = ma.edition_id
                          WHERE ma.id = mo.entity_id))) AS HasPendingReview
        FROM media_operations mo
        LEFT JOIN latest_job lj ON lj.entity_id = mo.entity_id
        WHERE mo.id IN (SELECT id FROM candidates);
        """;

    private readonly IDatabaseConnection _db;

    public IngestionQueueProgressRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<IngestionQueueFileRow>> ReadAsync(DateTimeOffset? since, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var rows = conn.Query<Row>(Sql, new { since = since?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) });
        IReadOnlyList<IngestionQueueFileRow> mapped = rows.Select(ToFileRow).ToList();
        return Task.FromResult(mapped);
    }

    private static IngestionQueueFileRow ToFileRow(Row row) => new(
        row.OperationId,
        ParseTime(row.CreatedAt),
        ParseTime(row.UpdatedAt),
        row.Stage,
        row.Status,
        row.ResultSummary,
        row.AssetId,
        row.IdentityState,
        row.IdentityUpdatedAt is null ? null : ParseTime(row.IdentityUpdatedAt),
        row.MediaType,
        row.HeldToolId,
        row.HasPendingReview != 0);

    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(
        value,
        CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private sealed class Row
    {
        public Guid OperationId { get; set; }
        public string CreatedAt { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
        public string? Stage { get; set; }
        public string Status { get; set; } = "";
        public string? ResultSummary { get; set; }
        public Guid? AssetId { get; set; }
        public string? IdentityState { get; set; }
        public string? IdentityUpdatedAt { get; set; }
        public string? MediaType { get; set; }
        public string? HeldToolId { get; set; }
        public long HasPendingReview { get; set; }
    }
}
