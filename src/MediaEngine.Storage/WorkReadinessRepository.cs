using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="IWorkReadinessRepository"/>. This class is the only writer of
/// <c>works.ready_at</c>; the rule it applies lives in <see cref="WorkReadinessSql"/>.
/// </summary>
public sealed class WorkReadinessRepository : IWorkReadinessRepository
{
    private const string LeafWorkKinds = "'standalone', 'child'";

    // The three stored artwork values. The key list must read exactly as in the partial index
    // idx_canonical_values_artwork_scored (step 0002) for SQLite to use that index.
    private const string ArtworkKeysSql =
        $"('{MetadataFieldConstants.CoverState}', '{MetadataFieldConstants.CoverUrl}', '{MetadataFieldConstants.ArtworkSettledAt}')";

    // The change look-ups, public so a test can check their query plans use the indexes added for them.
    public const string NewestJobTimeSql = "SELECT MAX(updated_at) FROM identity_jobs;";

    // Written as "newest first, take one" and pinned to the partial index so SQLite reads a single entry of it. Left
    // alone, the planner prefers the general key index and sorts every artwork row.
    public const string NewestArtworkTimeSql =
        $"SELECT last_scored_at FROM canonical_values INDEXED BY idx_canonical_values_artwork_scored WHERE key IN {ArtworkKeysSql} ORDER BY last_scored_at DESC LIMIT 1;";

    // A job belongs to its file's own work and to every work the file covers; the two are read separately so SQLite
    // never builds the whole work_owned_assets view.
    public const string TouchedByJobsSql = """
        SELECT e.work_id
        FROM identity_jobs ij
        INNER JOIN media_assets ma ON ma.id = ij.entity_id
        INNER JOIN editions e ON e.id = ma.edition_id
        WHERE ij.updated_at > @since AND ij.updated_at <= @max
        UNION
        SELECT c.work_id
        FROM identity_jobs ij
        INNER JOIN media_asset_coverage c ON c.asset_id = ij.entity_id
        WHERE ij.updated_at > @since AND ij.updated_at <= @max;
        """;

    // A file marked Unmatched (or tried again) counts as new work even when it has no identity job (a YouTube file with no ID is never
    // looked up), so it is shown at once instead of waiting for the full sweep.
    public const string NewestUnmatchedTimeSql = "SELECT MAX(last_tried_at) FROM unmatched_assets;";

    public const string TouchedByUnmatchedSql = """
        SELECT e.work_id
        FROM unmatched_assets ua
        INNER JOIN media_assets ma ON ma.id = ua.asset_id
        INNER JOIN editions e ON e.id = ma.edition_id
        WHERE ua.last_tried_at > @since AND ua.last_tried_at <= @max
        UNION
        SELECT c.work_id
        FROM unmatched_assets ua
        INNER JOIN media_asset_coverage c ON c.asset_id = ua.asset_id
        WHERE ua.last_tried_at > @since AND ua.last_tried_at <= @max;
        """;

    // An artwork value may sit on a file, on the Work, or on its parent or root; the parent's and
    // root's values count for every Work below them.
    public const string TouchedByArtworkSql = $"""
        WITH touched_values AS (
            SELECT entity_id
            FROM canonical_values
            WHERE key IN {ArtworkKeysSql}
              AND last_scored_at > @since AND last_scored_at <= @max
        )
        SELECT e.work_id
        FROM touched_values tv
        INNER JOIN media_assets ma ON ma.id = tv.entity_id
        INNER JOIN editions e ON e.id = ma.edition_id
        UNION
        SELECT c.work_id
        FROM touched_values tv
        INNER JOIN media_asset_coverage c ON c.asset_id = tv.entity_id
        UNION
        SELECT w.id
        FROM touched_values tv
        INNER JOIN works w ON w.id = tv.entity_id
        WHERE w.work_kind IN ({LeafWorkKinds})
        UNION
        SELECT child.id
        FROM touched_values tv
        INNER JOIN works child ON child.parent_work_id = tv.entity_id
        WHERE child.work_kind IN ({LeafWorkKinds})
        UNION
        SELECT leaf.id
        FROM touched_values tv
        INNER JOIN works mid ON mid.parent_work_id = tv.entity_id
        INNER JOIN works leaf ON leaf.parent_work_id = mid.id
        WHERE leaf.work_kind IN ({LeafWorkKinds});
        """;

