using Dapper;
using MediaEngine.Application.ReadModels;
using MediaEngine.Application.Services;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.ReadServices;

/// <summary>
/// Reads physical, owned children beneath an editor target. This intentionally
/// does not use provider search or provider catalogue membership: a row always
/// represents an asset currently in the local library.
/// </summary>
public sealed class MediaEditorOwnedChildReadService(IDatabaseConnection db) : IMediaEditorOwnedChildReadService
{
    private const int DefaultPageSize = 50;
    private const int MaximumPageSize = 100;

    public async Task<IReadOnlyList<MediaEditorAssetAccessSegment>> GetAccessSegmentsAsync(Guid parentEntityId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var parentWorkId = ResolveParentWorkId(connection, parentEntityId);
        if (parentWorkId is null) return [];
        return (await connection.QueryAsync<MediaEditorAssetAccessSegment>(new CommandDefinition("""
            WITH RECURSIVE work_tree(id) AS (
                SELECT @parentWorkId
                UNION ALL
                SELECT child.id FROM works child JOIN work_tree ON child.parent_work_id = work_tree.id
            )
            SELECT ma.library_id AS LibraryId, w.media_type AS MediaType,
                   MIN(ma.id) AS RepresentativeAssetId
            FROM work_tree tree
            JOIN works w ON w.id = tree.id
            JOIN editions e ON e.work_id = w.id
            JOIN media_assets ma ON ma.edition_id = e.id
            WHERE w.ownership = 'Owned' AND ma.status = 'Normal' AND ma.is_orphaned = 0
              AND ma.library_id IS NOT NULL
            GROUP BY ma.library_id, w.media_type;
            """, new { parentWorkId }, cancellationToken: ct))).ToList();
    }

    public async Task<MediaEditorOwnedChildSearchEnvelope?> SearchAsync(
        Guid parentEntityId,
        string? query,
        int page,
        int pageSize,
        int? season,
        int? disc,
        int? volume,
        string? matchStatus,
        string? fileStatus,
        CancellationToken ct,
        IReadOnlyList<string>? allowedSegments = null)
    {
        ct.ThrowIfCancellationRequested();
        page = Math.Max(1, page);
        pageSize = pageSize <= 0 ? DefaultPageSize : Math.Min(pageSize, MaximumPageSize);

        using var connection = db.CreateConnection();
        var parentWorkId = ResolveParentWorkId(connection, parentEntityId);

        if (parentWorkId is not { } resolvedParentWorkId || resolvedParentWorkId == Guid.Empty)
        {
            return null;
        }

        var rootWorkId = connection.QueryFirstOrDefault<Guid?>("""
            SELECT COALESCE(grandparent.id, parent.id, work.id)
            FROM works work
            LEFT JOIN works parent ON parent.id = work.parent_work_id
            LEFT JOIN works grandparent ON grandparent.id = parent.parent_work_id
            WHERE work.id = @resolvedParentWorkId;
            """, new { resolvedParentWorkId }) ?? resolvedParentWorkId;

        var normalizedQuery = (query ?? string.Empty).Trim();
        var normalizedMatchStatus = NormalizeFilter(matchStatus, "matched", "unmatched");
        var normalizedFileStatus = string.IsNullOrWhiteSpace(fileStatus) ? null : fileStatus.Trim();
        var parameters = new
        {
            parentWorkId = resolvedParentWorkId,
            rootWorkId,
            query = normalizedQuery,
            season,
            disc,
            volume,
            matchStatus = normalizedMatchStatus,
            fileStatus = normalizedFileStatus,
            applySegmentFilter = allowedSegments is not null ? 1 : 0,
            allowedSegments = allowedSegments?.Count > 0 ? allowedSegments : ["<none>"],
            limit = pageSize,
            offset = (page - 1) * pageSize,
        };

        var totalCount = connection.QuerySingle<int>(BuildCountSql(), parameters);
        var rows = connection.Query<OwnedChildRow>(BuildRowsSql(), parameters).ToList();
        var items = rows.Select(row => new MediaEditorOwnedChildEnvelope(
            row.AssetId,
            row.WorkId,
            row.ParentWorkId,
            row.RootWorkId,
            string.IsNullOrWhiteSpace(row.MatchedTitle) ? GetFileName(row.SourceFilePath) : row.MatchedTitle,
            row.MatchedTitle,
            row.MatchedNumber,
            GetFileName(row.SourceFilePath),
            row.SourceFilePath,
            row.MatchState,
            row.FileState,
            row.SeasonNumber,
            row.DiscNumber,
            row.VolumeNumber)).ToList();

        return new MediaEditorOwnedChildSearchEnvelope(
            parentEntityId, page, pageSize, totalCount, items);
    }

