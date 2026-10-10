using System.Diagnostics;
using Dapper;
using MediaEngine.Api.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Display;

public sealed class DisplayWorkProjectionReader
{
    private readonly IDatabaseConnection _db;
    private readonly ILogger<DisplayWorkProjectionReader>? _logger;

    public DisplayWorkProjectionReader(
        IDatabaseConnection db,
        ILogger<DisplayWorkProjectionReader>? logger = null)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DisplayWorkRow>> LoadAsync(CancellationToken ct, int limit = int.MaxValue, Guid? detailId = null)
    {
        var startedAt = Stopwatch.GetTimestamp();
        using var conn = _db.CreateConnection();
        var visibleWorkPredicate = HomeVisibilitySql.VisibleWorkPredicate("w.id", "w.curator_state", "w.is_catalog_only");
        var visibleAssetPredicate = HomeVisibilitySql.VisibleAssetPathPredicate("ma.file_path_root");
        var visibleEditionAssetPredicate = HomeVisibilitySql.VisibleAssetPathPredicate("editionAsset.file_path_root");
        var detailPredicate = detailId.HasValue ? """
            AND (w.id = @detailId OR p.id = @detailId OR gp.id = @detailId
                OR w.collection_id = (SELECT collection_id FROM works WHERE id = @detailId)
                OR EXISTS (
                    SELECT 1 FROM projection_credits credit
                    JOIN persons person ON person.id = @detailId
                    WHERE credit.media_asset_id = ma.id
                      AND (credit.person_name = person.name COLLATE NOCASE
                           OR (NULLIF(credit.person_qid, '') IS NOT NULL AND credit.person_qid = person.wikidata_qid))))
            """ : "";
        var displayYearSql = MediaDateSql.DisplayOriginalYear(
            "WorkId",
            "RootWorkId",
            "AssetId",
            "MediaType",
            "projection_credits");
        var sql = $"""
            WITH projection_credits AS MATERIALIZED (SELECT * FROM primary_person_media_credits),
            asset_dates AS MATERIALIZED (
                SELECT entity_id, MIN(claimed_at) AS claimed_at
                FROM metadata_claims GROUP BY entity_id
            ),
            ranked_assets AS (
                SELECT
                    w.id AS WorkId,
                    ma.library_id AS LibraryId,
                    w.collection_id AS CollectionId,
                    w.media_type AS MediaType,
                    w.work_kind AS WorkKind,
                    e.id AS EditionId,
                    CASE
                        WHEN w.media_type = 'Music' THEN COALESCE(p.id, w.id)
                        ELSE COALESCE(gp.id, p.id, w.id)
                    END AS RootWorkId,
                    ma.id AS AssetId,
                    MIN(mc.claimed_at) OVER (PARTITION BY w.id, ma.library_id) AS CreatedAt,
                    ROW_NUMBER() OVER (
                        PARTITION BY w.id, ma.library_id
                        ORDER BY CASE WHEN w.media_type = 'Books' AND LOWER(ma.file_path_root) LIKE '%.epub' THEN 0 ELSE 1 END,
                                 CASE WHEN mc.claimed_at IS NULL THEN 1 ELSE 0 END, mc.claimed_at ASC, ma.id
                    ) AS AssetRank
                FROM works w
                INNER JOIN work_owned_assets woa ON woa.work_id = w.id
                INNER JOIN editions e ON e.id = woa.edition_id
                INNER JOIN media_assets ma ON ma.id = woa.asset_id
                LEFT JOIN asset_dates mc ON mc.entity_id = ma.id
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
                WHERE w.work_kind != 'parent'
                  AND ma.status = 'Normal' AND ma.is_orphaned = 0
                  AND {visibleWorkPredicate}
                  AND {visibleAssetPredicate}
                  {detailPredicate}
            ),
            canonical_artist_credits AS (
                SELECT
                    credit.media_asset_id,
                    person.id AS person_id,
                    person.name AS person_name,
                    ROW_NUMBER() OVER (
                        PARTITION BY credit.media_asset_id
                        ORDER BY
                            credit.billing_order,
                            CASE WHEN NULLIF(TRIM(person.local_headshot_path), '') IS NOT NULL THEN 0 ELSE 1 END,
                            CASE WHEN NULLIF(TRIM(person.headshot_url), '') IS NOT NULL THEN 0 ELSE 1 END,
                            CASE WHEN NULLIF(TRIM(person.biography), '') IS NOT NULL THEN 0 ELSE 1 END,
                            CASE WHEN person.enriched_at IS NOT NULL THEN 0 ELSE 1 END,
                            CASE WHEN NULLIF(TRIM(person.wikidata_qid), '') IS NOT NULL THEN 0 ELSE 1 END,
                            person.created_at,
                            person.id
                    ) AS identity_rank
                FROM projection_credits credit
                INNER JOIN persons person
                    ON person.name = credit.person_name COLLATE NOCASE
                    OR (
                        NULLIF(TRIM(credit.person_qid), '') IS NOT NULL
                        AND person.wikidata_qid = credit.person_qid COLLATE NOCASE
                    )
                WHERE credit.credit_key = 'artist'
            )
            SELECT
                LibraryId,
                WorkId,
                CollectionId,
                MediaType,
                WorkKind,
                RootWorkId,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('episode_still_url', 'still_url') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('episode_still_url', 'still_url') LIMIT 1)) AS EpisodeStillUrl,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('episode_still_url_s', 'still_url_s') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('episode_still_url_s', 'still_url_s') LIMIT 1)) AS EpisodeStillSmallUrl,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('episode_still_url_m', 'still_url_m') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('episode_still_url_m', 'still_url_m') LIMIT 1)) AS EpisodeStillMediumUrl,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('episode_still_url_l', 'still_url_l') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('episode_still_url_l', 'still_url_l') LIMIT 1)) AS EpisodeStillLargeUrl,

                EditionId,
                AssetId,
                CASE WHEN {IngestionAvailability.UpdatingWorkPredicate("WorkId")} THEN 1 ELSE 0 END AS IsUpdatingDetails,
                CASE WHEN EXISTS (SELECT 1 FROM identity_jobs ready_job
                    WHERE ready_job.entity_id = AssetId AND ready_job.state IN ('Ready','ReadyWithoutUniverse'))
                    AND NOT {IngestionAvailability.UpdatingWorkPredicate("WorkId")} THEN 1 ELSE 0 END AS IsIdentityReady,
                COALESCE(
                    NULLIF(TRIM((SELECT wikidata_qid FROM works WHERE id = WorkId LIMIT 1)), ''),
                    NULLIF(TRIM((SELECT value FROM canonical_values WHERE entity_id = WorkId AND key = 'wikidata_qid' LIMIT 1)), ''),
                    NULLIF(TRIM((SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'wikidata_qid' LIMIT 1)), ''),
                    NULLIF(TRIM((SELECT value FROM canonical_values WHERE entity_id = AssetId AND key = 'wikidata_qid' LIMIT 1)), '')
                ) AS IdentityQid,
                COALESCE(CreatedAt, CURRENT_TIMESTAMP) AS CreatedAt,
                COALESCE(NULLIF(TRIM(json_extract((SELECT display_overrides_json FROM works WHERE id = WorkId LIMIT 1), '$.title')), ''),
                         (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key = 'issue_title' LIMIT 1),
                         (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key = 'issue_title' LIMIT 1),
                         (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key = 'episode_title' LIMIT 1),
                         (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key = 'episode_title' LIMIT 1),
                         CASE WHEN MediaType = 'Books' THEN (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key = 'title' LIMIT 1) END,
                         (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key = 'title' LIMIT 1),
                         (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'title' LIMIT 1),
                         'Untitled') AS Title,
                NULLIF(TRIM(json_extract((SELECT display_overrides_json FROM works WHERE id = WorkId LIMIT 1), '$.sort_title')), '') AS SortTitle,
                COALESCE(
                    NULLIF(TRIM(json_extract((SELECT display_overrides_json FROM works WHERE id = WorkId LIMIT 1), '$.tagline')), ''),
                    (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = WorkId AND key = 'tagline' LIMIT 1),
                    (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = RootWorkId AND key = 'tagline' LIMIT 1),
                    (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = AssetId AND key = 'tagline' LIMIT 1)
                ) AS Tagline,
                COALESCE(
                    NULLIF(TRIM(json_extract((SELECT display_overrides_json FROM works WHERE id = WorkId LIMIT 1), '$.description')), ''),
                    (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = WorkId AND key IN ('episode_description', 'short_description') ORDER BY CASE key WHEN 'episode_description' THEN 0 ELSE 1 END LIMIT 1),
                    CASE WHEN MediaType <> 'TV' THEN (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = RootWorkId AND key = 'short_description' LIMIT 1) END,
                    (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = AssetId AND key IN ('episode_description', 'short_description') ORDER BY CASE key WHEN 'episode_description' THEN 0 ELSE 1 END LIMIT 1)
                ) AS Description,
                (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = RootWorkId AND key = 'short_description' LIMIT 1) AS RootDescription,
                COALESCE(
                    (SELECT group_concat(value, '; ') FROM (SELECT value FROM canonical_value_arrays WHERE entity_id = WorkId AND key IN ('author', 'creator', 'writer') ORDER BY ordinal)),
                    (SELECT group_concat(value, '; ') FROM (SELECT value FROM canonical_value_arrays WHERE entity_id = RootWorkId AND key IN ('author', 'creator', 'writer') ORDER BY ordinal)),
                    (SELECT group_concat(value, '; ') FROM (SELECT value FROM canonical_value_arrays WHERE entity_id = AssetId AND key IN ('author', 'creator', 'writer') ORDER BY ordinal))
                ) AS Author,
                COALESCE(
                    (SELECT group_concat(value, '; ') FROM (SELECT value FROM canonical_value_arrays WHERE entity_id = WorkId AND key IN ('artist', 'album_artist', 'performer') ORDER BY ordinal)),
                    (SELECT group_concat(value, '; ') FROM (SELECT value FROM canonical_value_arrays WHERE entity_id = RootWorkId AND key IN ('artist', 'album_artist', 'performer') ORDER BY ordinal)),
                    (SELECT group_concat(value, '; ') FROM (SELECT value FROM canonical_value_arrays WHERE entity_id = AssetId AND key IN ('artist', 'album_artist', 'performer') ORDER BY ordinal))
                ) AS Artist,
                (SELECT primary_credit.person_id
                 FROM canonical_artist_credits primary_credit
                 WHERE primary_credit.media_asset_id = AssetId
                   AND primary_credit.identity_rank = 1
                 LIMIT 1) AS ArtistPersonId,
                (SELECT primary_credit.person_name
                 FROM canonical_artist_credits primary_credit
                 WHERE primary_credit.media_asset_id = AssetId
                   AND primary_credit.identity_rank = 1
                 LIMIT 1) AS ArtistPersonName,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key = 'album' LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'album' LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key = 'album' LIMIT 1)
                ) AS Album,
                {displayYearSql} AS Year,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'series_end_year' LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key = 'series_end_year' LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key = 'series_end_year' LIMIT 1)
                ) AS SeriesEndYear,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('content_rating', 'certification') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('content_rating', 'certification') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('content_rating', 'certification') LIMIT 1)
                ) AS ContentRating,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key = 'runtime' LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'runtime' LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key = 'runtime' LIMIT 1)
                ) AS Runtime,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('duration', 'duration_sec', 'duration_seconds') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('duration', 'duration_sec', 'duration_seconds') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('duration', 'duration_sec', 'duration_seconds') LIMIT 1)
                ) AS Duration,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key = 'page_count' LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'page_count' LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key = 'page_count' LIMIT 1)
                ) AS PageCount,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'rating' LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key = 'rating' LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key = 'rating' LIMIT 1)
                ) AS Rating,
                COALESCE(
                    NULLIF(TRIM(json_extract((SELECT display_overrides_json FROM works WHERE id = WorkId LIMIT 1), '$.genre')), ''),
                    (SELECT group_concat(value, ';') FROM (SELECT value FROM canonical_value_arrays WHERE entity_id = WorkId AND key = 'genre' ORDER BY ordinal)),
                    (SELECT group_concat(value, ';') FROM (SELECT value FROM canonical_value_arrays WHERE entity_id = RootWorkId AND key = 'genre' ORDER BY ordinal)),
                    (SELECT group_concat(value, ';') FROM (SELECT value FROM canonical_value_arrays WHERE entity_id = AssetId AND key = 'genre' ORDER BY ordinal))
                ) AS Genre,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key = 'series' LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'series' LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key = 'series' LIMIT 1)
                ) AS Series,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key = 'issue_number' LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key = 'issue_number' LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key = 'series_position' LIMIT 1)
                ) AS SeriesPosition,
                (SELECT display_name FROM collections WHERE id = CollectionId LIMIT 1) AS CollectionTitle,
                (SELECT description FROM collections WHERE id = CollectionId LIMIT 1) AS CollectionDescription,
                (SELECT collection_type FROM collections WHERE id = CollectionId LIMIT 1) AS CollectionType,
                (SELECT group_by_field FROM collections WHERE id = CollectionId LIMIT 1) AS CollectionGroupByField,
                COALESCE(
                    (SELECT CAST(value AS INTEGER)
                     FROM canonical_values
                     WHERE entity_id = CollectionId
                       AND key = 'sequence_total'
                       AND CAST(value AS INTEGER) > 0
                       AND EXISTS (
                           SELECT 1
                           FROM canonical_values scope
                           WHERE scope.entity_id = CollectionId
                             AND scope.key = 'sequence_total_scope'
                             AND scope.value = 'MainSequence')
                     LIMIT 1),
                    (
                        SELECT MAX(COALESCE(
                            CAST(json_extract(api_metadata_json, '$.expectedTotal') AS INTEGER),
                            CAST(json_extract(api_metadata_json, '$.expected_total') AS INTEGER)))
                        FROM series_manifest_hydrations
                        WHERE collection_id = CollectionId
                          AND json_extract(api_metadata_json, '$.completeness') = 'Complete'
                    )
                ) AS CollectionManifestTotalCount,
                COALESCE(
                    (SELECT value FROM canonical_value_arrays WHERE entity_id = WorkId AND key = 'narrator' ORDER BY ordinal LIMIT 1),
                    (SELECT value FROM canonical_value_arrays WHERE entity_id = RootWorkId AND key = 'narrator' ORDER BY ordinal LIMIT 1),
                    (SELECT value FROM canonical_value_arrays WHERE entity_id = AssetId AND key = 'narrator' ORDER BY ordinal LIMIT 1)
                ) AS Narrator,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('publisher', 'imprint') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('publisher', 'imprint') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('publisher', 'imprint') LIMIT 1)
                ) AS Publisher,
                (SELECT group_concat(value, '; ')
                 FROM (
                    SELECT DISTINCT value
                    FROM (
                        SELECT value FROM canonical_value_arrays
                         WHERE entity_id IN (WorkId, RootWorkId, AssetId) AND key IN ('publisher', 'imprint')
                        UNION ALL
                        SELECT value FROM canonical_values
                         WHERE entity_id IN (WorkId, RootWorkId, AssetId) AND key IN ('publisher', 'imprint')
                        UNION ALL
                        SELECT editionPublisher.value
                          FROM editions edition
                          INNER JOIN media_assets editionAsset ON editionAsset.edition_id=edition.id
                          INNER JOIN canonical_value_arrays editionPublisher
                                  ON editionPublisher.entity_id=edition.id
                                 AND editionPublisher.key IN ('publisher', 'imprint')
                         WHERE edition.work_id=WorkId
                           AND editionAsset.library_id=LibraryId
                           AND editionAsset.status='Normal' AND editionAsset.is_orphaned=0
                           AND {visibleEditionAssetPredicate}
                        UNION ALL
                        SELECT editionPublisher.value
                          FROM editions edition
                          INNER JOIN media_assets editionAsset ON editionAsset.edition_id=edition.id
                          INNER JOIN canonical_values editionPublisher
                                  ON editionPublisher.entity_id=edition.id
                                 AND editionPublisher.key IN ('publisher', 'imprint')
                         WHERE edition.work_id=WorkId
                           AND editionAsset.library_id=LibraryId
                           AND editionAsset.status='Normal' AND editionAsset.is_orphaned=0
                           AND {visibleEditionAssetPredicate}
                        UNION ALL
                        SELECT assetPublisher.value
                          FROM editions edition
                          INNER JOIN media_assets editionAsset ON editionAsset.edition_id=edition.id
                          INNER JOIN canonical_value_arrays assetPublisher
                                  ON assetPublisher.entity_id=editionAsset.id
                                 AND assetPublisher.key IN ('publisher', 'imprint')
                         WHERE edition.work_id=WorkId
                           AND editionAsset.library_id=LibraryId
                           AND editionAsset.status='Normal' AND editionAsset.is_orphaned=0
                           AND {visibleEditionAssetPredicate}
                        UNION ALL
                        SELECT assetPublisher.value
                          FROM editions edition
                          INNER JOIN media_assets editionAsset ON editionAsset.edition_id=edition.id
                          INNER JOIN canonical_values assetPublisher
                                  ON assetPublisher.entity_id=editionAsset.id
                                 AND assetPublisher.key IN ('publisher', 'imprint')
                         WHERE edition.work_id=WorkId
                           AND editionAsset.library_id=LibraryId
                           AND editionAsset.status='Normal' AND editionAsset.is_orphaned=0
                           AND {visibleEditionAssetPredicate}
                    ) publisherValues
                    WHERE NULLIF(TRIM(value), '') IS NOT NULL
                    ORDER BY value COLLATE NOCASE
                 )) AS SearchPublisher,
                COALESCE(
                    (SELECT group_concat(value, '; ') FROM (SELECT value FROM canonical_value_arrays WHERE entity_id = WorkId AND key = 'director' ORDER BY ordinal)),
                    (SELECT group_concat(value, '; ') FROM (SELECT value FROM canonical_value_arrays WHERE entity_id = RootWorkId AND key = 'director' ORDER BY ordinal)),
                    (SELECT group_concat(value, '; ') FROM (SELECT value FROM canonical_value_arrays WHERE entity_id = AssetId AND key = 'director' ORDER BY ordinal))
                ) AS Director,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('network', 'studio', 'broadcaster', 'streaming_service', 'platform') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('network', 'studio', 'broadcaster', 'streaming_service', 'platform') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('network', 'studio', 'broadcaster', 'streaming_service', 'platform') LIMIT 1)
                ) AS Network,
                COALESCE(
                    (SELECT group_concat(value, '; ') FROM (SELECT value FROM canonical_value_arrays WHERE entity_id = WorkId AND key IN ('network', 'studio', 'broadcaster', 'streaming_service', 'platform') ORDER BY ordinal)),
                    (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('network', 'studio', 'broadcaster', 'streaming_service', 'platform') LIMIT 1),
                    (SELECT group_concat(value, '; ') FROM (SELECT value FROM canonical_value_arrays WHERE entity_id = EditionId AND key IN ('network', 'studio', 'broadcaster', 'streaming_service', 'platform') ORDER BY ordinal)),
                    (SELECT value FROM canonical_values WHERE entity_id = EditionId AND key IN ('network', 'studio', 'broadcaster', 'streaming_service', 'platform') LIMIT 1),
                    (SELECT group_concat(value, '; ') FROM (SELECT value FROM canonical_value_arrays WHERE entity_id = AssetId AND key IN ('network', 'studio', 'broadcaster', 'streaming_service', 'platform') ORDER BY ordinal)),
                    (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('network', 'studio', 'broadcaster', 'streaming_service', 'platform') LIMIT 1),
                    (SELECT group_concat(value, '; ') FROM (SELECT value FROM canonical_value_arrays WHERE entity_id = RootWorkId AND key IN ('network', 'studio', 'broadcaster', 'streaming_service', 'platform') ORDER BY ordinal)),
                    (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('network', 'studio', 'broadcaster', 'streaming_service', 'platform') LIMIT 1)
                ) AS SearchNetwork,
                COALESCE(
                    (SELECT group_concat(value, '; ') FROM (SELECT value FROM canonical_value_arrays WHERE entity_id = WorkId AND key = 'country_of_origin' ORDER BY ordinal)),
                    (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key = 'country_of_origin' LIMIT 1),
                    (SELECT group_concat(value, '; ') FROM (SELECT value FROM canonical_value_arrays WHERE entity_id = EditionId AND key = 'country_of_origin' ORDER BY ordinal)),
                    (SELECT value FROM canonical_values WHERE entity_id = EditionId AND key = 'country_of_origin' LIMIT 1),
                    (SELECT group_concat(value, '; ') FROM (SELECT value FROM canonical_value_arrays WHERE entity_id = AssetId AND key = 'country_of_origin' ORDER BY ordinal)),
                    (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key = 'country_of_origin' LIMIT 1),
                    (SELECT group_concat(value, '; ') FROM (SELECT value FROM canonical_value_arrays WHERE entity_id = RootWorkId AND key = 'country_of_origin' ORDER BY ordinal)),
                    (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'country_of_origin' LIMIT 1)
                ) AS CountryOfOrigin,
                COALESCE(
                    (SELECT group_concat(value, '; ') FROM (SELECT value FROM canonical_value_arrays WHERE entity_id = WorkId AND key = 'franchise' ORDER BY ordinal)),
                    (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key = 'franchise' LIMIT 1),
                    (SELECT group_concat(value, '; ') FROM (SELECT value FROM canonical_value_arrays WHERE entity_id = EditionId AND key = 'franchise' ORDER BY ordinal)),
                    (SELECT value FROM canonical_values WHERE entity_id = EditionId AND key = 'franchise' LIMIT 1),
                    (SELECT group_concat(value, '; ') FROM (SELECT value FROM canonical_value_arrays WHERE entity_id = AssetId AND key = 'franchise' ORDER BY ordinal)),
                    (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key = 'franchise' LIMIT 1),
                    (SELECT group_concat(value, '; ') FROM (SELECT value FROM canonical_value_arrays WHERE entity_id = RootWorkId AND key = 'franchise' ORDER BY ordinal)),
                    (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'franchise' LIMIT 1)
                ) AS Franchise,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('source_service', 'source_platform') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('source_service', 'source_platform') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('source_service', 'source_platform') LIMIT 1)
                ) AS Source,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('quality', 'video_quality', 'resolution', 'video_resolution', 'video_resolution_label') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('quality', 'video_quality', 'resolution', 'video_resolution', 'video_resolution_label') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('quality', 'video_quality', 'resolution', 'video_resolution', 'video_resolution_label') LIMIT 1)
                ) AS Quality,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'show_name' LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'title' LIMIT 1)
                ) AS ShowName,
                (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key = 'season_number' LIMIT 1) AS SeasonNumber,
                (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key = 'episode_number' LIMIT 1) AS EpisodeNumber,
                (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key = 'track_number' LIMIT 1) AS TrackNumber,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('cover_url', 'cover', 'poster_url', 'poster', 'episode_still_url', 'episode_still', 'still_url', 'still') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('cover_url', 'cover', 'poster_url', 'poster', 'episode_still_url', 'episode_still', 'still_url', 'still') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('cover_url', 'cover', 'poster_url', 'poster', 'episode_still_url', 'episode_still', 'still_url', 'still') LIMIT 1)
                ) AS CoverUrl,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('cover_url_s', 'poster_url_s', 'episode_still_url_s', 'still_url_s') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('cover_url_s', 'poster_url_s', 'episode_still_url_s', 'still_url_s') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('cover_url_s', 'poster_url_s', 'episode_still_url_s', 'still_url_s') LIMIT 1)) AS CoverSmallUrl,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('cover_url_m', 'poster_url_m', 'episode_still_url_m', 'still_url_m') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('cover_url_m', 'poster_url_m', 'episode_still_url_m', 'still_url_m') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('cover_url_m', 'poster_url_m', 'episode_still_url_m', 'still_url_m') LIMIT 1)) AS CoverMediumUrl,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('cover_url_l', 'poster_url_l', 'episode_still_url_l', 'still_url_l') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('cover_url_l', 'poster_url_l', 'episode_still_url_l', 'still_url_l') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('cover_url_l', 'poster_url_l', 'episode_still_url_l', 'still_url_l') LIMIT 1)) AS CoverLargeUrl,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('square_url', 'square') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('square_url', 'square') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('square_url', 'square') LIMIT 1)
                ) AS SquareUrl,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = AssetId AND key = 'square_url_s' LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key = 'square_url_s' LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'square_url_s' LIMIT 1)) AS SquareSmallUrl,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = AssetId AND key = 'square_url_m' LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key = 'square_url_m' LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'square_url_m' LIMIT 1)) AS SquareMediumUrl,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = AssetId AND key = 'square_url_l' LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key = 'square_url_l' LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'square_url_l' LIMIT 1)) AS SquareLargeUrl,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('banner_url', 'banner', 'episode_still_url', 'episode_still', 'still_url', 'still') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('banner_url', 'banner', 'episode_still_url', 'episode_still', 'still_url', 'still') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('banner_url', 'banner', 'episode_still_url', 'episode_still', 'still_url', 'still') LIMIT 1)
                ) AS BannerUrl,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('banner_url_s', 'episode_still_url_s', 'still_url_s') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('banner_url_s', 'episode_still_url_s', 'still_url_s') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('banner_url_s', 'episode_still_url_s', 'still_url_s') LIMIT 1)) AS BannerSmallUrl,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('banner_url_m', 'episode_still_url_m', 'still_url_m') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('banner_url_m', 'episode_still_url_m', 'still_url_m') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('banner_url_m', 'episode_still_url_m', 'still_url_m') LIMIT 1)) AS BannerMediumUrl,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('banner_url_l', 'episode_still_url_l', 'still_url_l') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('banner_url_l', 'episode_still_url_l', 'still_url_l') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('banner_url_l', 'episode_still_url_l', 'still_url_l') LIMIT 1)) AS BannerLargeUrl,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('background_url', 'background', 'episode_still_url', 'episode_still', 'still_url', 'still') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('background_url', 'background', 'episode_still_url', 'episode_still', 'still_url', 'still') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('background_url', 'background', 'episode_still_url', 'episode_still', 'still_url', 'still') LIMIT 1)
                ) AS BackgroundUrl,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('background_url_s', 'episode_still_url_s', 'still_url_s') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('background_url_s', 'episode_still_url_s', 'still_url_s') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('background_url_s', 'episode_still_url_s', 'still_url_s') LIMIT 1)) AS BackgroundSmallUrl,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('background_url_m', 'episode_still_url_m', 'still_url_m') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('background_url_m', 'episode_still_url_m', 'still_url_m') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('background_url_m', 'episode_still_url_m', 'still_url_m') LIMIT 1)) AS BackgroundMediumUrl,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('background_url_l', 'episode_still_url_l', 'still_url_l') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('background_url_l', 'episode_still_url_l', 'still_url_l') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('background_url_l', 'episode_still_url_l', 'still_url_l') LIMIT 1)) AS BackgroundLargeUrl,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('logo_url', 'logo') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('logo_url', 'logo') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('logo_url', 'logo') LIMIT 1)
                ) AS LogoUrl,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = AssetId AND key = 'cover_state' LIMIT 1),
                         (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key = 'cover_state' LIMIT 1),
                         (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'cover_state' LIMIT 1)) AS CoverState,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = AssetId AND key = 'square_state' LIMIT 1),
                         (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key = 'square_state' LIMIT 1),
                         (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'square_state' LIMIT 1)) AS SquareState,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = AssetId AND key = 'banner_state' LIMIT 1),
                         (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key = 'banner_state' LIMIT 1),
                         (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'banner_state' LIMIT 1)) AS BannerState,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = AssetId AND key = 'background_state' LIMIT 1),
                         (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key = 'background_state' LIMIT 1),
                         (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'background_state' LIMIT 1)) AS BackgroundState,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = AssetId AND key = 'logo_state' LIMIT 1),
                         (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key = 'logo_state' LIMIT 1),
                         (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'logo_state' LIMIT 1)) AS LogoState,
                (SELECT value FROM canonical_values WHERE entity_id = CollectionId AND key IN ('cover_url', 'cover', 'poster_url', 'poster') LIMIT 1) AS CollectionCoverUrl,
                (SELECT value FROM canonical_values WHERE entity_id = CollectionId AND key IN ('square_url', 'square') LIMIT 1) AS CollectionSquareUrl,
                (SELECT value FROM canonical_values WHERE entity_id = CollectionId AND key IN ('banner_url', 'banner') LIMIT 1) AS CollectionBannerUrl,
                (SELECT value FROM canonical_values WHERE entity_id = CollectionId AND key IN ('background_url', 'background', 'hero_url', 'hero') LIMIT 1) AS CollectionBackgroundUrl,
                (SELECT value FROM canonical_values WHERE entity_id = CollectionId AND key IN ('logo_url', 'logo') LIMIT 1) AS CollectionLogoUrl,
                (SELECT value FROM canonical_values WHERE entity_id = CollectionId AND key = 'artwork_accent_hex' LIMIT 1) AS CollectionAccentColor,
                (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('cover_url', 'cover', 'poster_url', 'poster') LIMIT 1) AS RootCoverUrl,
                (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('cover_url_s', 'poster_url_s') LIMIT 1) AS RootCoverSmallUrl,
                (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('cover_url_m', 'poster_url_m') LIMIT 1) AS RootCoverMediumUrl,
                (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('cover_url_l', 'poster_url_l') LIMIT 1) AS RootCoverLargeUrl,
                (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('square_url', 'square') LIMIT 1) AS RootSquareUrl,
                (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'square_url_s' LIMIT 1) AS RootSquareSmallUrl,
                (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'square_url_m' LIMIT 1) AS RootSquareMediumUrl,
                (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'square_url_l' LIMIT 1) AS RootSquareLargeUrl,
                (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('banner_url', 'banner') LIMIT 1) AS RootBannerUrl,
                (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'banner_url_s' LIMIT 1) AS RootBannerSmallUrl,
                (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'banner_url_m' LIMIT 1) AS RootBannerMediumUrl,
                (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'banner_url_l' LIMIT 1) AS RootBannerLargeUrl,
                (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('background_url', 'background', 'hero_url', 'hero') LIMIT 1) AS RootBackgroundUrl,
                (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('background_url_s', 'hero_url_s') LIMIT 1) AS RootBackgroundSmallUrl,
                (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('background_url_m', 'hero_url_m') LIMIT 1) AS RootBackgroundMediumUrl,
                (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('background_url_l', 'hero_url_l') LIMIT 1) AS RootBackgroundLargeUrl,
                (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('logo_url', 'logo') LIMIT 1) AS RootLogoUrl,
                (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'cover_state' LIMIT 1) AS RootCoverState,
                (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'square_state' LIMIT 1) AS RootSquareState,
                (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'banner_state' LIMIT 1) AS RootBannerState,
                (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'background_state' LIMIT 1) AS RootBackgroundState,
                (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'logo_state' LIMIT 1) AS RootLogoState,
                (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'cover_width_px' LIMIT 1) AS RootCoverWidthPx,
                (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'cover_height_px' LIMIT 1) AS RootCoverHeightPx,
                (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'square_width_px' LIMIT 1) AS RootSquareWidthPx,
                (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'square_height_px' LIMIT 1) AS RootSquareHeightPx,
                (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'banner_width_px' LIMIT 1) AS RootBannerWidthPx,
                (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'banner_height_px' LIMIT 1) AS RootBannerHeightPx,
                (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'background_width_px' LIMIT 1) AS RootBackgroundWidthPx,
                (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'background_height_px' LIMIT 1) AS RootBackgroundHeightPx,
                (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'artwork_accent_hex' LIMIT 1) AS RootAccentColor,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('cover_width_px', 'episode_still_width_px') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('cover_width_px', 'episode_still_width_px') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('cover_width_px', 'episode_still_width_px') LIMIT 1)) AS CoverWidthPx,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('cover_height_px', 'episode_still_height_px') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('cover_height_px', 'episode_still_height_px') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('cover_height_px', 'episode_still_height_px') LIMIT 1)) AS CoverHeightPx,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = AssetId AND key = 'square_width_px' LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key = 'square_width_px' LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'square_width_px' LIMIT 1)) AS SquareWidthPx,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = AssetId AND key = 'square_height_px' LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key = 'square_height_px' LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'square_height_px' LIMIT 1)) AS SquareHeightPx,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('banner_width_px', 'episode_still_width_px') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('banner_width_px', 'episode_still_width_px') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('banner_width_px', 'episode_still_width_px') LIMIT 1)) AS BannerWidthPx,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('banner_height_px', 'episode_still_height_px') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('banner_height_px', 'episode_still_height_px') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('banner_height_px', 'episode_still_height_px') LIMIT 1)) AS BannerHeightPx,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('background_width_px', 'episode_still_width_px') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('background_width_px', 'episode_still_width_px') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('background_width_px', 'episode_still_width_px') LIMIT 1)) AS BackgroundWidthPx,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('background_height_px', 'episode_still_height_px') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('background_height_px', 'episode_still_height_px') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('background_height_px', 'episode_still_height_px') LIMIT 1)) AS BackgroundHeightPx,
                (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'artwork_accent_hex' LIMIT 1) AS AccentColor
            FROM ranked_assets
            WHERE AssetRank = 1
            ORDER BY CreatedAt DESC
            LIMIT @limit;
            """;

        var rows = (await conn.QueryAsync<DisplayWorkRow>(new CommandDefinition(sql, new { limit, detailId }, cancellationToken: ct))).ToList();
        var pseudonymNames = conn.Query<string>(new CommandDefinition(
                "SELECT name FROM persons WHERE is_pseudonym = 1 AND NULLIF(TRIM(name), '') IS NOT NULL;",
                cancellationToken: ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            row.Author = NormalizeContributorList(row.Author, pseudonymNames, preferCollectivePseudonym: true);
            row.Artist = NormalizeContributorList(row.Artist, pseudonymNames, preferCollectivePseudonym: false);
            row.Director = NormalizeContributorList(row.Director, pseudonymNames, preferCollectivePseudonym: false);
            row.CoverUrl = DisplayArtworkUrlResolver.Resolve(row.CoverUrl, row.AssetId, "cover", row.CoverState);
            row.SquareUrl = DisplayArtworkUrlResolver.Resolve(row.SquareUrl, row.AssetId, "square", row.SquareState);
            row.BannerUrl = DisplayArtworkUrlResolver.Resolve(row.BannerUrl, row.AssetId, "banner", row.BannerState);
            row.BackgroundUrl = DisplayArtworkUrlResolver.Resolve(row.BackgroundUrl, row.AssetId, "background", row.BackgroundState);
            row.LogoUrl = DisplayArtworkUrlResolver.Resolve(row.LogoUrl, row.AssetId, "logo", row.LogoState);
            row.RootCoverUrl = DisplayArtworkUrlResolver.Resolve(row.RootCoverUrl, row.AssetId, "cover", row.RootCoverState);
            row.RootSquareUrl = DisplayArtworkUrlResolver.Resolve(row.RootSquareUrl, row.AssetId, "square", row.RootSquareState);
            row.RootBannerUrl = DisplayArtworkUrlResolver.Resolve(row.RootBannerUrl, row.AssetId, "banner", row.RootBannerState);
            row.RootBackgroundUrl = DisplayArtworkUrlResolver.Resolve(row.RootBackgroundUrl, row.AssetId, "background", row.RootBackgroundState);
            row.RootLogoUrl = DisplayArtworkUrlResolver.Resolve(row.RootLogoUrl, row.AssetId, "logo", row.RootLogoState);
        }

        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        PerformanceMetrics.DatabaseReadDurationMs.Record(
            elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("operation", "display.works"));
        if (elapsed >= TimeSpan.FromSeconds(1))
        {
            _logger?.LogWarning(
                "Database read {Operation} took {ElapsedMs} ms with limit {Limit}, returned {ItemCount}",
                "display.works",
                elapsed.TotalMilliseconds,
                limit,
                rows.Count);
        }
        else
        {
            _logger?.LogDebug(
                "Database read {Operation} took {ElapsedMs} ms with limit {Limit}, returned {ItemCount}",
                "display.works",
                elapsed.TotalMilliseconds,
                limit,
                rows.Count);
        }

        return rows;
    }

    private static string? NormalizeContributorList(
        string? value,
        IReadOnlySet<string> pseudonymNames,
        bool preferCollectivePseudonym)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var names = value
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (preferCollectivePseudonym)
        {
            var pseudonyms = names.Where(pseudonymNames.Contains).ToList();
            if (pseudonyms.Count > 0)
            {
                names = pseudonyms;
            }
        }

        return string.Join("; ", names);
    }
}
