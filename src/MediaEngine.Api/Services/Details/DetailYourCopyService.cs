using MediaEngine.Api.Services.Details.Internals;
using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;

namespace MediaEngine.Api.Services.Details;

/// <summary>
/// Adds a page's "Your copy" row (details-page-bottom spec D4, D5, section 4): the chips and File details panel for the title's own files, or the
/// summary of a show's, season's or album's files. The page has already been composed for the viewer, so the files come from the same sets the
/// page was built from: the work's authorized files for a single title, and the viewer's visible works under the show or album for a group.
/// Nobody sees a copy of files they may not know about: a Kids profile (or a request with no profile) gets nothing, and a file in the Vault is
/// never read. Only a viewer who may already edit the item also receives the file's place on disk.
/// </summary>
public sealed class DetailYourCopyService(YourCopyReadRepository files, IProfileRepository profiles)
{
    // A very long show still shows one summary; this only bounds how many files are read for it.
    private const int MaxFiles = 3000;

    private static readonly HashSet<DetailEntityType> SingleTitleTypes =
    [
        DetailEntityType.Work,
        DetailEntityType.Movie,
        DetailEntityType.TvEpisode,
        DetailEntityType.Book,
        DetailEntityType.Audiobook,
        DetailEntityType.ComicIssue,
    ];

    private static readonly HashSet<DetailEntityType> GroupTypes =
    [
        DetailEntityType.TvShow,
        DetailEntityType.TvSeason,
        DetailEntityType.MusicAlbum,
    ];

    internal async Task ApplyAsync(
        DetailPageViewModel detail,
        IReadOnlyList<Guid>? authorizedAssetIds,
        IReadOnlyList<DisplayWorkRow>? authorizedWorks,
        Guid? profileId,
        DetailActionAuthorizationContext authorization,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var single = SingleTitleTypes.Contains(detail.EntityType);
        if (!single && !GroupTypes.Contains(detail.EntityType))
        {
            return;
        }

        // File facts are for grown-ups: a Kids profile, or a request that names no profile at all, reads nothing (fails closed).
        if (profileId is not { } id || await profiles.GetByIdAsync(id, ct).ConfigureAwait(false) is not { IsKidsProfile: false })
        {
            return;
        }

        var assetIds = single ? authorizedAssetIds : AssetsUnder(detail, authorizedWorks);
        if (assetIds is not { Count: > 0 })
        {
            return;
        }

        var found = await files.GetAsync(assetIds.Distinct().Take(MaxFiles).ToList(), ct).ConfigureAwait(false);
        detail.YourCopy = YourCopyBuilder.Build(detail, found, includeLocation: single && authorization.Allows("edit"));
    }

    // The viewer's visible files of the show or album the page is for: its episodes or tracks sit under its root work, or in its collection.
    private static List<Guid> AssetsUnder(DetailPageViewModel detail, IReadOnlyList<DisplayWorkRow>? authorizedWorks)
    {
        if (authorizedWorks is null || !Guid.TryParse(detail.Id, out var id))
        {
            return [];
        }

        return authorizedWorks
            .Where(work => work.AssetId != Guid.Empty && (work.RootWorkId == id || work.CollectionId == id))
            .Select(work => work.AssetId)
            .Distinct()
            .ToList();
    }
}
