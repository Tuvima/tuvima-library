using MediaEngine.Api.Services.Extras;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Contracts;
using MediaEngine.Ingestion.Contracts;

namespace MediaEngine.Api.Services.Details;

/// <summary>
/// Adds a title's Extras to its page (Extras spec WP2). The page has already been composed for the viewer, so only a title the viewer may
/// see gets here: Kids levels, hidden libraries and the Vault are applied before this runs, and a Kids profile that may not see the show
/// never receives its Extras. The Extras of a title are found from the folders its own files sit in.
/// </summary>
public sealed class DetailExtrasService(
    IMediaAssetRepository assets,
    IItemExtraRepository extras,
    ILibraryFolderResolver libraries)
{
    private const int MaxAssetsToLocate = 24;

    private static readonly HashSet<DetailEntityType> TitleTypes =
    [
        DetailEntityType.Work,
        DetailEntityType.Movie,
        DetailEntityType.TvShow,
        DetailEntityType.Book,
        DetailEntityType.Audiobook,
        DetailEntityType.ComicIssue,
        DetailEntityType.ComicSeries,
        DetailEntityType.MusicAlbum,
    ];

    internal async Task ApplyAsync(
        DetailPageViewModel detail,
        IReadOnlyList<Guid>? authorizedAssetIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(detail);
        if (!TitleTypes.Contains(detail.EntityType))
        {
            return;
        }

        var assetIds = new List<Guid>();
        if (authorizedAssetIds is not null)
        {
            assetIds.AddRange(authorizedAssetIds);
        }

        foreach (var item in detail.MediaGroups.SelectMany(group => group.Items))
        {
            if (Guid.TryParse(item.AssetId, out var assetId) && assetId != Guid.Empty)
            {
                assetIds.Add(assetId);
            }
        }

        var distinct = assetIds.Distinct().Take(MaxAssetsToLocate).ToList();
        if (distinct.Count == 0)
        {
            return;
        }

        var paths = await assets.GetFilePathsAsync(distinct, ct).ConfigureAwait(false);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Values)
        {
            foreach (var folder in CandidateFolders(path))
            {
                folders.Add(folder);
            }
        }

        if (folders.Count == 0)
        {
            return;
        }

        var found = await extras.ListByItemFoldersAsync([.. folders], ct).ConfigureAwait(false);
        if (found.Count > 0)
        {
            detail.Extras = ExtraCardBuilder.Build(found, File.Exists);
        }
    }

    /// <summary>
    /// The folders an Extra of this file's title can be filed under: the file's own folder and each folder above it, up to but not
    /// including the library folder. A show's Extras sit in its top folder, a film's in the film's folder.
    /// </summary>
    internal IEnumerable<string> CandidateFolders(string filePath)
    {
        var source = libraries.ResolveSourcePath(filePath);
        if (string.IsNullOrWhiteSpace(source))
        {
            yield break;
        }

        var root = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        while (!string.IsNullOrEmpty(directory)
               && directory.Length > root.Length
               && directory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            yield return directory;
            directory = Path.GetDirectoryName(directory);
        }
    }
}
