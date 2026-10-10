using Dapper;
using MediaEngine.Api.Models;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Collections;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.ReadServices;

public sealed class CollectionMediaLookupReadService(IDatabaseConnection db) : ICollectionMediaLookupReadService
{
    public async Task<List<CollectionResolvedItemDto>> ResolveMetadataAsync(
        IReadOnlyList<Guid> workIds,
        CancellationToken ct)
    {
        if (workIds.Count == 0)
        {
            return [];
        }

        using var conn = db.CreateConnection();
        var resolvedDisplayYearSql = MediaDateSql.DisplayOriginalYear(
            "w.id",
            "COALESCE(gp.id, p.id, w.id)",
            "ra.AssetId",
            "w.media_type");
        var rows = await conn.QueryAsync<ResolvedMetadataRow>(new CommandDefinition(
            $"""
            WITH RECURSIVE work_tree(RootWorkId, WorkId) AS (
                SELECT w.id, w.id
                FROM works w
                WHERE w.id IN @WorkIds
                UNION ALL
                SELECT work_tree.RootWorkId, child.id
                FROM works child
                INNER JOIN work_tree ON child.parent_work_id = work_tree.WorkId
            ),
            representative_assets AS (
                SELECT work_tree.RootWorkId AS WorkId, MIN(ma.id) AS AssetId
                FROM work_tree
                INNER JOIN work_owned_assets woa ON woa.work_id = work_tree.WorkId
                INNER JOIN media_assets ma ON ma.id = woa.asset_id
                GROUP BY work_tree.RootWorkId
            )
            SELECT w.id AS WorkId,
                   w.media_type AS MediaType,
                   ra.AssetId,
                   (SELECT ea.id
                    FROM entity_assets ea
                    WHERE ea.entity_id = w.id
                      AND ea.entity_type = 'Work'
                      AND ea.asset_type IN ('CoverArt', 'SeasonPoster')
                      AND COALESCE(
                          NULLIF(ea.local_image_path_s, ''),
                          NULLIF(ea.local_image_path_m, ''),
                          NULLIF(ea.local_image_path, '')) IS NOT NULL
                    ORDER BY ea.is_user_override DESC, ea.is_preferred DESC, ea.created_at DESC
                    LIMIT 1) AS CoverAssetId,
                   CAST(COALESCE(
                       (SELECT value FROM canonical_values WHERE entity_id = ra.AssetId AND key = 'title' LIMIT 1),
                       (SELECT value FROM canonical_values WHERE entity_id = w.id AND key = 'title' LIMIT 1),
                       (SELECT value FROM canonical_values WHERE entity_id = COALESCE(gp.id, p.id, w.id) AND key = 'title' LIMIT 1),
                       'Unknown') AS TEXT) AS Title,
                   CAST(COALESCE(
                       (SELECT value
                        FROM canonical_value_arrays
                        WHERE entity_id = ra.AssetId
                          AND key IN ('album_artist', 'artist', 'author', 'director')
                        ORDER BY CASE key WHEN 'album_artist' THEN 1 WHEN 'artist' THEN 2 WHEN 'author' THEN 3 ELSE 4 END, ordinal
                        LIMIT 1),
                       (SELECT value
                        FROM canonical_value_arrays
                        WHERE entity_id = COALESCE(gp.id, p.id, w.id)
                          AND key IN ('album_artist', 'artist', 'author', 'director')
                        ORDER BY CASE key WHEN 'album_artist' THEN 1 WHEN 'artist' THEN 2 WHEN 'author' THEN 3 ELSE 4 END, ordinal
                        LIMIT 1)) AS TEXT) AS Creator,
                   CAST({resolvedDisplayYearSql} AS TEXT) AS Year,
                   CAST(COALESCE(
                       (SELECT NULLIF(value, '')
                        FROM canonical_values
                        WHERE entity_id = w.id AND key IN ('cover_url', 'cover')
                        ORDER BY CASE key WHEN 'cover_url' THEN 1 ELSE 2 END
                        LIMIT 1),
                       (SELECT NULLIF(value, '')
                        FROM canonical_values
                        WHERE entity_id = w.id AND key IN ('poster_url', 'poster')
                        ORDER BY CASE key WHEN 'poster_url' THEN 1 ELSE 2 END
                        LIMIT 1),
                       NULL
                   ) AS TEXT) AS CoverUrl
            FROM works w
            LEFT JOIN works p ON p.id = w.parent_work_id
            LEFT JOIN works gp ON gp.id = p.parent_work_id
            LEFT JOIN representative_assets ra ON ra.WorkId = w.id
            WHERE w.id IN @WorkIds
            """,
            new { WorkIds = workIds.Select(GuidSql.ToBlob).ToArray() },
            cancellationToken: ct)).ConfigureAwait(false);

        var byId = rows.ToDictionary(row => row.WorkId);
        return workIds.Where(byId.ContainsKey).Select(workId =>
        {
            var row = byId[workId];
            return new CollectionResolvedItemDto
            {
                EntityId = row.WorkId,
                Title = row.Title,
                Creator = row.Creator,
                MediaType = row.MediaType,
                CoverUrl = row.CoverAssetId.HasValue
                    ? $"/stream/artwork/{row.CoverAssetId.Value:D}"
                    : !string.IsNullOrWhiteSpace(row.CoverUrl)
                        ? row.CoverUrl
                        : row.AssetId.HasValue ? $"/stream/{row.AssetId.Value:D}/cover" : null,
                Year = row.Year,
            };
        }).ToList();
    }