    private readonly IDatabaseConnection _db;

    public WorkReadinessRepository(IDatabaseConnection db) => _db = db;

    public Task<IReadOnlyList<WorkReadinessChange>> RefreshAsync(
        IReadOnlyCollection<Guid> workIds, IReadOnlyCollection<string> bypassIdentityLibraryIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (workIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<WorkReadinessChange>>([]);
        }

        using var conn = _db.CreateConnection();
        var changes = new List<WorkReadinessChange>();
        foreach (var batch in workIds.Distinct().Chunk(SqliteBatching.MaxParametersPerQuery))
        {
            ct.ThrowIfCancellationRequested();
            RefreshBatch(conn, batch, bypassIdentityLibraryIds, changes);
        }

        return Task.FromResult<IReadOnlyList<WorkReadinessChange>>(changes);
    }

    public Task<IReadOnlyList<WorkReadinessChange>> RefreshAllAsync(
        IReadOnlyCollection<string> bypassIdentityLibraryIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var leafIds = conn.Query<Guid>($"SELECT id FROM works WHERE work_kind IN ({LeafWorkKinds});").ToList();

        var changes = new List<WorkReadinessChange>();
        foreach (var batch in leafIds.Chunk(SqliteBatching.MaxParametersPerQuery))
        {
            ct.ThrowIfCancellationRequested();
            RefreshBatch(conn, batch, bypassIdentityLibraryIds, changes);
        }

        return Task.FromResult<IReadOnlyList<WorkReadinessChange>>(changes);
    }

    public Task<(IReadOnlyList<Guid> WorkIds, WorkReadinessWatermark Next)> FindTouchedWorkIdsAsync(
        WorkReadinessWatermark since, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();

        // Take each table's newest value first, then read only the rows up to it, so the watermark we hand
        // back never runs ahead of the rows we returned. An empty table maps to "" so its first row is
        // picked up by the next call instead of being swallowed while the watermark is established.
        var maxJobs = conn.ExecuteScalar<string?>(NewestJobTimeSql) ?? string.Empty;
        var maxReviews = conn.ExecuteScalar<string?>(
            "SELECT MAX(COALESCE(resolved_at, created_at)) FROM review_queue;") ?? string.Empty;
        var maxArtwork = conn.ExecuteScalar<string?>(NewestArtworkTimeSql) ?? string.Empty;
        var maxUnmatched = conn.ExecuteScalar<string?>(NewestUnmatchedTimeSql) ?? string.Empty;

        // A table that has not been seen yet only establishes its watermark; the full sweep covers history.
        var touched = new HashSet<Guid>();

        // Read a little below each watermark as well: a row is stamped before its write commits, so a slow write can
        // become visible with a time that is already behind the watermark. Re-checking a Work is idempotent.
        if (since.Jobs is not null)
        {
            touched.UnionWith(conn.Query<Guid>(TouchedByJobsSql, new { since = Overlap(since.Jobs), max = maxJobs }));
        }

        if (since.Reviews is not null)
        {
            touched.UnionWith(conn.Query<Guid>("""
                SELECT w.id
                FROM review_queue rq
                INNER JOIN works w ON w.id = rq.entity_id
                WHERE COALESCE(rq.resolved_at, rq.created_at) > @since
                  AND COALESCE(rq.resolved_at, rq.created_at) <= @max
                UNION
                SELECT e.work_id
                FROM review_queue rq
                INNER JOIN media_assets ma ON ma.id = rq.entity_id
                INNER JOIN editions e ON e.id = ma.edition_id
                WHERE COALESCE(rq.resolved_at, rq.created_at) > @since
                  AND COALESCE(rq.resolved_at, rq.created_at) <= @max
                UNION
                SELECT c.work_id
                FROM review_queue rq
                INNER JOIN media_asset_coverage c ON c.asset_id = rq.entity_id
                WHERE COALESCE(rq.resolved_at, rq.created_at) > @since
                  AND COALESCE(rq.resolved_at, rq.created_at) <= @max;
                """, new { since = Overlap(since.Reviews), max = maxReviews }));
        }

        if (since.Artwork is not null)
        {
            touched.UnionWith(conn.Query<Guid>(TouchedByArtworkSql, new { since = Overlap(since.Artwork), max = maxArtwork }));
        }

        if (since.Unmatched is not null)
        {
            touched.UnionWith(conn.Query<Guid>(TouchedByUnmatchedSql, new { since = Overlap(since.Unmatched), max = maxUnmatched }));
        }

        var next = new WorkReadinessWatermark(
            Newest(since.Jobs, maxJobs),
            Newest(since.Reviews, maxReviews),
            Newest(since.Artwork, maxArtwork),
            Newest(since.Unmatched, maxUnmatched));

        return Task.FromResult<(IReadOnlyList<Guid>, WorkReadinessWatermark)>((touched.ToList(), next));
    }

