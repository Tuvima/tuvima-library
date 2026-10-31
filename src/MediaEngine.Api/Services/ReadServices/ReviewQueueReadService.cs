using Dapper;
using MediaEngine.Api.Services.Display;
using MediaEngine.Application.ReadModels;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Review;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.ReadServices;

public sealed class ReviewQueueReadService : IReviewQueueReadService
{
    private static readonly string[] BridgeIdentifierKeys =
    [
        "isbn",
        "isbn_13",
        "isbn_10",
        "asin",
        "audible_id",
        "apple_books_id",
        "tmdb_id",
        "imdb_id",
        "tvdb_id",
        "musicbrainz_release_group_id",
        "musicbrainz_release_id",
        "musicbrainz_recording_id",
        "wikidata_qid",
    ];

    private static readonly string[] DetectedFactKeys =
    [
        "title", "author", "artist", "album", "album_artist", "director", "narrator",
        "year", "release_date", "runtime", "duration", "duration_sec", "language",
        "track_number", "disc_number", "isrc", "show_name", "season_number",
        "episode_number", "episode_title", "isbn", "file_name",
    ];

    private readonly IDatabaseConnection _db;
    private readonly IImportFolderService? _importFolders;
    private readonly IConfigurationLoader? _configuration;

    public ReviewQueueReadService(
        IDatabaseConnection db,
        IImportFolderService? importFolders = null,
        IConfigurationLoader? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
        _importFolders = importFolders;
        _configuration = configuration;
    }

    public Task<IReadOnlyList<ReviewItemDto>> GetPendingAsync(int limit, CancellationToken ct = default)
        => GetPendingCoreAsync(limit, null, ct);

