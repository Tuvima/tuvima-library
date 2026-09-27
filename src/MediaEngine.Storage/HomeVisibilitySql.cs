namespace MediaEngine.Storage;

public static class HomeVisibilitySql
{
    public static string VisibleAssetPathPredicate(string filePathSql) => $"""
        COALESCE({filePathSql}, '') NOT LIKE '%/.data/staging/%'
        AND COALESCE({filePathSql}, '') NOT LIKE '%\.data\staging\%'
        AND COALESCE({filePathSql}, '') NOT LIKE '%/quarantine/%'
        AND COALESCE({filePathSql}, '') NOT LIKE '%\quarantine\%'
        """;

    public static string VisibleWorkPredicate(
        string workIdSql,
        string curatorStateSql,
        string? catalogOnlySql = null)
    {
        var conditions = new List<string>
        {
            $"COALESCE({curatorStateSql}, '') NOT IN ('rejected', 'provisional')",
            VisibleAssetExistsPredicate(workIdSql),
        };

        if (!string.IsNullOrWhiteSpace(catalogOnlySql))
        {
            conditions.Add($"COALESCE({catalogOnlySql}, 0) = 0");
        }

        return string.Join("\nAND ", conditions);
    }

    public static string PendingReviewExclusion(string workIdSql) => $"""
        NOT EXISTS (
            SELECT 1
            FROM review_queue rq
            WHERE (
                    rq.entity_id = {workIdSql}
                    OR EXISTS (
                        SELECT 1
                        FROM media_assets ma_r
                        INNER JOIN editions e_r ON e_r.id = ma_r.edition_id
                        WHERE ma_r.id = rq.entity_id
                          AND e_r.work_id = {workIdSql}
                    )
              )
              AND rq.status = 'Pending'
              AND rq.trigger != 'WritebackFailed'
        )
        """;

    public static string LatestNeedsReviewStateExclusion(string workIdSql) => $"""
        COALESCE((
            SELECT ij.state
            FROM identity_jobs ij
            INNER JOIN media_assets ma_j ON ma_j.id = ij.entity_id
            INNER JOIN editions e_j ON e_j.id = ma_j.edition_id
            WHERE e_j.work_id = {workIdSql}
            ORDER BY
                COALESCE(ij.updated_at, ij.created_at) DESC,
                ij.created_at DESC
            LIMIT 1
        ), '') NOT IN ('QidNeedsReview', 'RetailMatchedNeedsReview')
        """;

    public static string LatestTerminalIdentityStateExclusion(string workIdSql) => $"""
        COALESCE((
            SELECT ij.state
            FROM identity_jobs ij
            INNER JOIN media_assets ma_j ON ma_j.id = ij.entity_id
            INNER JOIN editions e_j ON e_j.id = ma_j.edition_id
            WHERE e_j.work_id = {workIdSql}
            ORDER BY
                COALESCE(ij.updated_at, ij.created_at) DESC,
                ij.created_at DESC
            LIMIT 1
        ), '') NOT IN ('RetailNoMatch', 'Failed')
        """;

    public static string VisibleAssetExistsPredicate(string workIdSql) => $"""
        EXISTS (
            SELECT 1
            FROM editions e_v
            INNER JOIN media_assets ma_v ON ma_v.edition_id = e_v.id
            WHERE e_v.work_id = {workIdSql}
              AND ma_v.status = 'Normal' AND ma_v.is_orphaned = 0
              AND {VisibleAssetPathPredicate("ma_v.file_path_root")}
        )
        """;

    public static string PresentedAssetExistsPredicate(string workIdSql) => $"""
        EXISTS (
            SELECT 1
            FROM editions e_p
            INNER JOIN media_assets ma_p ON ma_p.edition_id = e_p.id
            WHERE e_p.work_id = {workIdSql}
              AND ma_p.presented_at IS NOT NULL
              AND {VisibleAssetPathPredicate("ma_p.file_path_root")}
        )
        """;
}