    public Task<IReadOnlyDictionary<Guid, WorkReadinessDetails>> GetDetailsAsync(
        IReadOnlyCollection<Guid> workIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var result = new Dictionary<Guid, WorkReadinessDetails>();
        if (workIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyDictionary<Guid, WorkReadinessDetails>>(result);
        }

        using var conn = _db.CreateConnection();
        foreach (var batch in workIds.Distinct().Chunk(SqliteBatching.MaxParametersPerQuery))
        {
            ct.ThrowIfCancellationRequested();
            var rows = conn.Query<DetailsRow>($"""
                SELECT w.id AS WorkId,
                       w.collection_id AS CollectionId,
                       w.media_type AS MediaType,
                       COALESCE(
                           (SELECT cv.value FROM canonical_values cv
                            WHERE cv.entity_id = w.id AND cv.key = 'title' AND TRIM(cv.value) <> ''),
                           (SELECT cv.value
                            FROM work_owned_assets woa_t
                            INNER JOIN canonical_values cv ON cv.entity_id = woa_t.asset_id
                            WHERE woa_t.work_id = w.id AND cv.key = 'title' AND TRIM(cv.value) <> ''
                            ORDER BY woa_t.is_covered, woa_t.asset_id
                            LIMIT 1),
                           (SELECT cv.value FROM canonical_values cv
                            WHERE cv.entity_id = COALESCE(
                                      (SELECT gp.id FROM works p
                                       INNER JOIN works gp ON gp.id = p.parent_work_id
                                       WHERE p.id = w.parent_work_id),
                                      w.parent_work_id)
                              AND cv.key = 'title' AND TRIM(cv.value) <> ''),
                           '') AS Title,
                       (SELECT ma_p.id
                        FROM work_owned_assets woa_p
                        INNER JOIN media_assets ma_p ON ma_p.id = woa_p.asset_id
                        WHERE woa_p.work_id = w.id
                          AND ma_p.status = 'Normal' AND ma_p.is_orphaned = 0
                          AND {HomeVisibilitySql.VisibleAssetPathPredicate("ma_p.file_path_root")}
                        ORDER BY woa_p.is_covered, CASE WHEN LOWER(ma_p.file_path_root) LIKE '%.epub' THEN 0 ELSE 1 END, ma_p.id
                        LIMIT 1) AS AssetId
                FROM works w
                WHERE w.id IN @ids;
                """, new { ids = ToBlobs(batch) });

            foreach (var row in rows)
            {
                result[row.WorkId] = new WorkReadinessDetails(row.WorkId, row.CollectionId, row.MediaType, row.Title, row.AssetId);
            }
        }

        return Task.FromResult<IReadOnlyDictionary<Guid, WorkReadinessDetails>>(result);
    }

    public Task<IReadOnlyList<Guid>> FindMatchedAwaitingArtworkAsync(
        IReadOnlyCollection<Guid> workIds, IReadOnlyCollection<string> bypassIdentityLibraryIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var result = new List<Guid>();
        if (workIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<Guid>>(result);
        }

        using var conn = _db.CreateConnection();
        foreach (var batch in workIds.Distinct().Chunk(SqliteBatching.MaxParametersPerQuery))
        {
            ct.ThrowIfCancellationRequested();
            result.AddRange(conn.Query<Guid>($"""
                SELECT w.id
                FROM works w
                WHERE w.id IN @ids
                  AND w.ready_at IS NULL
                  AND {WorkReadinessSql.MatchedAndPlacedPredicate("w")}
                  AND NOT {WorkReadinessSql.ArtworkPresentOrSettledPredicate("w")};
                """, new { ids = ToBlobs(batch), bypassIdentityLibraryIds = bypassIdentityLibraryIds.ToArray() }));
        }

        return Task.FromResult<IReadOnlyList<Guid>>(result);
    }

