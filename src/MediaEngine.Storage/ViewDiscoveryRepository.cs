using System.Data;
using Dapper;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// Reads only resolver-authorized, active personal media. Places come from actual
/// local metadata. People come only from named annotations or reviewed identity
/// annotations and retain their provenance in the projection.
/// </summary>
public sealed class ViewDiscoveryRepository(IDatabaseConnection database) : IViewDiscoveryRepository
{
    private const int MaximumLimit = 100;

    private class PlaceRow
    {
        public string Key { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public double Latitude { get; init; }
        public double Longitude { get; init; }
        public long AssetCount { get; init; }
        public Guid RepresentativeLibraryId { get; init; }
        public Guid RepresentativeAssetId { get; init; }
    }

    private sealed class AtlasRow : PlaceRow
    {
        public long ImageCount { get; init; }
        public long VideoCount { get; init; }
        public DateTimeOffset EarliestAt { get; init; }
        public DateTimeOffset LatestAt { get; init; }
    }

    private sealed class PlaceAssetRow
    {
        public Guid AssetId { get; init; }
        public string PlaceName { get; init; } = string.Empty;
        public long TotalCount { get; init; }
    }

    private sealed class AtlasTotalsRow
    {
        public long Mapped { get; init; }
        public long Unmapped { get; init; }
        public int ImageCount { get; init; }
        public int VideoCount { get; init; }
    }

    private sealed class AtlasTimelineRow
    {
        public DateTimeOffset Start { get; init; }
        public DateTimeOffset End { get; init; }
        public long AssetCount { get; init; }
        public long ImageCount { get; init; }
        public long VideoCount { get; init; }
    }

    private sealed class AtlasExtentRow
    {
        public DateTimeOffset? EarliestAt { get; init; }
        public DateTimeOffset? LatestAt { get; init; }
    }

    private sealed class PersonRow
    {
        public string Key { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public long AssetCount { get; init; }
        public Guid RepresentativeLibraryId { get; init; }
        public Guid RepresentativeAssetId { get; init; }
        public string? AnnotationKinds { get; init; }
        public string? ProvenanceSources { get; init; }
        public long HasReviewedEvidence { get; init; }
    }

