using System.Text.Json;
using Dapper;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

/// <summary>
/// Stores personal-library items independently from catalogue identities.
/// Logical items, hash-addressed files, and observed source paths are separate
/// so compound assets and exact duplicates retain truthful provenance.
/// </summary>
public sealed class LocalAssetRepository(IDatabaseConnection database) : ILocalAssetRepository
{
    private sealed class ItemRow
    {
        public Guid Id { get; init; }
        public string ScopeKind { get; init; } = LocalAssetScopeKinds.Personal;
        public Guid LibraryId { get; init; }
        public Guid? PersonalSpaceId { get; init; }
        public Guid? OwnerProfileId { get; init; }
        public string MediaKind { get; init; } = string.Empty;
        public string? Title { get; init; }
        public string FileName { get; init; } = string.Empty;
        public string MimeType { get; init; } = string.Empty;
        public DateTimeOffset? CapturedAt { get; init; }
        public DateTimeOffset? EmbeddedCapturedAt { get; init; }
        public long CapturedAtUserOverride { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public int? Width { get; init; }
        public int? Height { get; init; }
        public double? DurationSeconds { get; init; }
        public int? PageCount { get; init; }
        public string? DeviceMake { get; init; }
        public string? DeviceModel { get; init; }
        public double? Latitude { get; init; }
        public double? Longitude { get; init; }
        public string? LocationName { get; init; }
        public string? LocationCity { get; init; }
        public string? LocationRegion { get; init; }
        public string? LocationCountry { get; init; }
        public string? LocationCountryCode { get; init; }
        public string? LocationSource { get; init; }
        public long LocationUserOverride { get; init; }
        public double? EmbeddedLatitude { get; init; }
        public double? EmbeddedLongitude { get; init; }
        public string? Description { get; init; }
        public string? LensModel { get; init; }
        public string? ExposureTime { get; init; }
        public double? Aperture { get; init; }
        public int? Iso { get; init; }
        public double? FocalLengthMm { get; init; }
        public string? VideoCodec { get; init; }
        public double? FrameRate { get; init; }
        public long Favorite { get; init; }
        public long Hidden { get; init; }
        public DateTimeOffset? ArchivedAt { get; init; }
        public DateTimeOffset? TrashedAt { get; init; }
        public DateTimeOffset EffectiveAt { get; init; }
        public long SourceCount { get; init; }
        public long TotalCount { get; init; }
    }

    private sealed class FileRow
    {
        public Guid Id { get; init; }
        public string Role { get; init; } = string.Empty;
        public string? DerivativeKind { get; init; }
        public string MimeType { get; init; } = string.Empty;
        public long ByteSize { get; init; }
        public long SourceCount { get; init; }
        public string? FileName { get; init; }
    }

    private sealed class ItemFileRow
    {
        public Guid ItemId { get; init; }
        public Guid Id { get; init; }
        public string Role { get; init; } = string.Empty;
        public string? DerivativeKind { get; init; }
        public string MimeType { get; init; } = string.Empty;
        public long ByteSize { get; init; }
        public long SourceCount { get; init; }
        public string? FileName { get; init; }
    }

    private sealed class ItemTagRow
    {
        public Guid ItemId { get; init; }
        public string Tag { get; init; } = string.Empty;
    }

    private sealed class ContentRow
    {
        public Guid ItemId { get; init; }
        public Guid FileId { get; init; }
        public Guid LibraryId { get; init; }
        public Guid? OwnerProfileId { get; init; }
        public Guid? SourceId { get; init; }
        public Guid? DeviceId { get; init; }
        public string FilePath { get; init; } = string.Empty;
        public string MimeType { get; init; } = string.Empty;
        public long ByteSize { get; init; }
        public string ContentHash { get; init; } = string.Empty;
        public string Role { get; init; } = string.Empty;
        public string? DerivativeKind { get; init; }
    }

    private sealed class SearchDocumentRow
    {
        public string? Title { get; init; }
        public string FileName { get; init; } = string.Empty;
        public string MimeType { get; init; } = string.Empty;
        public string MediaKind { get; init; } = string.Empty;
        public string? CapturedAt { get; init; }
        public string? Device { get; init; }
        public string? Location { get; init; }
        public string? DocumentText { get; init; }
        public string? Tags { get; init; }
        public string? People { get; init; }
    }

    public LocalAssetPageDto Query(LocalAssetQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateQuery(query);
        ct.ThrowIfCancellationRequested();

        var mediaKinds = query.MediaKinds?
            .Where(kind => !string.IsNullOrWhiteSpace(kind))
            .Select(NormalizeMediaKind)
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
        var searchExpression = BuildSearchExpression(query.Search);

        using var connection = database.CreateConnection();
        var rows = connection.Query<ItemRow>("""
            SELECT li.id AS Id, li.scope_kind AS ScopeKind,
                   li.library_id AS LibraryId,
                   li.personal_space_id AS PersonalSpaceId,
                   li.owner_profile_id AS OwnerProfileId,
                   li.media_kind AS MediaKind,
                   li.title AS Title,
                   li.primary_file_name AS FileName,
                   li.primary_mime_type AS MimeType,
                   li.captured_at AS CapturedAt,
                   li.embedded_captured_at AS EmbeddedCapturedAt,
                   li.captured_at_user_override AS CapturedAtUserOverride,
                   li.created_at AS CreatedAt,
                   lm.width AS Width,
                   lm.height AS Height,
                   lm.duration_seconds AS DurationSeconds,
                   lm.page_count AS PageCount,
                   lm.device_make AS DeviceMake,
                   lm.device_model AS DeviceModel,
                   lm.latitude AS Latitude,
                   lm.longitude AS Longitude,
                   lm.location_name AS LocationName,
                   lm.location_city AS LocationCity, lm.location_region AS LocationRegion,
                   lm.location_country AS LocationCountry, lm.location_country_code AS LocationCountryCode,
                   lm.location_source AS LocationSource, lm.location_user_override AS LocationUserOverride,
                   lm.embedded_latitude AS EmbeddedLatitude, lm.embedded_longitude AS EmbeddedLongitude,
                   lm.description AS Description, lm.lens_model AS LensModel,
                   lm.exposure_time AS ExposureTime, lm.aperture AS Aperture, lm.iso AS Iso,
                   lm.focal_length_mm AS FocalLengthMm, lm.video_codec AS VideoCodec, lm.frame_rate AS FrameRate,
                   li.favorite AS Favorite,
                   li.hidden AS Hidden,
                   li.archived_at AS ArchivedAt,
                   li.trashed_at AS TrashedAt,
                   COALESCE(li.captured_at, li.created_at) AS EffectiveAt,
                   (SELECT COUNT(DISTINCT lfs.id)
                      FROM local_item_files lif
                      JOIN local_file_sources lfs
                        ON lfs.file_id = lif.file_id AND lfs.library_id = li.library_id
                     WHERE lif.item_id = li.id) AS SourceCount,
                   COUNT(*) OVER() AS TotalCount
              FROM local_items li
              LEFT JOIN local_item_metadata lm ON lm.item_id = li.id
             WHERE li.library_id = @LibraryId
               AND ((@HiddenOnly = 1 AND li.hidden = 1)
                    OR (@HiddenOnly = 0 AND (@IncludeHidden = 1 OR li.hidden = 0)))
               AND (@FavoritesOnly = 0 OR li.favorite = 1)
               AND (@Lifecycle = 3
                    OR (@Lifecycle = 0 AND li.archived_at IS NULL AND li.trashed_at IS NULL)
                    OR (@Lifecycle = 1 AND li.archived_at IS NOT NULL AND li.trashed_at IS NULL)
                    OR (@Lifecycle = 2 AND li.trashed_at IS NOT NULL))
               AND (@HasMediaKinds = 0 OR li.media_kind IN @MediaKinds)
               AND (@GalleryId IS NULL OR EXISTS (
                    SELECT 1 FROM view_gallery_items vgi
                     WHERE vgi.gallery_id = @GalleryId AND vgi.item_id = li.id))
               AND (@SearchExpression IS NULL OR EXISTS (
                    SELECT 1 FROM local_item_search lis
                      JOIN local_item_search_keys lsk ON lsk.rowid = lis.rowid
                     WHERE lsk.item_id = li.id
                       AND local_item_search MATCH @SearchExpression))
             ORDER BY COALESCE(li.captured_at, li.created_at) DESC, li.id
             LIMIT @Limit OFFSET @Offset;
            """, new
        {
            query.LibraryId,
            query.Offset,
            query.Limit,
            HiddenOnly = query.HiddenOnly ? 1 : 0,
            IncludeHidden = query.IncludeHidden ? 1 : 0,
            FavoritesOnly = query.FavoritesOnly ? 1 : 0,
            HasMediaKinds = mediaKinds.Length == 0 ? 0 : 1,
            MediaKinds = mediaKinds.Length == 0 ? [LocalAssetMediaKinds.Other] : mediaKinds,
            query.GalleryId,
            Lifecycle = (int)query.Lifecycle,
            SearchExpression = searchExpression,
        }).ToList();

        var items = MapItems(connection, rows);
        var total = rows.Count == 0 ? 0 : checked((int)rows[0].TotalCount);
        return new LocalAssetPageDto(
            items,
            query.Offset,
            query.Limit,
            total,
            query.Offset + items.Count < total);
    }

