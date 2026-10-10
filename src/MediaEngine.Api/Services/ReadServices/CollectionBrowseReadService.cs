using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Application.ReadModels;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Collections;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using BuiltInBrowseCollectionCatalog = MediaEngine.Api.Models.BuiltInBrowseCollectionCatalog;

namespace MediaEngine.Api.Services.ReadServices;

public sealed class CollectionSystemViewGroupReadModel
{
    public string GroupName { get; init; } = string.Empty;
    public int WorkCount { get; init; }
    public int DistinctTitleCount { get; init; }
    public int AlbumCount { get; init; }
    public Guid? FirstAssetId { get; init; }
    public Guid? RootWorkId { get; init; }
    public Guid? RootCoverAssetId { get; init; }
    public string? DisplayTitleOverride { get; init; }
    public string? DescriptionOverride { get; init; }
    public Guid? RootBackgroundAssetId { get; init; }
    public Guid? RootLogoAssetId { get; init; }
    public string? Creator { get; init; }
    public string? Network { get; init; }
    public string? Year { get; init; }
    public int? EarliestYear { get; init; }
    public int? LatestYear { get; init; }
    public string? Description { get; init; }
    public string? Tagline { get; init; }
    public string? CoverAspectClass { get; init; }
    public string? SquareAspectClass { get; init; }
    public string? BackgroundAspectClass { get; init; }
    public string? BannerAspectClass { get; init; }
    public long? CoverWidthPx { get; init; }
    public long? CoverHeightPx { get; init; }
    public long? SquareWidthPx { get; init; }
    public long? SquareHeightPx { get; init; }
    public long? BackgroundWidthPx { get; init; }
    public long? BackgroundHeightPx { get; init; }
    public long? BannerWidthPx { get; init; }
    public long? BannerHeightPx { get; init; }
    public long? SeasonCount { get; init; }
}

public sealed class CollectionSystemViewPreviewReadModel
{
    public string GroupName { get; init; } = string.Empty;
    public Guid WorkId { get; init; }
    public Guid RootWorkId { get; init; }
    public Guid? AssetId { get; init; }
    public string? Title { get; init; }
    public string? Position { get; init; }
}