    public Task<IReadOnlyList<ReviewItemDto>> GetPendingForLibraryAsync(int limit, string libraryId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryId);
        return GetPendingCoreAsync(limit, libraryId.Trim(), ct);
    }

    public async Task<IReadOnlyDictionary<string, int>> GetPendingCountsByLibraryAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        // The same items GetPendingForLibraryAsync lists: pending and review-ready, a Work through its first asset, never a Vault entity.
        var rows = await conn.QueryAsync<(string LibraryId, int Count)>($$"""
            SELECT lower(ma.library_id) AS LibraryId, COUNT(*) AS Count
            FROM review_queue rq
            JOIN media_assets ma ON ma.id = CASE
                WHEN rq.entity_type = 'MediaAsset' THEN rq.entity_id
                WHEN rq.entity_type = 'Work' THEN (
                    SELECT ma2.id FROM editions e JOIN media_assets ma2 ON ma2.edition_id = e.id
                    WHERE e.work_id = rq.entity_id ORDER BY ma2.file_path_root LIMIT 1)
                END
            WHERE rq.status = @status
              AND rq.review_ready_at IS NOT NULL
              AND {{VaultSql.NotVaultEntity("rq.entity_id")}}
              AND ma.library_id IS NOT NULL AND ma.library_id <> ''
            GROUP BY lower(ma.library_id)
            """, new { status = ReviewStatus.Pending }).ConfigureAwait(false);
        return rows.ToDictionary(row => row.LibraryId, row => row.Count, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<ReviewItemDto>> GetPendingCoreAsync(int limit, string? libraryId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var rows = (await conn.QueryAsync<ReviewDisplayRow>($$"""
            WITH pending AS (
                SELECT
                    rq.id,
                    rq.entity_id,
                    rq.entity_type,
                    rq.trigger,
                    rq.status,
                    rq.proposed_collection_id,
                    rq.confidence_score,
                    rq.candidates_json,
                    rq.detail,
                    rq.created_at,
                    rq.resolved_at,
                    rq.resolved_by,
                    rq.source_operation_id,
                    rq.source_capability_id,
                    rq.source_capability_sub_key,
                    rq.review_ready_at,
                    rq.automation_completed_at,
                    CASE
                        WHEN rq.entity_type = 'MediaAsset' THEN rq.entity_id
                        WHEN rq.entity_type = 'Work' THEN (
                            SELECT ma.id
                            FROM editions e
                            JOIN media_assets ma ON ma.edition_id = e.id
                            WHERE e.work_id = rq.entity_id
                            ORDER BY ma.file_path_root
                            LIMIT 1
                        )
                        ELSE NULL
                    END AS asset_id,
                    CASE
                        WHEN rq.entity_type = 'Work' THEN rq.entity_id
                        WHEN rq.entity_type = 'MediaAsset' THEN (
                            SELECT e.work_id
                            FROM media_assets ma
                            JOIN editions e ON e.id = ma.edition_id
                            WHERE ma.id = rq.entity_id
                            LIMIT 1
                        )
                        ELSE NULL
                    END AS work_id
                FROM review_queue rq
                WHERE rq.status = @status
                  AND rq.review_ready_at IS NOT NULL
                  AND {{VaultSql.NotVaultEntity("rq.entity_id")}}
            )
            SELECT
                p.id AS Id,
                p.entity_id AS EntityId,
                p.entity_type AS EntityType,
                p.trigger AS Trigger,
                p.status AS Status,
                p.proposed_collection_id AS ProposedCollectionId,
                p.confidence_score AS ConfidenceScore,
                p.candidates_json AS CandidatesJson,
                p.detail AS Detail,
                p.created_at AS CreatedAt,
                p.resolved_at AS ResolvedAt,
                p.resolved_by AS ResolvedBy,
                p.source_operation_id AS SourceOperationId,
                p.source_capability_id AS SourceCapabilityId,
                p.source_capability_sub_key AS SourceCapabilitySubKey,
                p.review_ready_at AS ReviewReadyAt,
                p.automation_completed_at AS AutomationCompletedAt,
                p.asset_id AS AssetId,
                p.work_id AS WorkId,
                COALESCE(
                    (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = p.work_id AND cv.key IN ('title', 'episode_title') AND cv.value <> '' ORDER BY CASE cv.key WHEN 'title' THEN 0 ELSE 1 END LIMIT 1),
                    (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = p.asset_id AND cv.key IN ('title', 'episode_title', 'file_name') AND cv.value <> '' ORDER BY CASE cv.key WHEN 'title' THEN 0 WHEN 'episode_title' THEN 1 ELSE 2 END LIMIT 1),
                    p.detail,
                    ma.file_path_root
                ) AS EntityTitle,
                COALESCE(
                    (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = p.asset_id AND cv.key IN ('media_type', 'media_type_detected') AND cv.value <> '' LIMIT 1),
                    (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = p.work_id AND cv.key IN ('media_type', 'media_type_detected') AND cv.value <> '' LIMIT 1),
                    w.media_type
                ) AS MediaType,
                COALESCE(
                    (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = p.asset_id AND cv.key IN ('cover_url', 'cover', 'poster_url') AND cv.value <> '' ORDER BY CASE cv.key WHEN 'cover_url' THEN 0 WHEN 'cover' THEN 1 ELSE 2 END LIMIT 1),
                    (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = p.work_id AND cv.key IN ('cover_url', 'cover', 'poster_url') AND cv.value <> '' ORDER BY CASE cv.key WHEN 'cover_url' THEN 0 WHEN 'cover' THEN 1 ELSE 2 END LIMIT 1)
                ) AS CoverUrl,
                ma.file_path_root AS FilePath
            FROM pending p
            LEFT JOIN media_assets ma ON ma.id = p.asset_id
            LEFT JOIN works w ON w.id = p.work_id
            WHERE ((p.entity_type = 'MediaAsset' AND ma.id IS NOT NULL)
               OR (p.entity_type = 'Work' AND w.id IS NOT NULL)
               OR (p.entity_type NOT IN ('MediaAsset', 'Work')))
              AND (@libraryId IS NULL OR (ma.id IS NOT NULL AND lower(ma.library_id) = lower(@libraryId)))
            ORDER BY p.created_at DESC
            LIMIT @limit;
            """, new { status = ReviewStatus.Pending, limit, libraryId })).AsList();

        var importPath = _importFolders?.GetImportPath();
        var managedRoots = ManagedFolderRoots();
        var result = new List<ReviewItemDto>(rows.Count);
        foreach (var row in rows)
        {
            var entry = ToEntry(row);
            var bridgeIds = await ReadBridgeIdentifiersAsync(conn, row, ct).ConfigureAwait(false);
            var detectedFacts = await ReadDetectedFactsAsync(conn, row, ct).ConfigureAwait(false);
            result.Add(ToContract(
                entry, row.MediaType, row.EntityTitle, ResolveCoverUrl(row), bridgeIds, detectedFacts,
                IsFromImportFolder(importPath, row.FilePath),
                IsFromMerge(managedRoots, row.FilePath)));
        }

        return result;
    }

    public async Task<ReviewItemDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var row = await conn.QueryFirstOrDefaultAsync<ReviewDisplayRow>($$"""
            WITH item AS (
                SELECT
                    rq.id,
                    rq.entity_id,
                    rq.entity_type,
                    rq.trigger,
                    rq.status,
                    rq.proposed_collection_id,
                    rq.confidence_score,
                    rq.candidates_json,
                    rq.detail,
                    rq.created_at,
                    rq.resolved_at,
                    rq.resolved_by,
                    rq.source_operation_id,
                    rq.source_capability_id,
                    rq.source_capability_sub_key,
                    rq.review_ready_at,
                    rq.automation_completed_at,
                    CASE
                        WHEN rq.entity_type = 'MediaAsset' THEN rq.entity_id
                        WHEN rq.entity_type = 'Work' THEN (
                            SELECT ma.id
                            FROM editions e
                            JOIN media_assets ma ON ma.edition_id = e.id
                            WHERE e.work_id = rq.entity_id
                            ORDER BY ma.file_path_root
                            LIMIT 1
                        )
                        ELSE NULL
                    END AS asset_id,
                    CASE
                        WHEN rq.entity_type = 'Work' THEN rq.entity_id
                        WHEN rq.entity_type = 'MediaAsset' THEN (
                            SELECT e.work_id
                            FROM media_assets ma
                            JOIN editions e ON e.id = ma.edition_id
                            WHERE ma.id = rq.entity_id
                            LIMIT 1
                        )
                        ELSE NULL
                    END AS work_id
                FROM review_queue rq
                WHERE rq.id = @id
                  AND {{VaultSql.NotVaultEntity("rq.entity_id")}}
            )
            SELECT
                i.id AS Id,
                i.entity_id AS EntityId,
                i.entity_type AS EntityType,
                i.trigger AS Trigger,
                i.status AS Status,
                i.proposed_collection_id AS ProposedCollectionId,
                i.confidence_score AS ConfidenceScore,
                i.candidates_json AS CandidatesJson,
                i.detail AS Detail,
                i.created_at AS CreatedAt,
                i.resolved_at AS ResolvedAt,
                i.resolved_by AS ResolvedBy,
                i.source_operation_id AS SourceOperationId,
                i.source_capability_id AS SourceCapabilityId,
                i.source_capability_sub_key AS SourceCapabilitySubKey,
                i.review_ready_at AS ReviewReadyAt,
                i.automation_completed_at AS AutomationCompletedAt,
                i.asset_id AS AssetId,
                i.work_id AS WorkId,
                COALESCE(
                    (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = i.work_id AND cv.key IN ('title', 'episode_title') AND cv.value <> '' ORDER BY CASE cv.key WHEN 'title' THEN 0 ELSE 1 END LIMIT 1),
                    (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = i.asset_id AND cv.key IN ('title', 'episode_title', 'file_name') AND cv.value <> '' ORDER BY CASE cv.key WHEN 'title' THEN 0 WHEN 'episode_title' THEN 1 ELSE 2 END LIMIT 1),
                    i.detail,
                    ma.file_path_root
                ) AS EntityTitle,
                COALESCE(
                    (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = i.asset_id AND cv.key IN ('media_type', 'media_type_detected') AND cv.value <> '' LIMIT 1),
                    (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = i.work_id AND cv.key IN ('media_type', 'media_type_detected') AND cv.value <> '' LIMIT 1),
                    w.media_type
                ) AS MediaType,
                COALESCE(
                    (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = i.asset_id AND cv.key IN ('cover_url', 'cover', 'poster_url') AND cv.value <> '' ORDER BY CASE cv.key WHEN 'cover_url' THEN 0 WHEN 'cover' THEN 1 ELSE 2 END LIMIT 1),
                    (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = i.work_id AND cv.key IN ('cover_url', 'cover', 'poster_url') AND cv.value <> '' ORDER BY CASE cv.key WHEN 'cover_url' THEN 0 WHEN 'cover' THEN 1 ELSE 2 END LIMIT 1)
                ) AS CoverUrl,
                ma.file_path_root AS FilePath
            FROM item i
            LEFT JOIN media_assets ma ON ma.id = i.asset_id
            LEFT JOIN works w ON w.id = i.work_id
            WHERE (i.entity_type = 'MediaAsset' AND ma.id IS NOT NULL)
               OR (i.entity_type = 'Work' AND w.id IS NOT NULL)
               OR (i.entity_type NOT IN ('MediaAsset', 'Work'));
            """, new { id });

        if (row is null)
        {
            return null;
        }

        var entry = ToEntry(row);
        var bridgeIds = await ReadBridgeIdentifiersAsync(conn, row, ct).ConfigureAwait(false);
        var detectedFacts = await ReadDetectedFactsAsync(conn, row, ct).ConfigureAwait(false);
        return ToContract(
            entry, row.MediaType, row.EntityTitle, ResolveCoverUrl(row), bridgeIds, detectedFacts,
            IsFromImportFolder(_importFolders?.GetImportPath(), row.FilePath),
            IsFromMerge(ManagedFolderRoots(), row.FilePath));
    }

    public async Task<int> GetPendingCountAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        return await conn.ExecuteScalarAsync<int>($$"""
            SELECT COUNT(DISTINCT rq.id)
            FROM review_queue rq
            WHERE rq.status = @status
              AND rq.review_ready_at IS NOT NULL
              AND (
                    (rq.entity_type = 'MediaAsset' AND EXISTS (
                        SELECT 1 FROM media_assets ma WHERE ma.id = rq.entity_id
                    ))
                 OR (rq.entity_type = 'Work' AND EXISTS (
                        SELECT 1 FROM works w WHERE w.id = rq.entity_id
                    ))
                 OR (rq.entity_type NOT IN ('MediaAsset', 'Work'))
              )
              AND {{VaultSql.NotVaultEntity("rq.entity_id")}};
            """, new { status = ReviewStatus.Pending });
    }

    public async Task<IReadOnlyList<ReviewReasonCount>> GetPendingReasonCountsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync<ReviewReasonCountRow>($$"""
            SELECT rq.trigger AS Trigger, rq.detail AS Detail, COUNT(DISTINCT rq.id) AS Count
            FROM review_queue rq
            WHERE rq.status = @status
              AND rq.review_ready_at IS NOT NULL
              AND (
                    (rq.entity_type = 'MediaAsset' AND EXISTS (
                        SELECT 1 FROM media_assets ma WHERE ma.id = rq.entity_id
                    ))
                 OR (rq.entity_type = 'Work' AND EXISTS (
                        SELECT 1 FROM works w WHERE w.id = rq.entity_id
                    ))
                 OR (rq.entity_type NOT IN ('MediaAsset', 'Work'))
              )
              AND {{VaultSql.NotVaultEntity("rq.entity_id")}}
            GROUP BY rq.trigger, rq.detail;
            """, new { status = ReviewStatus.Pending });

        return rows
            .Select(row => new ReviewReasonCount(row.Trigger, row.Detail, ToInt(row.Count)))
            .ToList();
    }

    private static ReviewQueueEntry ToEntry(ReviewDisplayRow row) => new()
    {
        Id = row.Id,
        EntityId = row.EntityId,
        EntityType = row.EntityType,
        Trigger = row.Trigger,
        Status = row.Status,
        ProposedCollectionId = row.ProposedCollectionId,
        ConfidenceScore = row.ConfidenceScore,
        CandidatesJson = row.CandidatesJson,
        Detail = row.Detail,
        CreatedAt = ParseDate(row.CreatedAt) ?? DateTimeOffset.UtcNow,
        ResolvedAt = ParseDate(row.ResolvedAt),
        ResolvedBy = row.ResolvedBy,
        SourceOperationId = row.SourceOperationId,
        SourceCapabilityId = row.SourceCapabilityId,
        SourceCapabilitySubKey = row.SourceCapabilitySubKey,
        ReviewReadyAt = ParseDate(row.ReviewReadyAt),
        AutomationCompletedAt = ParseDate(row.AutomationCompletedAt),
    };

    private static ReviewItemDto ToContract(
        ReviewQueueEntry entry,
        string? mediaType,
        string? entityTitle,
        string? coverUrl,
        Dictionary<string, string> bridgeIdentifiers,
        Dictionary<string, string> detectedFacts,
        bool fromImportFolder,
        bool fromMerge) => new()
        {
            Id = entry.Id,
            EntityId = entry.EntityId,
            EntityType = entry.EntityType,
            Trigger = entry.Trigger,
            Status = entry.Status,
            ProposedCollectionId = entry.ProposedCollectionId,
            ConfidenceScore = entry.ConfidenceScore,
            CandidatesJson = entry.CandidatesJson,
            Detail = entry.Detail,
            CreatedAt = entry.CreatedAt,
            ResolvedAt = entry.ResolvedAt,
            ResolvedBy = entry.ResolvedBy,
            SourceOperationId = entry.SourceOperationId,
            SourceCapabilityId = entry.SourceCapabilityId,
            SourceCapabilitySubKey = entry.SourceCapabilitySubKey,
            MediaType = mediaType,
            EntityTitle = entityTitle,
            CoverUrl = coverUrl,
            BridgeIdentifiers = bridgeIdentifiers,
            DetectedFacts = detectedFacts,
            FromImportFolder = fromImportFolder,
            FromMerge = fromMerge,
        };

    // A provider's web link never reaches the browser. Items in review often have no saved copy yet, so the work's
    // cover route is preferred: it serves the Engine's copy when there is one and otherwise fetches the image itself.
    private static string? ResolveCoverUrl(ReviewDisplayRow row)
    {
        if (!DisplayArtworkUrlResolver.IsExternalProviderUrl(row.CoverUrl))
        {
            return row.CoverUrl;
        }

        if (row.WorkId is Guid workId)
        {
            return $"/stream/entity/work/{workId:D}/cover";
        }

        return row.AssetId is Guid assetId ? DisplayArtworkUrlResolver.Resolve(row.CoverUrl, assetId, "cover", state: null) : null;
    }

    private static bool IsFromImportFolder(string? importPath, string? filePath) =>
        !string.IsNullOrWhiteSpace(importPath)
        && !string.IsNullOrWhiteSpace(filePath)
        && ImportFolderRules.IsInside(importPath, filePath);

    /// <summary>The folders Tuvima manages, read once per request. Path-based, so it does not depend on how the file got there.</summary>
    private List<string> ManagedFolderRoots() =>
        _configuration?.LoadLibraries().Libraries
            .SelectMany(library => library.Sources)
            .Where(source => source.IsManaged && !string.IsNullOrWhiteSpace(source.Path))
            .Select(source => source.Path)
            .ToList()
        ?? [];

    private static bool IsFromMerge(List<string> managedRoots, string? filePath) =>
        !string.IsNullOrWhiteSpace(filePath)
        && managedRoots.Any(root => FolderMergeFolders.IsInsideHoldingFolder(root, filePath));

    private static async Task<Dictionary<string, string>> ReadDetectedFactsAsync(
        System.Data.IDbConnection conn,
        ReviewDisplayRow row,
        CancellationToken ct)
    {
        var entityIds = new[] { row.EntityId, row.AssetId, row.WorkId }
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();

        if (entityIds.Length == 0)
        {
            return [];
        }

        var command = new CommandDefinition("""
            SELECT key AS Key, value AS Value
            FROM canonical_values
            WHERE entity_id IN @entityIds
              AND key IN @keys
              AND value IS NOT NULL
              AND value <> ''
            ORDER BY CASE key WHEN 'title' THEN 0 WHEN 'episode_title' THEN 1 ELSE 2 END;
            """, new
        {
            entityIds = entityIds.Select(GuidSql.ToBlob).ToArray(),
            keys = DetectedFactKeys,
        }, cancellationToken: ct);

        var rows = await conn.QueryAsync<KeyValueRow>(command).ConfigureAwait(false);
        return rows
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<Dictionary<string, string>> ReadBridgeIdentifiersAsync(
        System.Data.IDbConnection conn,
        ReviewDisplayRow row,
        CancellationToken ct)
    {
        var entityIds = new[] { row.EntityId, row.AssetId, row.WorkId }
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();

        if (entityIds.Length == 0)
        {
            return [];
        }

        var command = new CommandDefinition("""
            SELECT key AS Key, value AS Value
            FROM canonical_values
            WHERE entity_id IN @entityIds
              AND key IN @keys
              AND value IS NOT NULL
              AND value <> ''
            ORDER BY
                CASE key
                    WHEN 'wikidata_qid' THEN 0
                    WHEN 'isbn_13' THEN 1
                    WHEN 'isbn' THEN 2
                    ELSE 3
                END;
            """, new { entityIds = entityIds.Select(GuidSql.ToBlob).ToArray(), keys = BridgeIdentifierKeys }, cancellationToken: ct);

        var rows = await conn.QueryAsync<KeyValueRow>(command).ConfigureAwait(false);
        return rows
            .GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().Value,
                StringComparer.OrdinalIgnoreCase);
    }

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

    private static int ToInt(long value) =>
        value > int.MaxValue ? int.MaxValue : (int)Math.Max(0, value);

    private sealed class ReviewDisplayRow
    {
        public Guid Id { get; init; }
        public Guid EntityId { get; init; }
        public string EntityType { get; init; } = string.Empty;
        public string Trigger { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string? ProposedCollectionId { get; init; }
        public double? ConfidenceScore { get; init; }
        public string? CandidatesJson { get; init; }
        public string? Detail { get; init; }
        public string? CreatedAt { get; init; }
        public string? ResolvedAt { get; init; }
        public string? ResolvedBy { get; init; }
        public Guid? SourceOperationId { get; init; }
        public string? SourceCapabilityId { get; init; }
        public string? SourceCapabilitySubKey { get; init; }
        public string? ReviewReadyAt { get; init; }
        public string? AutomationCompletedAt { get; init; }
        public Guid? AssetId { get; init; }
        public Guid? WorkId { get; init; }
        public string? MediaType { get; init; }
        public string? EntityTitle { get; init; }
        public string? CoverUrl { get; init; }
        public string? FilePath { get; init; }
    }

    private sealed record KeyValueRow(string Key, string Value);

    private sealed class ReviewReasonCountRow
    {
        public string? Trigger { get; init; }
        public string? Detail { get; init; }
        public long Count { get; init; }
    }
}
