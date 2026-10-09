using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using MediaEngine.Application.ReadModels;
using MediaEngine.Application.Services;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Api.Services.ReadServices;

public sealed class MediaEditorNavigationReadService(
    IDatabaseConnection db,
    ISearchService searchService,
    IHierarchyAlignmentService hierarchyAlignment) : IMediaEditorNavigationReadService, IMediaEditorMembershipReadService
{
    public async Task<MediaEditorNavigatorEnvelope?> GetNavigatorAsync(Guid entityId, CancellationToken ct) =>
        await BuildMediaEditorNavigatorCoreAsync(entityId, db, ct);

    public async Task<IReadOnlyList<MembershipSuggestionEnvelope>> GetSuggestionsAsync(
        Guid entityId,
        string field,
        string? query,
        string? source,
        Guid? parentEntityId,
        string? parentValue,
        CancellationToken ct) =>
        await BuildMembershipSuggestionsAsync(entityId, field, query, source, parentEntityId, parentValue, db, searchService, ct);

    public async Task<MembershipPreviewEnvelope?> PreviewAsync(Guid entityId, MembershipPreviewRequest request, CancellationToken ct) =>
        await hierarchyAlignment.PreviewAsync(entityId, request, ct);

    public async Task<MembershipPreviewEnvelope?> ApplyAsync(Guid entityId, MembershipPreviewRequest request, CancellationToken ct) =>
        await hierarchyAlignment.ApplyAsync(entityId, request, ct);

    private static async Task<EditorLaunchContext?> ResolveEditorLaunchContextAsync(
        Guid entityId,
        IDatabaseConnection db,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = db.CreateConnection();

        var workRow = conn.QueryFirstOrDefault<EditorLaunchWorkRow>("""
            SELECT w.id                 AS WorkId,
                   w.media_type         AS MediaType,
                   w.work_kind          AS WorkKind,
                   w.parent_work_id     AS ParentWorkId,
                   COALESCE(gp.id, p.id, w.id) AS RootWorkId
            FROM works w
            LEFT JOIN works p ON p.id = w.parent_work_id
            LEFT JOIN works gp ON gp.id = p.parent_work_id
            WHERE w.id = @entityId
            LIMIT 1;
            """, new { entityId });

        if (workRow is not null)
        {
            var workId = workRow.WorkId;
            var representativeAsset = GetRepresentativeAssetForWorkTree(conn, workId);
            return new EditorLaunchContext(
                entityId,
                "Work",
                workId,
                workRow.ParentWorkId,
                workRow.RootWorkId ?? workId,
                string.IsNullOrWhiteSpace(workRow.MediaType) ? "Books" : workRow.MediaType,
                string.IsNullOrWhiteSpace(workRow.WorkKind) ? "standalone" : workRow.WorkKind,
                representativeAsset?.AssetId,
                representativeAsset?.FilePath,
                representativeAsset?.WritebackStatus);
        }

        var collectionRow = conn.QueryFirstOrDefault<EditorLaunchCollectionRow>("""
            SELECT target.id             AS WorkId,
                   target.media_type     AS MediaType,
                   target.work_kind      AS WorkKind,
                   target.parent_work_id AS ParentWorkId,
                   target.id             AS RootWorkId
            FROM collections c
            INNER JOIN works w ON w.collection_id = c.id
            LEFT JOIN works p ON p.id = w.parent_work_id
            LEFT JOIN works gp ON gp.id = p.parent_work_id
            INNER JOIN works target ON target.id = COALESCE(gp.id, p.id, w.id)
            WHERE c.id = @entityId
            ORDER BY
                CASE WHEN target.id = w.id THEN 0 ELSE 1 END,
                COALESCE(w.ordinal, 999999),
                w.id
            LIMIT 1;
            """, new { entityId });

        if (collectionRow is not null)
        {
            var collectionWorkId = collectionRow.WorkId;
            var representativeAsset = GetRepresentativeAssetForWorkTree(conn, collectionWorkId);
            return new EditorLaunchContext(
                entityId,
                "Collection",
                collectionWorkId,
                collectionRow.ParentWorkId,
                collectionRow.RootWorkId ?? collectionWorkId,
                string.IsNullOrWhiteSpace(collectionRow.MediaType) ? "Books" : collectionRow.MediaType,
                string.IsNullOrWhiteSpace(collectionRow.WorkKind) ? "standalone" : collectionRow.WorkKind,
                representativeAsset?.AssetId,
                representativeAsset?.FilePath,
                representativeAsset?.WritebackStatus);
        }

        var assetRow = conn.QueryFirstOrDefault<EditorLaunchAssetRow>("""
            SELECT a.id             AS AssetId,
                   a.file_path_root AS FilePath,
                   a.writeback_status AS WritebackStatus,
                   w.id             AS WorkId,
                   w.media_type     AS MediaType,
                   w.work_kind      AS WorkKind,
                   w.parent_work_id AS ParentWorkId,
                   COALESCE(gp.id, p.id, w.id) AS RootWorkId
            FROM media_assets a
            INNER JOIN editions e ON e.id = a.edition_id
            INNER JOIN works w ON w.id = e.work_id
            LEFT JOIN works p ON p.id = w.parent_work_id
            LEFT JOIN works gp ON gp.id = p.parent_work_id
            WHERE a.id = @entityId
            LIMIT 1;
            """, new { entityId });

        if (assetRow is null)
        {
            return null;
        }

        var assetWorkId = assetRow.WorkId;
        return new EditorLaunchContext(
            entityId,
            "MediaAsset",
            assetWorkId,
            assetRow.ParentWorkId,
            assetRow.RootWorkId ?? assetWorkId,
            string.IsNullOrWhiteSpace(assetRow.MediaType) ? "Books" : assetRow.MediaType,
            string.IsNullOrWhiteSpace(assetRow.WorkKind) ? "standalone" : assetRow.WorkKind,
            assetRow.AssetId,
            assetRow.FilePath,
            assetRow.WritebackStatus);
    }

    private static EditorAssetSample? GetRepresentativeAssetForWorkTree(
        System.Data.IDbConnection conn,
        Guid workId) =>
        conn.QueryFirstOrDefault<EditorAssetSample>("""
            WITH RECURSIVE work_tree(id, depth) AS (
                SELECT @workId, 0
                UNION ALL
                SELECT child.id, work_tree.depth + 1
                FROM works child
                INNER JOIN work_tree ON child.parent_work_id = work_tree.id
            )
            SELECT ma.id AS AssetIdValue,
                   ma.file_path_root AS FilePath,
                   ma.writeback_status AS WritebackStatus
            FROM work_tree
            INNER JOIN editions e ON e.work_id = work_tree.id
            INNER JOIN media_assets ma ON ma.edition_id = e.id
            ORDER BY work_tree.depth,
                     ma.id
            LIMIT 1;
            """, new { workId });

    private static string NormalizeEditorMediaType(string? mediaType) =>
        (mediaType ?? string.Empty).Trim() switch
        {
            "Book" => "Books",
            "Comic" => "Comics",
            "" => "Books",
            var value => value,
        };

    private static Guid? TryParseGuid(string? value) =>
        Guid.TryParse(value, out var parsed) ? parsed : null;

    private sealed record EditorAssetSample(Guid AssetIdValue, string? FilePath, string? WritebackStatus)
    {
        public Guid? AssetId => AssetIdValue;
    }

    private sealed record EditorLaunchWorkRow(Guid WorkId, string MediaType, string WorkKind, Guid? ParentWorkId, Guid? RootWorkId);

    private sealed record EditorLaunchCollectionRow(Guid WorkId, string MediaType, string WorkKind, Guid? ParentWorkId, Guid? RootWorkId);
    private sealed record EditorLaunchAssetRow(Guid AssetId, string? FilePath, string? WritebackStatus, Guid WorkId, string MediaType, string WorkKind, Guid? ParentWorkId, Guid? RootWorkId);

    private sealed record EditorLaunchContext(
        Guid LaunchEntityId,
        string LaunchEntityKind,
        Guid WorkId,
        Guid? ParentWorkId,
        Guid RootWorkId,
        string MediaType,
        string WorkKind,
        Guid? RepresentativeAssetId,
        string? RepresentativeMediaFilePath,
        string? RepresentativeWritebackStatus);

    private static async Task<MediaEditorNavigatorEnvelope?> BuildMediaEditorNavigatorCoreAsync(
        Guid entityId,
        IDatabaseConnection db,
        CancellationToken ct)
    {
        var launch = await ResolveEditorLaunchContextAsync(entityId, db, ct);
        if (launch is null)
        {
            return null;
        }

        var mediaType = NormalizeEditorMediaType(launch.MediaType);
        var rootWorkId = launch.RootWorkId == Guid.Empty ? launch.WorkId : launch.RootWorkId;

        using var conn = db.CreateConnection();
        var treeRows = QueryNavigatorTree(conn, rootWorkId);
        if (treeRows.Count == 0)
        {
            return null;
        }

        var enabled = IsSeriesAwareNavigatorEnabled(mediaType, launch, treeRows);
        if (!enabled)
        {
            return new MediaEditorNavigatorEnvelope(
                Enabled: false,
                MediaType: mediaType,
                ContainerEntityId: launch.WorkId,
                SelectedEntityId: launch.WorkId,
                ContainerLabel: GetNavigatorContainerLabel(mediaType),
                ContainerTitle: string.Empty,
                ContainerSubtitle: null,
                Nodes: []);
        }

        var valueMap = QueryNavigatorValues(conn, treeRows);
        var descendantOwnedCounts = ComputeNavigatorOwnedCounts(treeRows, valueMap);
        var orderedRows = OrderNavigatorRows(treeRows, valueMap);
        var nodes = orderedRows
            .Select(row => BuildNavigatorNodeEnvelope(mediaType, row, valueMap, descendantOwnedCounts))
            .ToList();

        var rootNode = nodes.FirstOrDefault(node => node.IsRoot)
            ?? nodes.FirstOrDefault();

        return new MediaEditorNavigatorEnvelope(
            Enabled: true,
            MediaType: mediaType,
            ContainerEntityId: rootWorkId,
            SelectedEntityId: launch.WorkId,
            ContainerLabel: GetNavigatorContainerLabel(mediaType),
            ContainerTitle: rootNode?.Title ?? string.Empty,
            ContainerSubtitle: rootNode?.Subtitle,
            Nodes: nodes);
    }

    private static async Task<IReadOnlyList<MembershipSuggestionEnvelope>> BuildMembershipSuggestionsAsync(
        Guid entityId,
        string field,
        string? query,
        string? source,
        Guid? parentEntityId,
        string? parentValue,
        IDatabaseConnection db,
        ISearchService searchService,
        CancellationToken ct)
    {
        var launch = await ResolveEditorLaunchContextAsync(entityId, db, ct);
        if (launch is null)
        {
            return [];
        }

        var mediaType = NormalizeEditorMediaType(launch.MediaType);
        var normalizedField = (field ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedSource = string.IsNullOrWhiteSpace(source) ? "local" : source.Trim().ToLowerInvariant();
        var normalizedQuery = (query ?? string.Empty).Trim();

        using var conn = db.CreateConnection();
        if (normalizedSource == "retail" && mediaType is "TV" or "Music" or "Comics")
        {
            return await BuildRetailMembershipSuggestionsAsync(mediaType, normalizedField, normalizedQuery, parentValue, searchService, ct);
        }

        return normalizedField switch
        {
            "show" when mediaType == "TV" => QueryTvShowSuggestions(conn, normalizedQuery),

            "season" when mediaType == "TV" && parentEntityId.HasValue => QuerySeasonSuggestions(conn, parentEntityId.Value, normalizedQuery),

            "artist" when mediaType == "Music" => QueryDistinctTextSuggestions(
                conn,
                mediaType,
                normalizedQuery,
                titleKey: "artist",
                subtitleKeys: ["album", "year"],
                suggestionKind: "artist"),

            "album" when mediaType == "Music" => QueryParentSuggestions(
                conn,
                mediaType,
                normalizedQuery,
                suggestionKind: "album",
                titleSelector: row => StringHelpers.FirstNonBlankOr(string.Empty, row.WorkAlbum, row.WorkTitle, FormatParentKeyFallback(row.ParentKey)),
                subtitleSelector: row => BuildDelimitedLabel(StringHelpers.FirstNonBlankOr(string.Empty, row.WorkArtist), StringHelpers.FirstNonBlankOr(string.Empty, row.WorkYear)),
                additionalFilter: row => string.IsNullOrWhiteSpace(parentValue)
                    || (!string.IsNullOrWhiteSpace(row.WorkArtist)
                        && row.WorkArtist.Contains(parentValue, StringComparison.OrdinalIgnoreCase))),

            "series" when mediaType is "Books" or "Audiobooks" or "Comics" => QueryParentSuggestions(
                conn,
                mediaType,
                normalizedQuery,
                suggestionKind: "series",
                titleSelector: row => StringHelpers.FirstNonBlankOr(string.Empty, row.WorkSeries, row.WorkTitle, FormatParentKeyFallback(row.ParentKey)),
                subtitleSelector: row => BuildDelimitedLabel(row.WorkAuthor, row.WorkYear)),
            _ => [],
        };
    }

    private static IReadOnlyList<NavigatorTreeRow> QueryNavigatorTree(SqliteConnection conn, Guid rootWorkId) =>
        conn.Query<NavigatorTreeRow>("""
            WITH RECURSIVE work_tree(id, parent_work_id, depth) AS (
                SELECT w.id, w.parent_work_id, 0
                FROM works w
                WHERE w.id = @rootId
                UNION ALL
                SELECT child.id, child.parent_work_id, work_tree.depth + 1
                FROM works child
                INNER JOIN work_tree ON child.parent_work_id = work_tree.id
            )
            SELECT work_tree.id             AS WorkId,
                   work_tree.parent_work_id AS ParentWorkId,
                   CAST(work_tree.depth AS INTEGER) AS Depth,
                   w.work_kind              AS WorkKind,
                   CAST(w.ordinal AS INTEGER) AS Ordinal,
                   CAST(w.is_catalog_only AS INTEGER) AS IsCatalogOnly,
                   CAST(COALESCE(w.ownership, 'Owned') AS TEXT) AS Ownership,
                   w.parent_key             AS ParentKey
            FROM work_tree
            INNER JOIN works w ON w.id = work_tree.id
            ORDER BY work_tree.depth,
                     COALESCE(w.parent_work_id, ''),
                     COALESCE(w.ordinal, 2147483647),
                     w.id;
            """, new { rootId = rootWorkId }).ToList();

    private static IReadOnlyList<NavigatorValueRow> QueryNavigatorParentValues(SqliteConnection conn, string mediaType) =>
        conn.Query<NavigatorValueRow>("""
            SELECT w.id AS WorkId, NULL AS AssetId,
                   (SELECT link.artwork_asset_id FROM entity_artwork_links link
                    WHERE link.entity_id = w.id AND link.entity_type = 'Work' AND link.role = 'Primary'
                    ORDER BY link.is_preferred DESC, link.sort_order, link.id LIMIT 1) AS ArtworkId,
                   MAX(CASE WHEN cv.key = 'title' THEN cv.value END) AS WorkTitle,
                   MAX(CASE WHEN cv.key = 'show_name' THEN cv.value END) AS WorkShowName,
                   MAX(CASE WHEN cv.key = 'album' THEN cv.value END) AS WorkAlbum,
                   MAX(CASE WHEN cv.key IN ('album_artist', 'artist') THEN cv.value END) AS WorkArtist,
                   MAX(CASE WHEN cv.key = 'series' THEN cv.value END) AS WorkSeries,
                   MAX(CASE WHEN cv.key IN ('author', 'creator', 'publisher') THEN cv.value END) AS WorkAuthor,
                   MAX(CASE WHEN cv.key IN ('year', 'series_start_year') THEN cv.value END) AS WorkYear,
                   w.parent_key AS ParentKey
            FROM works w LEFT JOIN canonical_values cv ON cv.entity_id = w.id
            WHERE w.media_type = @mediaType AND w.work_kind = 'parent'
              AND w.parent_work_id IS NULL
            GROUP BY w.id, w.parent_key;
            """, new { mediaType }).ToList();

    private static string? GetParentSuggestionValue(NavigatorValueRow row, string key) => key switch
    {
        "show_name" => row.WorkShowName,
        "album" => row.WorkAlbum,
        "artist" => row.WorkArtist,
        "series" => row.WorkSeries,
        "author" => row.WorkAuthor,
        "year" => row.WorkYear,
        "title" => row.WorkTitle,
        _ => null,
    };

    private static string? FormatParentKeyFallback(string? parentKey) =>
        string.IsNullOrWhiteSpace(parentKey)
            ? null
            : CultureInfo.CurrentCulture.TextInfo.ToTitleCase(parentKey.Replace('|', ' '));

    private static string BuildDelimitedLabel(params string?[] values) => string.Join(" • ", values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value!.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase));

    private static int? ToInt(long? value) =>
        value.HasValue && value.Value is >= int.MinValue and <= int.MaxValue ? (int)value.Value : null;

    private static int? ParseNavigatorOrdinal(string? raw, int? fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }
        var digits = new string(raw.Trim().TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var parsed) ? parsed : fallback;
    }

    private static Dictionary<Guid, NavigatorValueRow> QueryNavigatorValues(
        SqliteConnection conn,
        IReadOnlyList<NavigatorTreeRow> rows)
    {
        var workIdHexes = rows
            .Select(row => row.WorkId.ToString("N"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (workIdHexes.Length == 0)
        {
            return [];
        }

        var result = conn.Query<NavigatorValueRow>("""
            WITH representative_asset_ids AS (
                SELECT e.work_id AS WorkId,
                       MIN(ma.id) AS AssetId
                FROM editions e
                INNER JOIN media_assets ma ON ma.edition_id = e.id
                WHERE LOWER(HEX(e.work_id)) IN @workIdHexes
                GROUP BY e.work_id
            ),
            representative_assets AS (
                SELECT rai.WorkId,
                       rai.AssetId,
                       ma.file_path_root AS FilePath,
                       ma.content_hash AS ContentHash,
                       e.format_label AS FormatLabel
                FROM representative_asset_ids rai
                INNER JOIN media_assets ma ON ma.id = rai.AssetId
                INNER JOIN editions e ON e.id = ma.edition_id
            )
            SELECT w.id AS WorkId,
                   ra.AssetId,
                   CAST(ra.FilePath AS TEXT) AS AssetFilePath,
                   CAST(ra.ContentHash AS TEXT) AS AssetContentHash,
                   CAST(ra.FormatLabel AS TEXT) AS AssetFormatLabel,
                   CAST(MAX(CASE WHEN wv.key = 'title' THEN wv.value END) AS TEXT) AS WorkTitle,
                   CAST(MAX(CASE WHEN wv.key = 'show_name' THEN wv.value END) AS TEXT) AS WorkShowName,
                   CAST(MAX(CASE WHEN wv.key = 'network' THEN wv.value END) AS TEXT) AS WorkNetwork,
                   CAST(MAX(CASE WHEN wv.key = 'album' THEN wv.value END) AS TEXT) AS WorkAlbum,
                   CAST((SELECT value FROM canonical_value_arrays WHERE entity_id = w.id AND key IN ('album_artist', 'artist') ORDER BY CASE key WHEN 'album_artist' THEN 0 ELSE 1 END, ordinal LIMIT 1) AS TEXT) AS WorkArtist,
                   CAST(MAX(CASE WHEN wv.key = 'series' THEN wv.value END) AS TEXT) AS WorkSeries,
                   CAST((SELECT value FROM canonical_value_arrays WHERE entity_id = w.id AND key IN ('author', 'creator') ORDER BY ordinal LIMIT 1) AS TEXT) AS WorkAuthor,
                   CAST(MAX(CASE WHEN wv.key = 'year' THEN wv.value END) AS TEXT) AS WorkYear,
                   CAST(MAX(CASE WHEN wv.key = 'season_number' THEN wv.value END) AS TEXT) AS WorkSeasonNumber,
                   CAST(MAX(CASE WHEN wv.key = 'disc_number' THEN wv.value END) AS TEXT) AS WorkDiscNumber,
                   CAST(MAX(CASE WHEN av.key = 'title' THEN av.value END) AS TEXT) AS AssetTitle,
                   CAST(MAX(CASE WHEN av.key = 'issue_title' THEN av.value END) AS TEXT) AS AssetIssueTitle,
                   CAST(MAX(CASE WHEN wv.key = 'issue_title' THEN wv.value END) AS TEXT) AS WorkIssueTitle,
                   CAST(MAX(CASE WHEN av.key = 'episode_title' THEN av.value END) AS TEXT) AS AssetEpisodeTitle,
                   CAST(MAX(CASE WHEN av.key = 'episode_number' THEN av.value END) AS TEXT) AS AssetEpisodeNumber,
                   CAST(MAX(CASE WHEN av.key = 'season_number' THEN av.value END) AS TEXT) AS AssetSeasonNumber,
                   CAST(MAX(CASE WHEN av.key = 'track_number' THEN av.value END) AS TEXT) AS AssetTrackNumber,
                   CAST(MAX(CASE WHEN av.key = 'disc_number' THEN av.value END) AS TEXT) AS AssetDiscNumber,
                   CAST(MAX(CASE WHEN av.key = 'series_position' THEN av.value END) AS TEXT) AS AssetSeriesPosition,
                   CAST(MAX(CASE WHEN av.key = 'issue_number' THEN av.value END) AS TEXT) AS AssetIssueNumber,
                   CAST(MAX(CASE WHEN av.key = 'volume' THEN av.value END) AS TEXT) AS AssetVolume,
                   CAST(MAX(CASE WHEN av.key = 'file_size_bytes' THEN av.value END) AS TEXT) AS AssetFileSizeBytes,
                   CAST(MAX(CASE WHEN av.key = 'video_width' THEN av.value END) AS TEXT) AS AssetVideoWidth,
                   CAST(MAX(CASE WHEN av.key = 'video_height' THEN av.value END) AS TEXT) AS AssetVideoHeight,
                   CAST(MAX(CASE WHEN av.key = 'video_codec' THEN av.value END) AS TEXT) AS AssetVideoCodec,
                   CAST(MAX(CASE WHEN av.key = 'audio_codec' THEN av.value END) AS TEXT) AS AssetAudioCodec,
                   CAST(MAX(CASE WHEN av.key = 'bitrate_kbps' THEN av.value END) AS TEXT) AS AssetBitrateKbps,
                   CAST(MAX(CASE WHEN av.key = 'sample_rate' THEN av.value END) AS TEXT) AS AssetSampleRate,
                   CAST(MAX(CASE WHEN av.key = 'container' THEN av.value END) AS TEXT) AS AssetContainer,
                   CAST(pic.file_size AS TEXT) AS InspectionFileSize,
                   CAST(pic.container AS TEXT) AS InspectionContainer,
                   CAST(w.display_overrides_json AS TEXT) AS WorkDisplayOverridesJson,
                   (SELECT ea.id FROM entity_assets ea WHERE ea.entity_id = w.id
                    AND ea.asset_type = CASE WHEN w.media_type = 'TV' AND w.work_kind != 'parent' THEN 'EpisodeStill'
                        WHEN w.media_type = 'TV' AND w.parent_work_id IS NOT NULL THEN 'SeasonPoster' ELSE 'CoverArt' END
                    AND COALESCE(NULLIF(ea.local_image_path_m, ''), NULLIF(ea.local_image_path, '')) IS NOT NULL
                    ORDER BY ea.is_user_override DESC, ea.is_preferred DESC, COALESCE(ea.updated_at, ea.created_at) DESC LIMIT 1) AS ArtworkId,
                   (SELECT ea.aspect_class FROM entity_assets ea WHERE ea.entity_id = w.id
                    AND ea.asset_type = 'CoverArt' AND COALESCE(NULLIF(ea.local_image_path_m, ''), NULLIF(ea.local_image_path, '')) IS NOT NULL
                    ORDER BY ea.is_user_override DESC, ea.is_preferred DESC, COALESCE(ea.updated_at, ea.created_at) DESC LIMIT 1) AS ArtworkShape,
                   CAST(w.parent_key AS TEXT) AS ParentKey
            FROM works w
            LEFT JOIN representative_assets ra ON ra.WorkId = w.id
            LEFT JOIN playback_inspection_cache pic ON pic.asset_id = ra.AssetId AND pic.source_hash = ra.ContentHash
            LEFT JOIN canonical_values wv ON wv.entity_id = w.id
            LEFT JOIN canonical_values av ON av.entity_id = ra.AssetId
            WHERE LOWER(HEX(w.id)) IN @workIdHexes
            GROUP BY w.id, ra.AssetId, ra.FilePath, ra.ContentHash, ra.FormatLabel, pic.file_size, pic.container, w.display_overrides_json, w.parent_key;
            """, new { workIdHexes }).ToList();

        return result.ToDictionary(row => row.WorkId);
    }

    private static bool IsSeriesAwareNavigatorEnabled(
        string mediaType,
        EditorLaunchContext launch,
        IReadOnlyList<NavigatorTreeRow> rows) =>
        mediaType switch
        {
            "TV" or "Music" => true,
            "Movies" => true,
            "Books" or "Audiobooks" or "Comics" => rows.Count > 1,
            _ => false,
        };

    private static IReadOnlyDictionary<Guid, int> ComputeNavigatorOwnedCounts(
        IReadOnlyList<NavigatorTreeRow> rows,
        IReadOnlyDictionary<Guid, NavigatorValueRow> valueMap)
    {
        var childrenByParent = rows
            .Where(row => row.ParentWorkId.HasValue)
            .GroupBy(row => row.ParentWorkId!.Value)
            .ToDictionary(group => group.Key, group => group.Select(row => row.WorkId).ToList());
        var rowMap = rows.ToDictionary(row => row.WorkId);
        var cache = new Dictionary<Guid, int>();

        int CountOwned(Guid workId)
        {
            if (cache.TryGetValue(workId, out var cached))
            {
                return cached;
            }

            var row = rowMap[workId];
            var isParent = string.Equals(row.WorkKind, "parent", StringComparison.OrdinalIgnoreCase);
            int count;
            if (!isParent)
            {
                count = IsOwnedNavigatorLeaf(row, valueMap) ? 1 : 0;
            }
            else
            {
                count = childrenByParent.TryGetValue(workId, out var children)
                    ? children.Sum(CountOwned)
                    : 0;
            }

            cache[workId] = count;
            return count;
        }

        foreach (var row in rows)
        {
            CountOwned(row.WorkId);
        }

        return cache;
    }

    private static IReadOnlyList<NavigatorTreeRow> OrderNavigatorRows(
        IReadOnlyList<NavigatorTreeRow> rows,
        IReadOnlyDictionary<Guid, NavigatorValueRow> valueMap) =>
        rows.OrderBy(row => row.Depth)
            .ThenBy(row => row.ParentWorkId)
            .ThenBy(row => row.Ordinal ?? int.MaxValue)
            .ThenBy(row =>
            {
                if (!valueMap.TryGetValue(row.WorkId, out var value))
                {
                    return row.WorkId.ToString("D");
                }

                return StringHelpers.FirstNonBlankOr(string.Empty,
                    GetDisplayOverrideValue(value, "title"),
                    value.AssetEpisodeTitle,
                    value.AssetTitle,
                    GetDisplayOverrideValue(value, "title"),
                    value.WorkTitle,
                    value.WorkShowName,
                    value.WorkAlbum,
                    value.WorkSeries,
                    value.WorkAuthor,
                    row.WorkId.ToString("D"));
            }, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static MediaEditorNavigatorNodeEnvelope BuildNavigatorNodeEnvelope(
        string mediaType,
        NavigatorTreeRow row,
        IReadOnlyDictionary<Guid, NavigatorValueRow> valueMap,
        IReadOnlyDictionary<Guid, int> descendantOwnedCounts)
    {
        valueMap.TryGetValue(row.WorkId, out var value);
        var nodeKind = ResolveNavigatorNodeKind(mediaType, row);
        var scopeId = ResolveNavigatorScopeId(mediaType, nodeKind);
        var title = ResolveNavigatorTitle(mediaType, row, value);
        var subtitle = ResolveNavigatorSubtitle(mediaType, row, value, descendantOwnedCounts.TryGetValue(row.WorkId, out var ownedCount) ? ownedCount : 0);
        var ordinalLabel = ResolveNavigatorOrdinalLabel(mediaType, row, value);
        var isParent = string.Equals(row.WorkKind, "parent", StringComparison.OrdinalIgnoreCase);
        var quarantineCount = descendantOwnedCounts.TryGetValue(row.WorkId, out var count) ? count : 0;
        var isOwned = isParent ? quarantineCount > 0 : IsOwnedNavigatorLeaf(row, valueMap);
        var primaryAssetId = !isParent && isOwned ? value?.AssetId : null;
        var compactOrdinalLabel = ResolveCompactNavigatorOrdinalLabel(mediaType, row, value);
        var technicalBadges = primaryAssetId.HasValue ? BuildNavigatorTechnicalBadges(mediaType, value) : [];
        var isClickable = primaryAssetId.HasValue && isOwned && !isParent;
        var canSelectAsEditorTarget = (row.Depth == 0 || isParent)
            ? isOwned || row.Depth == 0
            : isClickable;

        return new MediaEditorNavigatorNodeEnvelope(
            NodeId: row.WorkId,
            ParentNodeId: row.ParentWorkId,
            EntityId: row.WorkId,
            ScopeId: scopeId,
            NodeKind: nodeKind,
            Label: GetNavigatorNodeLabel(nodeKind, row, value),
            Title: title,
            Subtitle: subtitle,
            OrdinalLabel: ordinalLabel,
            Depth: (int)row.Depth,
            IsRoot: row.Depth == 0,
            IsLeaf: !isParent,
            IsOwned: isOwned,
            PrimaryAssetId: primaryAssetId,
            CompactOrdinalLabel: compactOrdinalLabel,
            TechnicalBadges: technicalBadges,
            IsClickable: isClickable,
            CanSelectAsEditorTarget: canSelectAsEditorTarget,
            CanQuarantine: quarantineCount > 0,
            QuarantineCount: quarantineCount,
            ArtworkUrl: value?.ArtworkId is Guid artworkId ? $"/stream/artwork/{artworkId:D}" : null,
            ArtworkShape: nodeKind == "episode" ? "wide" : mediaType == "Music" ? "square" : value?.ArtworkShape?.ToLowerInvariant() ?? "portrait");
    }

    private static string ResolveNavigatorNodeKind(string mediaType, NavigatorTreeRow row) =>
        mediaType switch
        {
            "TV" when row.Depth == 0 => "series",
            "TV" when string.Equals(row.WorkKind, "parent", StringComparison.OrdinalIgnoreCase) => "season",
            "TV" => "episode",
            "Movies" when string.Equals(row.WorkKind, "parent", StringComparison.OrdinalIgnoreCase) => "film_series",
            "Movies" => "movie",
            "Music" when string.Equals(row.WorkKind, "parent", StringComparison.OrdinalIgnoreCase) => "album",
            "Music" => "track",
            "Comics" when string.Equals(row.WorkKind, "parent", StringComparison.OrdinalIgnoreCase) => "series",
            "Comics" => "issue",
            "Audiobooks" when string.Equals(row.WorkKind, "parent", StringComparison.OrdinalIgnoreCase) => "series",
            "Audiobooks" => "audiobook",
            "Books" when string.Equals(row.WorkKind, "parent", StringComparison.OrdinalIgnoreCase) => "series",
            "Books" => "book",
            _ => "work",
        };

    private static bool IsOwnedNavigatorLeaf(
        NavigatorTreeRow row,
        IReadOnlyDictionary<Guid, NavigatorValueRow> valueMap)
    {
        if (string.Equals(row.WorkKind, "parent", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return row.IsCatalogOnly == 0
               && !string.Equals(row.Ownership, "Unowned", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(row.Ownership, "Missing", StringComparison.OrdinalIgnoreCase)
               && valueMap.TryGetValue(row.WorkId, out var value)
               && value.AssetId.HasValue;
    }

    private static string ResolveNavigatorScopeId(string mediaType, string nodeKind) =>
        nodeKind switch
        {
            "series" => "series",
            "film_series" => "series",
            "season" => "season",
            "episode" => "episode",
            "movie" => "work",
            "album" => "album",
            "track" => "track",
            "issue" or "book" or "audiobook" when mediaType is "Comics" or "Books" or "Audiobooks" => "volume_issue",
            _ => "work",
        };

    private static string GetNavigatorContainerLabel(string mediaType) =>
        mediaType switch
        {
            "TV" => "Series",
            "Music" => "Album",
            "Movies" => "Movie",
            "Comics" or "Books" or "Audiobooks" => "Series",
            _ => "Item",
        };

    private static string GetNavigatorNodeLabel(string nodeKind, NavigatorTreeRow row, NavigatorValueRow? value) =>
        nodeKind switch
        {
            "series" => "Series",
            "film_series" => "Film Series",
            "season" => ResolveNavigatorOrdinalLabel("TV", row, value) ?? "Season",
            "episode" => ResolveNavigatorOrdinalLabel("TV", row, value) ?? "Episode",
            "movie" => "Movie",
            "album" => "Album",
            "track" => ResolveNavigatorOrdinalLabel("Music", row, value) ?? "Track",
            "issue" => ResolveNavigatorOrdinalLabel("Comics", row, value) ?? "Issue",
            "book" => ResolveNavigatorOrdinalLabel("Books", row, value) ?? "Book",
            "audiobook" => ResolveNavigatorOrdinalLabel("Audiobooks", row, value) ?? "Audiobook",
            _ => "Item",
        };

    private static string ResolveNavigatorTitle(string mediaType, NavigatorTreeRow row, NavigatorValueRow? value) =>
        mediaType switch
        {
            "TV" when row.Depth == 0 => StringHelpers.FirstNonBlankOr(string.Empty, GetDisplayOverrideValue(value, "title"), value?.WorkShowName, value?.WorkTitle, FormatParentKeyFallback(row.ParentKey), "Series"),
            "TV" when string.Equals(row.WorkKind, "parent", StringComparison.OrdinalIgnoreCase) =>
                $"Season {ParseNavigatorOrdinal(value?.AssetSeasonNumber ?? value?.WorkSeasonNumber, ToInt(row.Ordinal))?.ToString(CultureInfo.InvariantCulture) ?? "?"}",
            "TV" => StringHelpers.FirstNonBlankOr(string.Empty, GetDisplayOverrideValue(value, "title"), value?.AssetEpisodeTitle, value?.AssetTitle, value?.WorkTitle, $"Episode {row.Ordinal?.ToString(CultureInfo.InvariantCulture) ?? "?"}"),
            "Movies" when string.Equals(row.WorkKind, "parent", StringComparison.OrdinalIgnoreCase) =>
                StringHelpers.FirstNonBlankOr(string.Empty, GetDisplayOverrideValue(value, "title"), value?.WorkSeries, value?.WorkTitle, FormatParentKeyFallback(row.ParentKey), "Film Series"),
            "Movies" => StringHelpers.FirstNonBlankOr(string.Empty, GetDisplayOverrideValue(value, "title"), value?.AssetTitle, value?.WorkTitle, "Movie"),
            "Music" when string.Equals(row.WorkKind, "parent", StringComparison.OrdinalIgnoreCase) =>
                StringHelpers.FirstNonBlankOr(string.Empty, GetDisplayOverrideValue(value, "title"), value?.WorkAlbum, value?.WorkTitle, FormatParentKeyFallback(row.ParentKey), "Album"),
            "Music" => StringHelpers.FirstNonBlankOr(string.Empty, GetDisplayOverrideValue(value, "title"), value?.AssetTitle, value?.WorkTitle, $"Track {row.Ordinal?.ToString(CultureInfo.InvariantCulture) ?? "?"}"),
            "Comics" when string.Equals(row.WorkKind, "parent", StringComparison.OrdinalIgnoreCase) =>
                StringHelpers.FirstNonBlankOr(string.Empty, GetDisplayOverrideValue(value, "title"), value?.WorkSeries, value?.WorkTitle, FormatParentKeyFallback(row.ParentKey), "Series"),
            "Comics" => StringHelpers.FirstNonBlankOr(string.Empty, GetDisplayOverrideValue(value, "issue_title", "title"), value?.AssetIssueTitle, value?.WorkIssueTitle, value?.AssetTitle, value?.WorkTitle, $"Issue {row.Ordinal?.ToString(CultureInfo.InvariantCulture) ?? "?"}"),
            "Books" or "Audiobooks" when string.Equals(row.WorkKind, "parent", StringComparison.OrdinalIgnoreCase) =>
                StringHelpers.FirstNonBlankOr(string.Empty, GetDisplayOverrideValue(value, "title"), value?.WorkSeries, value?.WorkTitle, FormatParentKeyFallback(row.ParentKey), "Series"),
            _ => StringHelpers.FirstNonBlankOr(string.Empty, GetDisplayOverrideValue(value, "title"), value?.AssetTitle, value?.WorkTitle, "Item"),
        };

    private static string? GetDisplayOverrideValue(NavigatorValueRow? value, params string[] keys)
    {
        if (value is null || string.IsNullOrWhiteSpace(value.WorkDisplayOverridesJson))
        {
            return null;
        }

        try
        {
            var overrides = JsonSerializer.Deserialize<Dictionary<string, string>>(value.WorkDisplayOverridesJson);
            if (overrides is null)
            {
                return null;
            }

            foreach (var key in keys)
            {
                if (overrides.TryGetValue(key, out var overrideValue) && !string.IsNullOrWhiteSpace(overrideValue))
                {
                    return overrideValue.Trim();
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static string? ResolveNavigatorSubtitle(string mediaType, NavigatorTreeRow row, NavigatorValueRow? value, int ownedCount)
    {
        if (mediaType == "TV" && string.Equals(row.WorkKind, "parent", StringComparison.OrdinalIgnoreCase) && row.Depth > 0)
        {
            return ownedCount == 1 ? "1 episode" : $"{ownedCount} episodes";
        }

        if (row.Depth == 0)
        {
            return mediaType switch
            {
                "TV" => BuildDelimitedLabel(StringHelpers.FirstNonBlankOr(string.Empty, value?.WorkYear), StringHelpers.FirstNonBlankOr(string.Empty, value?.WorkNetwork)),
                "Music" => BuildDelimitedLabel(StringHelpers.FirstNonBlankOr(string.Empty, value?.WorkArtist), StringHelpers.FirstNonBlankOr(string.Empty, value?.WorkYear)),
                "Comics" or "Books" or "Audiobooks" => BuildDelimitedLabel(StringHelpers.FirstNonBlankOr(string.Empty, value?.WorkAuthor), StringHelpers.FirstNonBlankOr(string.Empty, value?.WorkYear)),
                _ => value?.WorkYear,
            };
        }

        return mediaType switch
        {
            "TV" => value?.WorkYear,
            "Music" => value?.WorkArtist,
            "Books" or "Audiobooks" => value?.WorkAuthor,
            "Comics" => StringHelpers.FirstNonBlankOr(string.Empty, value?.AssetVolume, value?.WorkYear),
            _ => null,
        };
    }

    private static string? ResolveNavigatorOrdinalLabel(string mediaType, NavigatorTreeRow row, NavigatorValueRow? value)
    {
        var ordinal = ParseNavigatorOrdinal(
            mediaType switch
            {
                "TV" when string.Equals(row.WorkKind, "parent", StringComparison.OrdinalIgnoreCase) => value?.AssetSeasonNumber ?? value?.WorkSeasonNumber,
                "TV" => value?.AssetEpisodeNumber,
                "Music" => value?.AssetTrackNumber,
                "Comics" => value?.AssetIssueNumber ?? value?.AssetSeriesPosition,
                "Books" or "Audiobooks" => value?.AssetSeriesPosition,
                _ => null,
            },
            ToInt(row.Ordinal));

        if (ordinal is null)
        {
            return null;
        }

        return mediaType switch
        {
            "TV" when string.Equals(row.WorkKind, "parent", StringComparison.OrdinalIgnoreCase) => $"Season {ordinal}",
            "TV" => $"Episode {ordinal}",
            "Music" => $"Track {ordinal}",
            "Comics" => $"Issue {ordinal}",
            "Books" or "Audiobooks" => $"Book {ordinal}",
            _ => ordinal.Value.ToString(CultureInfo.InvariantCulture),
        };
    }

    private static string? ResolveCompactNavigatorOrdinalLabel(string mediaType, NavigatorTreeRow row, NavigatorValueRow? value)
    {
        var ordinal = ParseNavigatorOrdinal(
            mediaType switch
            {
                "TV" when string.Equals(row.WorkKind, "parent", StringComparison.OrdinalIgnoreCase) => value?.AssetSeasonNumber ?? value?.WorkSeasonNumber,
                "TV" => value?.AssetEpisodeNumber,
                "Music" => value?.AssetTrackNumber,
                _ => null,
            },
            ToInt(row.Ordinal));

        if (ordinal is null)
        {
            return null;
        }

        return mediaType switch
        {
            "TV" when string.Equals(row.WorkKind, "parent", StringComparison.OrdinalIgnoreCase) => $"S{ordinal.Value:00}",
            "TV" => $"E{ordinal.Value:00}",
            "Music" when ParseNavigatorOrdinal(value?.AssetDiscNumber ?? value?.WorkDiscNumber, null) is { } disc => $"D{disc} T{ordinal.Value:00}",
            "Music" => $"T{ordinal.Value:00}",
            _ => ordinal.Value.ToString(CultureInfo.InvariantCulture),
        };
    }

    private static IReadOnlyList<string> BuildNavigatorTechnicalBadges(string mediaType, NavigatorValueRow? value)
    {
        if (value is null)
        {
            return [];
        }

        var badges = new List<string>();
        if (mediaType == "TV")
        {
            AddIfPresent(badges, FormatResolutionBadge(value.AssetVideoWidth, value.AssetVideoHeight));
            AddIfPresent(badges, StringHelpers.FirstNonBlankOr(string.Empty, value.InspectionContainer, value.AssetContainer, Path.GetExtension(value.AssetFilePath)?.TrimStart('.'), value.AssetFormatLabel)?.ToUpperInvariant());
            if (badges.Count < 2)
            {
                AddIfPresent(badges, value.AssetVideoCodec?.ToUpperInvariant());
            }

            AddIfPresent(badges, FormatFileSizeBadge(StringHelpers.FirstNonBlankOr(string.Empty, value.InspectionFileSize, value.AssetFileSizeBytes)));
        }
        else if (mediaType == "Music")
        {
            AddIfPresent(badges, StringHelpers.FirstNonBlankOr(string.Empty, value.AssetAudioCodec, value.InspectionContainer, value.AssetContainer, Path.GetExtension(value.AssetFilePath)?.TrimStart('.'), value.AssetFormatLabel)?.ToUpperInvariant());
            AddIfPresent(badges, FormatBitrateBadge(value.AssetBitrateKbps));
            if (badges.Count < 2)
            {
                AddIfPresent(badges, FormatSampleRateBadge(value.AssetSampleRate));
            }

            AddIfPresent(badges, FormatFileSizeBadge(StringHelpers.FirstNonBlankOr(string.Empty, value.InspectionFileSize, value.AssetFileSizeBytes)));
        }

        return badges
            .Where(badge => !string.IsNullOrWhiteSpace(badge))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
    }

    private static void AddIfPresent(List<string> values, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values.Add(value.Trim());
        }
    }

    private static string? FormatResolutionBadge(string? widthValue, string? heightValue)
    {
        if (!int.TryParse(widthValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(heightValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)
            || width <= 0
            || height <= 0)
        {
            return null;
        }

        return height switch
        {
            >= 2160 => "4K",
            >= 1440 => "1440p",
            >= 1080 => "1080p",
            >= 720 => "720p",
            _ => $"{height}p",
        };
    }

    private static string? FormatFileSizeBadge(string? value)
    {
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) || bytes <= 0)
        {
            return null;
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit <= 1
            ? $"{size:F0} {units[unit]}"
            : $"{size:F1} {units[unit]}";
    }

    private static string? FormatBitrateBadge(string? value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bitrate) || bitrate <= 0)
        {
            return null;
        }

        return $"{bitrate} kbps";
    }

    private static string? FormatSampleRateBadge(string? value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var sampleRate) || sampleRate <= 0)
        {
            return null;
        }

        var khz = sampleRate >= 1000 ? sampleRate / 1000 : sampleRate;
        return $"{khz:0.#} kHz";
    }

    private static IReadOnlyList<MembershipSuggestionEnvelope> QueryParentSuggestions(
        SqliteConnection conn,
        string mediaType,
        string query,
        string suggestionKind,
        Func<NavigatorValueRow, string> titleSelector,
        Func<NavigatorValueRow, string?> subtitleSelector,
        Func<NavigatorValueRow, bool>? additionalFilter = null)
    {
        var rows = QueryNavigatorParentValues(conn, mediaType);
        return rows
            .Where(row =>
            {
                var title = titleSelector(row);
                return !string.IsNullOrWhiteSpace(title)
                       && (string.IsNullOrWhiteSpace(query) || title.Contains(query, StringComparison.OrdinalIgnoreCase))
                       && (additionalFilter?.Invoke(row) ?? true);
            })
            .OrderBy(row => titleSelector(row), StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .Select(row => new MembershipSuggestionEnvelope(
                row.WorkId,
                "local",
                true,
                suggestionKind,
                titleSelector(row),
                subtitleSelector(row),
                null,
                null,
                null,
                null,
                row.ArtworkId is { } artId
                    ? $"/api/v1/display/artwork/assets/{artId:D}/content?size=s" : null))
            .ToList();
    }

    private static IReadOnlyList<MembershipSuggestionEnvelope> QueryTvShowSuggestions(
        SqliteConnection conn, string query)
    {
        var shows = QueryParentSuggestions(conn, "TV", query, "show",
            row => StringHelpers.FirstNonBlankOr(string.Empty, row.WorkShowName, row.WorkTitle, FormatParentKeyFallback(row.ParentKey)),
            row => StringHelpers.FirstNonBlankOr(string.Empty, row.WorkYear, row.WorkNetwork));
        return shows.Select(show =>
        {
            var tvdbId = conn.QueryFirstOrDefault<string?>("""
                SELECT id_value FROM bridge_ids
                WHERE entity_id = @entityId AND id_type = @idType
                LIMIT 1;
                """, new { entityId = show.EntityId, idType = BridgeIdKeys.TvdbId })
                ?? conn.QueryFirstOrDefault<string?>("""
                    SELECT value FROM canonical_values
                    WHERE entity_id = @entityId AND key = @idType
                    LIMIT 1;
                    """, new { entityId = show.EntityId, idType = BridgeIdKeys.TvdbId });
            return string.IsNullOrWhiteSpace(tvdbId) ? show : show with
            {
                ProviderName = "tvdb",
                ProviderItemId = tvdbId,
                ExternalIdKey = BridgeIdKeys.TvdbId,
                ExternalIdValue = tvdbId,
            };
        }).ToList();
    }

    private static IReadOnlyList<MembershipSuggestionEnvelope> QuerySeasonSuggestions(
        SqliteConnection conn,
        Guid showEntityId,
        string query)
    {
        var rows = conn.Query<NavigatorTreeRow>("""
            SELECT w.id             AS WorkId,
                   w.parent_work_id AS ParentWorkId,
                   CAST(1 AS INTEGER) AS Depth,
                   w.work_kind      AS WorkKind,
                   CAST(w.ordinal AS INTEGER) AS Ordinal,
                   CAST(w.is_catalog_only AS INTEGER) AS IsCatalogOnly,
                   w.parent_key     AS ParentKey
            FROM works w
            WHERE w.parent_work_id = @showId
              AND w.work_kind = 'parent'
            ORDER BY COALESCE(w.ordinal, 2147483647), w.id;
            """, new { showId = showEntityId }).ToList();

        return rows
            .Select(row =>
            {
                var label = $"Season {row.Ordinal?.ToString(CultureInfo.InvariantCulture) ?? "?"}";
                var artworkId = conn.QueryFirstOrDefault<Guid?>("""
                    SELECT artwork_asset_id FROM entity_artwork_links
                    WHERE entity_id = @entityId AND entity_type = 'Work' AND role = 'Primary'
                    ORDER BY is_preferred DESC, sort_order, id LIMIT 1;
                    """, new { entityId = row.WorkId });
                return new MembershipSuggestionEnvelope(row.WorkId, "local", true, "season", label,
                    null, null, null, null, null,
                    artworkId is { } artId
                        ? $"/api/v1/display/artwork/assets/{artId:D}/content?size=s" : null);
            })
            .Where(row => string.IsNullOrWhiteSpace(query) || row.Label.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(12)
            .ToList();
    }

    private static IReadOnlyList<MembershipSuggestionEnvelope> QueryDistinctTextSuggestions(
        SqliteConnection conn,
        string mediaType,
        string query,
        string titleKey,
        string[] subtitleKeys,
        string suggestionKind)
    {
        var rows = QueryNavigatorParentValues(conn, mediaType);
        IEnumerable<NavigatorValueRow> filtered = rows.Where(row =>
        {
            var value = GetParentSuggestionValue(row, titleKey);
            return !string.IsNullOrWhiteSpace(value)
                   && (string.IsNullOrWhiteSpace(query) || value.Contains(query, StringComparison.OrdinalIgnoreCase));
        });

        return filtered
            .GroupBy(row => GetParentSuggestionValue(row, titleKey)!, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var representative = group.First();
                var subtitle = subtitleKeys
                    .Select(keyName => GetParentSuggestionValue(representative, keyName))
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                return new MembershipSuggestionEnvelope(representative.WorkId, "local", true, suggestionKind, group.Key, subtitle, null, null, null, null);
            })
            .OrderBy(row => row.Label, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
    }

    private static async Task<IReadOnlyList<MembershipSuggestionEnvelope>> BuildRetailMembershipSuggestionsAsync(
        string mediaType,
        string field,
        string query,
        string? parentValue,
        ISearchService searchService,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        if (mediaType == "TV" && field != "show")
        {
            return [];
        }

        if (mediaType == "Music" && field != "album")
        {
            return [];
        }

        if (mediaType == "Comics" && field != "series")
        {
            return [];
        }

        var searchFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (mediaType == "Music" && !string.IsNullOrWhiteSpace(parentValue))
        {
            searchFields["artist"] = parentValue.Trim();
        }

        if (mediaType == "Comics")
        {
            // Comic Vine exposes a run as a volume. Request its volume strategy
            // explicitly so an issue-level retail result cannot be offered as a
            // series target for this issue.
            searchFields["series"] = query;
            searchFields["container_search"] = "true";
        }

        var result = await searchService.SearchRetailAsync(
            new SearchRetailRequest(
                Query: query,
                MediaType: mediaType,
                MaxCandidates: 8,
                SearchFields: searchFields.Count == 0 ? null : searchFields),
            ct);

        return result.Candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Title))
            .Where(candidate => mediaType != "TV"
                || string.Equals(candidate.ProviderName, "tvdb", StringComparison.OrdinalIgnoreCase))
            .Select(candidate =>
            {
                var (externalIdKey, externalIdValue) = ResolveRetailExternalId(candidate);
                var subtitle = mediaType switch
                {
                    "TV" => BuildDelimitedLabel(candidate.Year, candidate.Description),
                    "Music" => BuildDelimitedLabel(candidate.Author, candidate.Year, candidate.ProviderName),
                    "Comics" => BuildDelimitedLabel(candidate.Author, candidate.Year, candidate.ProviderName),
                    _ => BuildDelimitedLabel(candidate.Author, candidate.Year),
                };

                return new MembershipSuggestionEnvelope(
                    EntityId: null,
                    Source: "retail",
                    LocalExisting: false,
                    Kind: mediaType switch
                    {
                        "TV" => "show",
                        "Music" => "album",
                        "Comics" => "series",
                        _ => "series",
                    },
                    Label: candidate.Title,
                    Subtitle: subtitle,
                    ProviderName: candidate.ProviderName,
                    ProviderItemId: candidate.ProviderItemId,
                    ExternalIdKey: externalIdKey,
                    ExternalIdValue: externalIdValue,
                    ImageUrl: candidate.CoverUrl);
            })
            .Take(8)
            .ToList();
    }

    private static (string? Key, string? Value) ResolveRetailExternalId(RetailCandidate candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.ProviderItemId))
        {
            if (string.Equals(candidate.ProviderName, "tvdb", StringComparison.OrdinalIgnoreCase))
            {
                return (BridgeIdKeys.TvdbId, candidate.ProviderItemId);
            }
            if (string.Equals(candidate.ProviderName, "tmdb", StringComparison.OrdinalIgnoreCase))
            {
                return (BridgeIdKeys.TmdbId, candidate.ProviderItemId);
            }

            if (string.Equals(candidate.ProviderName, "musicbrainz", StringComparison.OrdinalIgnoreCase))
            {
                return (BridgeIdKeys.MusicBrainzReleaseGroupId, candidate.ProviderItemId);
            }

            if (string.Equals(candidate.ProviderName, "apple_music", StringComparison.OrdinalIgnoreCase))
            {
                return (BridgeIdKeys.AppleMusicCollectionId, candidate.ProviderItemId);
            }

            if (string.Equals(candidate.ProviderName, "comicvine", StringComparison.OrdinalIgnoreCase))
            {
                return (BridgeIdKeys.ComicVineVolumeId, candidate.ProviderItemId);
            }
        }

        return (null, null);
    }

    private sealed class NavigatorTreeRow
    {
        public Guid WorkId { get; init; }
        public Guid? ParentWorkId { get; init; }
        public long Depth { get; init; }
        public string WorkKind { get; init; } = string.Empty;
        public long? Ordinal { get; init; }
        public long IsCatalogOnly { get; init; }
        public string Ownership { get; init; } = "Owned";
        public string? ParentKey { get; init; }
    }
    private sealed class NavigatorValueRow
    {
        public Guid? ArtworkId { get; set; }
        public string? ArtworkShape { get; set; }
        public Guid WorkId { get; init; }
        public Guid? AssetId { get; init; }
        public string? AssetFilePath { get; init; }
        public string? AssetContentHash { get; init; }
        public string? AssetFormatLabel { get; init; }
        public string? WorkTitle { get; init; }
        public string? WorkShowName { get; init; }
        public string? WorkNetwork { get; init; }
        public string? WorkAlbum { get; init; }
        public string? WorkArtist { get; init; }
        public string? WorkSeries { get; init; }
        public string? WorkAuthor { get; init; }
        public string? WorkYear { get; init; }
        public string? WorkSeasonNumber { get; init; }
        public string? WorkDiscNumber { get; init; }
        public string? AssetTitle { get; init; }
        public string? AssetEpisodeTitle { get; init; }
        public string? AssetEpisodeNumber { get; init; }
        public string? AssetSeasonNumber { get; init; }
        public string? AssetTrackNumber { get; init; }
        public string? AssetDiscNumber { get; init; }
        public string? AssetSeriesPosition { get; init; }
        public string? AssetIssueNumber { get; init; }
        public string? AssetIssueTitle { get; init; }
        public string? WorkIssueTitle { get; init; }
        public string? AssetVolume { get; init; }
        public string? AssetFileSizeBytes { get; init; }
        public string? AssetVideoWidth { get; init; }
        public string? AssetVideoHeight { get; init; }
        public string? AssetVideoCodec { get; init; }
        public string? AssetAudioCodec { get; init; }
        public string? AssetBitrateKbps { get; init; }
        public string? AssetSampleRate { get; init; }
        public string? AssetContainer { get; init; }
        public string? InspectionFileSize { get; init; }
        public string? InspectionContainer { get; init; }
        public string? WorkDisplayOverridesJson { get; init; }
        public string? ParentKey { get; init; }
    }
}
