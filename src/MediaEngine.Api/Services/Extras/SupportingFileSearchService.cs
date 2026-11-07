using MediaEngine.Api.Services.Display;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.Extras;

namespace MediaEngine.Api.Services.Extras;

/// <summary>A supporting file that search found, with the visible title it belongs to.</summary>
/// <param name="Extra">The file.</param>
/// <param name="Owner">One of the owner title's works the searching profile may see (an episode stands for its show).</param>
public sealed record SupportingFileSearchMatch(ItemExtra Extra, DisplayWorkRow Owner);

/// <summary>
/// Finds supporting files for the main search box (universal editor v5 item 18, D4): by title, description and file name. A file has no
/// visibility of its own; it follows the title it belongs to. The caller passes the works the searching profile may see (profile
/// visibility, Kids levels, hidden libraries; the Vault is only in view in an unlocked Vault session). A file is returned only when every
/// file of the media under its title folder belongs to a visible work, so a Kids profile never learns of a file in a title it may not open,
/// and never through a visible title that merely shares the folder. Candidates are read a page at a time until enough visible ones are
/// found, so many matches in titles the profile cannot see never push the visible ones out; owners are looked up one page at a time in a
/// single query.
/// </summary>
public sealed class SupportingFileSearchService(
    IItemExtraRepository extras,
    IMediaAssetRepository assets,
    IExtrasConfigurationSource? rules = null)
{
    // Candidates are read this many at a time and at most this many pages deep, which bounds the work of one keystroke.
    internal const int PageSize = 100;
    internal const int MaxPages = 20;

    /// <summary>The supporting-files rules in force (role words and file types).</summary>
    public ExtrasConfiguration Configuration => rules?.Current ?? ExtrasConfigurationProvider.Active;

    public async Task<IReadOnlyList<SupportingFileSearchMatch>> SearchAsync(
        string query, IReadOnlyList<DisplayWorkRow> visibleWorks, int limit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(visibleWorks);
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0 || limit <= 0 || visibleWorks.Count == 0)
        {
            return [];
        }

        var byAsset = new Dictionary<Guid, DisplayWorkRow>();
        foreach (var row in visibleWorks.Where(row => row.AssetId != Guid.Empty))
        {
            byAsset.TryAdd(row.AssetId, row);
        }

        var owners = new Dictionary<string, DisplayWorkRow?>(StringComparer.OrdinalIgnoreCase);
        var matches = new List<SupportingFileSearchMatch>();
        var pictureFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var page = 0; page < MaxPages; page++)
        {
            var candidates = await extras.SearchAsync(words, PageSize, page * PageSize, ct).ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                break;
            }

            // The owners of every folder this page names that is not known yet, in one query.
            var unknown = candidates.Select(extra => extra.ItemFolder).Distinct(StringComparer.OrdinalIgnoreCase).Where(folder => !owners.ContainsKey(folder)).ToList();
            if (unknown.Count > 0)
            {
                var files = await assets.ListFilesUnderFoldersAsync(unknown, ct).ConfigureAwait(false);
                foreach (var folder in unknown)
                {
                    owners[folder] = OwnerOf(folder, files, byAsset);
                }
            }

            foreach (var extra in candidates)
            {
                // A folder of pictures is one item, so it is found once.
                if (extra.Kind == ItemExtraKinds.Picture && !pictureFolders.Add($"{extra.ItemFolder}|{extra.GroupKey}"))
                {
                    continue;
                }

                if (owners[extra.ItemFolder] is { } owner)
                {
                    matches.Add(new SupportingFileSearchMatch(extra, owner));
                    if (matches.Count >= limit)
                    {
                        return matches;
                    }
                }
            }

            if (candidates.Count < PageSize)
            {
                break;
            }
        }

        return matches;
    }

    // The first visible work under the folder, or none when any media file under it belongs to a work the profile may not see.
    private static DisplayWorkRow? OwnerOf(string folder, IReadOnlyList<MediaAssetFileRef> files, IReadOnlyDictionary<Guid, DisplayWorkRow> visible)
    {
        var prefix = folder.Replace('\\', '/').TrimEnd('/') + "/";
        DisplayWorkRow? owner = null;
        foreach (var file in files
                     .Where(file => file.FilePath.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(file => file.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            if (!visible.TryGetValue(file.AssetId, out var row))
            {
                return null;
            }

            owner ??= row;
        }

        return owner;
    }
}
