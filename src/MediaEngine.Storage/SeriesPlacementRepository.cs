using System.Data;
using System.Globalization;
using Dapper;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// A person's locked Series choice for a work. The lock lives on <c>works.series_placement_locked</c>; while it is 1,
/// <c>works.collection_id</c> is the person's choice (null = "not in a Series") and no automatic path changes it.
/// </summary>
public sealed class SeriesPlacementRepository : ISeriesPlacementRepository
{
    private readonly IDatabaseConnection _db;

    public SeriesPlacementRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task<SeriesPlacementLock?> GetLockAsync(Guid workId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        return Task.FromResult(LoadLock(conn, transaction: null, workId));
    }

    public Task<Guid?> ResolveWorkIdAsync(Guid entityId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var workId = conn.ExecuteScalar<Guid?>(
            """
            -- readiness: the work behind one editor entity id, looked up by id
            SELECT id FROM works WHERE id = @entityId
            UNION ALL
            SELECT e.work_id FROM editions e WHERE e.id = @entityId
            UNION ALL
            SELECT e.work_id FROM media_assets ma JOIN editions e ON e.id = ma.edition_id WHERE ma.id = @entityId
            LIMIT 1;
            """,
            new { entityId });
        return Task.FromResult(workId);
    }

    public Task<string?> GetWorkMediaTypeAsync(Guid workId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var mediaType = conn.ExecuteScalar<string?>(
            "-- readiness: media type of one work, looked up by id\nSELECT media_type FROM works WHERE id = @workId;",
            new { workId });
        return Task.FromResult(mediaType);
    }

    public Task<SeriesReviewEntities> GetReviewEntitiesAsync(Guid workId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();

        // media_assets has no created_at; rowid is the order the files were first stored in. A Normal original comes
        // first, so the stable key never moves to a rendition or an orphaned file while a better one exists.
        var rows = conn.Query<AssetKeyRow>(
            """
            SELECT ma.id AS Id,
                   CASE WHEN ma.status = 'Normal' AND ma.is_orphaned = 0 AND ma.rendition_purpose = 'Original'
                        THEN 1 ELSE 0 END AS IsStable
            FROM media_assets ma
            JOIN editions e ON e.id = ma.edition_id
            WHERE e.work_id = @workId
            ORDER BY CASE WHEN ma.status = 'Normal' AND ma.is_orphaned = 0 AND ma.rendition_purpose = 'Original' THEN 0 ELSE 1 END,
                     ma.rowid;
            """,
            new { workId }).AsList();

        var stable = rows.FirstOrDefault(r => r.IsStable == 1)?.Id;
        var ids = rows.Select(r => r.Id).Append(workId).ToList();
        return Task.FromResult(new SeriesReviewEntities(stable, ids));
    }

    /// <summary>Reads the lock inside a connection the caller owns (the linking pass holds its own transaction).</summary>
    internal static SeriesPlacementLock? LoadLock(IDbConnection conn, IDbTransaction? transaction, Guid workId)
    {
        var row = conn.QueryFirstOrDefault<LockRow>(
            """
            -- readiness: the placement lock of one work, looked up by id
            SELECT w.series_placement_locked AS Locked,
                   w.collection_id AS CollectionId,
                   c.wikidata_qid AS SeriesQid,
                   c.media_type AS MediaType
            FROM works w
            LEFT JOIN collections c ON c.id = w.collection_id
            WHERE w.id = @workId;
            """,
            new { workId },
            transaction);
        if (row is null || row.Locked != 1)
        {
            return null;
        }

        return row.CollectionId is null
            ? SeriesPlacementLock.NoSeries
            : new SeriesPlacementLock(row.CollectionId, row.SeriesQid, row.MediaType);
    }

