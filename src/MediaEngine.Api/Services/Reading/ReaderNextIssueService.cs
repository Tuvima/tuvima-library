using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Reading;

/// <summary>
/// Finds the next issue the library owns after a comic, using the Series order the library already keeps (manifest number, the issue's own
/// stored position, then its decimal position). Only owned issues count, a missing issue is skipped, and an issue the profile may not read is skipped too.
/// </summary>
internal sealed class ReaderNextIssueService(
    IDatabaseConnection database,
    CatalogueResourceAuthorizationService authorization,
    IMediaAssetRepository assets,
    IReaderTitleService titles) : IReaderNextIssueService
{
    // A Series has at most a few hundred issues; looking at the next handful of owned ones is enough to get past gaps the profile cannot read.
    private const int CandidateLimit = 10;

    public async Task<ReaderNextIssue?> FindAsync(HttpContext context, Guid assetId, Guid? profileId, CancellationToken ct)
    {
        (Guid WorkId, Guid? CollectionId)? current;
        using (var connection = database.CreateConnection())
        {
            current = await connection.QueryFirstOrDefaultAsync<(Guid WorkId, Guid? CollectionId)?>(
                new CommandDefinition(
                    """
                    SELECT w.id AS WorkId, w.collection_id AS CollectionId
                    FROM work_owned_assets woa
                    JOIN works w ON w.id = woa.work_id
                    WHERE woa.asset_id = @assetId
                    LIMIT 1
                    """,
                    new { assetId },
                    cancellationToken: ct));
        }

        if (current is not { CollectionId: { } collectionId })
        {
            return null;
        }

        // Every owned issue of the Series with what the Series order reads: the manifest's number for this Series, the issue's own
        // stored (Series, position) pair, then its decimal position. The same rule the Series page and shelf sort by.
        IReadOnlyList<MemberRow> members;
        using (var connection = database.CreateConnection())
        {
            members = (await connection.QueryAsync<MemberRow>(
                new CommandDefinition(
                    """
                    SELECT w.id AS WorkId,
                           COALESCE(NULLIF(CAST(title_cv.value AS TEXT), ''), '') AS Title,
                           CAST(COALESCE(w.ordinal_sort, w.ordinal) AS REAL) AS WorkOrdinalSort,
                           item.parsed_ordinal AS ManifestOrdinal,
                           item.ordinal_scope_qid AS OrdinalScopeQid,
                           item.series_qid AS SeriesQid,
                           item.membership_scope AS MembershipScope,
                           NULLIF(item.publication_date, '') AS PublicationDate,
                           (SELECT NULLIF(cva.value, '') FROM canonical_value_arrays cva
                             WHERE cva.entity_id = w.id AND cva.key = 'series_placement'
                               AND cva.value_qid = (SELECT c.wikidata_qid FROM collections c WHERE c.id = @collectionId) COLLATE NOCASE
                             ORDER BY cva.ordinal LIMIT 1) AS PlacementPosition
                    FROM works w
                    LEFT JOIN canonical_values title_cv ON title_cv.entity_id = w.id AND title_cv.key = 'title'
                    LEFT JOIN series_manifest_items item ON item.linked_work_id = w.id AND item.collection_id = @collectionId
                    WHERE w.collection_id = @collectionId
                      AND w.ownership = 'Owned'
                      AND w.is_catalog_only = 0
                    """,
                    new { collectionId },
                    cancellationToken: ct))).AsList();
        }

        var ordered = SeriesOrdering.Order(
            members.GroupBy(member => member.WorkId).Select(group => group.First()),
            member => SeriesOrdering.BuildInput(
                member.MembershipScope,
                member.ManifestOrdinal,
                member.OrdinalScopeQid,
                member.SeriesQid,
                SeriesPositionPairs.SortValueOf(member.PlacementPosition) ?? member.WorkOrdinalSort,
                member.PublicationDate,
                member.Title));
        var at = ordered.FindIndex(member => member.WorkId == current.Value.WorkId);
        if (at < 0)
        {
            return null;
        }

        // A numbered Series run is the "next" one; supplementary and collected members are not the next issue.
        IReadOnlyList<Guid> candidates = ordered
            .Skip(at + 1)
            .Where(member => !SeriesOrdering.IsAlsoInThisWorld(member.MembershipScope))
            .Take(CandidateLimit)
            .Select(member => member.WorkId)
            .ToList();

        foreach (var workId in candidates)
        {
            var allowed = await authorization.GetAuthorizedAssetIdsForWorkAsync(
                context, workId, profileId, ApplicationPermissionIds.LibraryRead, ct);
            foreach (var candidate in allowed)
            {
                var asset = await assets.FindByIdAsync(candidate, ct);
                if (asset is null || ReadEndpoints.ReaderFormatOf(asset.FilePathRoot) != "comic")
                {
                    continue;
                }

                var title = await titles.GetAsync(candidate, isComic: true, ct);
                return new ReaderNextIssue(candidate, workId, collectionId, title.Series, title.Subtitle ?? title.Title);
            }
        }

        return null;
    }

    private sealed class MemberRow
    {
        public Guid WorkId { get; init; }
        public string Title { get; init; } = string.Empty;
        public double? WorkOrdinalSort { get; init; }
        public double? ManifestOrdinal { get; init; }
        public string? OrdinalScopeQid { get; init; }
        public string? SeriesQid { get; init; }
        public string? MembershipScope { get; init; }
        public string? PublicationDate { get; init; }
        public string? PlacementPosition { get; init; }
    }
}