    // Dapper expands a list parameter element by element without the Guid handler, so ids go in as BLOBs.
    private static byte[][] ToBlobs(IEnumerable<Guid> ids) => ids.Select(GuidSql.ToBlob).ToArray();

    /// <summary>How far below a watermark each change look-up reads again.</summary>
    public static readonly TimeSpan WatermarkOverlap = TimeSpan.FromSeconds(30);

    // The point to read from: the watermark, or the overlap before now when that is earlier, written in the same shape as the watermark itself: each table stamps
    // its own text format (date and time with a space, ISO with an offset, ISO ending in Z) and text compares by shape.
    private static string Overlap(string watermark)
    {
        if (watermark.Length == 0
            || !DateTimeOffset.TryParse(
                watermark,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return watermark;
        }

        // Rows older than the overlap can no longer arrive late, so an idle library re-reads nothing.
        var settled = DateTimeOffset.UtcNow - WatermarkOverlap;
        var start = parsed < settled ? parsed : settled;
        if (!watermark.Contains('T', StringComparison.Ordinal))
        {
            return start.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        }

        return watermark.EndsWith('Z')
            ? start.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture)
            : start.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Newest(string? seen, string current) =>
        seen is null || string.CompareOrdinal(current, seen) > 0 ? current : seen;

    private static void RefreshBatch(
        SqliteConnection conn,
        Guid[] batch,
        IReadOnlyCollection<string> bypassIdentityLibraryIds,
        List<WorkReadinessChange> changes)
    {
        var now = DateTimeOffset.UtcNow;
        var at = now.ToString("O");
        var computed = WorkReadinessSql.ComputedReadyPredicate("w");
        var stillReady = WorkReadinessSql.StillReadyPredicate("w");

        // Each statement is atomic, so a Work is never marked from a half-read state. A Work needs the full rule to
        // become Ready; once Ready it only has to keep passing the "still ready" rule, so ordinary re-identification
        // does not flicker it off. A Work that stays Ready matches neither statement and keeps its time. The first
        // time a Work is ever Ready is recorded once in first_ready_at and never cleared; it decides what is announced.
        var becameReady = conn.Query<ChangedRow>($"""
            UPDATE works AS w
            SET ready_at = @at,
                first_ready_at = COALESCE(first_ready_at, @at)
            WHERE w.id IN @ids
              AND w.work_kind IN ({LeafWorkKinds})
              AND w.ready_at IS NULL
              AND {computed}
            RETURNING id AS Id, media_type AS MediaType, first_ready_at AS FirstReadyAt;
            """, new { at, ids = ToBlobs(batch), bypassIdentityLibraryIds = bypassIdentityLibraryIds.ToArray() }).ToList();

        var stoppedBeingReady = conn.Query<ChangedRow>($"""
            UPDATE works AS w
            SET ready_at = NULL
            WHERE w.id IN @ids
              AND w.work_kind IN ({LeafWorkKinds})
              AND w.ready_at IS NOT NULL
              AND NOT ({stillReady})
            RETURNING id AS Id, media_type AS MediaType, first_ready_at AS FirstReadyAt;
            """, new { ids = ToBlobs(batch), bypassIdentityLibraryIds = bypassIdentityLibraryIds.ToArray() }).ToList();

        changes.AddRange(becameReady.Select(r => new WorkReadinessChange(
            r.Id, r.MediaType, true, now, IsFirstReady: string.Equals(r.FirstReadyAt, at, StringComparison.Ordinal))));
        changes.AddRange(stoppedBeingReady.Select(r => new WorkReadinessChange(r.Id, r.MediaType, false, now)));
    }

    private sealed class ChangedRow
    {
        public Guid Id { get; set; }
        public string MediaType { get; set; } = string.Empty;
        public string? FirstReadyAt { get; set; }
    }

    private sealed class DetailsRow
    {
        public Guid WorkId { get; set; }
        public Guid? CollectionId { get; set; }
        public string MediaType { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public Guid? AssetId { get; set; }
    }
}