    public LocalAssetTimelinePage QueryTimeline(LocalAssetTimelineQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if ((query.AuthorizedLibraryIds is null || query.AuthorizedLibraryIds.Count == 0)
            && !query.IncludeSharedLibraryAssets)
        {
            throw new ArgumentException("At least one resolver-authorized library is required.", nameof(query));
        }

        if (query.AuthorizedLibraryIds?.Any(id => id == Guid.Empty) == true)
        {
            throw new ArgumentException("Authorized library IDs cannot be empty.", nameof(query));
        }

        if ((query.BeforeEffectiveAt.HasValue) != (query.BeforeItemId.HasValue))
        {
            throw new ArgumentException("Both timeline cursor values are required together.", nameof(query));
        }

        if (query.AnchorBefore.HasValue && query.BeforeEffectiveAt.HasValue)
        {
            throw new ArgumentException("A timeline date anchor cannot be combined with a cursor.", nameof(query));
        }

        if (query.Limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "Limit must be between 1 and 500.");
        }

        ct.ThrowIfCancellationRequested();

        var libraryIds = query.AuthorizedLibraryIds?.Distinct().ToArray() ?? [];
        var mediaKinds = query.MediaKinds?
            .Where(kind => !string.IsNullOrWhiteSpace(kind))
            .Select(NormalizeMediaKind)
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
        var searchExpression = BuildSearchExpression(query.Search);
        var libraryPredicate = libraryIds.Length == 0
            ? "0 = 1"
            : string.Join(" OR ", libraryIds.Select((_, index) => $"li.library_id = @LibraryId{index}"));
        // The Shared scope reads only the libraries the resolver authorized (the caller's household's), never every
        // household's shared assets.

        var smartRule = query.SmartRule is null
            ? new LocalAssetSmartRuleSql("1 = 1", new DynamicParameters())
            : LocalAssetSmartRuleSqlCompiler.Compile(query.SmartRule);
        var parameters = new DynamicParameters(new
        {
            query.IncludeHidden,
            query.HiddenOnly,
            query.FavoritesOnly,
            Lifecycle = (int)query.Lifecycle,
            HasMediaKinds = mediaKinds.Length == 0 ? 0 : 1,
            MediaKinds = mediaKinds.Length == 0 ? [LocalAssetMediaKinds.Other] : mediaKinds,
            query.GalleryId,
            SearchExpression = searchExpression,
            query.BeforeEffectiveAt,
            query.AddedBefore,
            query.AddedAfterKey,
            query.BeforeItemId,
            query.AnchorBefore,
            query.WithoutLocation,
            LocationSearch = string.IsNullOrWhiteSpace(query.Search) ? null : "%" + query.Search.Trim().Replace("~", "~~").Replace("%", "~%").Replace("_", "~_") + "%",
            PersonKey = query.PersonKey?.Trim().ToLowerInvariant(),
            query.From,
            query.To,
            query.TimelineEligibleOnly,
            Take = query.Limit + 1,
        });
        parameters.AddDynamicParams(smartRule.Parameters);
        for (var index = 0; index < libraryIds.Length; index++)
        {
            parameters.Add($"LibraryId{index}", GuidSql.ToBlob(libraryIds[index]), System.Data.DbType.Binary);
        }

        var effectiveAtSql = query.SortByAddedAt ? "li.created_at" : "COALESCE(li.captured_at, li.created_at)";
        var orderSql = query.SortByAddedAt ? "li.created_at DESC, lower(hex(li.id)) ASC" : "COALESCE(li.captured_at, li.created_at) DESC, li.id DESC";
        using var connection = database.CreateConnection();
        var rows = connection.Query<ItemRow>(new CommandDefinition($$"""
            SELECT li.id AS Id, li.scope_kind AS ScopeKind, li.library_id AS LibraryId,
                   li.personal_space_id AS PersonalSpaceId, li.owner_profile_id AS OwnerProfileId,
                   li.media_kind AS MediaKind, li.title AS Title,
                   li.primary_file_name AS FileName, li.primary_mime_type AS MimeType,
                   li.captured_at AS CapturedAt, li.embedded_captured_at AS EmbeddedCapturedAt,
                   li.captured_at_user_override AS CapturedAtUserOverride, li.created_at AS CreatedAt,
                   lm.width AS Width, lm.height AS Height,
                   lm.duration_seconds AS DurationSeconds, lm.page_count AS PageCount,
                   lm.device_make AS DeviceMake, lm.device_model AS DeviceModel,
                   lm.latitude AS Latitude, lm.longitude AS Longitude,
                   lm.location_name AS LocationName,
                   lm.location_city AS LocationCity, lm.location_region AS LocationRegion,
                   lm.location_country AS LocationCountry, lm.location_country_code AS LocationCountryCode,
                   lm.location_source AS LocationSource, lm.location_user_override AS LocationUserOverride,
                   lm.embedded_latitude AS EmbeddedLatitude, lm.embedded_longitude AS EmbeddedLongitude,
                   lm.description AS Description, lm.lens_model AS LensModel,
                   lm.exposure_time AS ExposureTime, lm.aperture AS Aperture, lm.iso AS Iso,
                   lm.focal_length_mm AS FocalLengthMm, lm.video_codec AS VideoCodec, lm.frame_rate AS FrameRate,
                   li.favorite AS Favorite, li.hidden AS Hidden,
                   li.archived_at AS ArchivedAt, li.trashed_at AS TrashedAt,
                   {{effectiveAtSql}} AS EffectiveAt,
                   (SELECT COUNT(DISTINCT lfs.id)
                      FROM local_item_files lif
                      JOIN local_file_sources lfs
                        ON lfs.file_id = lif.file_id AND lfs.library_id = li.library_id
                     WHERE lif.item_id = li.id) AS SourceCount,
                   0 AS TotalCount
              FROM local_items li
              LEFT JOIN local_item_metadata lm ON lm.item_id = li.id
              LEFT JOIN view_shared_assets vsa ON vsa.item_id = li.id
             WHERE ({{libraryPredicate}})
               AND ((@HiddenOnly = 1 AND li.hidden = 1)
                    OR (@HiddenOnly = 0 AND (@IncludeHidden = 1 OR li.hidden = 0)))
               AND (@FavoritesOnly = 0 OR li.favorite = 1)
               AND (@Lifecycle = 3
                    OR (@Lifecycle = 0 AND li.archived_at IS NULL AND li.trashed_at IS NULL)
                    OR (@Lifecycle = 1 AND li.archived_at IS NOT NULL AND li.trashed_at IS NULL)
                    OR (@Lifecycle = 2 AND li.trashed_at IS NOT NULL))
               AND (@HasMediaKinds = 0 OR li.media_kind IN @MediaKinds)
               AND (@GalleryId IS NULL OR EXISTS (
                    SELECT 1 FROM view_gallery_items vgi
                     WHERE vgi.gallery_id = @GalleryId AND vgi.item_id = li.id))
               AND (@TimelineEligibleOnly = 0 OR EXISTS (
                    SELECT 1
                      FROM local_item_files tlif
                      JOIN local_file_sources tlfs ON tlfs.file_id = tlif.file_id AND tlfs.library_id = li.library_id
                      LEFT JOIN view_sources tvs ON tvs.id = tlfs.source_id
                      LEFT JOIN view_source_policies tvsp ON tvsp.source_id = tvs.id
                     WHERE tlif.item_id = li.id
                       AND (vsa.item_id IS NOT NULL OR COALESCE((
                           SELECT vftp.include_in_timeline
                             FROM view_folder_timeline_policies vftp
                            WHERE vftp.source_id = tvs.id
                              AND (tlfs.file_path = vftp.absolute_path COLLATE NOCASE
                                   OR (substr(tlfs.file_path, 1, length(vftp.absolute_path)) = vftp.absolute_path COLLATE NOCASE
                                       AND substr(tlfs.file_path, length(vftp.absolute_path) + 1, 1) IN ('/', '\')))
                            ORDER BY length(vftp.absolute_path) DESC
                            LIMIT 1), tvsp.include_in_timeline,
                           CASE WHEN tvs.source_type = 'browser_upload' THEN 1 ELSE 0 END) = 1))
               )
               AND ({{smartRule.Predicate}})
               AND (@SearchExpression IS NULL OR EXISTS (
                    SELECT 1 FROM local_item_search lis
                      JOIN local_item_search_keys lsk ON lsk.rowid = lis.rowid
                     WHERE lsk.item_id = li.id AND local_item_search MATCH @SearchExpression))
               AND (@AddedBefore IS NULL OR li.created_at < @AddedBefore
                    OR (li.created_at = @AddedBefore AND ('view:' || lower(hex(li.id))) > @AddedAfterKey))
               AND (@BeforeEffectiveAt IS NULL
                    OR COALESCE(li.captured_at, li.created_at) < @BeforeEffectiveAt
                    OR (COALESCE(li.captured_at, li.created_at) = @BeforeEffectiveAt
                        AND li.id < @BeforeItemId))
               AND (@AnchorBefore IS NULL
                    OR COALESCE(li.captured_at, li.created_at) < @AnchorBefore)
               AND (@From IS NULL OR COALESCE(li.captured_at, li.created_at) >= @From)
               AND (@To IS NULL OR COALESCE(li.captured_at, li.created_at) <= @To)
               AND (@WithoutLocation = 0 OR @LocationSearch IS NULL OR lm.location_name LIKE @LocationSearch ESCAPE '~'
                    OR printf('%.3f,%.3f', ROUND(lm.latitude, 3), ROUND(lm.longitude, 3)) LIKE @LocationSearch ESCAPE '~')
               AND (@WithoutLocation = 0 OR lm.latitude IS NULL OR lm.longitude IS NULL
                    OR lm.latitude NOT BETWEEN -90 AND 90 OR lm.longitude NOT BETWEEN -180 AND 180)
               AND (@PersonKey IS NULL OR EXISTS (
                    SELECT 1 FROM local_item_annotations lia WHERE lia.item_id = li.id
                      AND LOWER(TRIM(lia.annotation_value)) = @PersonKey
                      AND (lia.annotation_kind IN ('person_name', 'named_person', 'face_name')
                           OR (lia.annotation_kind IN ('person_identity', 'face_identity') AND lia.reviewed_at IS NOT NULL))))
             ORDER BY {{orderSql}}
             LIMIT @Take;
            """, parameters, cancellationToken: ct)).ToList();
        var hasMore = rows.Count > query.Limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var items = MapItems(connection, rows);
        var last = rows.LastOrDefault();
        return new LocalAssetTimelinePage(
            items,
            hasMore && last is not null ? new LocalAssetTimelineCursor(last.EffectiveAt, last.Id) : null,
            hasMore);
    }