    public async Task<SeriesPlacementChoiceResult> SetChoiceAsync(Guid workId, Guid? collectionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            var workMediaType = conn.ExecuteScalar<string?>(
                "-- readiness: media type of one work, looked up by id\nSELECT media_type FROM works WHERE id = @workId;",
                new { workId },
                tx);
            if (workMediaType is null)
            {
                return SeriesPlacementChoiceResult.WorkNotFound;
            }

            if (collectionId is { } shelfId)
            {
                var shelf = conn.QueryFirstOrDefault<ShelfRow>(
                    "SELECT collection_type AS CollectionType, media_type AS MediaType FROM collections WHERE id = @shelfId;",
                    new { shelfId },
                    tx);
                if (shelf is null)
                {
                    return SeriesPlacementChoiceResult.ShelfNotFound;
                }

                if (!string.Equals(shelf.CollectionType, "ContentGroup", StringComparison.OrdinalIgnoreCase)
                    || !SeriesPlacementRule.MediaMatches(shelf.MediaType, workMediaType))
                {
                    return SeriesPlacementChoiceResult.NotASeriesShelfOfThisMediaType;
                }
            }

            // The shelf's own manifest, when it lists the work, gives the position; otherwise the work sorts first.
            var sortOrder = collectionId is { } manifestShelf
                ? conn.ExecuteScalar<double?>(
                    """
                    SELECT COALESCE(parsed_ordinal, sort_order)
                    FROM series_manifest_items
                    WHERE collection_id = @manifestShelf AND linked_work_id = @workId AND is_collection = 0
                    ORDER BY is_expanded_from_collection
                    LIMIT 1;
                    """,
                    new { manifestShelf, workId },
                    tx) ?? 0d
                : 0d;

            // Only Series (ContentGroup) shelf rows are the placement's to manage; manual and smart collections stay.
            conn.Execute(
                """
                DELETE FROM collection_items
                WHERE work_id = @workId
                  AND (@target IS NULL OR collection_id <> @target)
                  AND collection_id IN (SELECT id FROM collections WHERE collection_type = 'ContentGroup');
                """,
                new { workId, target = GuidSql.ToDb(collectionId) },
                tx);

            if (collectionId is { } keep)
            {
                conn.Execute(
                    """
                    INSERT INTO collection_items (id, collection_id, work_id, sort_order, progress_state, added_at)
                    SELECT @id, @keep, @workId, @sortOrder, 'not_started', @addedAt
                    WHERE NOT EXISTS (
                        SELECT 1 FROM collection_items WHERE collection_id = @keep AND work_id = @workId
                    );
                    """,
                    new
                    {
                        id = Guid.NewGuid(),
                        keep,
                        workId,
                        sortOrder,
                        addedAt = DateTimeOffset.UtcNow.ToString("O"),
                    },
                    tx);
            }

            conn.Execute(
                "UPDATE works SET collection_id = @collectionId, series_placement_locked = 1 WHERE id = @workId;",
                new { collectionId = GuidSql.ToDb(collectionId), workId },
                tx);
            return SeriesPlacementChoiceResult.Applied;
        }, ct).ConfigureAwait(false);
    }

    public Task ReleaseChoiceAsync(Guid workId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            conn.Execute(
                "UPDATE works SET series_placement_locked = 0, collection_id = NULL WHERE id = @workId;",
                new { workId },
                tx);
            conn.Execute(
                """
                DELETE FROM collection_items
                WHERE work_id = @workId
                  AND collection_id IN (SELECT id FROM collections WHERE collection_type = 'ContentGroup');
                """,
                new { workId },
                tx);
        }, ct);
    }

    public Task<SeriesPlacementView?> GetViewAsync(Guid workId, string? query, int candidateLimit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();

        var work = conn.QueryFirstOrDefault<WorkRow>(
            """
            -- readiness: one work's media type, shelf and lock, looked up by id
            SELECT media_type AS MediaType, collection_id AS CollectionId, series_placement_locked AS Locked
            FROM works
            WHERE id = @workId;
            """,
            new { workId });
        if (work is null)
        {
            return Task.FromResult<SeriesPlacementView?>(null);
        }

        // One row per Series shelf whose manifest lists the work, with the scope and position that manifest gives it.
        var listedRows = conn.Query<ListedRow>(
            """
            SELECT smi.collection_id AS CollectionId,
                   COALESCE(NULLIF(c.display_name, ''), c.wikidata_qid, smi.series_qid) AS Label,
                   c.wikidata_qid AS SeriesQid,
                   COALESCE(NULLIF(c.media_type, ''), NULLIF(smi.media_type, ''), '') AS MediaType,
                   smi.membership_scope AS Scope,
                   smi.raw_ordinal AS RawOrdinal,
                   smi.parsed_ordinal AS ParsedOrdinal,
                   smi.is_expanded_from_collection AS IsExpanded
            FROM series_manifest_items smi
            JOIN collections c ON c.id = smi.collection_id AND c.collection_type = 'ContentGroup'
            WHERE smi.linked_work_id = @workId
              AND smi.is_collection = 0;
            """,
            new { workId }).AsList();

        var listed = listedRows
            .Where(r => SeriesPlacementRule.MediaMatches(r.MediaType, work.MediaType))
            .GroupBy(r => r.CollectionId)
            .Select(group =>
            {
                var best = group
                    .OrderBy(r => SeriesPlacementRule.ScopeRank(r.Scope))
                    .ThenBy(r => r.IsExpanded)
                    .First();
                return new SeriesPlacementShelf(
                    best.CollectionId,
                    best.Label ?? string.Empty,
                    best.SeriesQid,
                    best.MediaType,
                    string.IsNullOrWhiteSpace(best.Scope) ? SeriesMembershipScopeNames.MainSequence : best.Scope,
                    PositionLabel(best));
            })
            .OrderBy(s => SeriesPlacementRule.ScopeRank(s.Scope))
            .ThenBy(s => s.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        SeriesPlacementShelf? current = null;
        if (work.CollectionId is { } currentId)
        {
            current = listed.FirstOrDefault(s => s.CollectionId == currentId);
            if (current is null)
            {
                var shelf = conn.QueryFirstOrDefault<ShelfRow>(
                    """
                    SELECT COALESCE(NULLIF(display_name, ''), wikidata_qid, '') AS Label,
                           wikidata_qid AS SeriesQid,
                           media_type AS MediaType
                    FROM collections
                    WHERE id = @currentId;
                    """,
                    new { currentId });
                if (shelf is not null)
                {
                    current = new SeriesPlacementShelf(
                        currentId,
                        shelf.Label ?? string.Empty,
                        shelf.SeriesQid,
                        shelf.MediaType ?? work.MediaType ?? string.Empty,
                        SeriesMembershipScopeNames.MainSequence,
                        PositionLabel: null);
                }
            }
        }

        var excluded = listed
            .Select(s => s.CollectionId)
            .Append(work.CollectionId ?? Guid.Empty)
            .Distinct()
            .Select(GuidSql.ToBlob)
            .ToArray();
        var limit = Math.Clamp(candidateLimit, 0, 50);
        var pattern = string.IsNullOrWhiteSpace(query) ? null : "%" + EscapeLike(query.Trim()) + "%";
        IReadOnlyList<SeriesPlacementShelf> candidates = limit == 0
            ? []
            : conn.Query<ShelfRow>(
                """
                SELECT id AS CollectionId,
                       COALESCE(NULLIF(display_name, ''), wikidata_qid, '') AS Label,
                       wikidata_qid AS SeriesQid,
                       media_type AS MediaType
                FROM collections
                WHERE collection_type = 'ContentGroup'
                  AND LOWER(COALESCE(media_type, '')) = LOWER(@mediaType)
                  AND id NOT IN @excluded
                  AND (@pattern IS NULL OR display_name LIKE @pattern ESCAPE '\' COLLATE NOCASE)
                ORDER BY display_name COLLATE NOCASE, id
                LIMIT @limit;
                """,
                new { mediaType = work.MediaType ?? string.Empty, excluded, pattern, limit })
            .Select(r => new SeriesPlacementShelf(
                r.CollectionId,
                r.Label ?? string.Empty,
                r.SeriesQid,
                r.MediaType ?? work.MediaType ?? string.Empty,
                SeriesMembershipScopeNames.MainSequence,
                PositionLabel: null))
            .ToList();

        return Task.FromResult<SeriesPlacementView?>(new SeriesPlacementView(
            workId,
            work.MediaType ?? string.Empty,
            current,
            work.Locked == 1,
            listed,
            candidates));
    }

    private static string? PositionLabel(ListedRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.RawOrdinal))
        {
            return row.RawOrdinal.Trim();
        }

        return row.ParsedOrdinal?.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private sealed class AssetKeyRow
    {
        public Guid Id { get; set; }
        public int IsStable { get; set; }
    }

    private sealed class LockRow
    {
        public int Locked { get; set; }
        public Guid? CollectionId { get; set; }
        public string? SeriesQid { get; set; }
        public string? MediaType { get; set; }
    }

    private sealed class WorkRow
    {
        public string? MediaType { get; set; }
        public Guid? CollectionId { get; set; }
        public int Locked { get; set; }
    }

    private sealed class ShelfRow
    {
        public Guid CollectionId { get; set; }
        public string? CollectionType { get; set; }
        public string? Label { get; set; }
        public string? SeriesQid { get; set; }
        public string? MediaType { get; set; }
    }

    private sealed class ListedRow
    {
        public Guid CollectionId { get; set; }
        public string? Label { get; set; }
        public string? SeriesQid { get; set; }
        public string MediaType { get; set; } = string.Empty;
        public string? Scope { get; set; }
        public string? RawOrdinal { get; set; }
        public double? ParsedOrdinal { get; set; }
        public bool IsExpanded { get; set; }
    }
}
