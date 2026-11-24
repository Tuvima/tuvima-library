using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="IAreaReadinessReadRepository"/>: one grouped read over each file's newest identity job. A file
/// "waits for a source" while its job is queued and its last outcome is an unavailable capability (the retail worker leaves a job
/// there when no metadata source is enabled for its media type); it is "in review" when a Pending review item names it or its work,
/// the same rule the intake queue uses, so the number agrees with Needs Review.
/// </summary>
public sealed class AreaReadinessReadRepository(IDatabaseConnection db) : IAreaReadinessReadRepository
{
    private const string UnavailableCapability = "UnavailableCapability";

    internal static readonly string StageSql = $"""
        WITH latest_job AS (
            SELECT entity_id, state, last_outcome_category
            FROM (
                SELECT ij.entity_id, ij.state, ij.last_outcome_category,
                       ROW_NUMBER() OVER (
                           PARTITION BY ij.entity_id
                           ORDER BY ij.updated_at DESC, ij.created_at DESC) AS rn
                FROM identity_jobs ij)
            WHERE rn = 1
        ),
        files AS (
            SELECT ma.library_id AS LibraryId,
                   lj.state AS State,
                   lj.last_outcome_category AS Outcome,
                   EXISTS (
                       SELECT 1
                       FROM review_queue rq
                       WHERE rq.status = 'Pending'
                         AND rq.trigger != 'WritebackFailed'
                         AND rq.entity_id IN (
                             ma.id,
                             (SELECT e.work_id FROM editions e WHERE e.id = ma.edition_id))) AS InReview
            FROM media_assets ma
            LEFT JOIN latest_job lj ON lj.entity_id = ma.id
            WHERE ma.library_id IS NOT NULL
              AND ma.is_orphaned = 0
              AND {VaultSql.NotVaultAsset("ma.id")}
        )
        SELECT LibraryId,
               COUNT(*) AS Found,
               SUM(CASE WHEN InReview = 0 AND State IS NOT NULL AND State IN ('Ready', 'ReadyWithoutUniverse') THEN 1 ELSE 0 END) AS Ready,
               SUM(CASE WHEN InReview = 1 THEN 1 ELSE 0 END) AS InReview,
               SUM(CASE WHEN InReview = 0 AND State = 'Queued' AND Outcome = '{UnavailableCapability}' THEN 1 ELSE 0 END) AS WaitingForSource,
               SUM(CASE WHEN InReview = 0
                         AND (State IS NULL
                              OR (State IN ('Queued', 'RetailSearching', 'RetailMatched', 'RetailMatchedNeedsReview',
                                            'BridgeSearching', 'QidResolved', 'Hydrating', 'UniverseEnriching')
                                  AND NOT (State = 'Queued' AND Outcome = '{UnavailableCapability}')))
                        THEN 1 ELSE 0 END) AS InProgress
        FROM files
        GROUP BY LibraryId;
        """;

    public Task<IReadOnlyList<LibraryFileStageCount>> GetFileStageCountsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var rows = conn.Query<StageRow>(StageSql).Select(row => new LibraryFileStageCount(
            row.LibraryId.ToLowerInvariant(), row.Found, row.InProgress, row.WaitingForSource, row.InReview, row.Ready)).ToList();
        return Task.FromResult<IReadOnlyList<LibraryFileStageCount>>(rows);
    }

    public Task<int> GetOpenIntakeCountAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var count = conn.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM media_operations
            WHERE operation_type = 'ingestion.file'
              AND status IN ('pending', 'queued', 'leased', 'running');
            """);
        return Task.FromResult(count);
    }

    private sealed class StageRow
    {
        public string LibraryId { get; set; } = "";
        public int Found { get; set; }
        public int Ready { get; set; }
        public int InReview { get; set; }
        public int WaitingForSource { get; set; }
        public int InProgress { get; set; }
    }
}