    private sealed class ItemPersonRow
    {
        public Guid ItemId { get; init; }
        public string Person { get; init; } = string.Empty;
    }

    private sealed class TimelineBucketRow
    {
        public int Year { get; init; }
        public int Month { get; init; }
        public long AssetCount { get; init; }
        public DateTimeOffset EarliestAt { get; init; }
        public DateTimeOffset LatestAt { get; init; }
    }

    public IReadOnlyList<LocalAssetTimelineBucket> QueryTimelineIndex(
        LocalAssetTimelineQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if ((query.AuthorizedLibraryIds is null || query.AuthorizedLibraryIds.Count == 0)
            && !query.IncludeSharedLibraryAssets)
        {
            throw new ArgumentException("At least one resolver-authorized library is required.", nameof(query));
        }
        if (query.AuthorizedLibraryIds?.Any(id => id == Guid.Empty) == true)
        {
            throw new ArgumentException("Authorized library IDs cannot be empty.", nameof(query));
        }

        ct.ThrowIfCancellationRequested();
        var libraryIds = query.AuthorizedLibraryIds?.Distinct().ToArray() ?? [];
        var mediaKinds = query.MediaKinds?
            .Where(kind => !string.IsNullOrWhiteSpace(kind))
            .Select(NormalizeMediaKind)
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
        var searchExpression = BuildSearchExpression(query.Search);
        var libraryPredicate = libraryIds.Length == 0
            ? "0 = 1"
            : string.Join(" OR ", libraryIds.Select((_, index) => $"li.library_id = @LibraryId{index}"));
        // The Shared scope reads only the libraries the resolver authorized (the caller's household's), never every
        // household's shared assets.

        var smartRule = query.SmartRule is null
            ? new LocalAssetSmartRuleSql("1 = 1", new DynamicParameters())
            : LocalAssetSmartRuleSqlCompiler.Compile(query.SmartRule);
        var parameters = new DynamicParameters(new
        {
            query.IncludeHidden,
            query.HiddenOnly,
            query.FavoritesOnly,
            Lifecycle = (int)query.Lifecycle,
            HasMediaKinds = mediaKinds.Length == 0 ? 0 : 1,
            MediaKinds = mediaKinds.Length == 0 ? [LocalAssetMediaKinds.Other] : mediaKinds,
            query.GalleryId,
            SearchExpression = searchExpression,
            query.TimelineEligibleOnly,
        });
        if (query.WithoutLocation)
        {
            parameters.Add("SearchExpression", null);
        }
        parameters.AddDynamicParams(smartRule.Parameters);
        for (var index = 0; index < libraryIds.Length; index++)
        {
            parameters.Add($"LibraryId{index}", GuidSql.ToBlob(libraryIds[index]), System.Data.DbType.Binary);
        }

        using var connection = database.CreateConnection();
        var rows = connection.Query<TimelineBucketRow>(new CommandDefinition($$"""
            SELECT CAST(strftime('%Y', COALESCE(li.captured_at, li.created_at)) AS INTEGER) AS Year,
                   CAST(strftime('%m', COALESCE(li.captured_at, li.created_at)) AS INTEGER) AS Month,
                   COUNT(DISTINCT li.id) AS AssetCount,
                   MIN(COALESCE(li.captured_at, li.created_at)) AS EarliestAt,
                   MAX(COALESCE(li.captured_at, li.created_at)) AS LatestAt
              FROM local_items li
              LEFT JOIN local_item_metadata lm ON lm.item_id = li.id
              LEFT JOIN view_shared_assets vsa ON vsa.item_id = li.id
             WHERE ({{libraryPredicate}})
               AND ((@HiddenOnly = 1 AND li.hidden = 1)
                    OR (@HiddenOnly = 0 AND (@IncludeHidden = 1 OR li.hidden = 0)))
               AND (@FavoritesOnly = 0 OR li.favorite = 1)
               AND (@Lifecycle = 3
                    OR (@Lifecycle = 0 AND li.archived_at IS NULL AND li.trashed_at IS NULL)
                    OR (@Lifecycle = 1 AND li.archived_at IS NOT NULL AND li.trashed_at IS NULL)
                    OR (@Lifecycle = 2 AND li.trashed_at IS NOT NULL))
               AND (@HasMediaKinds = 0 OR li.media_kind IN @MediaKinds)
               AND (@GalleryId IS NULL OR EXISTS (
                    SELECT 1 FROM view_gallery_items vgi
                     WHERE vgi.gallery_id = @GalleryId AND vgi.item_id = li.id))
               AND (@TimelineEligibleOnly = 0 OR EXISTS (
                    SELECT 1
                      FROM local_item_files tlif
                      JOIN local_file_sources tlfs ON tlfs.file_id = tlif.file_id AND tlfs.library_id = li.library_id
                      LEFT JOIN view_sources tvs ON tvs.id = tlfs.source_id
                      LEFT JOIN view_source_policies tvsp ON tvsp.source_id = tvs.id
                     WHERE tlif.item_id = li.id
                       AND (vsa.item_id IS NOT NULL OR COALESCE((
                           SELECT vftp.include_in_timeline
                             FROM view_folder_timeline_policies vftp
                            WHERE vftp.source_id = tvs.id
                              AND (tlfs.file_path = vftp.absolute_path COLLATE NOCASE
                                   OR (substr(tlfs.file_path, 1, length(vftp.absolute_path)) = vftp.absolute_path COLLATE NOCASE
                                       AND substr(tlfs.file_path, length(vftp.absolute_path) + 1, 1) IN ('/', '\')))
                            ORDER BY length(vftp.absolute_path) DESC
                            LIMIT 1), tvsp.include_in_timeline,
                           CASE WHEN tvs.source_type = 'browser_upload' THEN 1 ELSE 0 END) = 1))
               )
               AND ({{smartRule.Predicate}})
               AND (@SearchExpression IS NULL OR EXISTS (
                    SELECT 1 FROM local_item_search lis
                      JOIN local_item_search_keys lsk ON lsk.rowid = lis.rowid
                     WHERE lsk.item_id = li.id AND local_item_search MATCH @SearchExpression))
             GROUP BY Year, Month
             ORDER BY Year DESC, Month DESC;
            """, parameters, cancellationToken: ct)).ToList();
        return rows.Select(row => new LocalAssetTimelineBucket(
            row.Year,
            row.Month,
            checked((int)row.AssetCount),
            row.EarliestAt,
            row.LatestAt)).ToList();
    }