    public ViewPlaceDiscoveryPage QueryPlaces(
        ViewPlaceDiscoveryQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var libraries = Validate(query.AuthorizedLibraryIds, query.Limit, query.Cursor, query.Search);
        if (libraries.Length == 0 && !query.IncludeSharedLibraryAssets)
        {
            return new ViewPlaceDiscoveryPage([], null, false, false);
        }

        ct.ThrowIfCancellationRequested();
        var parameters = Parameters(libraries, query.Limit, query.Search, query.Cursor);
        var libraryPredicate = query.IncludeSharedLibraryAssets
            ? "EXISTS (SELECT 1 FROM view_shared_assets vsa WHERE vsa.item_id = li.id)"
            : LibraryPredicate("li", libraries.Length);
        using var connection = database.CreateConnection();
        var hasEligibleData = connection.QuerySingle<bool>(new CommandDefinition($$"""
            SELECT EXISTS (
                SELECT 1
                  FROM local_items li
                  JOIN local_item_metadata lm ON lm.item_id = li.id
                 WHERE ({{libraryPredicate}})
                   AND li.hidden = 0
                   AND li.archived_at IS NULL
                   AND li.trashed_at IS NULL
                   AND lm.latitude IS NOT NULL
                   AND lm.longitude IS NOT NULL);
            """, parameters, cancellationToken: ct));

        var rows = connection.Query<PlaceRow>(new CommandDefinition($$"""
            WITH eligible AS (
                SELECT li.id AS ItemId, li.library_id AS LibraryId,
                       COALESCE(li.captured_at, li.created_at) AS EffectiveAt,
                       LOWER(COALESCE(
                           NULLIF(TRIM(lm.location_name), ''),
                           'location')) || '@' ||
                           printf('%.1f,%.1f', ROUND(lm.latitude, 1), ROUND(lm.longitude, 1)) AS PlaceKey,
                       COALESCE(
                           NULLIF(TRIM(lm.location_name), ''),
                           printf('%.3f, %.3f', ROUND(lm.latitude, 3), ROUND(lm.longitude, 3))) AS PlaceName,
                       ROUND(lm.latitude, 3) AS Latitude,
                       ROUND(lm.longitude, 3) AS Longitude
                  FROM local_items li
                  JOIN local_item_metadata lm ON lm.item_id = li.id
                 WHERE ({{libraryPredicate}})
                   AND li.hidden = 0
                   AND li.archived_at IS NULL
                   AND li.trashed_at IS NULL
                   AND lm.latitude IS NOT NULL
                   AND lm.longitude IS NOT NULL
                   AND (@SearchPattern IS NULL
                        OR lm.location_name LIKE @SearchPattern ESCAPE '\'
                        OR printf('%.3f,%.3f', ROUND(lm.latitude, 3), ROUND(lm.longitude, 3))
                           LIKE @SearchPattern ESCAPE '\')
            ), ranked AS (
                SELECT *, ROW_NUMBER() OVER (
                    PARTITION BY PlaceKey
                    ORDER BY EffectiveAt DESC, ItemId DESC) AS RepresentativeRank
                  FROM eligible
            ), grouped AS (
                SELECT PlaceKey AS Key,
                       MAX(PlaceName) AS Name,
                       ROUND(AVG(Latitude), 3) AS Latitude,
                       ROUND(AVG(Longitude), 3) AS Longitude,
                       COUNT(DISTINCT ItemId) AS AssetCount,
                       MAX(CASE WHEN RepresentativeRank = 1 THEN LibraryId END) AS RepresentativeLibraryId,
                       MAX(CASE WHEN RepresentativeRank = 1 THEN ItemId END) AS RepresentativeAssetId
                  FROM ranked
                 GROUP BY PlaceKey
            )
            SELECT Key, Name, Latitude, Longitude, AssetCount, RepresentativeLibraryId, RepresentativeAssetId
              FROM grouped
             WHERE (@CursorCount IS NULL
                    OR AssetCount < @CursorCount
                    OR (AssetCount = @CursorCount AND Key > @CursorKey))
             ORDER BY AssetCount DESC, Key
             LIMIT @Take;
            """, parameters, cancellationToken: ct)).ToList();

        var hasMore = rows.Count > query.Limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var items = rows.Select(row => new ViewPlaceDiscoveryRow(
            row.Key,
            row.Name,
            row.Latitude,
            row.Longitude,
            checked((int)row.AssetCount),
            row.RepresentativeLibraryId,
            row.RepresentativeAssetId)).ToList();
        var last = items.LastOrDefault();
        return new ViewPlaceDiscoveryPage(
            items,
            hasMore && last is not null ? new ViewDiscoveryCursor(last.AssetCount, last.Key) : null,
            hasMore,
            hasEligibleData);
    }

