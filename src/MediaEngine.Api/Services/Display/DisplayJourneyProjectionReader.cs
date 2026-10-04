using Dapper;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Display;

public sealed class DisplayJourneyProjectionReader
{
    private readonly IDatabaseConnection _db;

    public DisplayJourneyProjectionReader(IDatabaseConnection db)
    {
        _db = db;
    }

    public Task<IReadOnlyList<DisplayJourneyRow>> LoadAsync(Guid? profileId, string? lane, CancellationToken ct, bool includeCompleted = false)
    {
        ct.ThrowIfCancellationRequested();
        if (profileId is null || profileId == Guid.Empty) return Task.FromResult<IReadOnlyList<DisplayJourneyRow>>([]);
        using var conn = _db.CreateConnection();
        var visibleWorkPredicate = HomeVisibilitySql.VisibleWorkPredicate("w.id", "w.curator_state", "w.is_catalog_only");
        var visibleAssetPredicate = HomeVisibilitySql.VisibleAssetPathPredicate("ma.file_path_root");
        var displayYearSql = MediaDateSql.DisplayOriginalYear(
            "w.id",
            "COALESCE(gpw.id, pw.id, w.id)",
            "ma.id",
            "w.media_type");
        var sql = $"""
            SELECT
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = ma.id AND key IN ('episode_still_url', 'still_url') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = w.id AND key IN ('episode_still_url', 'still_url') LIMIT 1)) AS EpisodeStillUrl,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = ma.id AND key IN ('episode_still_url_s', 'still_url_s') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = w.id AND key IN ('episode_still_url_s', 'still_url_s') LIMIT 1)) AS EpisodeStillSmallUrl,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = ma.id AND key IN ('episode_still_url_m', 'still_url_m') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = w.id AND key IN ('episode_still_url_m', 'still_url_m') LIMIT 1)) AS EpisodeStillMediumUrl,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = ma.id AND key IN ('episode_still_url_l', 'still_url_l') LIMIT 1), (SELECT value FROM canonical_values WHERE entity_id = w.id AND key IN ('episode_still_url_l', 'still_url_l') LIMIT 1)) AS EpisodeStillLargeUrl,
                us.asset_id AS AssetId,
                ma.library_id AS LibraryId,
                us.user_id AS ProfileId,
                w.id AS WorkId,
                CASE
                    WHEN w.media_type = 'Music' THEN COALESCE(pw.id, w.id)
                    ELSE COALESCE(gpw.id, pw.id, w.id)
                END AS RootWorkId,
                w.collection_id AS CollectionId,
                w.media_type AS MediaType,
                us.progress_pct AS ProgressPct,
                us.last_accessed AS LastAccessed,
                COALESCE(cv_issue_title_a.value, cv_issue_title_w.value, cv_title_a.value, cv_title_w.value, 'Untitled') AS Title,
                COALESCE(
                    NULLIF(TRIM(json_extract(w.display_overrides_json, '$.tagline')), ''),
                    (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = w.id AND key = 'tagline' LIMIT 1),
                    (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key = 'tagline' LIMIT 1),
                    (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = ma.id AND key = 'tagline' LIMIT 1)
                ) AS Tagline,
                COALESCE(
                    (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = w.id AND key IN ('episode_description', 'short_description') ORDER BY CASE key WHEN 'episode_description' THEN 0 ELSE 1 END LIMIT 1),
                    CASE WHEN w.media_type <> 'TV' THEN (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key = 'short_description' LIMIT 1) END,
                    (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = ma.id AND key IN ('episode_description', 'short_description') ORDER BY CASE key WHEN 'episode_description' THEN 0 ELSE 1 END LIMIT 1)
                ) AS Description,
                COALESCE(
                    (SELECT value FROM canonical_value_arrays WHERE entity_id = w.id AND key IN ('author', 'creator') ORDER BY ordinal LIMIT 1),
                    (SELECT value FROM canonical_value_arrays WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key IN ('author', 'creator') ORDER BY ordinal LIMIT 1),
                    (SELECT value FROM canonical_value_arrays WHERE entity_id = ma.id AND key IN ('author', 'creator') ORDER BY ordinal LIMIT 1)
                ) AS Author,
                COALESCE(
                    (SELECT value FROM canonical_value_arrays WHERE entity_id = w.id AND key IN ('artist', 'album_artist', 'performer') ORDER BY ordinal LIMIT 1),
                    (SELECT value FROM canonical_value_arrays WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key IN ('artist', 'album_artist', 'performer') ORDER BY ordinal LIMIT 1),
                    (SELECT value FROM canonical_value_arrays WHERE entity_id = ma.id AND key IN ('artist', 'album_artist', 'performer') ORDER BY ordinal LIMIT 1)
                ) AS Artist,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = w.id AND key = 'album' LIMIT 1),
                    cv_album_w.value,
                    (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key = 'album' LIMIT 1)
                ) AS Album,
                {displayYearSql} AS Year,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = w.id AND key IN ('content_rating', 'certification') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key IN ('content_rating', 'certification') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key IN ('content_rating', 'certification') LIMIT 1)
                ) AS ContentRating,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = w.id AND key = 'runtime' LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key = 'runtime' LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key = 'runtime' LIMIT 1)
                ) AS Runtime,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = w.id AND key IN ('duration', 'duration_sec', 'duration_seconds') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key IN ('duration', 'duration_sec', 'duration_seconds') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key IN ('duration', 'duration_sec', 'duration_seconds') LIMIT 1)
                ) AS Duration,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = w.id AND key = 'page_count' LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key = 'page_count' LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key = 'page_count' LIMIT 1)
                ) AS PageCount,
                COALESCE(cv_rating_w.value, cv_rating_item.value, cv_rating_a.value) AS Rating,
                COALESCE(
                    NULLIF(TRIM(json_extract(w.display_overrides_json, '$.genre')), ''),
                    (SELECT group_concat(value, ';') FROM (SELECT value FROM canonical_value_arrays WHERE entity_id = w.id AND key = 'genre' ORDER BY ordinal)),
                    (SELECT group_concat(value, ';') FROM (SELECT value FROM canonical_value_arrays WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key = 'genre' ORDER BY ordinal)),
                    (SELECT group_concat(value, ';') FROM (SELECT value FROM canonical_value_arrays WHERE entity_id = ma.id AND key = 'genre' ORDER BY ordinal))
                ) AS Genre,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = w.id AND key = 'series' LIMIT 1),
                    cv_series_w.value,
                    (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key = 'series' LIMIT 1)
                ) AS Series,
                COALESCE(cv_issue_a.value, cv_issue_w.value, cv_series_position_a.value) AS SeriesPosition,
                cv_show_w.value AS ShowName,
                COALESCE(
                    (SELECT value FROM canonical_value_arrays WHERE entity_id = w.id AND key = 'narrator' ORDER BY ordinal LIMIT 1),
                    (SELECT value FROM canonical_value_arrays WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key = 'narrator' ORDER BY ordinal LIMIT 1),
                    (SELECT value FROM canonical_value_arrays WHERE entity_id = ma.id AND key = 'narrator' ORDER BY ordinal LIMIT 1)
                ) AS Narrator,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key IN ('network', 'studio', 'broadcaster', 'streaming_service', 'platform') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = w.id AND key IN ('network', 'studio', 'broadcaster', 'streaming_service', 'platform') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key IN ('network', 'studio', 'broadcaster', 'streaming_service', 'platform') LIMIT 1)
                ) AS Network,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key IN ('source_service', 'source_platform') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = w.id AND key IN ('source_service', 'source_platform') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key IN ('source_service', 'source_platform') LIMIT 1)
                ) AS Source,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key IN ('quality', 'video_quality', 'resolution', 'video_resolution', 'video_resolution_label') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = w.id AND key IN ('quality', 'video_quality', 'resolution', 'video_resolution', 'video_resolution_label') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key IN ('quality', 'video_quality', 'resolution', 'video_resolution', 'video_resolution_label') LIMIT 1)
                ) AS Quality,
                CASE WHEN w.media_type = 'TV' THEN cv_cover_w.value
                    ELSE COALESCE(cv_cover_a.value, cv_cover_item.value, cv_cover_w.value) END AS CoverUrl,
                CASE WHEN w.media_type = 'TV' THEN
                    (SELECT value FROM canonical_values WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key IN ('cover_url_s', 'poster_url_s') LIMIT 1)
                ELSE COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key IN ('cover_url_s', 'poster_url_s') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = w.id AND key IN ('cover_url_s', 'poster_url_s') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key IN ('cover_url_s', 'poster_url_s') LIMIT 1)
                ) END AS CoverSmallUrl,
                CASE WHEN w.media_type = 'TV' THEN
                    (SELECT value FROM canonical_values WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key IN ('cover_url_m', 'poster_url_m') LIMIT 1)
                ELSE COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key IN ('cover_url_m', 'poster_url_m') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = w.id AND key IN ('cover_url_m', 'poster_url_m') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key IN ('cover_url_m', 'poster_url_m') LIMIT 1)
                ) END AS CoverMediumUrl,
                CASE WHEN w.media_type = 'TV' THEN
                    (SELECT value FROM canonical_values WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key IN ('cover_url_l', 'poster_url_l') LIMIT 1)
                ELSE COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key IN ('cover_url_l', 'poster_url_l') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = w.id AND key IN ('cover_url_l', 'poster_url_l') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key IN ('cover_url_l', 'poster_url_l') LIMIT 1)
                ) END AS CoverLargeUrl,
                COALESCE(cv_square_a.value, cv_square_item.value, cv_square_w.value) AS SquareUrl,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key = 'square_url_s' LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = w.id AND key = 'square_url_s' LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key = 'square_url_s' LIMIT 1)
                ) AS SquareSmallUrl,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key = 'square_url_m' LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = w.id AND key = 'square_url_m' LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key = 'square_url_m' LIMIT 1)
                ) AS SquareMediumUrl,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key = 'square_url_l' LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = w.id AND key = 'square_url_l' LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key = 'square_url_l' LIMIT 1)
                ) AS SquareLargeUrl,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key IN ('episode_still_url', 'episode_still', 'still_url', 'still') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = w.id AND key IN ('episode_still_url', 'episode_still', 'still_url', 'still') LIMIT 1),
                    cv_background_a.value, cv_background_item.value, cv_background_w.value) AS BackgroundUrl,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key IN ('episode_still_url_s', 'still_url_s') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = w.id AND key IN ('episode_still_url_s', 'still_url_s') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key IN ('background_url_s', 'episode_still_url_s', 'still_url_s') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = w.id AND key IN ('background_url_s', 'episode_still_url_s', 'still_url_s') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key IN ('background_url_s', 'episode_still_url_s', 'still_url_s') LIMIT 1)
                ) AS BackgroundSmallUrl,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key IN ('episode_still_url_m', 'still_url_m') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = w.id AND key IN ('episode_still_url_m', 'still_url_m') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key IN ('background_url_m', 'episode_still_url_m', 'still_url_m') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = w.id AND key IN ('background_url_m', 'episode_still_url_m', 'still_url_m') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key IN ('background_url_m', 'episode_still_url_m', 'still_url_m') LIMIT 1)
                ) AS BackgroundMediumUrl,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key IN ('episode_still_url_l', 'still_url_l') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = w.id AND key IN ('episode_still_url_l', 'still_url_l') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key IN ('background_url_l', 'episode_still_url_l', 'still_url_l') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = w.id AND key IN ('background_url_l', 'episode_still_url_l', 'still_url_l') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key IN ('background_url_l', 'episode_still_url_l', 'still_url_l') LIMIT 1)
                ) AS BackgroundLargeUrl,
                COALESCE(cv_banner_a.value, cv_banner_item.value, cv_banner_w.value) AS BannerUrl,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key IN ('banner_url_s', 'episode_still_url_s', 'still_url_s') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = w.id AND key IN ('banner_url_s', 'episode_still_url_s', 'still_url_s') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key IN ('banner_url_s', 'episode_still_url_s', 'still_url_s') LIMIT 1)
                ) AS BannerSmallUrl,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key IN ('banner_url_m', 'episode_still_url_m', 'still_url_m') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = w.id AND key IN ('banner_url_m', 'episode_still_url_m', 'still_url_m') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key IN ('banner_url_m', 'episode_still_url_m', 'still_url_m') LIMIT 1)
                ) AS BannerMediumUrl,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key IN ('banner_url_l', 'episode_still_url_l', 'still_url_l') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = w.id AND key IN ('banner_url_l', 'episode_still_url_l', 'still_url_l') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = COALESCE(gpw.id, pw.id, w.id) AND key IN ('banner_url_l', 'episode_still_url_l', 'still_url_l') LIMIT 1)
                ) AS BannerLargeUrl,
                COALESCE(cv_logo_a.value, cv_logo_item.value, cv_logo_w.value) AS LogoUrl,
                CASE WHEN w.media_type = 'TV' THEN cv_cover_state_w.value
                    ELSE COALESCE(cv_cover_state_a.value, cv_cover_state_item.value, cv_cover_state_w.value) END AS CoverState,
                COALESCE(cv_square_state_a.value, cv_square_state_item.value, cv_square_state_w.value) AS SquareState,
                COALESCE(cv_background_state_a.value, cv_background_state_item.value, cv_background_state_w.value) AS BackgroundState,
                COALESCE(cv_banner_state_a.value, cv_banner_state_item.value, cv_banner_state_w.value) AS BannerState,
                COALESCE(cv_logo_state_a.value, cv_logo_state_item.value, cv_logo_state_w.value) AS LogoState,
                cv_season_a.value AS SeasonNumber,
                cv_episode_a.value AS EpisodeNumber,
                cv_track_a.value AS TrackNumber,
                NULL AS CoverWidthPx,
                NULL AS CoverHeightPx,
                NULL AS SquareWidthPx,
                NULL AS SquareHeightPx,
                NULL AS BannerWidthPx,
                NULL AS BannerHeightPx,
                NULL AS BackgroundWidthPx,
                NULL AS BackgroundHeightPx,
                cv_accent_w.value AS AccentColor,
                us.extended_properties AS SavedPlaybackTiming
            FROM user_states us
            JOIN media_assets ma ON ma.id = us.asset_id
            JOIN editions e ON e.id = ma.edition_id
            JOIN works w ON w.id = e.work_id
            LEFT JOIN works pw ON pw.id = w.parent_work_id
            LEFT JOIN works gpw ON gpw.id = pw.parent_work_id
            LEFT JOIN canonical_values cv_title_a ON cv_title_a.entity_id = ma.id AND cv_title_a.key = 'title'
            LEFT JOIN canonical_values cv_title_w ON cv_title_w.entity_id = COALESCE(gpw.id, pw.id, w.id) AND cv_title_w.key = 'title'
            LEFT JOIN canonical_values cv_issue_title_a ON cv_issue_title_a.entity_id = ma.id AND cv_issue_title_a.key = 'issue_title'
            LEFT JOIN canonical_values cv_issue_title_w ON cv_issue_title_w.entity_id = w.id AND cv_issue_title_w.key = 'issue_title'
            LEFT JOIN canonical_values cv_album_w ON cv_album_w.entity_id = COALESCE(gpw.id, pw.id, w.id) AND cv_album_w.key = 'album'
            LEFT JOIN canonical_values cv_rating_w ON cv_rating_w.entity_id = COALESCE(gpw.id, pw.id, w.id) AND cv_rating_w.key = 'rating'
            LEFT JOIN canonical_values cv_rating_item ON cv_rating_item.entity_id = w.id AND cv_rating_item.key = 'rating'
            LEFT JOIN canonical_values cv_rating_a ON cv_rating_a.entity_id = ma.id AND cv_rating_a.key = 'rating'
            LEFT JOIN canonical_values cv_series_w ON cv_series_w.entity_id = COALESCE(gpw.id, pw.id, w.id) AND cv_series_w.key = 'series'
            LEFT JOIN canonical_values cv_series_position_a ON cv_series_position_a.entity_id = ma.id AND cv_series_position_a.key = 'series_position'
            LEFT JOIN canonical_values cv_issue_a ON cv_issue_a.entity_id = ma.id AND cv_issue_a.key = 'issue_number'
            LEFT JOIN canonical_values cv_issue_w ON cv_issue_w.entity_id = w.id AND cv_issue_w.key = 'issue_number'
            LEFT JOIN canonical_values cv_show_w ON cv_show_w.entity_id = COALESCE(gpw.id, pw.id, w.id) AND cv_show_w.key = 'show_name'
            LEFT JOIN canonical_values cv_cover_a ON cv_cover_a.entity_id = ma.id AND cv_cover_a.key IN ('cover_url', 'cover', 'poster_url', 'poster')
            LEFT JOIN canonical_values cv_square_a ON cv_square_a.entity_id = ma.id AND cv_square_a.key IN ('square_url', 'square')
            LEFT JOIN canonical_values cv_background_a ON cv_background_a.entity_id = ma.id AND cv_background_a.key IN ('background_url', 'background', 'episode_still_url', 'episode_still', 'still_url', 'still')
            LEFT JOIN canonical_values cv_banner_a ON cv_banner_a.entity_id = ma.id AND cv_banner_a.key IN ('banner_url', 'banner', 'episode_still_url', 'episode_still', 'still_url', 'still')
            LEFT JOIN canonical_values cv_logo_a ON cv_logo_a.entity_id = ma.id AND cv_logo_a.key IN ('logo_url', 'logo')
            LEFT JOIN canonical_values cv_cover_item ON cv_cover_item.entity_id = w.id AND cv_cover_item.key IN ('cover_url', 'cover', 'poster_url', 'poster')
            LEFT JOIN canonical_values cv_square_item ON cv_square_item.entity_id = w.id AND cv_square_item.key IN ('square_url', 'square')
            LEFT JOIN canonical_values cv_background_item ON cv_background_item.entity_id = w.id AND cv_background_item.key IN ('background_url', 'background', 'episode_still_url', 'episode_still', 'still_url', 'still')
            LEFT JOIN canonical_values cv_banner_item ON cv_banner_item.entity_id = w.id AND cv_banner_item.key IN ('banner_url', 'banner', 'episode_still_url', 'episode_still', 'still_url', 'still')
            LEFT JOIN canonical_values cv_logo_item ON cv_logo_item.entity_id = w.id AND cv_logo_item.key IN ('logo_url', 'logo')
            LEFT JOIN canonical_values cv_cover_w ON cv_cover_w.entity_id = COALESCE(gpw.id, pw.id, w.id) AND cv_cover_w.key IN ('cover_url', 'cover', 'poster_url', 'poster')
            LEFT JOIN canonical_values cv_square_w ON cv_square_w.entity_id = COALESCE(gpw.id, pw.id, w.id) AND cv_square_w.key IN ('square_url', 'square')
            LEFT JOIN canonical_values cv_background_w ON cv_background_w.entity_id = COALESCE(gpw.id, pw.id, w.id) AND cv_background_w.key IN ('background_url', 'background', 'episode_still_url', 'episode_still', 'still_url', 'still')
            LEFT JOIN canonical_values cv_banner_w ON cv_banner_w.entity_id = COALESCE(gpw.id, pw.id, w.id) AND cv_banner_w.key IN ('banner_url', 'banner', 'episode_still_url', 'episode_still', 'still_url', 'still')
            LEFT JOIN canonical_values cv_logo_w ON cv_logo_w.entity_id = COALESCE(gpw.id, pw.id, w.id) AND cv_logo_w.key IN ('logo_url', 'logo')
            LEFT JOIN canonical_values cv_cover_state_a ON cv_cover_state_a.entity_id = ma.id AND cv_cover_state_a.key = 'cover_state'
            LEFT JOIN canonical_values cv_square_state_a ON cv_square_state_a.entity_id = ma.id AND cv_square_state_a.key = 'square_state'
            LEFT JOIN canonical_values cv_background_state_a ON cv_background_state_a.entity_id = ma.id AND cv_background_state_a.key = 'background_state'
            LEFT JOIN canonical_values cv_banner_state_a ON cv_banner_state_a.entity_id = ma.id AND cv_banner_state_a.key = 'banner_state'
            LEFT JOIN canonical_values cv_logo_state_a ON cv_logo_state_a.entity_id = ma.id AND cv_logo_state_a.key = 'logo_state'
            LEFT JOIN canonical_values cv_cover_state_item ON cv_cover_state_item.entity_id = w.id AND cv_cover_state_item.key = 'cover_state'
            LEFT JOIN canonical_values cv_square_state_item ON cv_square_state_item.entity_id = w.id AND cv_square_state_item.key = 'square_state'
            LEFT JOIN canonical_values cv_background_state_item ON cv_background_state_item.entity_id = w.id AND cv_background_state_item.key = 'background_state'
            LEFT JOIN canonical_values cv_banner_state_item ON cv_banner_state_item.entity_id = w.id AND cv_banner_state_item.key = 'banner_state'
            LEFT JOIN canonical_values cv_logo_state_item ON cv_logo_state_item.entity_id = w.id AND cv_logo_state_item.key = 'logo_state'
            LEFT JOIN canonical_values cv_cover_state_w ON cv_cover_state_w.entity_id = COALESCE(gpw.id, pw.id, w.id) AND cv_cover_state_w.key = 'cover_state'
            LEFT JOIN canonical_values cv_square_state_w ON cv_square_state_w.entity_id = COALESCE(gpw.id, pw.id, w.id) AND cv_square_state_w.key = 'square_state'
            LEFT JOIN canonical_values cv_background_state_w ON cv_background_state_w.entity_id = COALESCE(gpw.id, pw.id, w.id) AND cv_background_state_w.key = 'background_state'
            LEFT JOIN canonical_values cv_banner_state_w ON cv_banner_state_w.entity_id = COALESCE(gpw.id, pw.id, w.id) AND cv_banner_state_w.key = 'banner_state'
            LEFT JOIN canonical_values cv_logo_state_w ON cv_logo_state_w.entity_id = COALESCE(gpw.id, pw.id, w.id) AND cv_logo_state_w.key = 'logo_state'
            LEFT JOIN canonical_values cv_season_a ON cv_season_a.entity_id = ma.id AND cv_season_a.key = 'season_number'
            LEFT JOIN canonical_values cv_episode_a ON cv_episode_a.entity_id = ma.id AND cv_episode_a.key = 'episode_number'
            LEFT JOIN canonical_values cv_track_a ON cv_track_a.entity_id = ma.id AND cv_track_a.key = 'track_number'
            LEFT JOIN canonical_values cv_accent_w ON cv_accent_w.entity_id = COALESCE(gpw.id, pw.id, w.id) AND cv_accent_w.key = 'artwork_accent_hex'
            WHERE us.user_id = @ProfileId
              AND (@IncludeCompleted = 1 OR (us.progress_pct > 0 AND (us.progress_pct < 99.5 OR w.media_type = 'Music')))
              AND (@IncludeCompleted = 1 OR COALESCE(json_extract(us.extended_properties, '$.hide_continue'), 'false') <> 'true')
              AND w.work_kind != 'parent'
              AND {visibleWorkPredicate}
              AND {visibleAssetPredicate}
            GROUP BY us.asset_id
            ORDER BY us.last_accessed DESC;
            """;

        var rows = conn.Query<DisplayJourneyRow, string?, DisplayJourneyRow>(sql, (row, savedTiming) =>
        {
            (row.PositionSeconds, row.DurationSeconds) = SavedPlaybackTiming.Read(savedTiming);
            return row;
        }, new { ProfileId = profileId.Value, IncludeCompleted = includeCompleted }, splitOn: "SavedPlaybackTiming").ToList();
        foreach (var row in rows)
        {
            row.CoverUrl = DisplayArtworkUrlResolver.Resolve(row.CoverUrl, row.AssetId, "cover", row.CoverState);
            row.SquareUrl = DisplayArtworkUrlResolver.Resolve(row.SquareUrl, row.AssetId, "square", row.SquareState);
            row.BannerUrl = DisplayArtworkUrlResolver.Resolve(row.BannerUrl, row.AssetId, "banner", row.BannerState);
            row.BackgroundUrl = DisplayArtworkUrlResolver.Resolve(row.BackgroundUrl, row.AssetId, "background", row.BackgroundState);
            row.LogoUrl = DisplayArtworkUrlResolver.Resolve(row.LogoUrl, row.AssetId, "logo", row.LogoState);
        }

        return Task.FromResult<IReadOnlyList<DisplayJourneyRow>>((DisplayMediaRules.NormalizeLane(lane) switch
        {
            "watch" => rows.Where(row => DisplayMediaRules.IsWatchKind(row.MediaType)),
            "read" => rows.Where(row => DisplayMediaRules.IsReadKind(row.MediaType)),
            "listen" => rows.Where(row => DisplayMediaRules.IsListenKind(row.MediaType)),
            _ => rows,
        }).ToList());
    }
}