    public async Task<Dictionary<string, int>> CountMediaTypesAsync(
        IReadOnlyList<Guid> workIds,
        CancellationToken ct)
    {
        if (workIds.Count == 0)
        {
            return [];
        }

        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<MediaTypeCountRow>(new CommandDefinition(
            """
            WITH requested(WorkHex) AS (
                SELECT UPPER(CAST(value AS TEXT))
                FROM json_each(@WorkIdsJson)
            )
            SELECT w.media_type AS MediaType,
                   COUNT(*) AS ItemCount
            FROM works w
            INNER JOIN requested ON hex(w.id) = requested.WorkHex
            GROUP BY w.media_type
            ORDER BY w.media_type COLLATE NOCASE;
            """,
            new
            {
                WorkIdsJson = System.Text.Json.JsonSerializer.Serialize(
                    workIds.Select(workId => workId.ToString("N"))),
            },
            cancellationToken: ct)).ConfigureAwait(false);

        return rows.ToDictionary(
            row => row.MediaType,
            row => checked((int)row.ItemCount),
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<List<CollectionMediaLookupDto>> LookupAsync(
        string? query,
        string? mediaTypes,
        IReadOnlySet<Guid> existingWorkIds,
        IReadOnlySet<Guid> allowedWorkIds,
        int? offset,
        int? limit,
        CancellationToken ct)
    {
        if (allowedWorkIds.Count == 0)
        {
            return [];
        }

        var take = Math.Clamp(limit ?? 24, 1, 100);
        var skip = Math.Max(0, offset ?? 0);
        var normalizedQuery = (query ?? string.Empty).Trim();
        var searchLike = string.IsNullOrWhiteSpace(normalizedQuery) ? null : $"%{normalizedQuery}%";
        var requestedMediaTypes = (mediaTypes ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        using var conn = db.CreateConnection();
        var visibleWorkPredicate = HomeVisibilitySql.VisibleWorkPredicate("w.id", "w.curator_state", "w.is_catalog_only");
        var visibleAssetPredicate = HomeVisibilitySql.VisibleAssetPathPredicate("ma.file_path_root");
        var lookupDisplayYearSql = MediaDateSql.DisplayOriginalYear(
            "w.id",
            "COALESCE(gp.id, p.id, w.id)",
            "ra.AssetId",
            "w.media_type");
        var rows = (await conn.QueryAsync<CollectionMediaLookupRow>(new CommandDefinition(
            $"""
            WITH RECURSIVE work_descendants(root_id, work_id, depth) AS (
                SELECT id, id, 0
                FROM works
                UNION ALL
                SELECT work_descendants.root_id, child.id, work_descendants.depth + 1
                FROM works child
                INNER JOIN work_descendants ON child.parent_work_id = work_descendants.work_id
            ),
            representative_assets AS (
                SELECT work_descendants.root_id AS WorkId,
                       MIN(ma.id) AS AssetId
                FROM work_descendants
                INNER JOIN work_owned_assets woa ON woa.work_id = work_descendants.work_id
                INNER JOIN media_assets ma ON ma.id = woa.asset_id
                WHERE {visibleAssetPredicate}
                GROUP BY work_descendants.root_id
            )
            SELECT CASE
                       WHEN w.work_kind = 'child' THEN COALESCE(gp.id, p.id, w.id)
                       WHEN w.work_kind = 'parent' AND p.id IS NOT NULL THEN COALESCE(gp.id, p.id, w.id)
                       ELSE w.id
                   END AS WorkId,
                   COALESCE(gp.collection_id, p.collection_id, w.collection_id) AS CollectionId,
                   w.media_type AS MediaType,
                   CASE
                       WHEN COALESCE(gp.id, p.id, w.id) != w.id THEN 'parent'
                       ELSE w.work_kind
                   END AS WorkKind,
                   w.ordinal AS Ordinal,
                   ra.AssetId,
                   COALESCE(NULLIF(show_root.value, ''), NULLIF(title_root.value, ''), NULLIF(episode_title.value, ''), NULLIF(title_work.value, ''), NULLIF(title_asset.value, ''), 'Untitled') AS Title,
                   COALESCE(NULLIF(author_work.value, ''), NULLIF(artist_root.value, ''), NULLIF(artist_work.value, ''), NULLIF(author_asset.value, ''), NULLIF(artist_asset.value, '')) AS Creator,
                   {lookupDisplayYearSql} AS Year,
                   CAST(NULLIF(cover_root.value, '') AS TEXT) AS RootArtworkUrl,
                   COALESCE(NULLIF(cover_root.value, ''), NULLIF(cover_work.value, ''), NULLIF(cover_asset.value, '')) AS ArtworkUrl,
                   COALESCE(NULLIF(show_root.value, ''), NULLIF(show_work.value, ''), NULLIF(title_root.value, '')) AS ShowName,
                   COALESCE(NULLIF(season_work.value, ''), NULLIF(season_asset.value, '')) AS SeasonNumber,
                   COALESCE(NULLIF(album_root.value, ''), NULLIF(album_work.value, ''), NULLIF(album_asset.value, '')) AS Album,
                   COALESCE(NULLIF(artist_root.value, ''), NULLIF(artist_work.value, ''), NULLIF(artist_asset.value, '')) AS Artist
            FROM works w
            LEFT JOIN works p ON p.id = w.parent_work_id
            LEFT JOIN works gp ON gp.id = p.parent_work_id
            LEFT JOIN representative_assets ra ON ra.WorkId = w.id
            LEFT JOIN canonical_values title_work ON title_work.entity_id = w.id AND title_work.key = 'title'
            LEFT JOIN canonical_values title_root ON title_root.entity_id = COALESCE(gp.id, p.id, w.id) AND title_root.key = 'title'
            LEFT JOIN canonical_values episode_title ON episode_title.entity_id = w.id AND episode_title.key = 'episode_title'
            LEFT JOIN canonical_value_arrays author_work ON author_work.entity_id = w.id AND author_work.key = 'author' AND author_work.ordinal = 0
            LEFT JOIN canonical_value_arrays artist_work ON artist_work.entity_id = w.id AND artist_work.key = 'artist' AND artist_work.ordinal = 0
            LEFT JOIN canonical_value_arrays artist_root ON artist_root.entity_id = COALESCE(gp.id, p.id, w.id) AND artist_root.key = 'album_artist' AND artist_root.ordinal = 0
            LEFT JOIN canonical_values cover_work ON cover_work.entity_id = w.id AND cover_work.key IN ('cover_url', 'cover', 'poster_url', 'poster', 'episode_still_url', 'episode_still', 'still_url', 'still')
            LEFT JOIN canonical_values cover_root ON cover_root.entity_id = COALESCE(gp.id, p.id, w.id) AND cover_root.key IN ('cover_url', 'cover', 'poster_url', 'poster')
            LEFT JOIN canonical_values show_work ON show_work.entity_id = w.id AND show_work.key = 'show_name'
            LEFT JOIN canonical_values show_root ON show_root.entity_id = COALESCE(gp.id, p.id, w.id) AND show_root.key IN ('show_name', 'title')
            LEFT JOIN canonical_values season_work ON season_work.entity_id = w.id AND season_work.key = 'season_number'
            LEFT JOIN canonical_values album_work ON album_work.entity_id = w.id AND album_work.key = 'album'
            LEFT JOIN canonical_values album_root ON album_root.entity_id = COALESCE(gp.id, p.id, w.id) AND album_root.key = 'album'
            LEFT JOIN canonical_values title_asset ON title_asset.entity_id = ra.AssetId AND title_asset.key = 'title'
            LEFT JOIN canonical_value_arrays author_asset ON author_asset.entity_id = ra.AssetId AND author_asset.key = 'author' AND author_asset.ordinal = 0
            LEFT JOIN canonical_value_arrays artist_asset ON artist_asset.entity_id = ra.AssetId AND artist_asset.key = 'artist' AND artist_asset.ordinal = 0
            LEFT JOIN canonical_values cover_asset ON cover_asset.entity_id = ra.AssetId AND cover_asset.key IN ('cover_url', 'cover', 'poster_url', 'poster', 'episode_still_url', 'episode_still', 'still_url', 'still')
            LEFT JOIN canonical_values season_asset ON season_asset.entity_id = ra.AssetId AND season_asset.key = 'season_number'
            LEFT JOIN canonical_values album_asset ON album_asset.entity_id = ra.AssetId AND album_asset.key = 'album'
            WHERE w.work_kind != 'catalog'
              AND ra.AssetId IS NOT NULL
              AND {visibleWorkPredicate}
              AND w.id IN @allowedWorkIds
              AND (@mediaTypeCount = 0 OR w.media_type IN @mediaTypes)
              AND (
                    @searchLike IS NULL
                 OR title_work.value LIKE @searchLike
                 OR title_asset.value LIKE @searchLike
                 OR episode_title.value LIKE @searchLike
                 OR author_work.value LIKE @searchLike
                 OR artist_work.value LIKE @searchLike
                 OR artist_root.value LIKE @searchLike
                 OR album_root.value LIKE @searchLike
                 OR album_work.value LIKE @searchLike
                 OR show_root.value LIKE @searchLike
                 OR show_work.value LIKE @searchLike
              )
            ORDER BY Title COLLATE NOCASE, w.id
            LIMIT @limit OFFSET @offset;
            """,
            new
            {
                searchLike,
                mediaTypes = requestedMediaTypes,
                mediaTypeCount = requestedMediaTypes.Length,
                allowedWorkIds = allowedWorkIds.Select(GuidSql.ToBlob).ToArray(),
                limit = take,
                offset = skip,
            },
            cancellationToken: ct))).ToList();

        return rows
            .GroupBy(row => row.WorkId)
            .Select(group => group.First())
            .Select(row => new CollectionMediaLookupDto
            {
                WorkId = row.WorkId,
                Title = row.Title,
                Subtitle = BuildLookupSubtitle(row),
                Creator = row.Creator,
                MediaType = row.MediaType,
                Year = row.Year,
                ArtworkUrl = IsTvContainer(row.WorkKind, row.MediaType)
                ? row.RootArtworkUrl
                : !string.IsNullOrWhiteSpace(row.ArtworkUrl)
                    ? row.ArtworkUrl
                    : row.AssetId.HasValue ? $"/stream/{row.AssetId.Value}/cover" : null,
                ParentContext = BuildLookupParentContext(row),
                Route = BuildLookupRoute(row),
                AlreadyInCollection = existingWorkIds.Contains(row.WorkId),
            }).ToList();
    }

    public async Task<List<CollectionItemDto>> ResolveItemsAsync(
        Guid collectionId,
        IReadOnlyList<CollectionItem> items,
        CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return [];
        }

        using var conn = db.CreateConnection();
        var visibleWorkPredicate = HomeVisibilitySql.VisibleWorkPredicate("w.id", "w.curator_state", "w.is_catalog_only");
        var visibleAssetPredicate = HomeVisibilitySql.VisibleAssetPathPredicate("ma.file_path_root");
        var rows = (await conn.QueryAsync<ResolvedCollectionItemRow>(new CommandDefinition(
            $"""
            WITH requested(ItemId, SourceWorkHex, SortOrder) AS (
                SELECT CAST(value ->> 'id' AS TEXT),
                       UPPER(CAST(value ->> 'work_id_hex' AS TEXT)),
                       CAST(value ->> 'sort_order' AS INTEGER)
                FROM json_each(@ItemsJson)
            ),
            display_work AS (
                SELECT requested.ItemId,
                       requested.SortOrder,
                       CASE
                           WHEN w.work_kind = 'child' THEN COALESCE(gp.id, p.id, w.id)
                           WHEN w.work_kind = 'parent' AND p.id IS NOT NULL THEN COALESCE(gp.id, p.id, w.id)
                           ELSE w.id
                       END AS WorkId
                FROM requested
                INNER JOIN works w ON hex(w.id) = requested.SourceWorkHex
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
            ),
            work_tree(RootWorkId, WorkId) AS (
                SELECT display_work.WorkId,
                       display_work.WorkId
                FROM display_work
                UNION ALL
                SELECT work_tree.RootWorkId,
                       child.id
                FROM works child
                INNER JOIN work_tree ON child.parent_work_id = work_tree.WorkId
            ),
            representative_assets AS (
                SELECT work_tree.RootWorkId AS WorkId,
                       MIN(ma.id) AS AssetId
                FROM work_tree
                INNER JOIN work_owned_assets woa ON woa.work_id = work_tree.WorkId
                INNER JOIN media_assets ma ON ma.id = woa.asset_id
                WHERE {visibleAssetPredicate}
                GROUP BY work_tree.RootWorkId
            )
            SELECT CAST(display_work.ItemId AS TEXT) AS ItemId,
                   display_work.WorkId,
                   CAST(COALESCE(NULLIF(title_work.value, ''), NULLIF(show_name.value, ''), NULLIF(series_item.item_label, ''), 'Untitled') AS TEXT) AS Title,
                   CAST(COALESCE(NULLIF(author_work.value, ''), NULLIF(artist_work.value, '')) AS TEXT) AS Creator,
                   w.media_type AS MediaType,
                   w.work_kind AS WorkKind,
                   CAST(NULLIF(cover_work.value, '') AS TEXT) AS CoverUrl,
                   (SELECT ea.id
                    FROM entity_assets ea
                    WHERE ea.entity_id = display_work.WorkId
                      AND ea.entity_type = 'Work'
                      AND ea.asset_type IN ('CoverArt', 'SeasonPoster')
                      AND COALESCE(
                          NULLIF(ea.local_image_path_s, ''),
                          NULLIF(ea.local_image_path_m, ''),
                          NULLIF(ea.local_image_path, '')) IS NOT NULL
                    ORDER BY ea.is_user_override DESC, ea.is_preferred DESC, ea.created_at DESC
                    LIMIT 1) AS CoverAssetId,
                   ra.AssetId,
                   CAST(display_work.SortOrder AS INTEGER) AS SortOrder
            FROM display_work
            INNER JOIN works w ON w.id = display_work.WorkId
            LEFT JOIN representative_assets ra ON ra.WorkId = w.id
            LEFT JOIN canonical_values title_work ON title_work.entity_id = w.id AND title_work.key = 'title'
            LEFT JOIN canonical_values show_name ON show_name.entity_id = w.id AND show_name.key = 'show_name'
            LEFT JOIN canonical_value_arrays author_work ON author_work.entity_id = w.id AND author_work.key = 'author' AND author_work.ordinal = 0
            LEFT JOIN canonical_value_arrays artist_work ON artist_work.entity_id = w.id AND artist_work.key IN ('artist', 'album_artist') AND artist_work.ordinal = 0
            LEFT JOIN canonical_values cover_work ON cover_work.entity_id = w.id AND cover_work.key IN ('cover_url', 'cover', 'poster_url', 'poster')
            LEFT JOIN series_manifest_items series_item ON series_item.linked_work_id = w.id AND series_item.collection_id = @CollectionId
            WHERE ({visibleWorkPredicate} OR ra.AssetId IS NOT NULL)
            ORDER BY display_work.SortOrder, Title COLLATE NOCASE, w.id
            """,
            new
            {
                CollectionId = collectionId,
                ItemsJson = System.Text.Json.JsonSerializer.Serialize(items.Select(item => new
                {
                    id = item.Id.ToString("D"),
                    work_id_hex = item.WorkId.ToString("N"),
                    sort_order = item.SortOrder,
                })),
            },
            cancellationToken: ct))).ToList();

        return rows
            .GroupBy(row => row.WorkId)
            .Select(group => group.OrderBy(row => row.SortOrder).First())
            .Select(row => new CollectionItemDto
            {
                Id = Guid.ParseExact(row.ItemId, "D"),
                WorkId = row.WorkId,
                Title = row.Title,
                Creator = row.Creator,
                MediaType = row.MediaType,
                CoverUrl = row.CoverAssetId.HasValue
                ? $"/stream/artwork/{row.CoverAssetId.Value:D}"
                : !string.IsNullOrWhiteSpace(row.CoverUrl)
                    ? row.CoverUrl
                    : row.AssetId.HasValue ? $"/stream/{row.AssetId.Value:D}/cover" : null,
                SortOrder = row.SortOrder,
                DetailRoute = BuildResolvedItemRoute(row),
            }).ToList();
    }

    private static bool IsTvContainer(string? workKind, string mediaType) =>
        string.Equals(workKind, "parent", StringComparison.OrdinalIgnoreCase)
        && mediaType.Contains("TV", StringComparison.OrdinalIgnoreCase);

    private sealed class MediaTypeCountRow
    {
        public string MediaType { get; init; } = string.Empty;
        public long ItemCount { get; init; }
    }

    private static string BuildResolvedItemRoute(ResolvedCollectionItemRow row)
    {
        if (IsTvContainer(row.WorkKind, row.MediaType))
        {
            return $"/details/tvshow/{row.WorkId:D}?context=watch";
        }

        if (string.Equals(row.WorkKind, "parent", StringComparison.OrdinalIgnoreCase)
            && row.MediaType.Contains("Music", StringComparison.OrdinalIgnoreCase))
        {
            return $"/details/musicalbum/{row.WorkId:D}?context=listen";
        }

        var context = row.MediaType.Contains("TV", StringComparison.OrdinalIgnoreCase)
                      || row.MediaType.Contains("movie", StringComparison.OrdinalIgnoreCase)
            ? "watch"
            : row.MediaType.Contains("music", StringComparison.OrdinalIgnoreCase)
              || row.MediaType.Contains("audio", StringComparison.OrdinalIgnoreCase)
                ? "listen"
                : row.MediaType.Contains("comic", StringComparison.OrdinalIgnoreCase)
                    ? "comics"
                    : "read";
        return $"/details/work/{row.WorkId:D}?context={context}";
    }

    private static string? BuildLookupSubtitle(CollectionMediaLookupRow row)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(row.Creator))
        {
            parts.Add(row.Creator);
        }

        if (!string.IsNullOrWhiteSpace(row.Year))
        {
            parts.Add(row.Year);
        }

        if (!string.IsNullOrWhiteSpace(row.MediaType))
        {
            parts.Add(row.MediaType);
        }

        return parts.Count == 0 ? null : string.Join(" | ", parts);
    }

