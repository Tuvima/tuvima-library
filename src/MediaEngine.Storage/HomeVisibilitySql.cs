namespace MediaEngine.Storage;

public static class HomeVisibilitySql
{
    public static string VisibleAssetPathPredicate(string filePathSql) => $"""
        COALESCE({filePathSql}, '') NOT LIKE '%/.data/staging/%'
        AND COALESCE({filePathSql}, '') NOT LIKE '%\.data\staging\%'
        AND COALESCE({filePathSql}, '') NOT LIKE '%/quarantine/%'
        AND COALESCE({filePathSql}, '') NOT LIKE '%\quarantine\%'
        """;

    /// <summary>
    /// The work has a file that can be shown. A work's files include the files that cover it, such as the combined
    /// file that holds several episodes (<c>work_owned_assets</c>).
    /// </summary>
    public static string VisibleAssetExistsPredicate(string workIdSql) => $"""
        EXISTS (
            SELECT 1
            FROM work_owned_assets woa_v
            INNER JOIN media_assets ma_v ON ma_v.id = woa_v.asset_id
            WHERE woa_v.work_id = {workIdSql}
              AND ma_v.status = 'Normal' AND ma_v.is_orphaned = 0
              AND {VisibleAssetPathPredicate("ma_v.file_path_root")}
        )
        """;
}
