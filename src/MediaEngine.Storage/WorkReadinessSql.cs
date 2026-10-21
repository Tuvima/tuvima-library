using MediaEngine.Domain;

namespace MediaEngine.Storage;

/// <summary>
/// The one definition of "Ready", as SQL. A leaf Work (<c>work_kind</c> standalone or child) is Ready when all of
/// these hold: it is not rejected or provisional; it has a visible file; its latest identity job says it was matched
/// to a real title (Wikidata is not required, so a Work with no Wikidata match still counts); it is not waiting in
/// Needs Review; it is placed in its show (TV) or album (Music); and its artwork is present or settled.
///
/// Ready is sticky: the full rule must hold for a Work to become Ready the first time, but once it is Ready it stays
/// Ready through the ordinary steps of re-identification (queued again, searching, hydrating) and loses the mark only
/// on a negative outcome (see <see cref="StillReadyPredicate"/>).
///
/// A Work's files include the files that cover it (a combined file holding several episodes), so the file, job and
/// review checks all follow <c>work_owned_assets</c>.
///
/// The Engine's readiness service evaluates <see cref="ComputedReadyPredicate"/> and stores the result in
/// <c>works.ready_at</c>. Every display query (Home, hero, lanes, Watch, Read, Listen, collections, the Library list)
/// must filter with <see cref="ReadyWorkPredicate"/> and must never define its own
/// visibility rule; a guardrail test fails if one does.
/// </summary>
public static class WorkReadinessSql
{
    /// <summary>
    /// The name of the list parameter that <see cref="ComputedReadyPredicate"/> and <see cref="MatchedAndPlacedPredicate"/>
    /// read: the ids of libraries whose metadata policy skips external identity. Their works have no identity job, so
    /// a file in one of these libraries counts as matched without one. Pass an empty list when there are none.
    /// </summary>
    public const string BypassIdentityLibraryIdsParameter = "bypassIdentityLibraryIds";

    // The identity states that are not negative outcomes. A Ready work whose latest job is in one of these stays Ready.
    private const string ContinuingStates =
        "'Queued', 'RetailSearching', 'RetailMatched', 'BridgeSearching', 'QidResolved', 'QidNoMatch', 'Hydrating', 'UniverseEnriching', 'Ready', 'ReadyWithoutUniverse'";

    private const string MatchedStates =
        "'RetailMatched', 'BridgeSearching', 'QidResolved', 'QidNoMatch', 'Hydrating', 'UniverseEnriching', 'Ready', 'ReadyWithoutUniverse'";

    /// <summary>
    /// The full Ready rule, computed from the current data. Used only by the readiness repository's refresh
    /// statement; display queries use <see cref="ReadyWorkPredicate"/> instead.
    /// </summary>
    public static string ComputedReadyPredicate(string workAlias) => $"""
        {MatchedAndPlacedPredicate(workAlias)}
        AND {ArtworkPresentOrSettledPredicate(workAlias)}
        """;

    /// <summary>
    /// Everything in the Ready rule except artwork: a leaf Work that is eligible, visible, matched, not in review
    /// and placed. The repository uses it to find Works that are only waiting for artwork.
    /// </summary>
    public static string MatchedAndPlacedPredicate(string workAlias) => $"""
        {EligiblePredicate(workAlias)}
        AND {MatchedPredicate(workAlias)}
        """;

    /// <summary>
    /// What a Work that is already Ready must keep satisfying. It is Ready for good unless something negative happens:
    /// its latest identity job ends in review, no match or failure, a review is pending, the curator rejects it, it
    /// loses its file or its place in a show or album, or it becomes catalog-only. Matching in progress and the artwork
    /// state are not asked again.
    /// </summary>
    public static string StillReadyPredicate(string workAlias) => $"""
        {EligiblePredicate(workAlias)}
        AND {ContinuingPredicate(workAlias)}
        """;

    // The conditions that do not depend on identity: a leaf work that is eligible, visible, not in review and placed.
    private static string EligiblePredicate(string workAlias) => $"""
        {workAlias}.work_kind IN ('standalone', 'child')
        AND COALESCE({workAlias}.is_catalog_only, 0) = 0
        AND COALESCE({workAlias}.curator_state, '') NOT IN ('rejected', 'provisional')
        AND {HomeVisibilitySql.VisibleAssetExistsPredicate($"{workAlias}.id")}
        AND {PendingReviewExclusion($"{workAlias}.id")}
        AND {PlacedPredicate(workAlias)}
        """;

    /// <summary>
    /// The single display gate for a leaf Work alias. The stored mark carries matching, review, placement and
    /// artwork; the file check stays at read time because removing a file leaves no timestamp.
    /// </summary>
    public static string ReadyWorkPredicate(string workAlias) => $"""
        {ReadyMarkPredicate(workAlias)}
        AND COALESCE({workAlias}.is_catalog_only, 0) = 0
        AND {HomeVisibilitySql.VisibleAssetExistsPredicate($"{workAlias}.id")}
        """;

    /// <summary>
    /// Only the stored Ready mark, with no file check. For queries that already join the exact file row they list
    /// and check that row themselves.
    /// </summary>
    public static string ReadyMarkPredicate(string workAlias) => $"{workAlias}.ready_at IS NOT NULL";

