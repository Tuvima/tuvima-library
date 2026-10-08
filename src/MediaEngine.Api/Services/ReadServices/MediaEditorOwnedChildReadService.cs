using Dapper;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaEngine.Application.ReadModels;
using MediaEngine.Application.Services;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
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
    private static readonly string[] IdentityCanonicalKeys =
    [
        .. BridgeIdKeys.All.OrderBy(key => key, StringComparer.Ordinal),
        MetadataFieldConstants.IdentityRevision,
        MetadataFieldConstants.IdentityProvider,
        MetadataFieldConstants.IdentityProviderItemId,
        MetadataFieldConstants.WikidataQidScope,
        MetadataFieldConstants.SeasonNumber,
        MetadataFieldConstants.EpisodeNumber,
        MetadataFieldConstants.TrackNumber,
        MetadataFieldConstants.DiscNumber,
        MetadataFieldConstants.IssueNumber,
        // These legacy canonical placement keys have no shared constants yet.
        "volume_number", "part_number"
    ];

    public Task<IReadOnlyDictionary<Guid, string>> GetSelectionRevisionsForAssetsAsync(
        Guid parentEntityId, IReadOnlyList<Guid> assetIds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (assetIds.Count == 0 || assetIds.Count > 1000 || assetIds.Contains(Guid.Empty))
        {
            return Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
        }

        return db.ExecuteReadAsync<IReadOnlyDictionary<Guid, string>>((connection, transaction, token) =>
        {
        var parentWorkId = ResolveParentWorkId(connection, parentEntityId, transaction);
        if (parentWorkId is null)
        {
            return new Dictionary<Guid, string>();
        }
        var rows = new List<OwnedChildRow>(assetIds.Count);
        foreach (var batch in assetIds.Distinct().Chunk(400))
        {
            ct.ThrowIfCancellationRequested();
            rows.AddRange(connection.Query<OwnedChildRow>(new CommandDefinition("""
                SELECT ma.id AS AssetId, e.id AS EditionId, w.id AS WorkId,
                       w.parent_work_id AS ParentWorkId,
                       COALESCE(grandparent.id, parent.id, w.id) AS RootWorkId,
                       ma.library_id AS LibraryId, w.media_type AS MediaType,
                       w.work_kind AS WorkKind, w.ordinal AS WorkOrdinal,
                       w.ordinal_sort AS WorkOrdinalSort,
                       w.wikidata_qid AS WorkWikidataQid,
                       e.wikidata_qid AS EditionWikidataQid
                FROM media_assets ma
                JOIN editions e ON e.id = ma.edition_id
                JOIN works w ON w.id = e.work_id
                LEFT JOIN works parent ON parent.id = w.parent_work_id
                LEFT JOIN works grandparent ON grandparent.id = parent.parent_work_id
                WHERE ma.id IN @assetIds AND ma.status = 'Normal'
                  AND ma.is_orphaned = 0 AND w.ownership = 'Owned';
                """, new { assetIds = batch.Select(GuidSql.ToBlob).ToArray() },
                transaction, cancellationToken: ct)));
        }
        var revisions = GetSelectionRevisions(connection, rows, ct, transaction);
        return revisions;
        }, ct);
    }

    public async Task<IReadOnlyList<MediaEditorAssetAccessSegment>> GetAccessSegmentsAsync(Guid parentEntityId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var parentWorkId = ResolveParentWorkId(connection, parentEntityId);
        if (parentWorkId is null)
        {
            return [];
        }
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

    public Task<MediaEditorOwnedChildSearchEnvelope?> SearchAsync(
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

        return db.ExecuteReadAsync<MediaEditorOwnedChildSearchEnvelope?>((connection, transaction, token) =>
        {
        var parentWorkId = ResolveParentWorkId(connection, parentEntityId, transaction);

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
            """, new { resolvedParentWorkId }, transaction) ?? resolvedParentWorkId;

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

        var totalCount = connection.QuerySingle<int>(BuildCountSql(), parameters, transaction);
        var rows = connection.Query<OwnedChildRow>(BuildRowsSql(), parameters, transaction).ToList();
        var selectionRevisions = GetSelectionRevisions(connection, rows, ct, transaction);
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
            row.VolumeNumber,
            row.EditionId,
            row.EditionLabel,
            row.EditionAssetCount,
            row.WorkEditionCount,
            !row.HasEditionMetadata && !row.HasEditionArtwork
                && !HasMeaningfulEditionLabel(row.EditionLabel),
            row.WorkId,
            row.HasEditionArtwork ? row.EditionId
                : row.MediaType == "Music" ? row.RootWorkId : row.WorkId,
            row.HasEditionMetadata ? row.EditionId : row.WorkId,
            "asset",
            row.EditionReleaseId,
            row.ParentWorkId,
            selectionRevisions[row.AssetId])).ToList();

        return new MediaEditorOwnedChildSearchEnvelope(
            parentEntityId, page, pageSize, totalCount, items);
        }, ct);
    }

    public Task<MediaEditorOwnedChildSelectionSnapshotEnvelope?> SnapshotMatchingAsync(
        Guid parentEntityId,
        string? query,
        int? season,
        int? disc,
        int? volume,
        string? matchStatus,
        string? fileStatus,
        CancellationToken ct,
        IReadOnlyList<string> allowedSegments)
    {
        ct.ThrowIfCancellationRequested();
        return db.ExecuteReadAsync<MediaEditorOwnedChildSelectionSnapshotEnvelope?>((connection, transaction, token) =>
        {
        var parentWorkId = ResolveParentWorkId(connection, parentEntityId, transaction);
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
            """, new { resolvedParentWorkId }, transaction) ?? resolvedParentWorkId;
        var parameters = new
        {
            parentWorkId = resolvedParentWorkId,
            rootWorkId,
            query = (query ?? string.Empty).Trim(),
            season,
            disc,
            volume,
            matchStatus = NormalizeFilter(matchStatus, "matched", "unmatched"),
            fileStatus = string.IsNullOrWhiteSpace(fileStatus) ? null : fileStatus.Trim(),
            applySegmentFilter = 1,
            allowedSegments = allowedSegments.Count > 0 ? allowedSegments : ["<none>"],
            limit = 1001,
            offset = 0,
        };
        var rows = connection.Query<OwnedChildRow>(
            new CommandDefinition(BuildRowsSql(), parameters, transaction, cancellationToken: ct)).ToList();
        if (rows.Count > 1000)
        {
            return new(parentEntityId, true, []);
        }

        var revisions = GetSelectionRevisions(connection, rows, ct, transaction);
        var items = rows.Select(row => new MediaEditorOwnedChildSelectionItemEnvelope(
            row.AssetId, revisions[row.AssetId], row.LibraryId, row.MediaType)).ToList();
        return new(parentEntityId, false, items);
        }, ct);
    }

    private static Guid? ResolveParentWorkId(System.Data.IDbConnection connection, Guid parentEntityId,
        System.Data.IDbTransaction? transaction = null) =>
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
            """, new { parentEntityId }, transaction);

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

    private static bool HasMeaningfulEditionLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return false;
        }
        var value = label.Trim().ToLowerInvariant();
        return new[] { "edition", "release", "cut", "version", "printing", "variant",
            "remaster", "narrat", "translation", "illustrated", "deluxe", "anniversary" }
            .Any(value.Contains);
    }

    private static IReadOnlyDictionary<Guid, string> GetSelectionRevisions(
        System.Data.IDbConnection connection, IReadOnlyList<OwnedChildRow> rows, CancellationToken ct,
        System.Data.IDbTransaction? transaction = null)
    {
        var entityIds = rows.SelectMany(row => new Guid?[]
            { row.AssetId, row.EditionId, row.WorkId, row.ParentWorkId, row.RootWorkId })
            .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
        var identities = new Dictionary<Guid, List<IdentityPiece>>();
        foreach (var batch in entityIds.Chunk(400))
        {
            ct.ThrowIfCancellationRequested();
            var idBlobs = batch.Select(GuidSql.ToBlob).ToArray();
            foreach (var identity in connection.Query<IdentityPiece>(new CommandDefinition("""
                SELECT entity_id AS EntityId, 'canonical' AS Kind,
                       key AS IdentityKey, value AS IdentityValue
                FROM canonical_values
                WHERE entity_id IN @idBlobs AND key IN @identityKeys
                UNION ALL
                SELECT entity_id AS EntityId, 'bridge' AS Kind,
                       id_type AS IdentityKey, id_value AS IdentityValue
                FROM bridge_ids
                WHERE entity_id IN @idBlobs;
                """, new { idBlobs, identityKeys = IdentityCanonicalKeys },
                transaction, cancellationToken: ct)))
            {
                if (!identities.TryGetValue(identity.EntityId, out var pieces))
                {
                    identities[identity.EntityId] = pieces = [];
                }
                pieces.Add(identity);
            }
        }

        return rows.ToDictionary(row => row.AssetId, row =>
        {
            var relatedIds = new Guid?[]
                { row.AssetId, row.EditionId, row.WorkId, row.ParentWorkId, row.RootWorkId };
            var identityState = relatedIds.Where(id => id.HasValue).Select(id => id!.Value)
                .Distinct().SelectMany(id => identities.GetValueOrDefault(id) ?? [])
                .OrderBy(piece => piece.EntityId)
                .ThenBy(piece => piece.Kind, StringComparer.Ordinal)
                .ThenBy(piece => piece.IdentityKey, StringComparer.Ordinal)
                .ThenBy(piece => piece.IdentityValue, StringComparer.Ordinal)
                .ToArray();
            // Only membership and identity facts enter this digest. Paths, file status,
            // codec data, and display artwork may change without invalidating selection.
            var snapshot = JsonSerializer.Serialize(new
            {
                version = 1,
                row.AssetId,
                row.EditionId,
                row.WorkId,
                row.ParentWorkId,
                row.RootWorkId,
                row.LibraryId,
                row.MediaType,
                row.WorkKind,
                row.WorkOrdinal,
                row.WorkOrdinalSort,
                row.WorkWikidataQid,
                row.EditionWikidataQid,
                identityState
            });
            return "v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot))).ToLowerInvariant();
        });
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
        SELECT AssetId, EditionId, EditionLabel, EditionAssetCount, WorkEditionCount,
               HasEditionMetadata, HasEditionArtwork, EditionReleaseId, MediaType,
               WorkId, ParentWorkId, RootWorkId, LibraryId, WorkKind,
               WorkOrdinal, WorkOrdinalSort, WorkWikidataQid, EditionWikidataQid,
               MatchedTitle, MatchedNumber,
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
               e.id AS EditionId,
               COALESCE(NULLIF(e.format_label, ''),
                   (SELECT NULLIF(cv.value, '') FROM canonical_values cv
                    WHERE cv.entity_id = e.id AND cv.key IN ('edition_title', 'edition_label', 'release_title')
                    LIMIT 1)) AS EditionLabel,
               (SELECT COUNT(*) FROM media_assets edition_asset
                WHERE edition_asset.edition_id = e.id AND edition_asset.status = 'Normal'
                  AND edition_asset.is_orphaned = 0) AS EditionAssetCount,
               (SELECT COUNT(*) FROM editions work_edition
                WHERE work_edition.work_id = w.id AND EXISTS (
                    SELECT 1 FROM media_assets owned_asset WHERE owned_asset.edition_id = work_edition.id
                      AND owned_asset.status = 'Normal' AND owned_asset.is_orphaned = 0)) AS WorkEditionCount,
               CASE WHEN e.wikidata_qid IS NOT NULL
                    OR EXISTS (SELECT 1 FROM canonical_values edition_value
                               WHERE edition_value.entity_id = e.id AND edition_value.key IN
                               ('isbn', 'asin', 'publisher', 'edition_release_date', 'edition_release_year',
                                'edition_title', 'edition_label', 'musicbrainz_release_id', 'narrator'))
                    THEN 1 ELSE 0 END AS HasEditionMetadata,
               CASE WHEN EXISTS (SELECT 1 FROM entity_artwork_links edition_art
                                 WHERE edition_art.entity_id = e.id AND edition_art.entity_type = 'Edition')
                    THEN 1 ELSE 0 END AS HasEditionArtwork,
               (SELECT cv.value FROM canonical_values cv
                WHERE cv.entity_id = e.id AND cv.key = 'musicbrainz_release_id'
                LIMIT 1) AS EditionReleaseId,
               w.media_type AS MediaType,
               w.id AS WorkId,
               w.parent_work_id AS ParentWorkId,
               @rootWorkId AS RootWorkId,
               ma.library_id AS LibraryId,
               w.work_kind AS WorkKind,
               w.ordinal AS WorkOrdinal,
               w.ordinal_sort AS WorkOrdinalSort,
               w.wikidata_qid AS WorkWikidataQid,
               e.wikidata_qid AS EditionWikidataQid,
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
        public Guid EditionId { get; init; }
        public string? EditionLabel { get; init; }
        public int EditionAssetCount { get; init; }
        public int WorkEditionCount { get; init; }
        public bool HasEditionMetadata { get; init; }
        public bool HasEditionArtwork { get; init; }
        public string? EditionReleaseId { get; init; }
        public string MediaType { get; init; } = string.Empty;
        public Guid WorkId { get; init; }
        public Guid? ParentWorkId { get; init; }
        public Guid RootWorkId { get; init; }
        public string? LibraryId { get; init; }
        public string WorkKind { get; init; } = string.Empty;
        public int? WorkOrdinal { get; init; }
        public double? WorkOrdinalSort { get; init; }
        public string? WorkWikidataQid { get; init; }
        public string? EditionWikidataQid { get; init; }
        public string? MatchedTitle { get; init; }
        public string? MatchedNumber { get; init; }
        public string SourceFilePath { get; init; } = string.Empty;
        public string MatchState { get; init; } = "unmatched";
        public string FileState { get; init; } = "unknown";
        public int? SeasonNumber { get; init; }
        public int? DiscNumber { get; init; }
        public int? VolumeNumber { get; init; }
    }

    private sealed class IdentityPiece
    {
        public Guid EntityId { get; init; }
        public string Kind { get; init; } = string.Empty;
        public string IdentityKey { get; init; } = string.Empty;
        public string IdentityValue { get; init; } = string.Empty;
    }
}