    public LocalAssetDto? Find(Guid itemId, CancellationToken ct = default)
    {
        if (itemId == Guid.Empty)
        {
            throw new ArgumentException("Item ID is required.", nameof(itemId));
        }

        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var row = connection.QuerySingleOrDefault<ItemRow>("""
            SELECT li.id AS Id, li.scope_kind AS ScopeKind, li.library_id AS LibraryId, li.media_kind AS MediaKind,
                   li.personal_space_id AS PersonalSpaceId, li.owner_profile_id AS OwnerProfileId,
                   li.title AS Title, li.primary_file_name AS FileName,
                   li.primary_mime_type AS MimeType, li.captured_at AS CapturedAt,
                   li.embedded_captured_at AS EmbeddedCapturedAt,
                   li.captured_at_user_override AS CapturedAtUserOverride,
                   li.created_at AS CreatedAt, lm.width AS Width, lm.height AS Height,
                   lm.duration_seconds AS DurationSeconds, lm.page_count AS PageCount,
                   lm.device_make AS DeviceMake, lm.device_model AS DeviceModel,
                   lm.latitude AS Latitude, lm.longitude AS Longitude,
                   lm.location_name AS LocationName,
                   lm.location_city AS LocationCity, lm.location_region AS LocationRegion,
                   lm.location_country AS LocationCountry, lm.location_country_code AS LocationCountryCode,
                   lm.location_source AS LocationSource, lm.location_user_override AS LocationUserOverride,
                   lm.embedded_latitude AS EmbeddedLatitude, lm.embedded_longitude AS EmbeddedLongitude,
                   lm.description AS Description, lm.lens_model AS LensModel,
                   lm.exposure_time AS ExposureTime, lm.aperture AS Aperture, lm.iso AS Iso,
                   lm.focal_length_mm AS FocalLengthMm, lm.video_codec AS VideoCodec, lm.frame_rate AS FrameRate,
                   li.favorite AS Favorite,
                   li.hidden AS Hidden,
                   li.archived_at AS ArchivedAt, li.trashed_at AS TrashedAt,
                   COALESCE(li.captured_at, li.created_at) AS EffectiveAt,
                   (SELECT COUNT(DISTINCT lfs.id)
                      FROM local_item_files lif
                      JOIN local_file_sources lfs
                        ON lfs.file_id = lif.file_id AND lfs.library_id = li.library_id
                     WHERE lif.item_id = li.id) AS SourceCount,
                   1 AS TotalCount
              FROM local_items li
              LEFT JOIN local_item_metadata lm ON lm.item_id = li.id
             WHERE li.id = @itemId;
            """, new { itemId });
        return row is null ? null : MapItem(connection, row);
    }

    public LocalAssetContentLocation? ResolveContent(
        Guid itemId,
        string role = LocalAssetFileRoles.Primary,
        CancellationToken ct = default)
    {
        if (itemId == Guid.Empty)
        {
            throw new ArgumentException("Item ID is required.", nameof(itemId));
        }

        role = NormalizeRole(role);
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var row = connection.QueryFirstOrDefault<ContentRow>("""
            SELECT li.id AS ItemId, lf.id AS FileId, li.library_id AS LibraryId,
                   li.owner_profile_id AS OwnerProfileId,
                   lfs.source_id AS SourceId, lfs.device_id AS DeviceId,
                   lfs.file_path AS FilePath, lf.mime_type AS MimeType,
                   lf.byte_size AS ByteSize, lf.content_hash AS ContentHash,
                   lif.role AS Role, lif.derivative_kind AS DerivativeKind
              FROM local_items li
              JOIN local_item_files lif ON lif.item_id = li.id
              JOIN local_files lf ON lf.id = lif.file_id
              JOIN local_file_sources lfs
                ON lfs.file_id = lf.id AND lfs.library_id = li.library_id
             WHERE li.id = @itemId AND lif.role = @role
             ORDER BY lfs.indexed_at DESC, lfs.id
             LIMIT 1;
            """, new { itemId, role });
        return row is null
            ? null
            : new LocalAssetContentLocation(
                row.ItemId,
                row.FileId,
                row.LibraryId,
                row.OwnerProfileId,
                row.SourceId,
                row.DeviceId,
                row.FilePath,
                row.MimeType,
                row.ByteSize,
                row.ContentHash,
                row.Role,
                row.DerivativeKind);
    }