    /// <summary>
    /// The "matched" condition of the Ready rule: the latest identity job across the Work's assets says the Work was
    /// matched to a real title, or the Work lives in a library that never runs identity (local-only or manual metadata
    /// policy). Reads the <see cref="BypassIdentityLibraryIdsParameter"/> list parameter. The live ingestion page uses
    /// this same text to say which condition a not-yet-Ready row is still waiting for.
    /// </summary>
    public static string MatchedPredicate(string workAlias) => $"""
        ({LatestJobState(workAlias)} IN ({MatchedStates}) OR {InBypassLibrary(workAlias)})
        """;

    // A Work that is already Ready keeps the mark while its latest job is in any non-negative state (or it needs none).
    private static string ContinuingPredicate(string workAlias) => $"""
        ({LatestJobState(workAlias)} IN ({ContinuingStates}) OR {InBypassLibrary(workAlias)})
        """;

    private static string LatestJobState(string workAlias) => $"""
        COALESCE((
            SELECT ij_m.state
            FROM identity_jobs ij_m
            INNER JOIN work_owned_assets woa_m ON woa_m.asset_id = ij_m.entity_id
            WHERE woa_m.work_id = {workAlias}.id
            ORDER BY ij_m.updated_at DESC, ij_m.created_at DESC
            LIMIT 1
        ), '')
        """;

    private static string InBypassLibrary(string workAlias) => $"""
        EXISTS (
            SELECT 1
            FROM work_owned_assets woa_b
            INNER JOIN media_assets ma_b ON ma_b.id = woa_b.asset_id
            WHERE woa_b.work_id = {workAlias}.id
              AND ma_b.library_id COLLATE NOCASE IN @{BypassIdentityLibraryIdsParameter}
        )
        """;

    /// <summary>
    /// The "placed" condition of the Ready rule. A Videos episode may stand alone or sit under a series (directly or through a section). A TV episode must sit under a show, directly or through a season that
    /// itself sits under a show (a season is a parent that has a number; one whose show is gone does not count as the
    /// show). A track must sit under an album. Other media types stand alone by design, so this is always true for them.
    /// </summary>
    public static string PlacedPredicate(string workAlias) => $"""
        (
            CASE
                WHEN {workAlias}.media_type = 'TV' THEN
                    CASE WHEN {workAlias}.parent_work_id IS NOT NULL
                          AND EXISTS (
                              SELECT 1
                              FROM works parent_p
                              WHERE parent_p.id = {workAlias}.parent_work_id
                                AND (
                                    (parent_p.parent_work_id IS NULL AND parent_p.ordinal IS NULL)
                                    OR EXISTS (SELECT 1 FROM works root_p WHERE root_p.id = parent_p.parent_work_id)
                                )
                          )
                         THEN 1 ELSE 0 END
                WHEN {workAlias}.media_type = 'Videos' THEN
                    CASE WHEN {workAlias}.parent_work_id IS NULL THEN 1
                         WHEN EXISTS (
                              SELECT 1
                              FROM works parent_v
                              WHERE parent_v.id = {workAlias}.parent_work_id
                                AND (
                                    (parent_v.parent_work_id IS NULL AND parent_v.ordinal IS NULL)
                                    OR EXISTS (SELECT 1 FROM works root_v WHERE root_v.id = parent_v.parent_work_id)
                                )
                          )
                         THEN 1 ELSE 0 END
                WHEN {workAlias}.media_type = 'Music' THEN
                    CASE WHEN {workAlias}.parent_work_id IS NOT NULL
                          AND EXISTS (SELECT 1 FROM works album_p WHERE album_p.id = {workAlias}.parent_work_id)
                         THEN 1 ELSE 0 END
                ELSE 1
            END = 1
        )
        """;

    /// <summary>
    /// A stored cover is present, or a stored value says the artwork search is settled, on the Work's own files,
    /// the Work itself, its parent (season or album) or its root (show or series). Only stored values count; a
    /// finished job without artwork does not.
    /// </summary>
    public static string ArtworkPresentOrSettledPredicate(string workAlias) => $"""
        (
            EXISTS (
                SELECT 1
                FROM canonical_values cv_w
                WHERE cv_w.entity_id IN (
                        {workAlias}.id,
                        {workAlias}.parent_work_id,
                        (SELECT gp_a.parent_work_id FROM works gp_a WHERE gp_a.id = {workAlias}.parent_work_id)
                    )
                  AND {ArtworkValue("cv_w")}
            )
            OR EXISTS (
                SELECT 1
                FROM work_owned_assets woa_a
                INNER JOIN canonical_values cv_a ON cv_a.entity_id = woa_a.asset_id
                WHERE woa_a.work_id = {workAlias}.id
                  AND {ArtworkValue("cv_a")}
            )
        )
        """;

    private static string ArtworkValue(string cvAlias) => $"""
        (
            ({cvAlias}.key = '{MetadataFieldConstants.CoverState}' AND {cvAlias}.value = 'present')
            OR ({cvAlias}.key IN ('{MetadataFieldConstants.CoverUrl}', '{MetadataFieldConstants.ArtworkSettledAt}') AND TRIM({cvAlias}.value) <> '')
        )
        """;

    // No Pending review row on the Work or its assets, other than a failed write-back (which does not block display).
    private static string PendingReviewExclusion(string workIdSql) => $"""
        NOT EXISTS (
            SELECT 1
            FROM review_queue rq
            WHERE (
                    rq.entity_id = {workIdSql}
                    OR EXISTS (
                        SELECT 1
                        FROM work_owned_assets woa_r
                        WHERE woa_r.asset_id = rq.entity_id
                          AND woa_r.work_id = {workIdSql}
                    )
              )
              AND rq.status = 'Pending'
              AND rq.trigger != 'WritebackFailed'
        )
        """;
}
