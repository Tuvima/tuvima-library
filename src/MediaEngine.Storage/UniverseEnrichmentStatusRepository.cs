using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// Counts universe-enrichment progress for a set of works, from the same evidence the item detail uses for its
/// <c>Stage3Status</c>: the <c>stage3_enriched_at</c> mark on the work (or one of its files) and the latest identity
/// job state of the work or its files.
/// </summary>
public sealed class UniverseEnrichmentStatusRepository : IUniverseEnrichmentStatusRepository
{
    private readonly IDatabaseConnection _db;

    public UniverseEnrichmentStatusRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task<UniverseEnrichmentProgress> GetProgressAsync(
        IReadOnlyCollection<Guid> workIds,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (workIds.Count == 0)
        {
            return Task.FromResult(new UniverseEnrichmentProgress(0, 0, 0));
        }

        var workCount = 0;
        var enriched = 0;
        var active = 0;
        using var conn = _db.CreateConnection();
        foreach (var chunk in workIds.Distinct().Chunk(SqliteBatching.MaxParametersPerQuery))
        {
            ct.ThrowIfCancellationRequested();
            var row = conn.QuerySingle<CountRow>(
                """
                -- readiness: progress counts for works the authorised display projection already listed
                WITH scoped AS (
                    SELECT w.id AS WorkId,
                           CASE WHEN EXISTS (
                               SELECT 1
                               FROM canonical_values cv
                               WHERE cv.key = 'stage3_enriched_at'
                                 AND (cv.entity_id = w.id
                                      OR cv.entity_id IN (
                                          SELECT ma.id
                                          FROM media_assets ma
                                          INNER JOIN editions e ON e.id = ma.edition_id
                                          WHERE e.work_id = w.id))
                           ) THEN 1 ELSE 0 END AS Enriched,
                           CASE WHEN EXISTS (
                               SELECT 1
                               FROM identity_jobs j
                               WHERE j.state IN ('QidResolved', 'Hydrating', 'UniverseEnriching')
                                 AND (j.entity_id = w.id
                                      OR j.entity_id IN (
                                          SELECT ma.id
                                          FROM media_assets ma
                                          INNER JOIN editions e ON e.id = ma.edition_id
                                          WHERE e.work_id = w.id))
                                 AND NOT EXISTS (
                                     SELECT 1
                                     FROM identity_jobs newer
                                     WHERE newer.entity_id = j.entity_id
                                       AND (newer.updated_at > j.updated_at
                                            OR (newer.updated_at = j.updated_at AND newer.created_at > j.created_at)))
                           ) THEN 1 ELSE 0 END AS Active
                    FROM works w
                    WHERE w.id IN @workIds
                )
                SELECT COUNT(*) AS WorkCount,
                       COALESCE(SUM(Enriched), 0) AS EnrichedCount,
                       COALESCE(SUM(CASE WHEN Enriched = 0 AND Active = 1 THEN 1 ELSE 0 END), 0) AS ActiveCount
                FROM scoped;
                """,
                new { workIds = chunk.Select(GuidSql.ToBlob).ToArray() });
            workCount += row.WorkCount;
            enriched += row.EnrichedCount;
            active += row.ActiveCount;
        }

        return Task.FromResult(new UniverseEnrichmentProgress(workCount, enriched, active));
    }

    private sealed class CountRow
    {
        public int WorkCount { get; set; }
        public int EnrichedCount { get; set; }
        public int ActiveCount { get; set; }
    }
}