    public ViewAtlasDiscoveryPage QueryAtlas(
        ViewAtlasDiscoveryQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var libraries = ValidateAtlas(query.AuthorizedLibraryIds, query.Limit, query.Search, query.Year,
            query.MediaKind, query.From, query.To, query.TimelineResolution);
        if (libraries.Length == 0 && !query.IncludeSharedLibraryAssets)
        {
            return new ViewAtlasDiscoveryPage([], [], 0, 0, false);
        }

        ct.ThrowIfCancellationRequested();
        var parameters = new DynamicParameters(new
        {
            Take = query.Limit,
            SearchPattern = SearchPattern(query.Search),
            Year = query.Year,
            MediaKind = string.IsNullOrWhiteSpace(query.MediaKind) ? null : query.MediaKind.Trim().ToLowerInvariant(),
            query.From,
            query.To,
            Resolution = query.TimelineResolution.Trim().ToLowerInvariant(),
            FavoritesOnly = query.FavoritesOnly ? 1 : 0,
        });
        AddLibraries(parameters, libraries);
        var libraryPredicate = query.IncludeSharedLibraryAssets
            ? "EXISTS (SELECT 1 FROM view_shared_assets vsa WHERE vsa.item_id = li.id)"
            : LibraryPredicate("li", libraries.Length);
        var facetPredicate = $$"""
            ({{libraryPredicate}})
            AND LOWER(li.media_kind) IN ('image', 'video')
            AND (@SearchPattern IS NULL OR lm.location_name LIKE @SearchPattern ESCAPE '\'
                 OR printf('%.3f,%.3f', ROUND(lm.latitude, 3), ROUND(lm.longitude, 3)) LIKE @SearchPattern ESCAPE '\')
            AND li.hidden = 0
            AND li.archived_at IS NULL
            AND li.trashed_at IS NULL
            AND (@MediaKind IS NULL OR LOWER(li.media_kind) = @MediaKind)
            AND (@FavoritesOnly = 0 OR li.favorite = 1)
            """;
        var commonPredicate = $$"""
            {{facetPredicate}}
            AND (@Year IS NULL OR CAST(strftime('%Y', COALESCE(li.captured_at, li.created_at)) AS INTEGER) = @Year)
            AND (@From IS NULL OR COALESCE(li.captured_at, li.created_at) >= @From)
            AND (@To IS NULL OR COALESCE(li.captured_at, li.created_at) <= @To)
            """;

        using var connection = database.CreateConnection();
        var rows = connection.Query<AtlasRow>(new CommandDefinition($$"""
            WITH eligible AS (
                SELECT li.id AS ItemId, li.library_id AS LibraryId, li.media_kind AS MediaKind,
                       COALESCE(li.captured_at, li.created_at) AS EffectiveAt,
                       LOWER(COALESCE(NULLIF(TRIM(lm.location_name), ''), 'location')) || '@' ||
                           printf('%.1f,%.1f', ROUND(lm.latitude, 1), ROUND(lm.longitude, 1)) AS PlaceKey,
                       COALESCE(NULLIF(TRIM(lm.location_name), ''),
                           printf('%.3f, %.3f', ROUND(lm.latitude, 3), ROUND(lm.longitude, 3))) AS PlaceName,
                       lm.latitude AS Latitude, lm.longitude AS Longitude
                  FROM local_items li
                  JOIN local_item_metadata lm ON lm.item_id = li.id
                 WHERE {{commonPredicate}}
                   AND lm.latitude BETWEEN -90 AND 90
                   AND lm.longitude BETWEEN -180 AND 180
                   AND (@SearchPattern IS NULL
                        OR lm.location_name LIKE @SearchPattern ESCAPE '\'
                        OR printf('%.3f,%.3f', ROUND(lm.latitude, 3), ROUND(lm.longitude, 3)) LIKE @SearchPattern ESCAPE '\')
            ), ranked AS (
                SELECT *, ROW_NUMBER() OVER (
                    PARTITION BY PlaceKey ORDER BY EffectiveAt DESC, ItemId DESC) AS RepresentativeRank
                  FROM eligible
            )
            SELECT PlaceKey AS Key, MAX(PlaceName) AS Name,
                   ROUND(AVG(Latitude), 5) AS Latitude, ROUND(AVG(Longitude), 5) AS Longitude,
                   COUNT(DISTINCT ItemId) AS AssetCount,
                   COUNT(DISTINCT CASE WHEN LOWER(MediaKind) = 'image' THEN ItemId END) AS ImageCount,
                   COUNT(DISTINCT CASE WHEN LOWER(MediaKind) = 'video' THEN ItemId END) AS VideoCount,
                   MIN(EffectiveAt) AS EarliestAt, MAX(EffectiveAt) AS LatestAt,
                   MAX(CASE WHEN RepresentativeRank = 1 THEN LibraryId END) AS RepresentativeLibraryId,
                   MAX(CASE WHEN RepresentativeRank = 1 THEN ItemId END) AS RepresentativeAssetId
              FROM ranked
             GROUP BY PlaceKey
             ORDER BY AssetCount DESC, Key
             LIMIT @Take;
            """, parameters, cancellationToken: ct)).ToList();

        var totals = connection.QuerySingle<AtlasTotalsRow>(new CommandDefinition($$"""
            SELECT COUNT(DISTINCT CASE WHEN lm.latitude BETWEEN -90 AND 90 AND lm.longitude BETWEEN -180 AND 180 THEN li.id END) AS Mapped,
                   COUNT(DISTINCT CASE WHEN lm.latitude IS NULL OR lm.longitude IS NULL OR lm.latitude NOT BETWEEN -90 AND 90 OR lm.longitude NOT BETWEEN -180 AND 180 THEN li.id END) AS Unmapped,
                   COUNT(DISTINCT CASE WHEN lm.latitude BETWEEN -90 AND 90 AND lm.longitude BETWEEN -180 AND 180 AND LOWER(li.media_kind) = 'image' THEN li.id END) AS ImageCount,
                   COUNT(DISTINCT CASE WHEN lm.latitude BETWEEN -90 AND 90 AND lm.longitude BETWEEN -180 AND 180 AND LOWER(li.media_kind) = 'video' THEN li.id END) AS VideoCount
              FROM local_items li
              LEFT JOIN local_item_metadata lm ON lm.item_id = li.id
             WHERE {{commonPredicate}};
            """, parameters, cancellationToken: ct));

        var years = connection.Query<int>(new CommandDefinition($$"""
            SELECT DISTINCT CAST(strftime('%Y', COALESCE(li.captured_at, li.created_at)) AS INTEGER)
              FROM local_items li
              LEFT JOIN local_item_metadata lm ON lm.item_id = li.id
             WHERE {{facetPredicate}}
             ORDER BY 1 DESC;
            """, parameters, cancellationToken: ct)).Where(year => year > 0).ToList();

        var timeline = connection.Query<AtlasTimelineRow>(new CommandDefinition($$"""
            WITH eligible AS (
                SELECT li.id AS ItemId, LOWER(li.media_kind) AS MediaKind,
                       COALESCE(li.captured_at, li.created_at) AS EffectiveAt
                  FROM local_items li
                  JOIN local_item_metadata lm ON lm.item_id = li.id
                 WHERE {{facetPredicate}}
                   AND lm.latitude BETWEEN -90 AND 90 AND lm.longitude BETWEEN -180 AND 180
                   AND (@SearchPattern IS NULL
                        OR lm.location_name LIKE @SearchPattern ESCAPE '\'
                        OR printf('%.3f,%.3f', ROUND(lm.latitude, 3), ROUND(lm.longitude, 3)) LIKE @SearchPattern ESCAPE '\')
            ), bucketed AS (
                SELECT ItemId, MediaKind,
                       CASE @Resolution
                           WHEN 'day' THEN substr(EffectiveAt, 1, 10) || 'T00:00:00+00:00'
                           WHEN 'month' THEN substr(EffectiveAt, 1, 7) || '-01T00:00:00+00:00'
                           ELSE substr(EffectiveAt, 1, 4) || '-01-01T00:00:00+00:00'
                       END AS Start
                  FROM eligible
            ), units AS (
                SELECT *, CASE @Resolution
                    WHEN 'day' THEN CAST(julianday(Start) AS INTEGER)
                    WHEN 'month' THEN CAST(substr(Start, 1, 4) AS INTEGER) * 12 + CAST(substr(Start, 6, 2) AS INTEGER)
                    ELSE CAST(substr(Start, 1, 4) AS INTEGER) END AS UnitIndex,
                    CASE @Resolution WHEN 'day' THEN datetime(Start, '+1 day')
                         WHEN 'month' THEN datetime(Start, '+1 month')
                         ELSE datetime(Start, '+1 year') END AS End
                FROM bucketed
            ), bounds AS (
                SELECT MIN(UnitIndex) AS FirstUnit, MAX(1, (MAX(UnitIndex) - MIN(UnitIndex) + 180) / 180) AS Stride FROM units
            )
            SELECT MIN(Start) AS Start, MAX(End) AS End,
                   COUNT(DISTINCT ItemId) AS AssetCount,
                   COUNT(DISTINCT CASE WHEN MediaKind = 'image' THEN ItemId END) AS ImageCount,
                   COUNT(DISTINCT CASE WHEN MediaKind = 'video' THEN ItemId END) AS VideoCount
              FROM units CROSS JOIN bounds
             GROUP BY (UnitIndex - FirstUnit) / Stride
             ORDER BY Start;
            """, parameters, cancellationToken: ct)).ToList();

        var extent = connection.QuerySingle<AtlasExtentRow>(new CommandDefinition($$"""
            SELECT MIN(COALESCE(li.captured_at, li.created_at)) AS EarliestAt,
                   MAX(COALESCE(li.captured_at, li.created_at)) AS LatestAt
              FROM local_items li
              JOIN local_item_metadata lm ON lm.item_id = li.id
             WHERE {{facetPredicate}}
                   AND lm.latitude BETWEEN -90 AND 90 AND lm.longitude BETWEEN -180 AND 180
               AND (@SearchPattern IS NULL
                    OR lm.location_name LIKE @SearchPattern ESCAPE '\'
                    OR printf('%.3f,%.3f', ROUND(lm.latitude, 3), ROUND(lm.longitude, 3)) LIKE @SearchPattern ESCAPE '\');
            """, parameters, cancellationToken: ct));

        return new ViewAtlasDiscoveryPage(
            rows.Select(row => new ViewAtlasDiscoveryRow(
                row.Key, row.Name, row.Latitude, row.Longitude, checked((int)row.AssetCount),
                checked((int)row.ImageCount), checked((int)row.VideoCount), row.EarliestAt, row.LatestAt,
                row.RepresentativeLibraryId, row.RepresentativeAssetId)).ToList(),
            years,
            checked((int)totals.Mapped),
            checked((int)totals.Unmapped),
            rows.Count > 0 || totals.Mapped > 0,
            timeline.Select(bucket => new ViewAtlasTimelineBucketRow(
                bucket.Start, checked((int)bucket.AssetCount), checked((int)bucket.ImageCount),
                checked((int)bucket.VideoCount), bucket.End)).ToList(),
            extent.EarliestAt,
            extent.LatestAt, totals.ImageCount, totals.VideoCount);
    }

