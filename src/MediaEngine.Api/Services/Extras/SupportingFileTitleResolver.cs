using MediaEngine.Domain.Contracts;
using MediaEngine.Ingestion.Contracts;

namespace MediaEngine.Api.Services.Extras;

/// <summary>Finds the folders a root title keeps its supporting files in.</summary>
public interface ISupportingFileTitleResolver
{
    /// <summary>The title folders of this title, or an empty list when it has none a library reads.</summary>
    Task<IReadOnlyList<string>> ResolveItemFoldersAsync(Guid titleId, CancellationToken ct = default);
}

/// <summary>
/// Finds the folders a root title keeps its supporting files in (universal editor v5 WP4). The editor opens on a work, a show, a season,
/// an album or a Series; the files live under the folder the title sits in, found from the title's own media files. A Vault title is
/// never reachable here (the deletion repository leaves Vault works out), so a title without a folder simply has no supporting files.
/// </summary>
public sealed class SupportingFileTitleResolver(ILibraryItemDeletionRepository titles, IItemExtrasScanner scanner) : ISupportingFileTitleResolver
{
    // Up to this many of a show's or album's works are looked at to find its folder; they all sit under the same one.
    private const int MaxWorksToLocate = 5;
    private const int MaxFilesPerWork = 3;

    /// <summary>The title folders of this title, or an empty list when it has none a library reads.</summary>
    public async Task<IReadOnlyList<string>> ResolveItemFoldersAsync(Guid titleId, CancellationToken ct = default)
    {
        if (titleId == Guid.Empty)
        {
            return [];
        }

        var group = await titles.ResolveGroupAsync(titleId, ct).ConfigureAwait(false);
        IReadOnlyList<Guid> ids = group is { OwnedWorkIds.Count: > 0 } ? [.. group.OwnedWorkIds.Take(MaxWorksToLocate)] : [titleId];

        var folders = new List<string>();
        foreach (var work in await titles.GetWorksAsync(ids, ct).ConfigureAwait(false))
        {
            foreach (var asset in work.Assets.Take(MaxFilesPerWork))
            {
                if (await scanner.ResolveTitleFolderAsync(asset.FilePath, ct).ConfigureAwait(false) is { } folder
                    && !folders.Contains(folder.ItemFolder, StringComparer.OrdinalIgnoreCase))
                {
                    folders.Add(folder.ItemFolder);
                }
            }
        }

        return folders;
    }
}
