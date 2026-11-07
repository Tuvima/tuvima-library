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
/// visibility, Kids levels, hidden libraries; the Vault is only in view in an unlocked Vault session), and a file whose title has no
/// visible work is never returned, so a Kids profile never learns of a file in a title it may not open.
/// </summary>
public sealed class SupportingFileSearchService(
    IItemExtraRepository extras,
    IMediaAssetRepository assets,
    IExtrasConfigurationSource? rules = null)
{
    // A search is a handful of results on screen; this bounds the candidates looked at before visibility is applied.
    private const int MaxCandidates = 200;

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

        var candidates = await extras.SearchAsync(words, MaxCandidates, ct).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return [];
        }

        var byAsset = new Dictionary<Guid, DisplayWorkRow>();
        foreach (var row in visibleWorks.Where(row => row.AssetId != Guid.Empty))
        {
            byAsset.TryAdd(row.AssetId, row);
        }

        // The owner of each folder is looked up once: the first work the profile may see that has a file under it.
        var owners = new Dictionary<string, DisplayWorkRow?>(StringComparer.OrdinalIgnoreCase);
        var matches = new List<SupportingFileSearchMatch>();
        var pictureFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var extra in candidates)
        {
            // A folder of pictures is one item, so it is found once.
            if (extra.Kind == ItemExtraKinds.Picture && !pictureFolders.Add($"{extra.ItemFolder}|{extra.GroupKey}"))
            {
                continue;
            }

            if (!owners.TryGetValue(extra.ItemFolder, out var owner))
            {
                var files = await assets.ListFilesUnderAsync(extra.ItemFolder, ct).ConfigureAwait(false);
                owner = files.Select(file => byAsset.GetValueOrDefault(file.AssetId)).FirstOrDefault(row => row is not null);
                owners[extra.ItemFolder] = owner;
            }

            if (owner is not null)
            {
                matches.Add(new SupportingFileSearchMatch(extra, owner));
                if (matches.Count >= limit)
                {
                    break;
                }
            }
        }

        return matches;
    }
}
