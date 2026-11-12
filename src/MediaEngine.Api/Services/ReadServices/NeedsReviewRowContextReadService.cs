using Dapper;
using MediaEngine.Application.ReadModels;
using MediaEngine.Application.Services;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.ReadServices;

/// <summary>
/// Reads, for a whole Needs Review list at once, the family each row's Work sits in and the pictures the Engine can really serve. Every
/// question is one batched query over the ids given (never one query per row): the asset to Work map, the climb up <c>parent_work_id</c> to
/// the top parent, the family titles, and which Works hold a cover.
/// </summary>
public sealed class NeedsReviewRowContextReadService(IDatabaseConnection db) : INeedsReviewRowContextReadService
{
    // A parent chain is episode, season, show at most; the cap only guards against a loop in bad data.
    private const int MaxClimb = 6;

    public Task<NeedsReviewRowContexts> ReadAsync(
        IReadOnlyCollection<Guid> assetIds, IReadOnlyCollection<Guid> workIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assetIds);
        ArgumentNullException.ThrowIfNull(workIds);
        ct.ThrowIfCancellationRequested();
        if (assetIds.Count == 0 && workIds.Count == 0)
        {
            return Task.FromResult(NeedsReviewRowContexts.Empty);
        }

        using var conn = db.CreateConnection();

        // 1. Each file's Work (and that Work's media type, which decides whose cover the file shows).
        var assetWorks = assetIds.Count == 0
            ? []
            : conn.Query<AssetWorkRow>("""
                -- readiness: Needs Review lists items by definition not ready; this only looks up the Works of files already on the list
                SELECT ma.id AS AssetId, w.id AS WorkId, w.media_type AS MediaType
                FROM media_assets ma
                JOIN editions e ON e.id = ma.edition_id
                JOIN works w ON w.id = e.work_id
                WHERE ma.id IN (SELECT unhex(value) FROM json_each(@ids));
                """, new { ids = GuidSql.ToJsonHexArray(assetIds.Distinct()) }).AsList();

        var allWorkIds = assetWorks.Select(row => row.WorkId).Concat(workIds).Distinct().ToList();

        // 2. Climb parent_work_id to each Work's top parent. A Work with no parent has no row here.
        var tops = allWorkIds.Count == 0
            ? new Dictionary<Guid, Guid>()
            : conn.Query<TopRow>($"""
                -- readiness: Needs Review lists items by definition not ready; this only climbs from Works already on the list
                WITH RECURSIVE chain(start_id, id, parent_id, depth) AS (
                    SELECT w.id, w.id, w.parent_work_id, 0
                    FROM works w
                    WHERE w.id IN (SELECT unhex(value) FROM json_each(@ids))
                    UNION ALL
                    SELECT c.start_id, w.id, w.parent_work_id, c.depth + 1
                    FROM chain c
                    JOIN works w ON w.id = c.parent_id
                    WHERE c.depth < {MaxClimb}
                )
                SELECT start_id AS StartId, id AS TopId
                FROM chain
                WHERE parent_id IS NULL AND depth > 0;
                """, new { ids = GuidSql.ToJsonHexArray(allWorkIds) })
                .GroupBy(row => row.StartId)
                .ToDictionary(group => group.Key, group => group.First().TopId);

        var topIds = tops.Values.Distinct().ToList();

        // 3. The families' titles, the same way the library reads a parent's title.
        var titles = topIds.Count == 0
            ? new Dictionary<Guid, string?>()
            : conn.Query<TitleRow>("""
                -- readiness: lookup of the titles of the given parent Works by id
                SELECT w.id AS Id,
                       (SELECT cv.value FROM canonical_values cv
                        WHERE cv.entity_id = w.id AND cv.key IN ('title', 'album', 'show_name', 'series') AND cv.value <> ''
                        ORDER BY CASE cv.key WHEN 'title' THEN 0 WHEN 'album' THEN 1 WHEN 'show_name' THEN 2 ELSE 3 END LIMIT 1) AS Title
                FROM works w
                WHERE w.id IN (SELECT unhex(value) FROM json_each(@ids));
                """, new { ids = GuidSql.ToJsonHexArray(topIds) })
                .ToDictionary(row => row.Id, row => row.Title);

