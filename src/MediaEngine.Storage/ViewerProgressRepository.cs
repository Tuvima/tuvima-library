using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>Reads a profile's progress per work from <c>user_states</c> for the spoiler rule.</summary>
public sealed class ViewerProgressRepository(IDatabaseConnection db) : IViewerProgressRepository
{
    private const int QidBatchSize = 400;

    public Task<IReadOnlyList<ViewerWorkProgress>> GetStartedWorksAsync(Guid profileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        // One row per work: a work is owned through its edition's files, or covered by a combined file.
        var rows = connection.Query<ProgressRow>(new CommandDefinition(
            """
            SELECT woa.work_id AS WorkId,
                   MAX(us.progress_pct) AS ProgressPct
            FROM user_states us
            JOIN work_owned_assets woa ON woa.asset_id = us.asset_id
            WHERE us.user_id = @profileId
              AND us.progress_pct > 0
            GROUP BY woa.work_id;
            """,
            new { profileId },
            cancellationToken: ct))
            .Select(row => new ViewerWorkProgress(row.WorkId, row.ProgressPct))
            .ToList();
        return Task.FromResult<IReadOnlyList<ViewerWorkProgress>>(rows);
    }

    public Task<IReadOnlyDictionary<Guid, string>> GetWorkQidsAsync(IEnumerable<Guid> workIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var requested = workIds.Where(id => id != Guid.Empty).Distinct().ToList();
        var found = new Dictionary<Guid, string>();
        if (requested.Count == 0)
        {
            return Task.FromResult<IReadOnlyDictionary<Guid, string>>(found);
        }

        using var connection = db.CreateConnection();
        foreach (var batch in requested.Chunk(QidBatchSize))
        {
            foreach (var row in connection.Query<WorkQidRow>(new CommandDefinition(
                         """
                         SELECT w.id AS WorkId,
                                COALESCE(
                                    NULLIF(TRIM(w.wikidata_qid), ''),
                                    (SELECT NULLIF(TRIM(cv.value), '')
                                     FROM canonical_values cv
                                     WHERE cv.entity_id = w.id AND cv.key = 'wikidata_qid'
                                     LIMIT 1)) AS Qid
                         FROM works w
                         WHERE w.id IN @ids;
                         """,
                         new { ids = batch.Select(GuidSql.ToBlob).ToArray() },
                         cancellationToken: ct)))
            {
                if (!string.IsNullOrWhiteSpace(row.Qid))
                {
                    found[row.WorkId] = row.Qid;
                }
            }
        }

        return Task.FromResult<IReadOnlyDictionary<Guid, string>>(found);
    }

    public Task<IReadOnlySet<string>> GetChildWorkQidsAsync(IEnumerable<string> workQids, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var requested = workQids
            .Where(qid => !string.IsNullOrWhiteSpace(qid))
            .Select(qid => qid.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (requested.Count == 0)
        {
            return Task.FromResult<IReadOnlySet<string>>(found);
        }

        using var connection = db.CreateConnection();
        foreach (var batch in requested.Chunk(QidBatchSize))
        {
            foreach (var qid in connection.Query<string>(new CommandDefinition(
                         """
                         SELECT DISTINCT wikidata_qid
                         FROM works
                         WHERE work_kind = 'child'
                           AND wikidata_qid IN @qids;
                         """,
                         new { qids = batch },
                         cancellationToken: ct)))
            {
                found.Add(qid);
            }
        }

        return Task.FromResult<IReadOnlySet<string>>(found);
    }

    private sealed class WorkQidRow
    {
        public Guid WorkId { get; set; }
        public string? Qid { get; set; }
    }

    private sealed class ProgressRow
    {
        public Guid WorkId { get; set; }
        public double ProgressPct { get; set; }
    }
}
