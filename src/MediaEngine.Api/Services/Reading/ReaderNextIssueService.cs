using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Reading;

/// <summary>
/// Finds the next issue the library owns after a comic, using the Series order the library already keeps (a Work's place in its
/// Collection). Only owned issues count, a missing issue is skipped, and an issue the profile may not read is skipped too.
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
        (Guid WorkId, Guid? CollectionId, double? Position)? current;
        using (var connection = database.CreateConnection())
        {
            current = await connection.QueryFirstOrDefaultAsync<(Guid WorkId, Guid? CollectionId, double? Position)?>(
                new CommandDefinition(
                    """
                    SELECT w.id AS WorkId, w.collection_id AS CollectionId, COALESCE(w.ordinal_sort, w.ordinal) AS Position
                    FROM work_owned_assets woa
                    JOIN works w ON w.id = woa.work_id
                    WHERE woa.asset_id = @assetId
                    LIMIT 1
                    """,
                    new { assetId },
                    cancellationToken: ct));
        }

        if (current is not { CollectionId: { } collectionId, Position: { } position })
        {
            return null;
        }

        IReadOnlyList<Guid> candidates;
        using (var connection = database.CreateConnection())
        {
            candidates = (await connection.QueryAsync<Guid>(
                new CommandDefinition(
                    """
                    SELECT w.id
                    FROM works w
                    WHERE w.collection_id = @collectionId
                      AND w.id <> @workId
                      AND w.ownership = 'Owned'
                      AND w.is_catalog_only = 0
                      AND COALESCE(w.ordinal_sort, w.ordinal) > @position
                    ORDER BY COALESCE(w.ordinal_sort, w.ordinal), w.id
                    LIMIT @limit
                    """,
                    new { collectionId, workId = current.Value.WorkId, position, limit = CandidateLimit },
                    cancellationToken: ct))).AsList();
        }

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
}
