using Dapper;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Display;

/// <summary>
/// What a profile's visibility rules need to know about one visible (work, library) pair: which work, which library,
/// which kind of media and what content rating it carries.
/// </summary>
public sealed class DisplayVisibleWorkRow
{
    public Guid WorkId { get; set; }
    public string? LibraryId { get; set; }
    public string MediaType { get; set; } = string.Empty;
    public string? ContentRating { get; set; }
}

/// <summary>
/// The light twin of <see cref="DisplayWorkProjectionReader"/> for screens that only need to know which works a profile
/// may see. It returns exactly the (work, library) pairs the full read returns with no limit, with the same Ready rule,
/// visible-path rule, rank-1 file choice and content-rating fallback, but reads none of the other display values.
/// </summary>
public sealed class DisplayVisibleWorkReader
{
    private readonly IDatabaseConnection _db;

    public DisplayVisibleWorkReader(IDatabaseConnection db) => _db = db;

    // The ranking mirrors DisplayWorkProjectionReader.BuildSql; DisplayVisibleWorkReaderTests prove the two return the
    // same rows. The content rating reads the first stored name present on the work, then its show or album, then its
    // file. When both names exist on one entity the data store returns 'certification' before 'content_rating' (the
    // canonical_values primary key is (entity_id, key)), exactly as the full read does today.
    internal static string BuildSql()
    {
        var visibleWorkPredicate = WorkReadinessSql.ReadyWorkPredicate("w");
        var visibleAssetPredicate = HomeVisibilitySql.VisibleAssetPathPredicate("ma.file_path_root");
        return $"""
            WITH asset_dates AS MATERIALIZED (
                SELECT entity_id, MIN(claimed_at) AS claimed_at
                FROM metadata_claims GROUP BY entity_id
            ),
            ranked_assets AS (
                SELECT
                    w.id AS WorkId,
                    ma.library_id AS LibraryId,
                    w.media_type AS MediaType,
                    CASE
                        WHEN w.media_type = 'Music' THEN COALESCE(p.id, w.id)
                        ELSE COALESCE(gp.id, p.id, w.id)
                    END AS RootWorkId,
                    ma.id AS AssetId,
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
            )
            SELECT
                LibraryId,
                WorkId,
                MediaType,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('content_rating', 'certification') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('content_rating', 'certification') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('content_rating', 'certification') LIMIT 1)
                ) AS ContentRating
            FROM ranked_assets
            WHERE AssetRank = 1;
            """;
    }

    public async Task<IReadOnlyList<DisplayVisibleWorkRow>> LoadAsync(CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        return (await conn.QueryAsync<DisplayVisibleWorkRow>(new CommandDefinition(BuildSql(), cancellationToken: ct))).ToList();
    }
}