    public ViewPlaceAssetDiscoveryPage QueryPlaceAssets(
        ViewPlaceAssetDiscoveryQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.PlaceKey) || query.PlaceKey.Length > 300)
        {
            throw new ArgumentException("A valid place key is required.", nameof(query));
        }
        if (query.Offset < 0 || query.Limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "Place media paging is invalid.");
        }

        var libraries = ValidateAtlas(query.AuthorizedLibraryIds, query.Limit, null, query.Year,
            query.MediaKind, query.From, query.To, "year");
        if (libraries.Length == 0 && !query.IncludeSharedLibraryAssets)
        {
            return new ViewPlaceAssetDiscoveryPage(string.Empty, [], 0, false);
        }

        var parameters = new DynamicParameters(new
        {
            query.PlaceKey,
            query.Offset,
            Take = query.Limit + 1,
            query.Year,
            MediaKind = string.IsNullOrWhiteSpace(query.MediaKind) ? null : query.MediaKind.Trim().ToLowerInvariant(),
            query.From,
            query.To,
            FavoritesOnly = query.FavoritesOnly ? 1 : 0,
        });
        AddLibraries(parameters, libraries);
        var libraryPredicate = query.IncludeSharedLibraryAssets
            ? "EXISTS (SELECT 1 FROM view_shared_assets vsa WHERE vsa.item_id = li.id)"
            : LibraryPredicate("li", libraries.Length);
        using var connection = database.CreateConnection();
        var rows = connection.Query<PlaceAssetRow>(new CommandDefinition($$"""
            WITH eligible AS (
                SELECT li.id AS AssetId,
                       COALESCE(NULLIF(TRIM(lm.location_name), ''),
                           printf('%.3f, %.3f', ROUND(lm.latitude, 3), ROUND(lm.longitude, 3))) AS PlaceName,
                       COALESCE(li.captured_at, li.created_at) AS EffectiveAt,
                       LOWER(COALESCE(NULLIF(TRIM(lm.location_name), ''), 'location')) || '@' ||
                           printf('%.1f,%.1f', ROUND(lm.latitude, 1), ROUND(lm.longitude, 1)) AS PlaceKey
                  FROM local_items li
                  JOIN local_item_metadata lm ON lm.item_id = li.id
                 WHERE ({{libraryPredicate}})
                   AND li.hidden = 0 AND li.archived_at IS NULL AND li.trashed_at IS NULL
                   AND lm.latitude BETWEEN -90 AND 90 AND lm.longitude BETWEEN -180 AND 180
                   AND (@Year IS NULL OR CAST(strftime('%Y', COALESCE(li.captured_at, li.created_at)) AS INTEGER) = @Year)
                   AND (@MediaKind IS NULL OR LOWER(li.media_kind) = @MediaKind)
                   AND (@From IS NULL OR COALESCE(li.captured_at, li.created_at) >= @From)
                   AND (@To IS NULL OR COALESCE(li.captured_at, li.created_at) <= @To)
                   AND (@FavoritesOnly = 0 OR li.favorite = 1)
            )
            SELECT AssetId, PlaceName, COUNT(*) OVER () AS TotalCount
              FROM eligible
             WHERE PlaceKey = @PlaceKey
             ORDER BY EffectiveAt DESC, AssetId DESC
             LIMIT @Take OFFSET @Offset;
            """, parameters, cancellationToken: ct)).ToList();
        var total = rows.FirstOrDefault()?.TotalCount ?? 0;
        var hasMore = rows.Count > query.Limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        return new ViewPlaceAssetDiscoveryPage(
            rows.FirstOrDefault()?.PlaceName ?? string.Empty,
            rows.Select(row => row.AssetId).ToList(),
            checked((int)total),
            hasMore);
    }

    public ViewPeopleDiscoveryPage QueryPeople(
        ViewPeopleDiscoveryQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var libraries = Validate(query.AuthorizedLibraryIds, query.Limit, query.Cursor, query.Search);
        if (libraries.Length == 0 && !query.IncludeSharedLibraryAssets)
        {
            return new ViewPeopleDiscoveryPage([], null, false, false);
        }

        ct.ThrowIfCancellationRequested();
        var parameters = Parameters(libraries, query.Limit, query.Search, query.Cursor);
        var libraryPredicate = query.IncludeSharedLibraryAssets
            ? "EXISTS (SELECT 1 FROM view_shared_assets vsa WHERE vsa.item_id = li.id)"
            : LibraryPredicate("li", libraries.Length);
        var evidencePredicate = """
            ((lia.annotation_kind IN ('person_name', 'named_person', 'face_name'))
             OR (lia.annotation_kind IN ('person_identity', 'face_identity')
                 AND lia.reviewed_at IS NOT NULL))
            """;
        using var connection = database.CreateConnection();
        var hasEligibleData = connection.QuerySingle<bool>(new CommandDefinition($$"""
            SELECT EXISTS (
                SELECT 1
                  FROM local_item_annotations lia
                  JOIN local_items li ON li.id = lia.item_id
                 WHERE ({{libraryPredicate}})
                   AND li.hidden = 0
                   AND li.archived_at IS NULL
                   AND li.trashed_at IS NULL
                   AND TRIM(lia.annotation_value) <> ''
                   AND {{evidencePredicate}});
            """, parameters, cancellationToken: ct));

        var rows = connection.Query<PersonRow>(new CommandDefinition($$"""
            WITH eligible AS (
                SELECT li.id AS ItemId, li.library_id AS LibraryId,
                       COALESCE(li.captured_at, li.created_at) AS EffectiveAt,
                       LOWER(TRIM(lia.annotation_value)) AS PersonKey,
                       TRIM(lia.annotation_value) AS DisplayName,
                       lia.annotation_kind AS AnnotationKind,
                       lia.source AS ProvenanceSource,
                       CASE WHEN lia.reviewed_at IS NULL THEN 0 ELSE 1 END AS IsReviewed
                  FROM local_item_annotations lia
                  JOIN local_items li ON li.id = lia.item_id
                 WHERE ({{libraryPredicate}})
                   AND li.hidden = 0
                   AND li.archived_at IS NULL
                   AND li.trashed_at IS NULL
                   AND TRIM(lia.annotation_value) <> ''
                   AND {{evidencePredicate}}
                   AND (@SearchPattern IS NULL
                        OR lia.annotation_value LIKE @SearchPattern ESCAPE '\')
            ), ranked AS (
                SELECT *, ROW_NUMBER() OVER (
                    PARTITION BY PersonKey
                    ORDER BY EffectiveAt DESC, ItemId DESC) AS RepresentativeRank
                  FROM eligible
            ), grouped AS (
                SELECT PersonKey AS Key,
                       MAX(DisplayName) AS DisplayName,
                       COUNT(DISTINCT ItemId) AS AssetCount,
                       MAX(CASE WHEN RepresentativeRank = 1 THEN LibraryId END) AS RepresentativeLibraryId,
                       MAX(CASE WHEN RepresentativeRank = 1 THEN ItemId END) AS RepresentativeAssetId,
                       GROUP_CONCAT(DISTINCT AnnotationKind) AS AnnotationKinds,
                       GROUP_CONCAT(DISTINCT ProvenanceSource) AS ProvenanceSources,
                       MAX(IsReviewed) AS HasReviewedEvidence
                  FROM ranked
                 GROUP BY PersonKey
            )
            SELECT Key, DisplayName, AssetCount, RepresentativeLibraryId, RepresentativeAssetId,
                   AnnotationKinds, ProvenanceSources, HasReviewedEvidence
              FROM grouped
             WHERE (@CursorCount IS NULL
                    OR AssetCount < @CursorCount
                    OR (AssetCount = @CursorCount AND Key > @CursorKey))
             ORDER BY AssetCount DESC, Key
             LIMIT @Take;
            """, parameters, cancellationToken: ct)).ToList();

        var hasMore = rows.Count > query.Limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var items = rows.Select(row => new ViewPersonDiscoveryRow(
            row.Key,
            row.DisplayName,
            checked((int)row.AssetCount),
            row.RepresentativeLibraryId,
            row.RepresentativeAssetId,
            SplitEvidence(row.AnnotationKinds),
            SplitEvidence(row.ProvenanceSources),
            row.HasReviewedEvidence != 0)).ToList();
        var last = items.LastOrDefault();
        return new ViewPeopleDiscoveryPage(
            items,
            hasMore && last is not null ? new ViewDiscoveryCursor(last.AssetCount, last.Key) : null,
            hasMore,
            hasEligibleData);
    }

    private static Guid[] Validate(
        IReadOnlyCollection<Guid> authorizedLibraryIds,
        int limit,
        ViewDiscoveryCursor? cursor,
        string? search)
    {
        ArgumentNullException.ThrowIfNull(authorizedLibraryIds);
        if (limit is < 1 or > MaximumLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), $"Limit must be between 1 and {MaximumLimit}.");
        }

        if (authorizedLibraryIds.Any(id => id == Guid.Empty))
        {
            throw new ArgumentException("Authorized library IDs cannot be empty.", nameof(authorizedLibraryIds));
        }

        if (cursor is { AssetCount: < 1 } || cursor is { Key.Length: 0 })
        {
            throw new ArgumentException("The discovery cursor is invalid.", nameof(cursor));
        }

        if (search?.Length > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(search), "Search cannot exceed 200 characters.");
        }

        return authorizedLibraryIds.Distinct().ToArray();
    }

    private static Guid[] ValidateAtlas(
        IReadOnlyCollection<Guid> authorizedLibraryIds,
        int limit,
        string? search,
        int? year,
        string? mediaKind,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? timelineResolution)
    {
        ArgumentNullException.ThrowIfNull(authorizedLibraryIds);
        if (limit is < 1 or > 50000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }
        if (search?.Length > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(search));
        }
        if (year is < 1800 or > 9999)
        {
            throw new ArgumentOutOfRangeException(nameof(year));
        }
        var normalizedKind = mediaKind?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalizedKind)
            && normalizedKind is not ("image" or "video"))
        {
            throw new ArgumentException("Atlas media kind must be image or video.", nameof(mediaKind));
        }
        if (from.HasValue && to.HasValue && from > to)
        {
            throw new ArgumentException("Atlas date range is invalid.", nameof(from));
        }
        var normalizedResolution = timelineResolution?.Trim().ToLowerInvariant();
        if (normalizedResolution is not ("year" or "month" or "day"))
        {
            throw new ArgumentException("Atlas timeline resolution must be year, month, or day.", nameof(timelineResolution));
        }
        if (authorizedLibraryIds.Any(id => id == Guid.Empty))
        {
            throw new ArgumentException("Authorized library IDs cannot be empty.", nameof(authorizedLibraryIds));
        }
        return authorizedLibraryIds.Distinct().ToArray();
    }

    private static DynamicParameters Parameters(
        IReadOnlyList<Guid> libraries,
        int limit,
        string? search,
        ViewDiscoveryCursor? cursor)
    {
        var parameters = new DynamicParameters(new
        {
            Take = limit + 1,
            SearchPattern = SearchPattern(search),
            CursorCount = cursor?.AssetCount,
            CursorKey = cursor?.Key,
        });
        for (var index = 0; index < libraries.Count; index++)
        {
            parameters.Add($"LibraryId{index}", GuidSql.ToBlob(libraries[index]), DbType.Binary);
        }

        return parameters;
    }

    private static void AddLibraries(DynamicParameters parameters, IReadOnlyList<Guid> libraries)
    {
        for (var index = 0; index < libraries.Count; index++)
        {
            parameters.Add($"LibraryId{index}", GuidSql.ToBlob(libraries[index]), DbType.Binary);
        }
    }

    private static string LibraryPredicate(string alias, int count) =>
        string.Join(" OR ", Enumerable.Range(0, count).Select(index => $"{alias}.library_id = @LibraryId{index}"));

    private static string? SearchPattern(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var escaped = value.Trim()
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
        return $"%{escaped}%";
    }

    private static IReadOnlyList<string> SplitEvidence(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
}