    private static Guid? ResolveParentWorkId(System.Data.IDbConnection connection, Guid parentEntityId) =>
        connection.QueryFirstOrDefault<Guid?>("""
            SELECT id FROM works WHERE id = @parentEntityId
            UNION ALL
            SELECT e.work_id
            FROM media_assets ma
            INNER JOIN editions e ON e.id = ma.edition_id
            WHERE ma.id = @parentEntityId
            UNION ALL
            SELECT COALESCE(grandparent.id, parent.id, member.id)
            FROM collections c
            INNER JOIN works member ON member.collection_id = c.id
            LEFT JOIN works parent ON parent.id = member.parent_work_id
            LEFT JOIN works grandparent ON grandparent.id = parent.parent_work_id
            WHERE c.id = @parentEntityId
            ORDER BY 1
            LIMIT 1;
            """, new { parentEntityId });

    private static string? NormalizeFilter(string? value, params string[] allowed) =>
        allowed.Contains(value?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            ? value!.Trim().ToLowerInvariant()
            : null;

    private static string GetFileName(string path)
    {
        var normalized = path.Replace('\\', '/');
        var separatorIndex = normalized.LastIndexOf('/');
        return separatorIndex >= 0 ? normalized[(separatorIndex + 1)..] : normalized;
    }

    private static string BuildCountSql() => $"""
        WITH RECURSIVE work_tree(id, parent_work_id, depth) AS (
            SELECT w.id, w.parent_work_id, 0 FROM works w WHERE w.id = @parentWorkId
            UNION ALL
            SELECT child.id, child.parent_work_id, work_tree.depth + 1
            FROM works child INNER JOIN work_tree ON child.parent_work_id = work_tree.id
        ),
        candidates AS ({CandidateSql})
        SELECT COUNT(*) FROM candidates;
        """;

    private static string BuildRowsSql() => $"""
        WITH RECURSIVE work_tree(id, parent_work_id, depth) AS (
            SELECT w.id, w.parent_work_id, 0 FROM works w WHERE w.id = @parentWorkId
            UNION ALL
            SELECT child.id, child.parent_work_id, work_tree.depth + 1
            FROM works child INNER JOIN work_tree ON child.parent_work_id = work_tree.id
        ),
        candidates AS ({CandidateSql})
        SELECT AssetId, WorkId, ParentWorkId, RootWorkId, MatchedTitle, MatchedNumber,
               SourceFilePath, MatchState, FileState,
               SeasonNumber, DiscNumber, VolumeNumber
        FROM candidates
        ORDER BY
            CASE WHEN SeasonNumber IS NULL THEN 1 ELSE 0 END, SeasonNumber,
            CASE WHEN VolumeNumber IS NULL THEN 1 ELSE 0 END, VolumeNumber,
            CASE WHEN DiscNumber IS NULL THEN 1 ELSE 0 END, DiscNumber,
            CASE WHEN WorkOrdinal IS NULL THEN 1 ELSE 0 END, WorkOrdinal,
            lower(MatchedTitle), AssetId
        LIMIT @limit OFFSET @offset;
        """;

    // Values use local canonical rows only. The path is returned for a selected
    // local asset and is searched as a whole, but never promoted into title text.
    private const string CandidateSql = """
        SELECT ma.id AS AssetId,
               w.id AS WorkId,
               w.parent_work_id AS ParentWorkId,
               @rootWorkId AS RootWorkId,
               COALESCE(
                   NULLIF((SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = ma.id AND cv.key IN ('episode_title', 'title') ORDER BY CASE cv.key WHEN 'episode_title' THEN 0 ELSE 1 END LIMIT 1), ''),
                   NULLIF((SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key IN ('episode_title', 'title') ORDER BY CASE cv.key WHEN 'episode_title' THEN 0 ELSE 1 END LIMIT 1), ''),
                   '') AS MatchedTitle,
               COALESCE(
                   NULLIF((SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = ma.id AND cv.key IN ('episode_number', 'track_number', 'issue_number', 'part_number') LIMIT 1), ''),
                   NULLIF((SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key IN ('episode_number', 'track_number', 'issue_number', 'part_number') LIMIT 1), ''),
                   CAST(w.ordinal AS TEXT)) AS MatchedNumber,
               ma.file_path_root AS SourceFilePath,
               CASE WHEN EXISTS (
                   SELECT 1 FROM canonical_values identity_value
                   WHERE identity_value.entity_id IN (ma.id, w.id)
                     AND identity_value.key IN ('tmdb_id', 'tvdb_id', 'musicbrainz_release_id', 'musicbrainz_recording_id', 'comicvine_issue_id', 'openlibrary_work_id', 'wikidata_qid')
                     AND NULLIF(identity_value.value, '') IS NOT NULL)
                   THEN 'matched' ELSE 'unmatched' END AS MatchState,
               COALESCE(NULLIF(ma.writeback_status, ''), 'unknown') AS FileState,
               CAST(COALESCE(
                   (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id IN (ma.id, w.id) AND cv.key = 'season_number' LIMIT 1),
                   (SELECT parent_ordinal.ordinal FROM works parent_ordinal WHERE parent_ordinal.id = w.parent_work_id)) AS INTEGER) AS SeasonNumber,
               CAST((SELECT cv.value FROM canonical_values cv WHERE cv.entity_id IN (ma.id, w.id) AND cv.key = 'disc_number' LIMIT 1) AS INTEGER) AS DiscNumber,
               CAST((SELECT cv.value FROM canonical_values cv WHERE cv.entity_id IN (ma.id, w.id) AND cv.key = 'volume_number' LIMIT 1) AS INTEGER) AS VolumeNumber,
               w.ordinal AS WorkOrdinal
        FROM work_tree tree
        INNER JOIN works w ON w.id = tree.id
        INNER JOIN editions e ON e.work_id = w.id
        INNER JOIN media_assets ma ON ma.edition_id = e.id
        WHERE w.ownership = 'Owned'
          AND ma.status = 'Normal'
          AND ma.is_orphaned = 0
          AND (@applySegmentFilter = 0 OR (ma.library_id || '|' || w.media_type) IN @allowedSegments)
          AND (tree.depth > 0 OR NOT EXISTS (
              SELECT 1 FROM works descendant
              INNER JOIN editions descendant_edition ON descendant_edition.work_id = descendant.id
              INNER JOIN media_assets descendant_asset ON descendant_asset.edition_id = descendant_edition.id
              WHERE descendant.parent_work_id = w.id
                AND descendant.ownership = 'Owned'
                AND descendant_asset.status = 'Normal'
                AND descendant_asset.is_orphaned = 0))
          AND (@query = '' OR ma.file_path_root LIKE '%' || @query || '%'
              OR EXISTS (SELECT 1 FROM canonical_values cv
                         WHERE cv.entity_id = ma.id
                           AND cv.key IN ('episode_title', 'title', 'episode_number', 'track_number', 'issue_number', 'part_number')
                           AND cv.value LIKE '%' || @query || '%')
              OR EXISTS (SELECT 1 FROM canonical_values cv
                         WHERE cv.entity_id = w.id
                           AND cv.key IN ('episode_title', 'title', 'episode_number', 'track_number', 'issue_number', 'part_number')
                           AND cv.value LIKE '%' || @query || '%')
              OR CAST(w.ordinal AS TEXT) LIKE '%' || @query || '%')
          AND (@season IS NULL OR CAST(COALESCE((SELECT cv.value FROM canonical_values cv WHERE cv.entity_id IN (ma.id, w.id) AND cv.key = 'season_number' LIMIT 1), (SELECT ordinal FROM works WHERE id = w.parent_work_id)) AS INTEGER) = @season)
          AND (@disc IS NULL OR CAST((SELECT cv.value FROM canonical_values cv WHERE cv.entity_id IN (ma.id, w.id) AND cv.key = 'disc_number' LIMIT 1) AS INTEGER) = @disc)
          AND (@volume IS NULL OR CAST((SELECT cv.value FROM canonical_values cv WHERE cv.entity_id IN (ma.id, w.id) AND cv.key = 'volume_number' LIMIT 1) AS INTEGER) = @volume)
          AND (@matchStatus IS NULL OR @matchStatus = CASE WHEN EXISTS (
              SELECT 1 FROM canonical_values identity_value WHERE identity_value.entity_id IN (ma.id, w.id)
              AND identity_value.key IN ('tmdb_id', 'tvdb_id', 'musicbrainz_release_id', 'musicbrainz_recording_id', 'comicvine_issue_id', 'openlibrary_work_id', 'wikidata_qid')
              AND NULLIF(identity_value.value, '') IS NOT NULL) THEN 'matched' ELSE 'unmatched' END)
          AND (@fileStatus IS NULL OR lower(COALESCE(NULLIF(ma.writeback_status, ''), 'unknown')) = lower(@fileStatus))
        """;

    private sealed class OwnedChildRow
    {
        public Guid AssetId { get; init; }
        public Guid WorkId { get; init; }
        public Guid? ParentWorkId { get; init; }
        public Guid RootWorkId { get; init; }
        public string? MatchedTitle { get; init; }
        public string? MatchedNumber { get; init; }
        public string SourceFilePath { get; init; } = string.Empty;
        public string MatchState { get; init; } = "unmatched";
        public string FileState { get; init; } = "unknown";
        public int? SeasonNumber { get; init; }
        public int? DiscNumber { get; init; }
        public int? VolumeNumber { get; init; }
    }
}