    public Task<LocalAssetUpsertResult> UpsertAsync(
        LocalAssetRegistration registration,
        CancellationToken ct = default)
    {
        ValidateRegistration(registration);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            var normalizedKind = NormalizeMediaKind(registration.MediaKind);
            var scopeKind = NormalizeScopeKind(registration.ScopeKind);
            if (scopeKind == LocalAssetScopeKinds.Personal)
            {
                var spaceOwner = connection.QuerySingleOrDefault<(Guid OwnerProfileId, Guid LibraryId)>("""
                    SELECT owner_profile_id AS OwnerProfileId, library_id AS LibraryId
                      FROM view_personal_spaces WHERE id = @PersonalSpaceId;
                    """, registration, transaction);
                if (spaceOwner.OwnerProfileId == Guid.Empty)
                {
                    throw new InvalidOperationException($"Personal Space '{registration.PersonalSpaceId:D}' does not exist.");
                }

                if (spaceOwner.OwnerProfileId != registration.OwnerProfileId || spaceOwner.LibraryId != registration.LibraryId)
                {
                    throw new InvalidOperationException("Asset owner, Personal Space, and configured library must identify the same ownership boundary.");
                }
            }
            else if (connection.ExecuteScalar<long>("""
                    SELECT COUNT(*) FROM view_shared_library
                     WHERE library_id=@LibraryId;
                    """, registration, transaction) == 0)
            {
                throw new InvalidOperationException("A Shared item must use a household's Shared library.");
            }

            var primary = registration.Files.SingleOrDefault(file =>
                string.Equals(file.Role, LocalAssetFileRoles.Primary, StringComparison.OrdinalIgnoreCase));

            var itemId = registration.ExistingItemId;
            if (itemId.HasValue)
            {
                var actualOwner = connection.QuerySingleOrDefault<(Guid LibraryId, Guid? PersonalSpaceId, Guid? OwnerProfileId, string ScopeKind)>(
                    """
                    SELECT library_id AS LibraryId, personal_space_id AS PersonalSpaceId,
                           owner_profile_id AS OwnerProfileId, scope_kind AS ScopeKind
                      FROM local_items WHERE id = @itemId;
                    """,
                    new { itemId }, transaction);
                if (actualOwner.LibraryId == Guid.Empty)
                {
                    throw new InvalidOperationException($"Local item '{itemId:D}' does not exist.");
                }
                if (actualOwner.LibraryId != registration.LibraryId
                    || actualOwner.PersonalSpaceId != registration.PersonalSpaceId
                    || actualOwner.OwnerProfileId != registration.OwnerProfileId
                    || actualOwner.ScopeKind != scopeKind)
                {
                    throw new InvalidOperationException("A local item cannot be attached across ownership boundaries.");
                }
            }
            else if (primary is not null)
            {
                var primaryHash = NormalizeHash(primary.ContentHash);
                itemId = connection.QuerySingleOrDefault<Guid?>("""
                    SELECT li.id
                      FROM local_items li
                      JOIN local_item_files lif ON lif.item_id = li.id AND lif.role = 'primary'
                      JOIN local_files lf ON lf.id = lif.file_id
                     WHERE li.scope_kind=@scopeKind AND li.library_id=@LibraryId
                       AND ((@scopeKind='shared' AND li.personal_space_id IS NULL)
                         OR li.personal_space_id=@PersonalSpaceId)
                       AND lf.content_hash = @primaryHash COLLATE NOCASE
                     LIMIT 1;
                    """, new { registration.PersonalSpaceId, registration.LibraryId, scopeKind, primaryHash }, transaction);
            }

            var itemAdded = !itemId.HasValue;
            itemId ??= Guid.NewGuid();
            if (itemAdded)
            {
                connection.Execute("""
                    INSERT INTO local_items
                        (id, scope_kind, personal_space_id, owner_profile_id, library_id, media_kind, title, primary_file_name,
                         primary_mime_type, captured_at, embedded_captured_at, captured_at_user_override,
                         created_at, updated_at, favorite, hidden)
                    VALUES
                        (@itemId, @scopeKind, @PersonalSpaceId, @OwnerProfileId, @LibraryId, @MediaKind, @Title, @FileName,
                         @MimeType, @CapturedAt, @CapturedAt, 0, @now, @now, 0, 0);
                    """, new
                {
                    itemId,
                    scopeKind,
                    registration.PersonalSpaceId,
                    registration.OwnerProfileId,
                    registration.LibraryId,
                    MediaKind = normalizedKind,
                    registration.Title,
                    FileName = primary!.FileName.Trim(),
                    MimeType = primary.MimeType.Trim().ToLowerInvariant(),
                    registration.CapturedAt,
                    now,
                }, transaction);
            }
            else
            {
                connection.Execute("""
                    UPDATE local_items
                       SET title = COALESCE(@Title, title),
                           embedded_captured_at = COALESCE(@CapturedAt, embedded_captured_at),
                           captured_at = CASE WHEN captured_at_user_override = 1
                               THEN captured_at ELSE COALESCE(@CapturedAt, captured_at) END,
                           updated_at = @now
                     WHERE id = @itemId;
                    """, new { itemId, registration.Title, registration.CapturedAt, now }, transaction);
            }

            connection.Execute("""
                INSERT INTO local_item_metadata
                    (item_id, width, height, duration_seconds, page_count, device_make,
                     device_model, latitude, longitude, location_name, embedded_latitude,
                     embedded_longitude, location_source, lens_model, exposure_time, aperture,
                     iso, focal_length_mm, video_codec, frame_rate, document_text, metadata_json, updated_at)
                VALUES
                    (@itemId, @Width, @Height, @DurationSeconds, @PageCount, @DeviceMake,
                     @DeviceModel, @Latitude, @Longitude, @LocationName, @Latitude,
                     @Longitude, CASE WHEN @Latitude IS NOT NULL AND @Longitude IS NOT NULL THEN 'embedded' END,
                     @LensModel, @ExposureTime, @Aperture, @Iso, @FocalLengthMm, @VideoCodec,
                     @FrameRate, @DocumentText, @MetadataJson, @now)
                ON CONFLICT(item_id) DO UPDATE SET
                    width = COALESCE(excluded.width, local_item_metadata.width),
                    height = COALESCE(excluded.height, local_item_metadata.height),
                    duration_seconds = COALESCE(excluded.duration_seconds, local_item_metadata.duration_seconds),
                    page_count = COALESCE(excluded.page_count, local_item_metadata.page_count),
                    device_make = COALESCE(excluded.device_make, local_item_metadata.device_make),
                    device_model = COALESCE(excluded.device_model, local_item_metadata.device_model),
                    embedded_latitude = COALESCE(excluded.embedded_latitude, local_item_metadata.embedded_latitude),
                    embedded_longitude = COALESCE(excluded.embedded_longitude, local_item_metadata.embedded_longitude),
                    latitude = CASE WHEN local_item_metadata.location_user_override = 1
                        THEN local_item_metadata.latitude ELSE COALESCE(excluded.latitude, local_item_metadata.latitude) END,
                    longitude = CASE WHEN local_item_metadata.location_user_override = 1
                        THEN local_item_metadata.longitude ELSE COALESCE(excluded.longitude, local_item_metadata.longitude) END,
                    location_name = CASE WHEN local_item_metadata.location_user_override = 1
                        THEN local_item_metadata.location_name ELSE COALESCE(excluded.location_name, local_item_metadata.location_name) END,
                    location_source = CASE WHEN local_item_metadata.location_user_override = 1
                        THEN local_item_metadata.location_source ELSE COALESCE(excluded.location_source, local_item_metadata.location_source) END,
                    lens_model = COALESCE(excluded.lens_model, local_item_metadata.lens_model),
                    exposure_time = COALESCE(excluded.exposure_time, local_item_metadata.exposure_time),
                    aperture = COALESCE(excluded.aperture, local_item_metadata.aperture),
                    iso = COALESCE(excluded.iso, local_item_metadata.iso),
                    focal_length_mm = COALESCE(excluded.focal_length_mm, local_item_metadata.focal_length_mm),
                    video_codec = COALESCE(excluded.video_codec, local_item_metadata.video_codec),
                    frame_rate = COALESCE(excluded.frame_rate, local_item_metadata.frame_rate),
                    document_text = COALESCE(excluded.document_text, local_item_metadata.document_text),
                    metadata_json = COALESCE(excluded.metadata_json, local_item_metadata.metadata_json),
                    updated_at = excluded.updated_at;
                """, new
            {
                itemId,
                registration.Width,
                registration.Height,
                registration.DurationSeconds,
                registration.PageCount,
                registration.DeviceMake,
                registration.DeviceModel,
                registration.Latitude,
                registration.Longitude,
                registration.LocationName,
                registration.LensModel,
                registration.ExposureTime,
                registration.Aperture,
                registration.Iso,
                registration.FocalLengthMm,
                registration.VideoCodec,
                registration.FrameRate,
                registration.DocumentText,
                registration.MetadataJson,
                now,
            }, transaction);

            var filesAdded = 0;
            var sourcesAdded = 0;
            var position = 0;
            foreach (var file in registration.Files)
            {
                token.ThrowIfCancellationRequested();
                ValidateSourceIdentity(connection, transaction, registration, scopeKind, file);
                var hash = NormalizeHash(file.ContentHash);
                var role = NormalizeRole(file.Role);
                var fileId = connection.QuerySingleOrDefault<Guid?>(
                    "SELECT id FROM local_files WHERE content_hash = @hash COLLATE NOCASE;",
                    new { hash }, transaction);
                if (!fileId.HasValue)
                {
                    fileId = Guid.NewGuid();
                    filesAdded += connection.Execute("""
                        INSERT INTO local_files
                            (id, content_hash, byte_size, mime_type, extension, created_at)
                        VALUES (@fileId, @hash, @ByteSize, @MimeType, @Extension, @now);
                        """, new
                    {
                        fileId,
                        hash,
                        file.ByteSize,
                        MimeType = file.MimeType.Trim().ToLowerInvariant(),
                        Extension = Path.GetExtension(file.FileName).ToLowerInvariant(),
                        now,
                    }, transaction);
                }

                var sourceExists = connection.ExecuteScalar<long>("""
                    SELECT COUNT(*) FROM local_file_sources
                     WHERE library_id = @LibraryId AND file_path = @FilePath COLLATE NOCASE;
                    """, new { registration.LibraryId, file.FilePath }, transaction) != 0;
                connection.Execute("""
                    INSERT INTO local_file_sources
                        (id, file_id, library_id, source_id, device_id, file_path, modified_at, indexed_at)
                    VALUES (@observationId, @fileId, @LibraryId, @SourceId, @DeviceId, @FilePath, @ModifiedAt, @now)
                    ON CONFLICT(library_id, file_path COLLATE NOCASE) DO UPDATE SET
                        file_id = excluded.file_id,
                        source_id = excluded.source_id,
                        device_id = excluded.device_id,
                        modified_at = excluded.modified_at,
                        indexed_at = excluded.indexed_at;
                    """, new
                {
                    observationId = Guid.NewGuid(),
                    fileId,
                    registration.LibraryId,
                    file.SourceId,
                    file.DeviceId,
                    file.FilePath,
                    file.ModifiedAt,
                    now,
                }, transaction);
                if (!sourceExists)
                {
                    sourcesAdded++;
                }

                connection.Execute("""
                    INSERT INTO local_item_files
                        (item_id, file_id, role, derivative_kind, position, added_at)
                    VALUES (@itemId, @fileId, @role, @DerivativeKind, @position, @now)
                    ON CONFLICT(item_id, file_id, role) DO UPDATE SET
                        derivative_kind = excluded.derivative_kind,
                        position = excluded.position;
                    """, new
                {
                    itemId,
                    fileId,
                    role,
                    file.DerivativeKind,
                    position,
                    now,
                }, transaction);
                position++;
            }

            if (registration.Tags is not null)
            {
                ReplaceTags(connection, transaction, itemId.Value, registration.Tags, now);
            }
            RebuildSearchDocument(connection, transaction, itemId.Value);
            return new LocalAssetUpsertResult(itemId.Value, itemAdded, filesAdded, sourcesAdded);
        }, ct);
    }

    public Task<bool> SetFlagsAsync(
        Guid itemId,
        bool? favorite,
        bool? hidden,
        CancellationToken ct = default)
    {
        if (itemId == Guid.Empty)
        {
            throw new ArgumentException("Item ID is required.", nameof(itemId));
        }

        if (!favorite.HasValue && !hidden.HasValue)
        {
            throw new ArgumentException("At least one flag value is required.");
        }
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            return connection.Execute("""
                UPDATE local_items
                   SET favorite = COALESCE(@Favorite, favorite),
                       hidden = COALESCE(@Hidden, hidden),
                       updated_at = @now
                 WHERE id = @itemId;
                """, new
            {
                itemId,
                Favorite = favorite.HasValue ? (favorite.Value ? 1 : 0) : (int?)null,
                Hidden = hidden.HasValue ? (hidden.Value ? 1 : 0) : (int?)null,
                now = DateTimeOffset.UtcNow,
            }, transaction) > 0;
        }, ct);
    }

    public Task<bool> SetLifecycleStateAsync(
        Guid itemId,
        LocalAssetLifecycleState state,
        CancellationToken ct = default)
    {
        if (itemId == Guid.Empty)
        {
            throw new ArgumentException("Item ID is required.", nameof(itemId));
        }

        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            return connection.Execute("""
                UPDATE local_items SET
                    archived_at = CASE WHEN @state = 1 THEN @now ELSE NULL END,
                    trashed_at = CASE WHEN @state = 2 THEN @now ELSE NULL END,
                    updated_at = @now
                 WHERE id = @itemId;
                """, new { itemId, state = (int)state, now }, transaction) != 0;
        }, ct);
    }

    public Task ReplaceTagsAsync(
        Guid itemId,
        IReadOnlyCollection<string> tags,
        CancellationToken ct = default)
    {
        if (itemId == Guid.Empty)
        {
            throw new ArgumentException("Item ID is required.", nameof(itemId));
        }

        ArgumentNullException.ThrowIfNull(tags);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (connection.ExecuteScalar<long>(
                    "SELECT COUNT(*) FROM local_items WHERE id = @itemId;",
                    new { itemId }, transaction) == 0)
            {
                throw new InvalidOperationException($"Local item '{itemId:D}' does not exist.");
            }
            ReplaceTags(connection, transaction, itemId, tags, DateTimeOffset.UtcNow);
            RebuildSearchDocument(connection, transaction, itemId);
        }, ct);
    }

    public Task ReplacePeopleAsync(Guid itemId, IReadOnlyCollection<string> people, CancellationToken ct = default)
    {
        if (itemId == Guid.Empty)
        {
            throw new ArgumentException("Item ID is required.", nameof(itemId));
        }
        ArgumentNullException.ThrowIfNull(people);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (connection.ExecuteScalar<long>("SELECT COUNT(*) FROM local_items WHERE id=@itemId;", new { itemId }, transaction) == 0)
            {
                throw new InvalidOperationException($"Local item '{itemId:D}' does not exist.");
            }
            connection.Execute("""
                DELETE FROM local_item_annotations
                 WHERE item_id=@itemId AND annotation_kind='person_name' AND source='user_assignment';
                """, new { itemId }, transaction);
            var now = DateTimeOffset.UtcNow;
            foreach (var person in people.Where(value => !string.IsNullOrWhiteSpace(value))
                         .Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                connection.Execute("""
                    INSERT INTO local_item_annotations
                        (id, item_id, annotation_kind, annotation_value, source, created_at, reviewed_at)
                    VALUES (@id, @itemId, 'person_name', @person, 'user_assignment', @now, @now);
                    """, new { id = Guid.NewGuid(), itemId, person, now }, transaction);
            }
            RebuildSearchDocument(connection, transaction, itemId);
        }, ct);
    }

    public IReadOnlyList<string> GetTagSuggestions(
        Guid profileId,
        string? search,
        int limit,
        CancellationToken ct = default)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("Profile ID is required.", nameof(profileId));
        }
        var take = Math.Clamp(limit, 1, 100);
        var searchPattern = string.IsNullOrWhiteSpace(search) ? null : $"%{search.Trim()}%";
        using var connection = database.CreateConnection();
        return connection.Query<string>(new CommandDefinition("""
            WITH terms(value) AS (
                SELECT lit.tag
                  FROM local_item_tags lit
                  JOIN local_items li ON li.id=lit.item_id
                 WHERE li.owner_profile_id=@profileId
                UNION ALL
                SELECT json.value FROM profile_work_preferences pref, json_each(pref.local_tags_json) json
                 WHERE pref.profile_id=@profileId AND json_valid(pref.local_tags_json)
                UNION ALL
                SELECT json.value FROM profile_person_preferences pref, json_each(pref.local_tags_json) json
                 WHERE pref.profile_id=@profileId AND json_valid(pref.local_tags_json)
            )
            SELECT TRIM(value)
              FROM terms
             WHERE TRIM(value) <> '' AND (@searchPattern IS NULL OR value LIKE @searchPattern COLLATE NOCASE)
             GROUP BY LOWER(TRIM(value))
             ORDER BY COUNT(*) DESC, value COLLATE NOCASE
             LIMIT @take;
            """, new { profileId, searchPattern, take }, cancellationToken: ct)).ToList();
    }

    public Task<bool> UpdateDescriptionAsync(Guid itemId, string? description, CancellationToken ct = default)
    {
        if (itemId == Guid.Empty)
        {
            throw new ArgumentException("Item ID is required.", nameof(itemId));
        }
        var normalized = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (normalized?.Length > 4000)
        {
            throw new ArgumentException("Description cannot exceed 4000 characters.", nameof(description));
        }
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var changed = connection.Execute("""
                UPDATE local_item_metadata
                   SET description = @normalized, updated_at = @now
                 WHERE item_id = @itemId;
                """, new { itemId, normalized, now = DateTimeOffset.UtcNow }, transaction) > 0;
            if (changed)
            {
                RebuildSearchDocument(connection, transaction, itemId);
            }
            return changed;
        }, ct);
    }

    public Task<bool> UpdateCapturedAtAsync(
        Guid itemId,
        DateTimeOffset? capturedAt,
        bool resetToEmbedded,
        CancellationToken ct = default)
    {
        if (itemId == Guid.Empty)
        {
            throw new ArgumentException("Item ID is required.", nameof(itemId));
        }
        if (!resetToEmbedded && capturedAt is null)
        {
            throw new ArgumentException("A captured date is required unless the embedded date is being restored.", nameof(capturedAt));
        }
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var changed = connection.Execute("""
                UPDATE local_items SET
                    captured_at = CASE WHEN @ResetToEmbedded = 1 THEN embedded_captured_at ELSE @CapturedAt END,
                    captured_at_user_override = CASE WHEN @ResetToEmbedded = 1 THEN 0 ELSE 1 END,
                    updated_at = @now
                 WHERE id = @ItemId;
                """, new
            {
                ItemId = itemId,
                CapturedAt = capturedAt,
                ResetToEmbedded = resetToEmbedded ? 1 : 0,
                now = DateTimeOffset.UtcNow,
            }, transaction) > 0;
            if (changed)
            {
                RebuildSearchDocument(connection, transaction, itemId);
            }
            return changed;
        }, ct);
    }

    public Task<bool> UpdateLocationAsync(Guid itemId, LocalAssetLocationUpdate update, CancellationToken ct = default)
    {
        if (itemId == Guid.Empty)
        {
            throw new ArgumentException("Item ID is required.", nameof(itemId));
        }
        ArgumentNullException.ThrowIfNull(update);
        if (!update.ResetToEmbedded && (update.Latitude.HasValue != update.Longitude.HasValue))
        {
            throw new ArgumentException("Latitude and longitude must be supplied together.", nameof(update));
        }
        if (update.Latitude is < -90 or > 90)
        {
            throw new ArgumentOutOfRangeException(nameof(update.Latitude));
        }
        if (update.Longitude is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(update.Longitude));
        }
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var changed = connection.Execute("""
                UPDATE local_item_metadata SET
                    latitude = CASE WHEN @ResetToEmbedded = 1 THEN embedded_latitude ELSE @Latitude END,
                    longitude = CASE WHEN @ResetToEmbedded = 1 THEN embedded_longitude ELSE @Longitude END,
                    location_name = CASE WHEN @ResetToEmbedded = 1 THEN NULL ELSE @Name END,
                    location_city = CASE WHEN @ResetToEmbedded = 1 THEN NULL ELSE @City END,
                    location_region = CASE WHEN @ResetToEmbedded = 1 THEN NULL ELSE @Region END,
                    location_country = CASE WHEN @ResetToEmbedded = 1 THEN NULL ELSE @Country END,
                    location_country_code = CASE WHEN @ResetToEmbedded = 1 THEN NULL ELSE @CountryCode END,
                    location_source = CASE WHEN @ResetToEmbedded = 1
                        THEN CASE WHEN embedded_latitude IS NOT NULL AND embedded_longitude IS NOT NULL THEN 'embedded' END
                        ELSE 'user' END,
                    location_user_override = CASE WHEN @ResetToEmbedded = 1 THEN 0 ELSE 1 END,
                    updated_at = @now
                 WHERE item_id = @ItemId;
                """, new
            {
                ItemId = itemId,
                update.Latitude,
                update.Longitude,
                Name = Clean(update.Name),
                City = Clean(update.City),
                Region = Clean(update.Region),
                Country = Clean(update.Country),
                CountryCode = Clean(update.CountryCode)?.ToUpperInvariant(),
                ResetToEmbedded = update.ResetToEmbedded ? 1 : 0,
                now = DateTimeOffset.UtcNow,
            }, transaction) > 0;
            if (changed)
            {
                RebuildSearchDocument(connection, transaction, itemId);
            }
            return changed;
        }, ct);
    }

    public Task<Guid> AddAnnotationAsync(
        Guid itemId,
        LocalAssetAnnotation annotation,
        CancellationToken ct = default)
    {
        if (itemId == Guid.Empty)
        {
            throw new ArgumentException("Item ID is required.", nameof(itemId));
        }

        ValidateAnnotation(annotation);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (connection.ExecuteScalar<long>(
                    "SELECT COUNT(*) FROM local_items WHERE id = @itemId;",
                    new { itemId }, transaction) == 0)
            {
                throw new InvalidOperationException($"Local item '{itemId:D}' does not exist.");
            }
            var id = Guid.NewGuid();
            connection.Execute("""
                INSERT INTO local_item_annotations
                    (id, item_id, annotation_kind, annotation_value, confidence, source,
                     model_name, model_version, provenance_json, created_at, reviewed_at)
                VALUES
                    (@id, @itemId, @Kind, @Value, @Confidence, @Source,
                     @ModelName, @ModelVersion, @ProvenanceJson, @now, @ReviewedAt);
                """, new
            {
                id,
                itemId,
                Kind = annotation.Kind.Trim(),
                Value = annotation.Value.Trim(),
                Source = annotation.Source.Trim(),
                annotation.Confidence,
                annotation.ModelName,
                annotation.ModelVersion,
                annotation.ProvenanceJson,
                now = DateTimeOffset.UtcNow,
                annotation.ReviewedAt,
            }, transaction);
            return id;
        }, ct);
    }

    private static LocalAssetDto MapItem(SqliteConnection connection, ItemRow row)
    {
        var files = connection.Query<FileRow>("""
            SELECT lf.id AS Id, lif.role AS Role, lif.derivative_kind AS DerivativeKind,
                   lf.mime_type AS MimeType, lf.byte_size AS ByteSize,
                   COUNT(lfs.id) AS SourceCount, MIN(lfs.file_path) AS FileName
              FROM local_item_files lif
              JOIN local_files lf ON lf.id = lif.file_id
              LEFT JOIN local_file_sources lfs
                ON lfs.file_id = lf.id AND lfs.library_id = @libraryId
             WHERE lif.item_id = @itemId
             GROUP BY lf.id, lif.role
             ORDER BY lif.position, lif.role;
            """, new { itemId = row.Id, libraryId = row.LibraryId })
            .Select(file => new LocalAssetFileDto(
                file.Id,
                file.Role,
                file.DerivativeKind,
                file.MimeType,
                file.ByteSize,
                checked((int)file.SourceCount),
                string.IsNullOrWhiteSpace(file.FileName) ? null : Path.GetFileName(file.FileName)))
            .ToList();
        var tags = connection.Query<string>("""
            SELECT tag FROM local_item_tags
             WHERE item_id = @itemId
             ORDER BY tag COLLATE NOCASE;
            """, new { itemId = row.Id }).ToList();
        var people = connection.Query<string>("""
            SELECT DISTINCT TRIM(annotation_value)
              FROM local_item_annotations
             WHERE item_id=@itemId AND TRIM(annotation_value) <> ''
               AND (annotation_kind IN ('person_name','named_person','face_name')
                    OR (annotation_kind IN ('person_identity','face_identity') AND reviewed_at IS NOT NULL))
             ORDER BY annotation_value COLLATE NOCASE;
            """, new { itemId = row.Id }).ToList();
        return MapItem(row, files, tags, people);
    }

    private static List<LocalAssetDto> MapItems(SqliteConnection connection, IReadOnlyList<ItemRow> rows)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var parameters = new DynamicParameters();
        var itemPredicate = string.Join(" OR ", rows.Select((row, index) =>
        {
            parameters.Add($"ItemId{index}", GuidSql.ToBlob(row.Id), System.Data.DbType.Binary);
            return $"lif.item_id = @ItemId{index}";
        }));
        var files = connection.Query<ItemFileRow>($$"""
            SELECT lif.item_id AS ItemId, lf.id AS Id, lif.role AS Role,
                   lif.derivative_kind AS DerivativeKind, lf.mime_type AS MimeType,
                   lf.byte_size AS ByteSize, COUNT(lfs.id) AS SourceCount,
                   MIN(lfs.file_path) AS FileName
              FROM local_item_files lif
              JOIN local_items li ON li.id = lif.item_id
              JOIN local_files lf ON lf.id = lif.file_id
              LEFT JOIN local_file_sources lfs
                ON lfs.file_id = lf.id AND lfs.library_id = li.library_id
             WHERE ({{itemPredicate}})
             GROUP BY lif.item_id, lf.id, lif.role
             ORDER BY lif.item_id, lif.position, lif.role;
            """, parameters).ToLookup(file => file.ItemId);
        var tagPredicate = itemPredicate.Replace("lif.item_id", "lit.item_id", StringComparison.Ordinal);
        var tags = connection.Query<ItemTagRow>($$"""
            SELECT lit.item_id AS ItemId, lit.tag AS Tag
              FROM local_item_tags lit
             WHERE ({{tagPredicate}})
             ORDER BY lit.item_id, lit.tag COLLATE NOCASE;
            """, parameters).ToLookup(tag => tag.ItemId);
        var personPredicate = itemPredicate.Replace("lif.item_id", "lia.item_id", StringComparison.Ordinal);
        var people = connection.Query<ItemPersonRow>($$"""
            SELECT lia.item_id AS ItemId, TRIM(lia.annotation_value) AS Person
              FROM local_item_annotations lia
             WHERE ({{personPredicate}}) AND TRIM(lia.annotation_value) <> ''
               AND (lia.annotation_kind IN ('person_name','named_person','face_name')
                    OR (lia.annotation_kind IN ('person_identity','face_identity') AND lia.reviewed_at IS NOT NULL))
             GROUP BY lia.item_id, LOWER(TRIM(lia.annotation_value))
             ORDER BY lia.item_id, lia.annotation_value COLLATE NOCASE;
            """, parameters).ToLookup(person => person.ItemId);

        return rows.Select(row => MapItem(
            row,
            files[row.Id].Select(file => new LocalAssetFileDto(
                file.Id, file.Role, file.DerivativeKind, file.MimeType,
                file.ByteSize, checked((int)file.SourceCount),
                string.IsNullOrWhiteSpace(file.FileName) ? null : Path.GetFileName(file.FileName))).ToList(),
            tags[row.Id].Select(tag => tag.Tag).ToList(),
            people[row.Id].Select(person => person.Person).ToList())).ToList();
    }

    private static LocalAssetDto MapItem(
        ItemRow row,
        IReadOnlyList<LocalAssetFileDto> files,
        IReadOnlyList<string> tags,
        IReadOnlyList<string>? people = null) => new(
            row.Id,
            row.LibraryId,
            row.PersonalSpaceId,
            row.OwnerProfileId,
            row.MediaKind,
            row.Title,
            row.FileName,
            row.MimeType,
            row.CapturedAt,
            row.CreatedAt,
            row.Width,
            row.Height,
            row.DurationSeconds,
            row.PageCount,
            row.DeviceMake,
            row.DeviceModel,
            row.Latitude,
            row.Longitude,
            row.LocationName,
            row.Favorite != 0,
            row.Hidden != 0,
            row.ArchivedAt,
            row.TrashedAt,
            checked((int)row.SourceCount),
            files,
            tags,
            $"/view/items/{row.Id:D}/thumbnail",
            $"/view/items/{row.Id:D}/content",
            row.ScopeKind,
            row.Description,
            row.LensModel,
            row.ExposureTime,
            row.Aperture,
            row.Iso,
            row.FocalLengthMm,
            row.VideoCodec,
            row.FrameRate,
            row.LocationCity,
            row.LocationRegion,
            row.LocationCountry,
            row.LocationCountryCode,
            row.LocationSource,
            row.LocationUserOverride != 0,
            row.EmbeddedLatitude,
            row.EmbeddedLongitude,
            row.EmbeddedCapturedAt,
            row.CapturedAtUserOverride != 0,
            people ?? []);

    private static void ReplaceTags(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid itemId,
        IReadOnlyCollection<string> tags,
        DateTimeOffset now)
    {
        connection.Execute(
            "DELETE FROM local_item_tags WHERE item_id = @itemId;",
            new { itemId }, transaction);
        foreach (var tag in tags
                     .Where(tag => !string.IsNullOrWhiteSpace(tag))
                     .Select(tag => tag.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            connection.Execute("""
                INSERT INTO local_item_tags (item_id, tag, added_at)
                VALUES (@itemId, @tag, @now);
                """, new { itemId, tag, now }, transaction);
        }
    }

    private static void RebuildSearchDocument(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid itemId)
    {
        var row = connection.QuerySingle<SearchDocumentRow>("""
            SELECT li.title AS Title, li.primary_file_name AS FileName,
                   li.primary_mime_type AS MimeType, li.media_kind AS MediaKind,
                   li.captured_at AS CapturedAt,
                   trim(COALESCE(lm.device_make, '') || ' ' || COALESCE(lm.device_model, '')) AS Device,
                   trim(COALESCE(lm.location_name, '') || ' ' || COALESCE(lm.location_city, '') || ' '
                        || COALESCE(lm.location_region, '') || ' ' || COALESCE(lm.location_country, '') || ' '
                        || COALESCE(lm.location_country_code, '')) AS Location,
                   trim(COALESCE(lm.description, '') || ' ' || COALESCE(lm.document_text, '')) AS DocumentText,
                   (SELECT group_concat(tag, ' ') FROM local_item_tags WHERE item_id = li.id) AS Tags,
                   (SELECT group_concat(annotation_value, ' ') FROM local_item_annotations
                     WHERE item_id=li.id AND TRIM(annotation_value) <> ''
                       AND (annotation_kind IN ('person_name','named_person','face_name')
                            OR (annotation_kind IN ('person_identity','face_identity') AND reviewed_at IS NOT NULL))) AS People
              FROM local_items li
              LEFT JOIN local_item_metadata lm ON lm.item_id = li.id
             WHERE li.id = @itemId;
            """, new { itemId }, transaction);
        connection.Execute("""
            INSERT INTO local_item_search_keys (item_id)
            VALUES (@itemId)
            ON CONFLICT(item_id) DO NOTHING;
            """, new { itemId }, transaction);
        var searchRowId = connection.QuerySingle<long>(
            "SELECT rowid FROM local_item_search_keys WHERE item_id = @itemId;",
            new { itemId }, transaction);
        connection.Execute(
            "DELETE FROM local_item_search WHERE rowid = @searchRowId;",
            new { searchRowId }, transaction);
        connection.Execute("""
            INSERT INTO local_item_search
                (rowid, title, file_name, mime_type, media_kind, captured_at,
                 device, location, document_text, tags)
            VALUES
                (@searchRowId, @Title, @FileName, @MimeType, @MediaKind, @CapturedAt,
                 @Device, @Location, @DocumentText, @Tags);
            """, new
        {
            searchRowId,
            row.Title,
            row.FileName,
            row.MimeType,
            row.MediaKind,
            row.CapturedAt,
            row.Device,
            row.Location,
            row.DocumentText,
            Tags = string.Join(' ', new[] { row.Tags, row.People }.Where(value => !string.IsNullOrWhiteSpace(value))),
        }, transaction);
    }

    private static void ValidateQuery(LocalAssetQuery query)
    {
        if (query.LibraryId == Guid.Empty)
        {
            throw new ArgumentException("Library ID is required.", nameof(query));
        }

        if (query.Offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "Offset cannot be negative.");
        }

        if (query.Limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "Limit must be between 1 and 500.");
        }

        if (query.HiddenOnly && !query.IncludeHidden)
        {
            throw new ArgumentException("Hidden-only queries must include hidden items.", nameof(query));
        }
        if (!Enum.IsDefined(query.Lifecycle))
        {
            throw new ArgumentOutOfRangeException(nameof(query), "Unsupported lifecycle filter.");
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void ValidateRegistration(LocalAssetRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (registration.LibraryId == Guid.Empty)
        {
            throw new ArgumentException("Library ID is required.", nameof(registration));
        }

        var scopeKind = NormalizeScopeKind(registration.ScopeKind);
        if (scopeKind == LocalAssetScopeKinds.Personal
            && (registration.PersonalSpaceId is not { } spaceId || spaceId == Guid.Empty
                || registration.OwnerProfileId is not { } ownerId || ownerId == Guid.Empty))
        {
            throw new ArgumentException("Personal items require non-empty Personal Space and owner profile IDs.", nameof(registration));
        }

        if (scopeKind == LocalAssetScopeKinds.Shared
            && (registration.PersonalSpaceId is not null || registration.OwnerProfileId is not null))
        {
            throw new ArgumentException("Shared items cannot carry personal ownership IDs.", nameof(registration));
        }

        NormalizeMediaKind(registration.MediaKind);
        if (registration.Files is null || registration.Files.Count == 0)
        {
            throw new ArgumentException("At least one physical file is required.", nameof(registration));
        }
        var primaryCount = registration.Files.Count(file =>
            string.Equals(file.Role, LocalAssetFileRoles.Primary, StringComparison.OrdinalIgnoreCase));
        if (primaryCount > 1)
        {
            throw new ArgumentException("A logical item can have only one primary file.", nameof(registration));
        }
        if (!registration.ExistingItemId.HasValue && primaryCount != 1)
        {
            throw new ArgumentException("A new logical item requires exactly one primary file.", nameof(registration));
        }
        if (registration.ExistingItemId.HasValue && registration.ExistingItemId.Value == Guid.Empty)
        {
            throw new ArgumentException("Existing item ID cannot be empty.", nameof(registration));
        }
        if (registration.Width < 0 || registration.Height < 0 || registration.DurationSeconds < 0 || registration.PageCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(registration), "Technical measurements cannot be negative.");
        }
        if (registration.Latitude is < -90 or > 90 || registration.Longitude is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(registration), "GPS coordinates are outside their valid range.");
        }
        ValidateJson(registration.MetadataJson, nameof(registration.MetadataJson));
        foreach (var file in registration.Files)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(file.FilePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(file.FileName);
            ArgumentException.ThrowIfNullOrWhiteSpace(file.MimeType);
            if (file.ByteSize < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(registration), "File size cannot be negative.");
            }

            NormalizeHash(file.ContentHash);
            NormalizeRole(file.Role);
            if (file.SourceId == Guid.Empty || file.DeviceId == Guid.Empty)
            {
                throw new ArgumentException("Source and device IDs cannot be empty.", nameof(registration));
            }

            if (string.Equals(file.Role, LocalAssetFileRoles.Derivative, StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(file.DerivativeKind))
            {
                throw new ArgumentException("Derivative files require a derivative kind.", nameof(registration));
            }
        }
    }

    private static void ValidateAnnotation(LocalAssetAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        ArgumentException.ThrowIfNullOrWhiteSpace(annotation.Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(annotation.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(annotation.Source);
        if (annotation.Confidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(annotation), "Confidence must be between zero and one.");
        }
        ValidateJson(annotation.ProvenanceJson, nameof(annotation.ProvenanceJson));
    }

    private static void ValidateJson(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        try
        {
            using var _ = JsonDocument.Parse(value);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Value must contain valid JSON.", parameterName, exception);
        }
    }

    private static string NormalizeHash(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        var normalized = hash.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Content hash must be a 64-character SHA-256 hexadecimal value.", nameof(hash));
        }
        return normalized;
    }

    private static string NormalizeMediaKind(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var normalized = kind.Trim().ToLowerInvariant();
        if (!LocalAssetMediaKinds.All.Contains(normalized))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), $"Unsupported local media kind '{kind}'.");
        }
        return normalized;
    }

    private static string NormalizeRole(string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        var normalized = role.Trim().ToLowerInvariant();
        if (!LocalAssetFileRoles.All.Contains(normalized))
        {
            throw new ArgumentOutOfRangeException(nameof(role), $"Unsupported local file role '{role}'.");
        }
        return normalized;
    }

    private static string? BuildSearchExpression(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return null;
        }

        var terms = search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(term => term.Replace("\"", "\"\"", StringComparison.Ordinal))
            .Where(term => term.Length != 0)
            .Take(20)
            .Select(term => $"\"{term}\"*")
            .ToArray();
        return terms.Length == 0 ? null : string.Join(" AND ", terms);
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeScopeKind(string value) => value switch
    {
        LocalAssetScopeKinds.Personal => LocalAssetScopeKinds.Personal,
        LocalAssetScopeKinds.Shared => LocalAssetScopeKinds.Shared,
        _ => throw new ArgumentException("Local item scope must be personal or shared.", nameof(value)),
    };

    private static void ValidateSourceIdentity(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction,
        LocalAssetRegistration registration,
        string scopeKind,
        LocalAssetFileRegistration file)
    {
        if (file.SourceId.HasValue && connection.ExecuteScalar<long>("""
                SELECT COUNT(1) FROM view_sources
                 WHERE id=@SourceId AND scope_kind=@scopeKind AND library_id=@LibraryId
                   AND ((@scopeKind='shared' AND personal_space_id IS NULL)
                     OR personal_space_id=@PersonalSpaceId);
                """, new { file.SourceId, registration.PersonalSpaceId, registration.LibraryId, scopeKind }, transaction) == 0)
        {
            throw new InvalidOperationException("A file source must belong to the item's exact View scope.");
        }

        if (scopeKind == LocalAssetScopeKinds.Shared && file.SourceId is null)
        {
            throw new InvalidOperationException("Shared files require a verified server-owned View source.");
        }

        if (scopeKind == LocalAssetScopeKinds.Shared && file.DeviceId is not null)
        {
            throw new InvalidOperationException("Shared file observations cannot belong to a personal device.");
        }

        if (file.DeviceId.HasValue && connection.ExecuteScalar<long>("""
                SELECT COUNT(1) FROM view_devices
                 WHERE id = @DeviceId AND personal_space_id = @PersonalSpaceId;
                """, new { file.DeviceId, registration.PersonalSpaceId }, transaction) == 0)
        {
            throw new InvalidOperationException("A file device must belong to the asset's Personal Space.");
        }
    }
}