public sealed class CollectionBrowseReadService(
    ICollectionRepository collectionRepo,
    IDatabaseConnection db,
    ILogger<CollectionBrowseReadService> logger) : ICollectionBrowseReadService
{
    private readonly CollectionRuleEvaluator _ruleEvaluator = new(db);

    public async Task<List<CollectionDto>> GetAllAsync(CancellationToken ct)
    {
        var collections = await collectionRepo.GetAllAsync(ct);

        var libraryWorkIds = new HashSet<Guid>();
        using (var conn = db.CreateConnection())
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT DISTINCT woa.work_id
                    FROM work_owned_assets woa
                    INNER JOIN media_assets ma ON ma.id = woa.asset_id
                    WHERE ma.file_path_root NOT LIKE '%/.data/staging/%'
                      AND ma.file_path_root NOT LIKE '%\.data\staging\%'
                    """;
                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    libraryWorkIds.Add(GuidSql.FromDb(reader.GetValue(0)));
                }
            }
        }

        var filtered = new List<CollectionDto>();
        foreach (var collection in collections)
        {
            var libraryWorks = collection.Works.Where(work => libraryWorkIds.Contains(work.Id)).ToList();
            if (libraryWorks.Count == 0)
            {
                continue;
            }

            var filteredCollection = new Collection
            {
                Id = collection.Id,
                UniverseId = collection.UniverseId,
                DisplayName = collection.DisplayName,
                CreatedAt = collection.CreatedAt,
                ParentCollectionId = collection.ParentCollectionId,
                WikidataQid = collection.WikidataQid,
            };
            filteredCollection.RestoreDefinition(
                collection.CollectionType,
                collection.Scope,
                collection.Resolution,
                collection.MatchMode,
                collection.SortDirection,
                collection.UniverseStatus);

            foreach (var work in libraryWorks)
            {
                filteredCollection.AddWork(work);
            }

            foreach (var relationship in collection.Relationships)
            {
                filteredCollection.AddRelationship(relationship);
            }

            filtered.Add(filteredCollection.ToContract());
        }

        return filtered;
    }

    public async Task<Guid?> GetRootWorkIdAsync(Guid workId, CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var value = await conn.ExecuteScalarAsync<object?>(new CommandDefinition(
            """
            SELECT COALESCE(gp.id, p.id, w.id)
            FROM works w
            LEFT JOIN works p ON p.id = w.parent_work_id
            LEFT JOIN works gp ON gp.id = p.parent_work_id
            WHERE w.id = @WorkId
            LIMIT 1
            """,
            new { WorkId = workId },
            cancellationToken: ct)).ConfigureAwait(false);
        return GuidSql.FromDbNullable(value);
    }

    public async Task<Guid?> GetRepresentativeAssetIdAsync(Guid workId, CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var value = await conn.ExecuteScalarAsync<object?>(new CommandDefinition(
            """
            SELECT MIN(ma.id)
            FROM works w
            LEFT JOIN works child ON child.parent_work_id = w.id
            LEFT JOIN works grandchild ON grandchild.parent_work_id = child.id
            INNER JOIN work_owned_assets woa ON woa.work_id IN (w.id, child.id, grandchild.id)
            INNER JOIN media_assets ma ON ma.id = woa.asset_id
            WHERE w.id = @WorkId
            """,
            new { WorkId = workId },
            cancellationToken: ct)).ConfigureAwait(false);
        return GuidSql.FromDbNullable(value);
    }

    public async Task<Dictionary<Guid, Guid?>> GetPrimaryAssetIdsAsync(
        IEnumerable<Guid> workIds,
        CancellationToken ct)
    {
        var ids = workIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<PrimaryAssetReadRow>(new CommandDefinition(
            """
            SELECT woa.work_id AS WorkId, MIN(ma.id) AS AssetId
            FROM work_owned_assets woa
            INNER JOIN media_assets ma ON ma.id = woa.asset_id
            WHERE woa.work_id IN @WorkIds
            GROUP BY woa.work_id
            """,
            new { WorkIds = ids.Select(GuidSql.ToBlob).ToArray() },
            cancellationToken: ct)).ConfigureAwait(false);
        return rows.ToDictionary(row => row.WorkId, row => row.AssetId);
    }

    public async Task<CollectionPaletteReadModel?> GetAssetPaletteAsync(Guid entityId, CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<CollectionPaletteReadModel>(new CommandDefinition(
            """
            SELECT primary_hex AS PrimaryHex,
                   secondary_hex AS SecondaryHex,
                   accent_hex AS AccentHex
            FROM entity_assets
            WHERE entity_id = @EntityId
              AND primary_hex IS NOT NULL
            ORDER BY is_preferred DESC, created_at DESC
            LIMIT 1
            """,
            new { EntityId = entityId },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CollectionArtistWorkReadModel>> GetArtistWorksAsync(
        string artistName,
        CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<CollectionArtistWorkReadModel>(new CommandDefinition(
            """
            WITH artist_works AS (
                SELECT DISTINCT w.id AS work_id
                FROM works w
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
                INNER JOIN editions e ON e.work_id = w.id
                INNER JOIN media_assets ma ON ma.edition_id = e.id
                INNER JOIN canonical_value_arrays cva
                    ON cva.entity_id IN (w.id, ma.id, COALESCE(gp.id, p.id, w.id))
                   AND cva.key IN ('album_artist', 'artist', 'performer')
                WHERE cva.value = @ArtistName COLLATE NOCASE
            ),
            work_data AS (
                SELECT
                    aw.work_id AS WorkId,
                    MIN(ma.id) AS AssetId,
                    MAX(CASE WHEN cv.key = 'title' THEN cv.value END) AS Title,
                    MAX(CASE WHEN cv.key = 'album' THEN cv.value END) AS Album,
                    MAX(CASE WHEN cva.key IN ('album_artist', 'artist', 'performer') THEN cva.value END) AS Artist,
                    MAX(CASE WHEN cv.key = 'track_number' THEN cv.value END) AS TrackNumber,
                    MAX(CASE WHEN cv.key = 'disc_number' THEN cv.value END) AS DiscNumber,
                    MAX(CASE WHEN cv.key = 'apple_music_id' THEN cv.value END) AS AppleMusicId,
                    MAX(CASE WHEN cv.key = 'release_year' THEN cv.value END) AS ReleaseYear,
                    MAX(CASE WHEN cv.key = 'year' THEN cv.value END) AS YearValue,
                    MAX(CASE WHEN cv.key IN ('duration_seconds', 'duration_sec') THEN cv.value END) AS DurationSecondsValue,
                    MAX(CASE WHEN cv.key = 'duration' THEN cv.value END) AS Duration,
                    MAX(CASE WHEN cv.key = 'runtime' THEN cv.value END) AS Runtime,
                    '/stream/' || MIN(ma.id) || '/cover' AS Cover,
                    MAX(CASE WHEN cva.key = 'genre' THEN cva.value END) AS Genre,
                    MAX(CASE WHEN cv.key = 'child_entities_json' THEN cv.value END) AS ChildEntitiesJson
                FROM artist_works aw
                INNER JOIN editions e ON e.work_id = aw.work_id
                INNER JOIN media_assets ma ON ma.edition_id = e.id
                INNER JOIN canonical_values cv ON cv.entity_id = ma.id
                LEFT JOIN works w ON w.id = aw.work_id
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
                LEFT JOIN canonical_value_arrays cva
                    ON cva.entity_id IN (aw.work_id, ma.id, COALESCE(gp.id, p.id, aw.work_id))
                GROUP BY aw.work_id
            )
            SELECT * FROM work_data ORDER BY Album, CAST(TrackNumber AS INTEGER), Title
            """,
            new { ArtistName = artistName },
            cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<CollectionSystemViewDetailWorkReadModel>> GetSystemViewDetailWorksAsync(
        string groupField,
        string groupValue,
        string? mediaType,
        string? artistName,
        CancellationToken ct, Guid? rootWorkId = null)
    {
        var sortFields = groupField.ToLowerInvariant() switch
        {
            "show_name" => "SeasonNumber, EpisodeNumber, Title",
            "artist" => "Album, CAST(TrackNumber AS INTEGER), Title",
            "album" => "CAST(TrackNumber AS INTEGER), Title",
            "series" => "CAST(SeriesIndex AS INTEGER), Title",
            _ => "Title",
        };
        var mediaTypeJoin = string.IsNullOrWhiteSpace(mediaType)
            ? "INNER JOIN works w ON w.id = e.work_id"
            : "INNER JOIN works w ON w.id = e.work_id AND w.media_type = @MediaType";
        var isMusicAlbumGroup = string.Equals(mediaType, "Music", StringComparison.OrdinalIgnoreCase)
            && string.Equals(groupField, "album", StringComparison.OrdinalIgnoreCase);
        var hierarchyRootSql = isMusicAlbumGroup
            ? "COALESCE(p.id, w.id)"
            : "COALESCE(gp.id, p.id, w.id)";

        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<CollectionSystemViewDetailWorkReadModel>(new CommandDefinition(
            $"""
            WITH matched_works AS (
                SELECT DISTINCT e.work_id
                FROM editions e
                INNER JOIN media_assets ma ON ma.edition_id = e.id
                {mediaTypeJoin}
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
                WHERE (
                    (@RootWorkId IS NOT NULL AND @RootWorkId IN (w.id, p.id, gp.id))
                    OR (@RootWorkId IS NULL AND (
                    (
                        @IsMusicAlbumGroup = 1
                        AND COALESCE(
                            (SELECT cv_parent_album.value
                             FROM canonical_values cv_parent_album
                             WHERE cv_parent_album.entity_id = {hierarchyRootSql}
                               AND cv_parent_album.key = 'album'
                             LIMIT 1),
                            (SELECT cv_parent_title.value
                             FROM canonical_values cv_parent_title
                             WHERE cv_parent_title.entity_id = {hierarchyRootSql}
                               AND cv_parent_title.key = 'title'
                             LIMIT 1),
                            (SELECT cv_asset_album.value
                             FROM canonical_values cv_asset_album
                             WHERE cv_asset_album.entity_id = ma.id
                               AND cv_asset_album.key = 'album'
                             LIMIT 1)
                        ) = @GroupValue COLLATE NOCASE
                    )
                    OR (
                        @IsMusicAlbumGroup = 0
                        AND EXISTS (
                            SELECT 1
                            FROM canonical_values cv
                            WHERE cv.key = @GroupField
                              AND cv.value = @GroupValue COLLATE NOCASE
                              AND cv.entity_id IN (ma.id, w.id, p.id, gp.id)
                        )
                    )
                )))
                  AND (
                      @ArtistName IS NULL
                      OR EXISTS (
                          SELECT 1
                          FROM canonical_value_arrays cva_artist
                          WHERE cva_artist.key IN ('album_artist', 'artist', 'author', 'creator')
                            AND cva_artist.value = @ArtistName COLLATE NOCASE
                            AND cva_artist.entity_id IN (ma.id, w.id, p.id, gp.id)
                      )
                  )
            ),
            work_data AS (
                SELECT
                    mw.work_id AS WorkId,
                    MIN(ma.id) AS AssetId,
                    {hierarchyRootSql} AS RootWorkId,
                    MAX(CASE WHEN cv.key = 'title' THEN cv.value END) AS Title,
                    MAX(CASE WHEN cv.key = 'episode_title' THEN cv.value END) AS EpisodeTitle,
                    MAX(CASE WHEN cv.key = 'show_name' THEN cv.value END) AS ShowName,
                    MAX(CASE WHEN cv.key = 'season_number' THEN cv.value END) AS SeasonNumber,
                    MAX(CASE WHEN cv.key = 'episode_number' THEN cv.value END) AS EpisodeNumber,
                    MAX(CASE WHEN cv.key = 'series' THEN cv.value END) AS Series,
                    MAX(CASE WHEN cv.key = 'series_index' THEN cv.value END) AS SeriesIndex,
                    MAX(CASE WHEN cv.key = 'album' THEN cv.value END) AS Album,
                    (SELECT value
                     FROM canonical_value_arrays
                     WHERE entity_id IN (ma.id, w.id, p.id, gp.id)
                       AND key IN ('album_artist', 'artist')
                     ORDER BY CASE key WHEN 'album_artist' THEN 1 ELSE 2 END, ordinal
                     LIMIT 1) AS Artist,
                    (SELECT value
                     FROM canonical_value_arrays
                     WHERE entity_id IN (ma.id, w.id, p.id, gp.id)
                       AND key IN ('author', 'creator')
                     ORDER BY ordinal
                     LIMIT 1) AS Author,
                    (SELECT value
                     FROM canonical_value_arrays
                     WHERE entity_id IN (ma.id, w.id, p.id, gp.id)
                       AND key = 'director'
                     ORDER BY ordinal
                     LIMIT 1) AS Director,
                    MAX(CASE WHEN cv.key = 'track_number' THEN cv.value END) AS TrackNumber,
                    MAX(CASE WHEN cv.key = 'disc_number' THEN cv.value END) AS DiscNumber,
                    MAX(CASE WHEN cv.key = 'apple_music_id' THEN cv.value END) AS AppleMusicId,
                    MAX(CASE WHEN cv.key = 'release_year' THEN cv.value END) AS ReleaseYear,
                    MAX(CASE WHEN cv.key = 'year' THEN cv.value END) AS YearValue,
                    MAX(CASE WHEN cv.key IN ('duration_seconds', 'duration_sec') THEN cv.value END) AS DurationSecondsValue,
                    MAX(CASE WHEN cv.key = 'duration' THEN cv.value END) AS Duration,
                    MAX(CASE WHEN cv.key = 'runtime' THEN cv.value END) AS Runtime,
                    COALESCE(
                        (SELECT NULLIF(value, '') FROM canonical_values
                         WHERE entity_id = {hierarchyRootSql}
                           AND key IN ('cover_url', 'cover', 'poster_url', 'poster') LIMIT 1),
                        MAX(CASE WHEN cv.key IN ('cover_url', 'cover', 'poster_url', 'poster') THEN NULLIF(cv.value, '') END)) AS Cover,
                    COALESCE(
                        (SELECT NULLIF(value, '') FROM canonical_values
                         WHERE entity_id = {hierarchyRootSql}
                           AND key IN ('background_url', 'background') LIMIT 1),
                        MAX(CASE WHEN cv.key IN ('background_url', 'background') THEN NULLIF(cv.value, '') END)) AS Background,
                    COALESCE(
                        (SELECT NULLIF(value, '') FROM canonical_values
                         WHERE entity_id = {hierarchyRootSql}
                           AND key IN ('banner_url', 'banner') LIMIT 1),
                        MAX(CASE WHEN cv.key IN ('banner_url', 'banner') THEN NULLIF(cv.value, '') END)) AS Banner,
                    COALESCE(
                        (SELECT NULLIF(value, '') FROM canonical_values
                         WHERE entity_id = {hierarchyRootSql}
                           AND key IN ('hero_url', 'hero') LIMIT 1),
                        MAX(CASE WHEN cv.key IN ('hero_url', 'hero') THEN NULLIF(cv.value, '') END)) AS Hero,
                    COALESCE(
                        (SELECT NULLIF(value, '') FROM canonical_values
                         WHERE entity_id = {hierarchyRootSql}
                           AND key IN ('clear_logo_url', 'clear_logo', 'logo_url', 'logo') LIMIT 1),
                        MAX(CASE WHEN cv.key IN ('clear_logo_url', 'clear_logo', 'logo_url', 'logo') THEN NULLIF(cv.value, '') END)) AS Logo,
                    COALESCE(
                        (SELECT NULLIF(value, '') FROM canonical_values
                         WHERE entity_id = {hierarchyRootSql}
                           AND key IN ('artwork_primary_hex', 'cover_primary_hex', 'primary_color') LIMIT 1),
                        MAX(CASE WHEN cv.key IN ('artwork_primary_hex', 'cover_primary_hex', 'primary_color') THEN NULLIF(cv.value, '') END)) AS PrimaryColor,
                    COALESCE(
                        (SELECT NULLIF(value, '') FROM canonical_values
                         WHERE entity_id = {hierarchyRootSql}
                           AND key IN ('artwork_secondary_hex', 'cover_secondary_hex', 'secondary_color') LIMIT 1),
                        MAX(CASE WHEN cv.key IN ('artwork_secondary_hex', 'cover_secondary_hex', 'secondary_color') THEN NULLIF(cv.value, '') END)) AS SecondaryColor,
                    COALESCE(
                        (SELECT NULLIF(value, '') FROM canonical_values
                         WHERE entity_id = {hierarchyRootSql}
                           AND key IN ('artwork_accent_hex', 'cover_accent_hex', 'accent_color', 'dominant_color') LIMIT 1),
                        MAX(CASE WHEN cv.key IN ('artwork_accent_hex', 'cover_accent_hex', 'accent_color', 'dominant_color') THEN NULLIF(cv.value, '') END)) AS AccentColor,
                    (SELECT group_concat(value, ';')
                     FROM (
                         SELECT cva_genre.value
                         FROM canonical_value_arrays cva_genre
                         WHERE cva_genre.entity_id IN (mw.work_id, ma.id, {hierarchyRootSql})
                           AND cva_genre.key = 'genre'
                         ORDER BY cva_genre.ordinal
                     )) AS Genre,
                    MAX(CASE WHEN cv.key = 'network' THEN cv.value END) AS Network,
                    MAX(CASE WHEN cv.key = 'child_entities_json' THEN cv.value END) AS ChildEntitiesJson
                FROM matched_works mw
                INNER JOIN works w ON w.id = mw.work_id
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
                INNER JOIN editions e ON e.work_id = mw.work_id
                INNER JOIN media_assets ma ON ma.edition_id = e.id
                INNER JOIN canonical_values cv ON cv.entity_id = ma.id
                GROUP BY mw.work_id
            )
            SELECT * FROM work_data ORDER BY {sortFields}
            """,
            new
            {
                GroupField = groupField,
                GroupValue = groupValue,
                RootWorkId = rootWorkId,
                ArtistName = string.IsNullOrWhiteSpace(artistName) ? null : artistName,
                IsMusicAlbumGroup = isMusicAlbumGroup ? 1 : 0,
                MediaType = mediaType,
            },
            cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    public IReadOnlyList<Guid> EvaluateRules(
        CollectionRuleDefinition definition,
        string? sortField = null,
        string sortDirection = "desc",
        int limit = 0,
        string? query = null,
        string? secondarySortField = null,
        string? secondarySortDirection = null) =>
        _ruleEvaluator.Evaluate(definition, sortField, sortDirection, limit, query, secondarySortField, secondarySortDirection);

    public int CountRuleMatches(CollectionRuleDefinition definition, string? query = null) =>
        _ruleEvaluator.Count(definition, query);

    public Task<IReadOnlyList<string>> GetFieldValuesAsync(
        string field,
        string? query,
        int limit,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var take = Math.Clamp(limit, 1, 500);
        using var conn = db.CreateConnection();
        var sql = string.Equals(field, "media_type", StringComparison.OrdinalIgnoreCase)
            ? """
              SELECT DISTINCT media_type
              FROM works
              WHERE NULLIF(media_type, '') IS NOT NULL
              AND (@Query IS NULL OR media_type LIKE @Query COLLATE NOCASE)
              ORDER BY media_type
              LIMIT @Limit
              """
            : """
              SELECT DISTINCT value
              FROM (
                  SELECT value
                  FROM canonical_values
                  WHERE key = @Field
                  UNION ALL
                  SELECT value
                  FROM canonical_value_arrays
                  WHERE key = @Field
              )
              WHERE NULLIF(value, '') IS NOT NULL
                AND (@Query IS NULL OR value LIKE @Query COLLATE NOCASE)
              ORDER BY value
              LIMIT @Limit
              """;
        var rows = conn.Query<string>(new CommandDefinition(
            sql,
            new { Field = field, Query = string.IsNullOrWhiteSpace(query) ? null : $"%{query.Trim()}%", Limit = take },
            cancellationToken: ct));
        return Task.FromResult<IReadOnlyList<string>>(rows.AsList());
    }

    public async Task<IReadOnlyList<CollectionRuleValueDto>> GetEntityFieldValuesAsync(
        string field,
        string? query,
        int limit,
        CancellationToken ct)
    {
        var take = Math.Clamp(limit, 1, 500);
        using var conn = db.CreateConnection();
        var sql = field.ToLowerInvariant() switch
        {
            "person_qid" => """
                SELECT person_qid AS Value,
                       MIN(person_name) AS Label,
                       COUNT(DISTINCT media_asset_id) AS LocalCount
                FROM primary_person_media_credits
                WHERE NULLIF(person_qid, '') IS NOT NULL
                GROUP BY person_qid
                HAVING @Query IS NULL
                    OR MIN(person_name) LIKE @Query COLLATE NOCASE
                    OR person_qid LIKE @Query COLLATE NOCASE
                ORDER BY LocalCount DESC, Label COLLATE NOCASE
                LIMIT @Limit
                """,
            "wikidata_franchise" => """
                SELECT rel_qid AS Value,
                       MIN(COALESCE(NULLIF(rel_label, ''), rel_qid)) AS Label,
                       COUNT(DISTINCT collection_id) AS LocalCount
                FROM collection_relationships
                WHERE rel_type IN ('franchise','fictional_universe')
                GROUP BY rel_qid
                HAVING @Query IS NULL
                    OR MIN(COALESCE(NULLIF(rel_label, ''), rel_qid)) LIKE @Query COLLATE NOCASE
                    OR rel_qid LIKE @Query COLLATE NOCASE
                ORDER BY LocalCount DESC, Label COLLATE NOCASE
                LIMIT @Limit
                """,
            _ => """
            SELECT value_qid AS Value,
                   MIN(value) AS Label,
                   COUNT(DISTINCT entity_id) AS LocalCount
            FROM canonical_value_arrays
            WHERE key = @Field
              AND NULLIF(value_qid, '') IS NOT NULL
              AND NULLIF(value, '') IS NOT NULL
            GROUP BY value_qid
            HAVING @Query IS NULL
                OR MIN(value) LIKE @Query COLLATE NOCASE
                OR value_qid LIKE @Query COLLATE NOCASE
            ORDER BY LocalCount DESC, Label COLLATE NOCASE
            LIMIT @Limit
            """,
        };
        var rows = await conn.QueryAsync<CollectionRuleValueRow>(new CommandDefinition(
            sql,
            new { Field = field, Query = string.IsNullOrWhiteSpace(query) ? null : $"%{query.Trim()}%", Limit = take },
            cancellationToken: ct)).ConfigureAwait(false);
        return rows
            .Select(row => new CollectionRuleValueDto(row.Value, row.Label, checked((int)row.LocalCount), RuleOptionGroup(field)))
            .ToList();
    }

    private static string RuleOptionGroup(string field) => field.ToLowerInvariant() switch
    {
        "award_received" or "award_nominated" or "award_family" or "nomination_family" or "director" or "cast_member" or "screenwriter" or "production_company" or "filming_location" or "network" => "Movies & TV",
        "author" or "illustrator" or "publisher" => "Books & Comics",
        "narrator" => "Audiobooks",
        "artist" or "composer" or "record_label" => "Music",
        _ => "Cross-media",
    };

    private sealed class CollectionRuleValueRow
    {
        public string Value { get; init; } = string.Empty;
        public string Label { get; init; } = string.Empty;
        public long LocalCount { get; init; }
    }

    public async Task<List<ContentGroupDto>> GetSystemViewGroupsAsync(
        string? mediaType,
        string? groupField,
        CancellationToken ct,
        IReadOnlySet<Guid>? allowedWorkIds = null)
    {
        var definitions = BuiltInBrowseCollectionCatalog
            .GetSystemViewDefinitions(mediaType, groupField)
            .Select(definition => definition.ToCollection())
            .ToList();
        if (definitions.Count == 0)
        {
            return [];
        }

        var result = new List<ContentGroupDto>();
        foreach (var collection in definitions)
        {
            ct.ThrowIfCancellationRequested();
            var definition = CollectionRuleEvaluator.ParseDefinition(collection.RuleJson);
            var predicates = definition.AllConditions;
            if (predicates.Count == 0 || string.IsNullOrWhiteSpace(collection.GroupByField))
            {
                continue;
            }

            var workIds = EvaluateRules(
                definition,
                collection.SortField,
                collection.SortDirection.ToStorageValue(),
                secondarySortField: collection.SecondarySortField,
                secondarySortDirection: collection.SecondarySortDirection?.ToStorageValue())
                .Where(workId => allowedWorkIds is null || allowedWorkIds.Contains(workId))
                .ToList();
            var primaryMediaType = predicates
                .FirstOrDefault(predicate => predicate.Field.Equals("media_type", StringComparison.OrdinalIgnoreCase))
                ?.Value ?? "Unknown";
            var rows = await QuerySystemViewGroupsAsync(workIds, collection.GroupByField, primaryMediaType, ct)
                .ConfigureAwait(false);
            var previews = await QuerySystemViewPreviewsAsync(workIds, collection.GroupByField, primaryMediaType, ct)
                .ConfigureAwait(false);
            await AddSystemViewRowsAsync(result, rows, previews, collection, primaryMediaType, collection.GroupByField, ct)
                .ConfigureAwait(false);
        }

        if (result.Count == 0
            && string.Equals(mediaType, "Music", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(groupField, "album", StringComparison.OrdinalIgnoreCase)
                || string.Equals(groupField, "artist", StringComparison.OrdinalIgnoreCase)))
        {
            using var conn = db.CreateConnection();
            var visibleAssetPredicate = HomeVisibilitySql.VisibleAssetPathPredicate("ma.file_path_root");
            var musicWorkIds = (await conn.QueryAsync<Guid>(new CommandDefinition(
                $"""
                SELECT DISTINCT w.id
                FROM works w
                INNER JOIN editions e ON e.work_id = w.id
                INNER JOIN media_assets ma ON ma.edition_id = e.id
                WHERE w.media_type = 'Music'
                  AND COALESCE(w.is_catalog_only, 0) = 0
                  AND {visibleAssetPredicate}
                """,
                cancellationToken: ct)).ConfigureAwait(false))
                .Where(workId => allowedWorkIds is null || allowedWorkIds.Contains(workId))
                .ToList();
            var fallbackRows = await QuerySystemViewGroupsAsync(musicWorkIds, groupField!, "Music", ct)
                .ConfigureAwait(false);
            var fallbackPreviews = await QuerySystemViewPreviewsAsync(musicWorkIds, groupField!, "Music", ct)
                .ConfigureAwait(false);
            await AddSystemViewRowsAsync(result, fallbackRows, fallbackPreviews, definitions[0], "Music", groupField!, ct)
                .ConfigureAwait(false);
        }

        logger.LogInformation(
            "Resolved {Count} system-view groups for media type {MediaType} and field {GroupField}",
            result.Count,
            mediaType ?? "(none)",
            groupField ?? "(none)");
        return result;
    }

    private async Task<IReadOnlyList<CollectionSystemViewGroupReadModel>> QuerySystemViewGroupsAsync(
        IReadOnlyList<Guid> workIds,
        string groupField,
        string primaryMediaType,
        CancellationToken ct)
    {
        if (workIds.Count == 0)
        {
            return [];
        }

        var visibleAssetPredicate = HomeVisibilitySql.VisibleAssetPathPredicate("ma.file_path_root");
        var isMusicAlbumGroup = string.Equals(primaryMediaType, "Music", StringComparison.OrdinalIgnoreCase)
            && string.Equals(groupField, "album", StringComparison.OrdinalIgnoreCase);
        var isTvShowGroup = string.Equals(primaryMediaType, "TV", StringComparison.OrdinalIgnoreCase)
            && string.Equals(groupField, "show_name", StringComparison.OrdinalIgnoreCase);
        var isTvNetworkGroup = string.Equals(primaryMediaType, "TV", StringComparison.OrdinalIgnoreCase)
            && string.Equals(groupField, "network", StringComparison.OrdinalIgnoreCase);
        var isTvAggregateGroup = isTvShowGroup || isTvNetworkGroup;
        var isPersonGroup = ResolvePersonRole(groupField) is not null;
        var hierarchyRootSql = isMusicAlbumGroup
            ? "COALESCE(p.id, w.id)"
            : "COALESCE(gp.id, p.id, w.id)";
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<CollectionSystemViewGroupReadModel>(new CommandDefinition(
            $"""
            WITH work_assets AS (
                SELECT w.id AS WorkId,
                       ma.id AS AssetId,
                       {hierarchyRootSql} AS RootWorkId,
                       primary_credit.person_name AS LinkedPersonName
                FROM works w
                INNER JOIN editions e ON e.work_id = w.id
                INNER JOIN media_assets ma ON ma.edition_id = e.id
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
                LEFT JOIN primary_person_media_credits primary_credit
                    ON primary_credit.media_asset_id = ma.id
                   AND primary_credit.credit_key = lower(@GroupField)
                   AND @IsPersonGroup = 1
                WHERE w.id IN @WorkIds
                  AND {visibleAssetPredicate}
            ),
            resolved AS (
                SELECT wa.*,
                       CASE WHEN @IsMusicAlbumGroup = 1 THEN COALESCE(
                           (SELECT value FROM canonical_values WHERE entity_id = wa.RootWorkId AND key = 'album' LIMIT 1),
                           (SELECT value FROM canonical_values WHERE entity_id = wa.RootWorkId AND key = 'title' LIMIT 1),
                           (SELECT value FROM canonical_values WHERE entity_id = wa.AssetId AND key = 'album' LIMIT 1))
                       WHEN @IsTvShowGroup = 1 THEN COALESCE(
                           (SELECT value FROM canonical_values WHERE entity_id = wa.RootWorkId AND key = 'title' LIMIT 1),
                           (SELECT value FROM canonical_values WHERE entity_id = wa.RootWorkId AND key IN ('show_name', 'series_title') ORDER BY CASE key WHEN 'show_name' THEN 1 ELSE 2 END LIMIT 1),
                           (SELECT value FROM canonical_values WHERE entity_id = wa.WorkId AND key = 'show_name' LIMIT 1),
                           (SELECT value FROM canonical_values WHERE entity_id = wa.AssetId AND key = 'show_name' LIMIT 1))
                       WHEN wa.LinkedPersonName IS NOT NULL THEN wa.LinkedPersonName
                       ELSE COALESCE(
                           (SELECT value FROM canonical_values WHERE entity_id = wa.RootWorkId AND key = @GroupField LIMIT 1),
                           (SELECT value FROM canonical_values WHERE entity_id = wa.WorkId AND key = @GroupField LIMIT 1),
                           (SELECT value FROM canonical_values WHERE entity_id = wa.AssetId AND key = @GroupField LIMIT 1))
                       END AS GroupName,
                       COALESCE(
                           (SELECT value FROM canonical_values WHERE entity_id = wa.RootWorkId AND key = 'title' LIMIT 1),
                           (SELECT value FROM canonical_values WHERE entity_id = wa.AssetId AND key = 'title' LIMIT 1),
                           (SELECT value FROM canonical_values WHERE entity_id = wa.WorkId AND key = 'title' LIMIT 1)) AS WorkTitle,
                       COALESCE(
                           (SELECT value FROM canonical_values WHERE entity_id = wa.RootWorkId AND key = 'album' LIMIT 1),
                           (SELECT value FROM canonical_values WHERE entity_id = wa.AssetId AND key = 'album' LIMIT 1)) AS AlbumName,
                       CASE WHEN @IsTvShowGroup = 1 THEN COALESCE(
                           (SELECT value FROM canonical_values WHERE entity_id = wa.RootWorkId AND key IN ('premiere_date', 'first_air_date', 'release_date', 'release_year', 'year') ORDER BY CASE key WHEN 'premiere_date' THEN 1 WHEN 'first_air_date' THEN 2 WHEN 'release_date' THEN 3 WHEN 'release_year' THEN 4 ELSE 5 END LIMIT 1),
                           (SELECT value FROM canonical_values WHERE entity_id = wa.WorkId AND key IN ('premiere_date', 'first_air_date', 'release_date', 'release_year', 'year', 'air_date') ORDER BY CASE key WHEN 'premiere_date' THEN 1 WHEN 'first_air_date' THEN 2 WHEN 'release_date' THEN 3 WHEN 'release_year' THEN 4 WHEN 'year' THEN 5 ELSE 6 END LIMIT 1),
                           (SELECT value FROM canonical_values WHERE entity_id = wa.AssetId AND key IN ('premiere_date', 'first_air_date', 'release_date', 'release_year', 'year', 'air_date') ORDER BY CASE key WHEN 'premiere_date' THEN 1 WHEN 'first_air_date' THEN 2 WHEN 'release_date' THEN 3 WHEN 'release_year' THEN 4 WHEN 'year' THEN 5 ELSE 6 END LIMIT 1))
                       ELSE COALESCE(
                           (SELECT value FROM canonical_values WHERE entity_id = wa.RootWorkId AND key = 'release_year' LIMIT 1),
                           (SELECT value FROM canonical_values WHERE entity_id = wa.RootWorkId AND key = 'year' LIMIT 1),
                           (SELECT value FROM canonical_values WHERE entity_id = wa.WorkId AND key = 'release_year' LIMIT 1),
                           (SELECT value FROM canonical_values WHERE entity_id = wa.WorkId AND key = 'year' LIMIT 1),
                           (SELECT value FROM canonical_values WHERE entity_id = wa.AssetId AND key = 'release_year' LIMIT 1),
                           (SELECT value FROM canonical_values WHERE entity_id = wa.AssetId AND key = 'year' LIMIT 1))
                       END AS WorkYear,
                       COALESCE(
                           (SELECT value FROM canonical_value_arrays WHERE entity_id = wa.RootWorkId AND key IN ('album_artist', 'artist', 'author', 'creator') ORDER BY ordinal LIMIT 1),
                           (SELECT value FROM canonical_value_arrays WHERE entity_id = wa.WorkId AND key IN ('album_artist', 'artist', 'author', 'creator') ORDER BY ordinal LIMIT 1),
                           (SELECT value FROM canonical_value_arrays WHERE entity_id = wa.AssetId AND key IN ('album_artist', 'artist', 'author', 'creator') ORDER BY ordinal LIMIT 1)) AS WorkCreator
                FROM work_assets wa
            ),
            grouped AS (
                SELECT GroupName,
                       COUNT(DISTINCT CASE WHEN @IsTvAggregateGroup = 1 THEN RootWorkId ELSE WorkId END) AS WorkCount,
                       COUNT(DISTINCT CASE
                           WHEN @IsTvAggregateGroup = 1 THEN COALESCE(NULLIF(WorkTitle, ''), hex(RootWorkId))
                           ELSE COALESCE(NULLIF(WorkTitle, ''), hex(WorkId))
                       END) AS DistinctTitleCount,
                       COUNT(DISTINCT COALESCE(NULLIF(AlbumName, ''), hex(WorkId))) AS AlbumCount,
                       MIN(CASE
                           WHEN CAST(SUBSTR(WorkYear, 1, 4) AS INTEGER) BETWEEN 1000 AND 9999
                           THEN CAST(SUBSTR(WorkYear, 1, 4) AS INTEGER)
                       END) AS EarliestYear,
                       MAX(CASE
                           WHEN CAST(SUBSTR(WorkYear, 1, 4) AS INTEGER) BETWEEN 1000 AND 9999
                           THEN CAST(SUBSTR(WorkYear, 1, 4) AS INTEGER)
                       END) AS LatestYear,
                       MIN(AssetId) AS FirstAssetId,
                       MIN(RootWorkId) AS RootWorkId,
                       MAX(NULLIF(TRIM(WorkCreator), '')) AS WorkCreator
                FROM resolved
                WHERE GroupName IS NOT NULL AND TRIM(GroupName) != ''
                GROUP BY lower(GroupName)
            ),
            metadata AS (
                SELECT cv.entity_id AS EntityId,
                       MAX(CASE WHEN cv.key = 'network' THEN cv.value END) AS Network,
                       COALESCE(
                           MAX(CASE WHEN cv.key = 'premiere_date' THEN cv.value END),
                           MAX(CASE WHEN cv.key = 'first_air_date' THEN cv.value END),
                           MAX(CASE WHEN cv.key = 'release_date' THEN cv.value END),
                           MAX(CASE WHEN cv.key = 'release_year' THEN cv.value END),
                           MAX(CASE WHEN cv.key = 'year' THEN cv.value END)) AS Year,
                       MAX(CASE WHEN cv.key = 'description' THEN cv.value END) AS Description,
                       MAX(CASE WHEN cv.key = 'tagline' THEN cv.value END) AS Tagline,
                       MAX(CASE WHEN cv.key = 'cover_aspect_class' THEN cv.value END) AS CoverAspectClass,
                       MAX(CASE WHEN cv.key = 'square_aspect_class' THEN cv.value END) AS SquareAspectClass,
                       MAX(CASE WHEN cv.key = 'background_aspect_class' THEN cv.value END) AS BackgroundAspectClass,
                       MAX(CASE WHEN cv.key = 'banner_aspect_class' THEN cv.value END) AS BannerAspectClass,
                       MAX(CASE WHEN cv.key = 'cover_width_px' THEN CAST(cv.value AS INTEGER) END) AS CoverWidthPx,
                       MAX(CASE WHEN cv.key = 'cover_height_px' THEN CAST(cv.value AS INTEGER) END) AS CoverHeightPx,
                       MAX(CASE WHEN cv.key = 'square_width_px' THEN CAST(cv.value AS INTEGER) END) AS SquareWidthPx,
                       MAX(CASE WHEN cv.key = 'square_height_px' THEN CAST(cv.value AS INTEGER) END) AS SquareHeightPx,
                       MAX(CASE WHEN cv.key = 'background_width_px' THEN CAST(cv.value AS INTEGER) END) AS BackgroundWidthPx,
                       MAX(CASE WHEN cv.key = 'background_height_px' THEN CAST(cv.value AS INTEGER) END) AS BackgroundHeightPx,
                       MAX(CASE WHEN cv.key = 'banner_width_px' THEN CAST(cv.value AS INTEGER) END) AS BannerWidthPx,
                       MAX(CASE WHEN cv.key = 'banner_height_px' THEN CAST(cv.value AS INTEGER) END) AS BannerHeightPx
                FROM canonical_values cv
                WHERE cv.entity_id IN (SELECT RootWorkId FROM grouped UNION SELECT FirstAssetId FROM grouped)
                GROUP BY cv.entity_id
            )
            SELECT g.GroupName,
                   g.WorkCount,
                   g.DistinctTitleCount,
                   g.AlbumCount,
                   g.FirstAssetId,
                   g.RootWorkId,
                   (SELECT json_extract(w.display_overrides_json, '$.title') FROM works w WHERE w.id = g.RootWorkId) AS DisplayTitleOverride,
                   (SELECT json_extract(w.display_overrides_json, '$.description') FROM works w WHERE w.id = g.RootWorkId) AS DescriptionOverride,
                   CASE WHEN @IsTvShowGroup = 1 OR @GroupField = 'series' THEN (SELECT ea.id
                     FROM entity_assets ea
                     WHERE ea.entity_id = g.RootWorkId
                       AND ea.entity_type = 'Work'
                       AND ea.asset_type = 'CoverArt'
                       AND (@IsTvShowGroup = 1 OR ea.is_user_override = 1)
                       AND COALESCE(NULLIF(ea.local_image_path_m, ''), NULLIF(ea.local_image_path, '')) IS NOT NULL
                     ORDER BY ea.is_user_override DESC, ea.is_preferred DESC, COALESCE(ea.updated_at, ea.created_at) DESC
                     LIMIT 1) ELSE NULL END AS RootCoverAssetId,
                   CASE WHEN @IsTvShowGroup = 1 THEN (SELECT ea.id
                     FROM entity_assets ea
                     WHERE ea.entity_id = g.RootWorkId
                       AND ea.entity_type = 'Work'
                       AND ea.asset_type = 'Background'
                       AND COALESCE(NULLIF(ea.local_image_path_m, ''), NULLIF(ea.local_image_path, '')) IS NOT NULL
                     ORDER BY ea.is_user_override DESC, ea.is_preferred DESC, COALESCE(ea.updated_at, ea.created_at) DESC
                     LIMIT 1) ELSE NULL END AS RootBackgroundAssetId,
                   CASE WHEN @IsTvShowGroup = 1 THEN (SELECT ea.id
                     FROM entity_assets ea
                     WHERE ea.entity_id = g.RootWorkId
                       AND ea.entity_type = 'Work'
                       AND ea.asset_type = 'Logo'
                       AND COALESCE(NULLIF(ea.local_image_path_m, ''), NULLIF(ea.local_image_path, '')) IS NOT NULL
                     ORDER BY ea.is_user_override DESC, ea.is_preferred DESC, COALESCE(ea.updated_at, ea.created_at) DESC
                     LIMIT 1) ELSE NULL END AS RootLogoAssetId,
                   g.WorkCreator AS Creator,
                   COALESCE(root.Network, asset.Network) AS Network,
                   COALESCE(root.Year, asset.Year) AS Year,
                   g.EarliestYear,
                   g.LatestYear,
                   COALESCE(root.Description, asset.Description) AS Description,
                   COALESCE(root.Tagline, asset.Tagline) AS Tagline,
                   COALESCE(root.CoverAspectClass, asset.CoverAspectClass) AS CoverAspectClass,
                   COALESCE(root.SquareAspectClass, asset.SquareAspectClass) AS SquareAspectClass,
                   COALESCE(root.BackgroundAspectClass, asset.BackgroundAspectClass) AS BackgroundAspectClass,
                   COALESCE(root.BannerAspectClass, asset.BannerAspectClass) AS BannerAspectClass,
                   COALESCE(root.CoverWidthPx, asset.CoverWidthPx) AS CoverWidthPx,
                   COALESCE(root.CoverHeightPx, asset.CoverHeightPx) AS CoverHeightPx,
                   COALESCE(root.SquareWidthPx, asset.SquareWidthPx) AS SquareWidthPx,
                   COALESCE(root.SquareHeightPx, asset.SquareHeightPx) AS SquareHeightPx,
                   COALESCE(root.BackgroundWidthPx, asset.BackgroundWidthPx) AS BackgroundWidthPx,
                   COALESCE(root.BackgroundHeightPx, asset.BackgroundHeightPx) AS BackgroundHeightPx,
                   COALESCE(root.BannerWidthPx, asset.BannerWidthPx) AS BannerWidthPx,
                   COALESCE(root.BannerHeightPx, asset.BannerHeightPx) AS BannerHeightPx,
                   CASE WHEN @IsTvShowGroup = 1 THEN (SELECT COUNT(DISTINCT season.value)
                     FROM resolved r2
                     INNER JOIN canonical_values season ON season.entity_id = r2.AssetId AND season.key = 'season_number'
                     WHERE r2.RootWorkId = g.RootWorkId) ELSE NULL END AS SeasonCount
            FROM grouped g
            LEFT JOIN metadata root ON root.EntityId = g.RootWorkId
            LEFT JOIN metadata asset ON asset.EntityId = g.FirstAssetId
            ORDER BY g.GroupName
            """,
            new
            {
                WorkIds = workIds.Select(GuidSql.ToBlob).ToArray(),
                GroupField = groupField,
                IsMusicAlbumGroup = isMusicAlbumGroup ? 1 : 0,
                IsTvShowGroup = isTvShowGroup ? 1 : 0,
                IsTvAggregateGroup = isTvAggregateGroup ? 1 : 0,
                IsPersonGroup = isPersonGroup ? 1 : 0,
            },
            cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<ContentGroupPreviewItemDto>>> QuerySystemViewPreviewsAsync(
        IReadOnlyList<Guid> workIds,
        string groupField,
        string primaryMediaType,
        CancellationToken ct)
    {
        if (workIds.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<ContentGroupPreviewItemDto>>(StringComparer.OrdinalIgnoreCase);
        }

        var visibleAssetPredicate = HomeVisibilitySql.VisibleAssetPathPredicate("ma.file_path_root");
        var isMusicAlbumGroup = string.Equals(primaryMediaType, "Music", StringComparison.OrdinalIgnoreCase)
            && string.Equals(groupField, "album", StringComparison.OrdinalIgnoreCase);
        var isTvShowGroup = string.Equals(primaryMediaType, "TV", StringComparison.OrdinalIgnoreCase)
            && string.Equals(groupField, "show_name", StringComparison.OrdinalIgnoreCase);
        var isTvNetworkGroup = string.Equals(primaryMediaType, "TV", StringComparison.OrdinalIgnoreCase)
            && string.Equals(groupField, "network", StringComparison.OrdinalIgnoreCase);
        var isTvAggregateGroup = isTvShowGroup || isTvNetworkGroup;
        var isPersonGroup = ResolvePersonRole(groupField) is not null;
        var hierarchyRootSql = isMusicAlbumGroup
            ? "COALESCE(p.id, w.id)"
            : "COALESCE(gp.id, p.id, w.id)";
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<CollectionSystemViewPreviewReadModel>(new CommandDefinition(
            $"""
            WITH work_assets AS (
                SELECT w.id AS WorkId,
                       MIN(ma.id) AS AssetId,
                       {hierarchyRootSql} AS RootWorkId,
                       primary_credit.person_name AS LinkedPersonName
                FROM works w
                INNER JOIN editions e ON e.work_id = w.id
                INNER JOIN media_assets ma ON ma.edition_id = e.id
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
                LEFT JOIN primary_person_media_credits primary_credit
                    ON primary_credit.media_asset_id = ma.id
                   AND primary_credit.credit_key = lower(@GroupField)
                   AND @IsPersonGroup = 1
                WHERE w.id IN @WorkIds
                  AND {visibleAssetPredicate}
                GROUP BY w.id, {hierarchyRootSql}, primary_credit.person_name
            )
            SELECT CASE WHEN @IsMusicAlbumGroup = 1 THEN COALESCE(
                       (SELECT value FROM canonical_values WHERE entity_id = wa.RootWorkId AND key = 'album' LIMIT 1),
                       (SELECT value FROM canonical_values WHERE entity_id = wa.RootWorkId AND key = 'title' LIMIT 1),
                       (SELECT value FROM canonical_values WHERE entity_id = wa.AssetId AND key = 'album' LIMIT 1))
                   WHEN @IsTvShowGroup = 1 THEN COALESCE(
                       (SELECT value FROM canonical_values WHERE entity_id = wa.RootWorkId AND key = 'title' LIMIT 1),
                       (SELECT value FROM canonical_values WHERE entity_id = wa.RootWorkId AND key IN ('show_name', 'series_title') ORDER BY CASE key WHEN 'show_name' THEN 1 ELSE 2 END LIMIT 1),
                       (SELECT value FROM canonical_values WHERE entity_id = wa.WorkId AND key = 'show_name' LIMIT 1),
                       (SELECT value FROM canonical_values WHERE entity_id = wa.AssetId AND key = 'show_name' LIMIT 1))
                   WHEN wa.LinkedPersonName IS NOT NULL THEN wa.LinkedPersonName
                   ELSE COALESCE(
                       (SELECT value FROM canonical_values WHERE entity_id = wa.RootWorkId AND key = @GroupField LIMIT 1),
                       (SELECT value FROM canonical_values WHERE entity_id = wa.WorkId AND key = @GroupField LIMIT 1),
                       (SELECT value FROM canonical_values WHERE entity_id = wa.AssetId AND key = @GroupField LIMIT 1))
                   END AS GroupName,
                   wa.WorkId,
                   wa.RootWorkId,
                   wa.AssetId,
                   CASE WHEN @IsTvAggregateGroup = 1 THEN COALESCE(
                       (SELECT value FROM canonical_values WHERE entity_id = wa.RootWorkId AND key = 'title' LIMIT 1),
                       (SELECT value FROM canonical_values WHERE entity_id = wa.WorkId AND key = 'show_name' LIMIT 1),
                       (SELECT value FROM canonical_values WHERE entity_id = wa.AssetId AND key = 'show_name' LIMIT 1),
                       'Untitled')
                   ELSE COALESCE(
                       (SELECT value FROM canonical_values WHERE entity_id = wa.AssetId AND key IN ('episode_title', 'title') ORDER BY key = 'episode_title' DESC LIMIT 1),
                       (SELECT value FROM canonical_values WHERE entity_id = wa.WorkId AND key = 'title' LIMIT 1),
                       'Untitled') END AS Title,
                   COALESCE(
                       (SELECT value FROM canonical_values WHERE entity_id = wa.AssetId AND key IN ('series_index', 'episode_number', 'track_number') ORDER BY CASE key WHEN 'series_index' THEN 1 WHEN 'episode_number' THEN 2 ELSE 3 END LIMIT 1),
                       (SELECT value FROM canonical_values WHERE entity_id = wa.WorkId AND key = 'series_index' LIMIT 1)) AS Position
            FROM work_assets wa
            """,
            new
            {
                WorkIds = workIds.Select(GuidSql.ToBlob).ToArray(),
                GroupField = groupField,
                IsMusicAlbumGroup = isMusicAlbumGroup ? 1 : 0,
                IsTvShowGroup = isTvShowGroup ? 1 : 0,
                IsTvAggregateGroup = isTvAggregateGroup ? 1 : 0,
                IsPersonGroup = isPersonGroup ? 1 : 0,
            },
            cancellationToken: ct)).ConfigureAwait(false);

        var previewShape = string.Equals(primaryMediaType, "Music", StringComparison.OrdinalIgnoreCase)
            ? "square"
            : "portrait";
        return rows
            .Where(row => !string.IsNullOrWhiteSpace(row.GroupName) && row.AssetId.HasValue)
            .GroupBy(row => row.GroupName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ContentGroupPreviewItemDto>)(isTvAggregateGroup
                    ? group.GroupBy(row => row.RootWorkId).Select(show => show.First())
                    : group)
                    .OrderBy(row => ParseSequencePosition(row.Position))
                    .ThenBy(row => row.Title, StringComparer.OrdinalIgnoreCase)
                    .Select(row => new ContentGroupPreviewItemDto(
                        isTvAggregateGroup ? row.RootWorkId : row.WorkId,
                        row.Title ?? "Untitled",
                        $"/stream/{row.AssetId!.Value:D}/cover",
                        previewShape,
                        row.Position))
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);
    }

    private async Task AddSystemViewRowsAsync(
        List<ContentGroupDto> result,
        IReadOnlyList<CollectionSystemViewGroupReadModel> rows,
        IReadOnlyDictionary<string, IReadOnlyList<ContentGroupPreviewItemDto>> previews,
        Collection collection,
        string primaryMediaType,
        string groupByField,
        CancellationToken ct)
    {
        var personRole = ResolvePersonRole(groupByField);
        foreach (var row in rows)
        {
            string? personPhotoUrl = null;
            Guid? personId = null;
            IReadOnlyList<string> personRoles = [];
            if (personRole is not null)
            {
                try
                {
                    var person = await ResolvePreferredPersonAsync(
                        row.GroupName,
                        groupByField,
                        ct).ConfigureAwait(false);
                    if (person is not null)
                    {
                        personId = person.Id;
                        personRoles = BuildPersonRoles(
                            ResolvePersonDisplayRole(primaryMediaType, groupByField),
                            person.StoredRoles);
                        if (!string.IsNullOrWhiteSpace(person.LocalHeadshotPath)
                            || !string.IsNullOrWhiteSpace(person.HeadshotUrl))
                        {
                            personPhotoUrl = $"/persons/{person.Id}/headshot";
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(
                        ex,
                        "Person lookup failed for {GroupField} system-view group {PersonName}",
                        groupByField,
                        row.GroupName);
                }
            }

            var assetRoute = row.FirstAssetId?.ToString("D");
            var usesRootTvArtwork = string.Equals(primaryMediaType, "TV", StringComparison.OrdinalIgnoreCase)
                && string.Equals(groupByField, "show_name", StringComparison.OrdinalIgnoreCase);
            result.Add(new ContentGroupDto
            {
                CollectionId = collection.Id,
                RootWorkId = row.RootWorkId,
                DisplayName = groupByField == "series" ? row.DisplayTitleOverride ?? row.GroupName : row.GroupName,
                PrimaryMediaType = primaryMediaType,
                WorkCount = row.WorkCount,
                DistinctTitleCount = row.DistinctTitleCount,
                PreviewItems = !usesRootTvArtwork && groupByField == "series" && row.RootCoverAssetId is Guid customCoverId && row.RootWorkId is Guid ownerId
                    ? [new ContentGroupPreviewItemDto(ownerId, row.DisplayTitleOverride ?? row.GroupName, $"/stream/artwork/{customCoverId:D}", "portrait", null)]
                    : previews.GetValueOrDefault(row.GroupName) ?? [],
                CoverUrl = usesRootTvArtwork || (groupByField == "series" && row.RootCoverAssetId.HasValue)
                    ? row.RootCoverAssetId is { } rootCoverId ? $"/stream/artwork/{rootCoverId:D}" : null
                    : assetRoute is null ? null : $"/stream/{assetRoute}/cover",
                BackgroundUrl = usesRootTvArtwork
                    ? row.RootBackgroundAssetId is { } rootBackgroundId ? $"/stream/artwork/{rootBackgroundId:D}" : null
                    : assetRoute is null ? null : $"/stream/{assetRoute}/background",
                BannerUrl = null,
                LogoUrl = usesRootTvArtwork
                    ? row.RootLogoAssetId is { } rootLogoId ? $"/stream/artwork/{rootLogoId:D}" : null
                    : assetRoute is null ? null : $"/stream/{assetRoute}/logo",
                CoverAspectClass = row.CoverAspectClass,
                SquareAspectClass = row.SquareAspectClass,
                BackgroundAspectClass = row.BackgroundAspectClass,
                BannerAspectClass = row.BannerAspectClass,
                CoverWidthPx = ToInt32(row.CoverWidthPx),
                CoverHeightPx = ToInt32(row.CoverHeightPx),
                SquareWidthPx = ToInt32(row.SquareWidthPx),
                SquareHeightPx = ToInt32(row.SquareHeightPx),
                BackgroundWidthPx = ToInt32(row.BackgroundWidthPx),
                BackgroundHeightPx = ToInt32(row.BackgroundHeightPx),
                BannerWidthPx = ToInt32(row.BannerWidthPx),
                BannerHeightPx = ToInt32(row.BannerHeightPx),
                Description = groupByField == "series" ? row.DescriptionOverride ?? row.Description : row.Description,
                Tagline = row.Tagline,
                Creator = row.Creator ?? (ResolvePersonRole(groupByField) is not null ? row.GroupName : null),
                UniverseStatus = "Complete",
                CreatedAt = collection.CreatedAt,
                ArtistPhotoUrl = string.Equals(groupByField, "artist", StringComparison.OrdinalIgnoreCase) ? personPhotoUrl : null,
                ArtistPersonId = string.Equals(groupByField, "artist", StringComparison.OrdinalIgnoreCase) ? personId : null,
                PersonPhotoUrl = personPhotoUrl,
                PersonId = personId,
                PersonRoles = personRoles,
                Network = row.Network,
                Year = row.Year,
                EarliestYear = row.EarliestYear,
                LatestYear = row.LatestYear,
                SeasonCount = row.SeasonCount is > 0 ? ToInt32(row.SeasonCount) : null,
                AlbumCount = row.AlbumCount > 0 ? row.AlbumCount : null,
            });
        }
    }

    private async Task<PreferredPersonRouteReadModel?> ResolvePreferredPersonAsync(
        string personName,
        string groupByField,
        CancellationToken ct)
    {
        var preferredRoles = groupByField.Trim().ToLowerInvariant() switch
        {
            "artist" => new[] { "Artist", "Performer", "Composer" },
            "author" => new[] { "Author", "Screenwriter" },
            "director" => new[] { "Director" },
            "narrator" => new[] { "Narrator", "Voice Actor" },
            _ => Array.Empty<string>(),
        };

        using var conn = db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<PreferredPersonRouteReadModel>(new CommandDefinition(
            """
            SELECT
                person.id AS Id,
                person.local_headshot_path AS LocalHeadshotPath,
                person.headshot_url AS HeadshotUrl,
                GROUP_CONCAT(role.role, '|') AS RolesCsv
            FROM persons person
            LEFT JOIN person_roles role ON role.person_id = person.id
            WHERE person.name = @PersonName COLLATE NOCASE
            GROUP BY
                person.id,
                person.local_headshot_path,
                person.headshot_url,
                person.biography,
                person.wikidata_qid,
                person.enriched_at,
                person.created_at
            ORDER BY
                CASE WHEN EXISTS (
                    SELECT 1
                    FROM person_roles preferred_role
                    WHERE preferred_role.person_id = person.id
                      AND preferred_role.role IN @PreferredRoles
                ) THEN 0 ELSE 1 END,
                CASE WHEN NULLIF(TRIM(person.local_headshot_path), '') IS NOT NULL THEN 0 ELSE 1 END,
                CASE WHEN NULLIF(TRIM(person.headshot_url), '') IS NOT NULL THEN 0 ELSE 1 END,
                CASE WHEN NULLIF(TRIM(person.biography), '') IS NOT NULL THEN 0 ELSE 1 END,
                CASE WHEN person.enriched_at IS NOT NULL THEN 0 ELSE 1 END,
                CASE WHEN NULLIF(TRIM(person.wikidata_qid), '') IS NOT NULL THEN 0 ELSE 1 END,
                person.created_at,
                person.id
            LIMIT 1
            """,
            new
            {
                PersonName = personName,
                PreferredRoles = preferredRoles,
            },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    private static int? ToInt32(long? value) =>
        value is >= int.MinValue and <= int.MaxValue ? (int)value.Value : null;

    private static decimal ParseSequencePosition(string? value) =>
        decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var position)
            ? position
            : decimal.MaxValue;

    private static string? ResolvePersonRole(string groupField) => groupField.Trim().ToLowerInvariant() switch
    {
        "author" => "Author",
        "director" => "Director",
        "narrator" => "Narrator",
        "artist" => "Artist",
        _ => null,
    };

    private static IReadOnlyList<string> BuildPersonRoles(string primaryRole, IEnumerable<string> storedRoles) =>
        new[] { primaryRole }
            .Concat(storedRoles)
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string ResolvePersonDisplayRole(string primaryMediaType, string groupField)
    {
        if (string.Equals(primaryMediaType, "Comics", StringComparison.OrdinalIgnoreCase)
            && string.Equals(groupField, "author", StringComparison.OrdinalIgnoreCase))
        {
            return "Creator";
        }

        return groupField.Trim().ToLowerInvariant() switch
        {
            "artist" => "Artist",
            "author" => "Author",
            "director" => "Director",
            "narrator" => "Narrator",
            _ => "Creator",
        };
    }

    private sealed record PrimaryAssetReadRow(Guid WorkId, Guid? AssetId);

    private sealed record PreferredPersonRouteReadModel
    {
        public Guid Id { get; init; }
        public string? LocalHeadshotPath { get; init; }
        public string? HeadshotUrl { get; init; }
        public string? RolesCsv { get; init; }
        public IReadOnlyList<string> StoredRoles => string.IsNullOrWhiteSpace(RolesCsv)
            ? []
            : RolesCsv.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