        // 4. Which Works hold a preferred cover: one the asset cover route can serve from disk, or any cover the Work's own route can fetch.
        var coverIds = topIds.Concat(allWorkIds).Distinct().ToList();
        var covers = coverIds.Count == 0
            ? new Dictionary<Guid, CoverRow>()
            : conn.Query<CoverRow>("""
                SELECT ea.entity_id AS EntityId,
                       MAX(CASE WHEN COALESCE(ea.local_image_path, '') <> '' THEN 1 ELSE 0 END) AS HasLocal,
                       MAX(CASE WHEN COALESCE(ea.local_image_path, '') <> '' OR COALESCE(ea.image_url, '') <> '' THEN 1 ELSE 0 END) AS HasAny
                FROM entity_assets ea
                WHERE ea.entity_type = 'Work' AND ea.asset_type = 'CoverArt' AND ea.is_preferred = 1
                  AND ea.entity_id IN (SELECT unhex(value) FROM json_each(@ids))
                GROUP BY ea.entity_id;
                """, new { ids = GuidSql.ToJsonHexArray(coverIds) })
                .ToDictionary(row => row.EntityId);

        NeedsReviewRowContext ForWork(Guid workId, string? coverUrl)
        {
            if (!tops.TryGetValue(workId, out var top))
            {
                return new NeedsReviewRowContext(workId, null, null, null, coverUrl);
            }

            // A top parent with no title is no family a person could name: the row shows on its own, like one with no parent.
            if (!titles.TryGetValue(top, out var title) || string.IsNullOrWhiteSpace(title))
            {
                return new NeedsReviewRowContext(workId, null, null, null, coverUrl);
            }

            var familyCover = covers.TryGetValue(top, out var cover) && cover.HasAny != 0 ? $"/stream/entity/work/{top:D}/cover" : null;
            return new NeedsReviewRowContext(workId, top, title, familyCover, coverUrl);
        }

        var byAsset = new Dictionary<Guid, NeedsReviewRowContext>();
        foreach (var row in assetWorks)
        {
            // The asset cover route serves the preferred cover of the file's cover owner: the Work itself for books, audiobooks and comics,
            // otherwise its top parent (an episode shows its show's picture). It serves from disk only, so require a saved copy.
            var owner = IsSelfScoped(row.MediaType) || !tops.TryGetValue(row.WorkId, out var top) ? row.WorkId : top;
            var coverUrl = covers.TryGetValue(owner, out var cover) && cover.HasLocal != 0 ? $"/stream/{row.AssetId:D}/cover" : null;
            byAsset[row.AssetId] = ForWork(row.WorkId, coverUrl);
        }

        var byWork = new Dictionary<Guid, NeedsReviewRowContext>();
        foreach (var workId in workIds.Distinct())
        {
            byWork[workId] = ForWork(workId, null);
        }

        return Task.FromResult(new NeedsReviewRowContexts { ByAsset = byAsset, ByWork = byWork });
    }

    // The same split the asset cover route makes (StreamEndpoints.ResolveArtworkOwnerEntityIdAsync).
    private static bool IsSelfScoped(string? mediaType) =>
        string.Equals(mediaType, "Books", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mediaType, "Audiobooks", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mediaType, "Comics", StringComparison.OrdinalIgnoreCase);

    private sealed class AssetWorkRow
    {
        public Guid AssetId { get; set; }
        public Guid WorkId { get; set; }
        public string? MediaType { get; set; }
    }

    private sealed class TopRow
    {
        public Guid StartId { get; set; }
        public Guid TopId { get; set; }
    }

    private sealed class TitleRow
    {
        public Guid Id { get; set; }
        public string? Title { get; set; }
    }

    private sealed class CoverRow
    {
        public Guid EntityId { get; set; }
        public long HasLocal { get; set; }
        public long HasAny { get; set; }
    }
}