    private static string? BuildLookupParentContext(CollectionMediaLookupRow row)
    {
        if (string.Equals(row.WorkKind, "parent", StringComparison.OrdinalIgnoreCase))
        {
            return row.MediaType.Contains("Music", StringComparison.OrdinalIgnoreCase) ? "Album"
                : row.MediaType.Contains("TV", StringComparison.OrdinalIgnoreCase) ? "Series"
                : row.MediaType.Contains("comic", StringComparison.OrdinalIgnoreCase) ? "Series"
                : row.MediaType.Contains("book", StringComparison.OrdinalIgnoreCase) ? "Series"
                : "Container";
        }

        if (row.MediaType.Contains("TV", StringComparison.OrdinalIgnoreCase))
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(row.ShowName))
            {
                parts.Add(row.ShowName);
            }

            if (!string.IsNullOrWhiteSpace(row.SeasonNumber))
            {
                parts.Add($"Season {row.SeasonNumber}");
            }

            return parts.Count == 0 ? null : string.Join(" / ", parts);
        }

        if (row.MediaType.Contains("Music", StringComparison.OrdinalIgnoreCase))
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(row.Artist))
            {
                parts.Add(row.Artist);
            }

            if (!string.IsNullOrWhiteSpace(row.Album))
            {
                parts.Add(row.Album);
            }

            return parts.Count == 0 ? null : string.Join(" / ", parts);
        }

        return null;
    }

    private static string BuildLookupRoute(CollectionMediaLookupRow row)
    {
        if (string.Equals(row.WorkKind, "parent", StringComparison.OrdinalIgnoreCase))
        {
            if (row.MediaType.Contains("TV", StringComparison.OrdinalIgnoreCase))
            {
                return $"/details/tvshow/{row.WorkId:D}?context=watch";
            }

            if (row.MediaType.Contains("Music", StringComparison.OrdinalIgnoreCase))
            {
                return $"/details/musicalbum/{row.WorkId:D}?context=listen";
            }

            if (row.MediaType.Contains("audio", StringComparison.OrdinalIgnoreCase))
            {
                return row.CollectionId.HasValue
                    ? $"/details/collection/{row.CollectionId.Value:D}?context=listen"
                    : "/listen/audiobooks?browse=series";
            }

            if (row.MediaType.Contains("comic", StringComparison.OrdinalIgnoreCase))
            {
                return row.CollectionId.HasValue
                    ? $"/details/comicseries/{row.CollectionId.Value:D}?context=comics"
                    : "/read/comics?browse=series";
            }

            if (row.MediaType.Contains("book", StringComparison.OrdinalIgnoreCase))
            {
                return row.CollectionId.HasValue
                    ? $"/details/bookseries/{row.CollectionId.Value:D}?context=read"
                    : "/read/books?browse=series";
            }
        }

        if (row.MediaType.Contains("TV", StringComparison.OrdinalIgnoreCase))
        {
            return $"/details/work/{row.WorkId:D}?context=watch";
        }

        if (row.MediaType.Contains("movie", StringComparison.OrdinalIgnoreCase))
        {
            return $"/details/work/{row.WorkId:D}?context=watch";
        }

        if (row.MediaType.Contains("music", StringComparison.OrdinalIgnoreCase))
        {
            return $"/listen/music?browse=songs&track={row.WorkId:D}";
        }

        if (row.MediaType.Contains("audio", StringComparison.OrdinalIgnoreCase))
        {
            return $"/details/work/{row.WorkId:D}?context=listen";
        }

        if (row.MediaType.Contains("comic", StringComparison.OrdinalIgnoreCase))
        {
            return $"/details/work/{row.WorkId:D}?context=comics";
        }

        return $"/details/work/{row.WorkId:D}?context=read";
    }

    private sealed class CollectionMediaLookupRow
    {
        public Guid WorkId { get; init; }
        public Guid? CollectionId { get; init; }
        public string MediaType { get; init; } = string.Empty;
        public string? WorkKind { get; init; }
        public int? Ordinal { get; init; }
        public Guid? AssetId { get; init; }
        public string Title { get; init; } = string.Empty;
        public string? Creator { get; init; }
        public string? Year { get; init; }
        public string? RootArtworkUrl { get; init; }
        public string? ArtworkUrl { get; init; }
        public string? ShowName { get; init; }
        public string? SeasonNumber { get; init; }
        public string? Album { get; init; }
        public string? Artist { get; init; }
    }

    private sealed class ResolvedCollectionItemRow
    {
        public string ItemId { get; init; } = string.Empty;
        public Guid WorkId { get; init; }
        public string Title { get; init; } = string.Empty;
        public string? Creator { get; init; }
        public string MediaType { get; init; } = string.Empty;
        public string? WorkKind { get; init; }
        public string? CoverUrl { get; init; }
        public Guid? CoverAssetId { get; init; }
        public Guid? AssetId { get; init; }
        public int SortOrder { get; init; }
    }

    private sealed class ResolvedMetadataRow
    {
        public Guid WorkId { get; init; }
        public string MediaType { get; init; } = string.Empty;
        public Guid? AssetId { get; init; }
        public Guid? CoverAssetId { get; init; }
        public string Title { get; init; } = string.Empty;
        public string? Creator { get; init; }
        public string? Year { get; init; }
        public string? CoverUrl { get; init; }
    }
}
