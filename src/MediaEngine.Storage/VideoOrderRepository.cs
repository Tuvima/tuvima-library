using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

public sealed class VideoOrderRepository : IVideoOrderRepository
{
    private readonly IDatabaseConnection _db;

    public VideoOrderRepository(IDatabaseConnection db) => _db = db;

    public Task<IReadOnlyList<VideoOrderGroup>> LoadAsync(Guid workId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();

        var self = conn.QuerySingleOrDefault<(Guid Id, Guid? ParentId, string Kind, string MediaType)?>(
            "SELECT id AS Id, parent_work_id AS ParentId, work_kind AS Kind, media_type AS MediaType FROM works WHERE id = @workId;",
            new { workId });
        if (self is not { MediaType: "Videos" })
        {
            return Task.FromResult<IReadOnlyList<VideoOrderGroup>>([]);
        }

        // Walk up to the series: a video's parent is the series or its section, and a section's parent is the series.
        var rootId = workId;
        var current = self.Value;
        while (current.ParentId is { } parentId)
        {
            rootId = parentId;
            current = conn.QuerySingle<(Guid Id, Guid? ParentId, string Kind, string MediaType)>(
                "SELECT id AS Id, parent_work_id AS ParentId, work_kind AS Kind, media_type AS MediaType FROM works WHERE id = @parentId;",
                new { parentId });
        }

        var sections = conn.Query<Guid>(
            "SELECT id FROM works WHERE parent_work_id = @rootId AND work_kind = 'parent' ORDER BY COALESCE(ordinal, 2147483647), id;",
            new { rootId }).ToList();
        var parents = sections.Count > 0 ? sections : [rootId];

        var groups = new List<VideoOrderGroup>();
        foreach (var parent in parents)
        {
            var entries = conn.Query<(Guid WorkId, Guid AssetId, string? Title)>(
                """
                SELECT w.id AS WorkId,
                       ma.id AS AssetId,
                       COALESCE(
                           (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key = 'title' LIMIT 1),
                           (SELECT value FROM canonical_values WHERE entity_id = w.id AND key = 'title' LIMIT 1)) AS Title
                FROM works w
                JOIN editions e ON e.work_id = w.id
                JOIN media_assets ma ON ma.edition_id = e.id
                WHERE w.parent_work_id = @parent AND w.work_kind = 'child' AND ma.status = 'Normal' AND ma.is_orphaned = 0
                GROUP BY w.id
                ORDER BY COALESCE(w.ordinal_sort, w.ordinal, 1e9), w.id;
                """,
                new { parent })
                .Select(row => new VideoOrderEntry(row.WorkId, row.AssetId, string.IsNullOrWhiteSpace(row.Title) ? "Untitled" : row.Title))
                .ToList();
            if (entries.Count == 0)
            {
                continue;
            }

            var title = conn.QuerySingleOrDefault<string>(
                "SELECT value FROM canonical_values WHERE entity_id = @parent AND key = 'title' LIMIT 1;",
                new { parent });
            groups.Add(new VideoOrderGroup(parent, string.IsNullOrWhiteSpace(title) ? "Videos" : title, entries));
        }

        return Task.FromResult<IReadOnlyList<VideoOrderGroup>>(groups);
    }

    public Task<IReadOnlyList<VideoOrderPlacement>?> ReorderAsync(
        Guid parentWorkId, IReadOnlyList<Guid> orderedWorkIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return _db.ExecuteWriteAsync<IReadOnlyList<VideoOrderPlacement>?>(
            (conn, tx, _) => Reorder(conn, tx, parentWorkId, orderedWorkIds), ct);
    }

    private static IReadOnlyList<VideoOrderPlacement>? Reorder(
        SqliteConnection conn, SqliteTransaction tx, Guid parentWorkId, IReadOnlyList<Guid> orderedWorkIds)
    {
        var current = conn.Query<(Guid WorkId, Guid AssetId)>(
            """
            SELECT w.id AS WorkId, MIN(ma.id) AS AssetId
            FROM works w
            JOIN editions e ON e.work_id = w.id
            JOIN media_assets ma ON ma.edition_id = e.id
            WHERE w.parent_work_id = @parentWorkId AND w.work_kind = 'child' AND w.media_type = 'Videos'
              AND ma.status = 'Normal' AND ma.is_orphaned = 0
            GROUP BY w.id;
            """,
            new { parentWorkId }, tx).ToDictionary(row => row.WorkId, row => row.AssetId);

        if (current.Count == 0
            || orderedWorkIds.Count != current.Count
            || orderedWorkIds.Distinct().Count() != orderedWorkIds.Count
            || orderedWorkIds.Any(id => !current.ContainsKey(id)))
        {
            return null;
        }

        // Positions are unique inside a parent, so park every video on a free negative number first.
        for (var i = 0; i < orderedWorkIds.Count; i++)
        {
            conn.Execute("UPDATE works SET ordinal_sort = @sort WHERE id = @id;", new { sort = -(i + 1d), id = orderedWorkIds[i] }, tx);
        }

        var placements = new List<VideoOrderPlacement>(orderedWorkIds.Count);
        for (var i = 0; i < orderedWorkIds.Count; i++)
        {
            var position = i + 1;
            conn.Execute("UPDATE works SET ordinal_sort = @sort, ordinal = @position WHERE id = @id;", new { sort = (double)position, position, id = orderedWorkIds[i] }, tx);
            placements.Add(new VideoOrderPlacement(orderedWorkIds[i], current[orderedWorkIds[i]], position));
        }

        return placements;
    }
}
