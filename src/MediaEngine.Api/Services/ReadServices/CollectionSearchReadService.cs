using Dapper;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Search;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.ReadServices;

public sealed class CollectionSearchReadService : ICollectionSearchReadService
{
    private readonly IDatabaseConnection _db;
    private readonly ISearchIndexRepository? _searchIndex;

    public CollectionSearchReadService(IDatabaseConnection db)
        : this(db, null)
    {
    }

    public CollectionSearchReadService(IDatabaseConnection db, ISearchIndexRepository? searchIndex)
    {
        _db = db;
        _searchIndex = searchIndex;
    }

    public async Task<List<SearchResultDto>> SearchAsync(string? query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
        {
            return [];
        }

        var trimmed = query.Trim();
        var like = $"%{trimmed}%";
        IReadOnlyList<Guid>? matchedWorkIds = null;
        if (_searchIndex is not null)
        {
            matchedWorkIds = await _searchIndex.SearchAsync(trimmed, 80, ct).ConfigureAwait(false);
            if (matchedWorkIds.Count == 0)
            {
                return [];
            }
        }

        var searchPredicate = matchedWorkIds is not null
            ? "w.id IN @workIds"
            : """
              (c.display_name LIKE @like COLLATE NOCASE
                   OR EXISTS (
                       SELECT 1
                       FROM canonical_values cv
                       WHERE cv.entity_id IN (ma.id, w.id, w.collection_id)
                         AND cv.value LIKE @like COLLATE NOCASE
                   )
                   OR EXISTS (
                       SELECT 1
                       FROM canonical_value_arrays cva
                       WHERE cva.entity_id IN (ma.id, w.id)
                         AND cva.value LIKE @like COLLATE NOCASE
                   )
                   OR EXISTS (
                       SELECT 1
                       FROM primary_person_media_credits credit
                       WHERE credit.media_asset_id = ma.id
                         AND credit.person_name LIKE @like COLLATE NOCASE
                   ))
              """;
        var visibleWorkPredicate = WorkReadinessSql.ReadyWorkPredicate("w");
        var visibleAssetPredicate = HomeVisibilitySql.VisibleAssetPathPredicate("ma.file_path_root");
        var displayYearSql = MediaDateSql.DisplayOriginalYear(
            "w.id",
            "COALESCE(grandparent.id, parent.id, w.id)",
            "ma.id",
            "w.media_type");

        using var conn = _db.CreateConnection();
        var rows = (await conn.QueryAsync<CollectionSearchRow>(new CommandDefinition($"""
            WITH matched AS (
                SELECT
                    w.id AS WorkId,
                    w.collection_id AS CollectionId,
                    w.media_type AS MediaType,
                    COALESCE(
                        issue_title.value,
                        issue_title_work.value,
                        episode_title.value,
                        title_asset.value,
                        title_work.value,
                        original_title.value,
                        'Work ' || substr(w.id, 1, 8)
                    ) AS Title,
                    COALESCE(
                        (SELECT credit.person_name
                         FROM primary_person_media_credits credit
                         WHERE credit.media_asset_id = ma.id
                           AND (
                               (w.media_type IN ('Books', 'Book', 'Comics', 'Comic') AND credit.credit_key = 'author')
                               OR (w.media_type IN ('Movies', 'Movie', 'TV', 'Television') AND credit.credit_key = 'director')
                               OR (w.media_type IN ('Music', 'Audio') AND credit.credit_key IN ('artist', 'performer'))
                               OR (w.media_type IN ('Audiobooks', 'Audiobook') AND credit.credit_key IN ('author', 'narrator'))
                           )
                         ORDER BY CASE credit.credit_key
                                      WHEN 'author' THEN 0
                                      WHEN 'director' THEN 0
                                      WHEN 'artist' THEN 0
                                      WHEN 'performer' THEN 0
                                      ELSE 1
                                  END,
                                  credit.billing_order,
                                  credit.person_name COLLATE NOCASE
                         LIMIT 1),
                        author_asset.value,
                        artist_asset.value,
                        director_asset.value,
                        author_work.value,
                        artist_work.value,
                        director_work.value
                    ) AS Author,
                    COALESCE(
                        c.display_name,
                        collection_title.value,
                        substr(lower(hex(COALESCE(w.collection_id, w.id))), 1, 8)
                    ) AS CollectionDisplayName,
                    COALESCE(
                        series_asset.value,
                        series_work.value,
                        series_root.value,
                        c.display_name
                    ) AS Series,
                    COALESCE(
                        issue_asset.value,
                        issue_work.value,
                        series_position_asset.value,
                        series_position_work.value,
                        CASE WHEN w.ordinal IS NOT NULL THEN CAST(w.ordinal AS TEXT) END
                    ) AS SeriesPosition,
                    COALESCE(
                        show_root.value,
                        show_work.value,
                        show_asset.value,
                        root_title.value,
                        CASE WHEN w.media_type IN ('TV', 'Television') THEN c.display_name END
                    ) AS ShowName,
                    COALESCE(season_asset.value, season_work.value) AS SeasonNumber,
                    COALESCE(episode_asset.value, episode_work.value) AS EpisodeNumber,
                    COALESCE(
                        cover_asset.value,
                        cover_url_asset.value,
                        cover_work.value,
                        cover_url_work.value
                    ) AS CoverUrl,
                    {displayYearSql} AS Year,
                    COALESCE(
                        (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key = 'description' LIMIT 1),
                        (SELECT value FROM canonical_values WHERE entity_id = COALESCE(grandparent.id, parent.id, w.id) AND key = 'description' LIMIT 1)
                    ) AS Description,
                    COALESCE(
                        (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key = 'rating' LIMIT 1),
                        (SELECT value FROM canonical_values WHERE entity_id = w.id AND key = 'rating' LIMIT 1)
                    ) AS Rating,
                    ROW_NUMBER() OVER (
                        PARTITION BY w.id
                        ORDER BY
                            CASE WHEN title_asset.value IS NULL AND title_work.value IS NULL THEN 1 ELSE 0 END,
                            ma.id
                    ) AS RowNumber
                FROM works w
                INNER JOIN editions e ON e.work_id = w.id
                INNER JOIN media_assets ma ON ma.edition_id = e.id
                LEFT JOIN works parent ON parent.id = w.parent_work_id
                LEFT JOIN works grandparent ON grandparent.id = parent.parent_work_id
                LEFT JOIN collections c ON c.id = w.collection_id
                LEFT JOIN canonical_values title_asset ON title_asset.entity_id = ma.id AND title_asset.key = 'title'
                LEFT JOIN canonical_values issue_title ON issue_title.entity_id = ma.id AND issue_title.key = 'issue_title'
                LEFT JOIN canonical_values episode_title ON episode_title.entity_id = ma.id AND episode_title.key = 'episode_title'
                LEFT JOIN canonical_values title_work ON title_work.entity_id = w.id AND title_work.key = 'title'
                LEFT JOIN canonical_values issue_title_work ON issue_title_work.entity_id = w.id AND issue_title_work.key = 'issue_title'
                LEFT JOIN canonical_values original_title ON original_title.entity_id = w.id AND original_title.key = 'original_title'
                LEFT JOIN canonical_values series_asset ON series_asset.entity_id = ma.id AND series_asset.key = 'series'
                LEFT JOIN canonical_values series_work ON series_work.entity_id = w.id AND series_work.key = 'series'
                LEFT JOIN canonical_values series_root ON series_root.entity_id = COALESCE(grandparent.id, parent.id, w.id) AND series_root.key = 'series'
                LEFT JOIN canonical_values series_position_asset ON series_position_asset.entity_id = ma.id AND series_position_asset.key = 'series_position'
                LEFT JOIN canonical_values series_position_work ON series_position_work.entity_id = w.id AND series_position_work.key = 'series_position'
                LEFT JOIN canonical_values issue_asset ON issue_asset.entity_id = ma.id AND issue_asset.key = 'issue_number'
                LEFT JOIN canonical_values issue_work ON issue_work.entity_id = w.id AND issue_work.key = 'issue_number'
                LEFT JOIN canonical_values show_asset ON show_asset.entity_id = ma.id AND show_asset.key = 'show_name'
                LEFT JOIN canonical_values show_work ON show_work.entity_id = w.id AND show_work.key = 'show_name'
                LEFT JOIN canonical_values show_root ON show_root.entity_id = COALESCE(grandparent.id, parent.id, w.id) AND show_root.key = 'show_name'
                LEFT JOIN canonical_values root_title ON root_title.entity_id = COALESCE(grandparent.id, parent.id, w.id) AND root_title.key = 'title'
                LEFT JOIN canonical_values season_asset ON season_asset.entity_id = ma.id AND season_asset.key = 'season_number'
                LEFT JOIN canonical_values season_work ON season_work.entity_id = w.id AND season_work.key = 'season_number'
                LEFT JOIN canonical_values episode_asset ON episode_asset.entity_id = ma.id AND episode_asset.key = 'episode_number'
                LEFT JOIN canonical_values episode_work ON episode_work.entity_id = w.id AND episode_work.key = 'episode_number'
                LEFT JOIN canonical_value_arrays author_asset ON author_asset.entity_id = ma.id AND author_asset.key = 'author' AND author_asset.ordinal = 0
                LEFT JOIN canonical_value_arrays artist_asset ON artist_asset.entity_id = ma.id AND artist_asset.key = 'artist' AND artist_asset.ordinal = 0
                LEFT JOIN canonical_value_arrays director_asset ON director_asset.entity_id = ma.id AND director_asset.key = 'director' AND director_asset.ordinal = 0
                LEFT JOIN canonical_value_arrays author_work ON author_work.entity_id = w.id AND author_work.key = 'author' AND author_work.ordinal = 0
                LEFT JOIN canonical_value_arrays artist_work ON artist_work.entity_id = w.id AND artist_work.key = 'artist' AND artist_work.ordinal = 0
                LEFT JOIN canonical_value_arrays director_work ON director_work.entity_id = w.id AND director_work.key = 'director' AND director_work.ordinal = 0
                LEFT JOIN canonical_values collection_title ON collection_title.entity_id = w.collection_id AND collection_title.key = 'title'
                LEFT JOIN canonical_values cover_asset ON cover_asset.entity_id = ma.id AND cover_asset.key = 'cover'
                LEFT JOIN canonical_values cover_url_asset ON cover_url_asset.entity_id = ma.id AND cover_url_asset.key = 'cover_url'
                LEFT JOIN canonical_values cover_work ON cover_work.entity_id = w.id AND cover_work.key = 'cover'
                LEFT JOIN canonical_values cover_url_work ON cover_url_work.entity_id = w.id AND cover_url_work.key = 'cover_url'
                WHERE w.work_kind != 'parent'
                  AND {visibleWorkPredicate}
                  AND {visibleAssetPredicate}
                  AND {searchPredicate}
            )
            SELECT WorkId,
                   CollectionId,
                   MediaType,
                   Title,
                   Author,
                   CollectionDisplayName,
                   Series,
                   SeriesPosition,
                   ShowName,
                   SeasonNumber,
                   EpisodeNumber,
                   CoverUrl,
                   Year,
                   Description,
                   Rating
            FROM matched
            WHERE RowNumber = 1
            ORDER BY Title COLLATE NOCASE
            LIMIT 20;
            """, new { like, workIds = matchedWorkIds }, cancellationToken: ct))).ToList();

        return rows.Select(row => new SearchResultDto
        {
            WorkId = row.WorkId,
            CollectionId = row.CollectionId,
            Title = row.Title,
            Author = row.Author,
            MediaType = row.MediaType ?? string.Empty,
            CollectionDisplayName = row.CollectionDisplayName ?? string.Empty,
            Series = row.Series,
            SeriesPosition = row.SeriesPosition,
            ShowName = row.ShowName,
            SeasonNumber = row.SeasonNumber,
            EpisodeNumber = row.EpisodeNumber,
            CoverUrl = row.CoverUrl,
            Year = row.Year,
            Description = row.Description,
            Rating = row.Rating,
        }).ToList();
    }

    private sealed class CollectionSearchRow
    {
        public Guid WorkId { get; init; }
        public Guid? CollectionId { get; init; }
        public string? MediaType { get; init; }
        public string Title { get; init; } = string.Empty;
        public string? Author { get; init; }
        public string? CollectionDisplayName { get; init; }
        public string? Series { get; init; }
        public string? SeriesPosition { get; init; }
        public string? ShowName { get; init; }
        public string? SeasonNumber { get; init; }
        public string? EpisodeNumber { get; init; }
        public string? CoverUrl { get; init; }
        public string? Year { get; init; }
        public string? Description { get; init; }
        public string? Rating { get; init; }
    }
}
