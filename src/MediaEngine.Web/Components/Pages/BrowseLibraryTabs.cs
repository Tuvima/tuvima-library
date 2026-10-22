using MediaEngine.Contracts.Display;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Components.Pages;

/// <summary>
/// Builds a lane's browse tabs from the Engine's library list (Library setup spec D54): a tab for each library that has folders, in the
/// list's order. A lookup kind (Movies, TV Shows, YouTube, Music, Audiobooks, Books, Comics) has one library, so its tab is named for the
/// kind and keeps the kind's key as its id; a plain kind's named libraries ("Lectures") each get a tab named for the library, with an id
/// built from the library id. Every tab reuses the fixed tab of its media type for grouping and layout and adds its library to the browse
/// query. When the list cannot be read, or yields no tab, the lane keeps its fixed tabs so browse never breaks.
/// </summary>
public static class BrowseLibraryTabs
{
    // The lookup kinds, and the name their one tab carries; any other kind is a named library whose own name is the tab name.
    private static readonly IReadOnlyDictionary<string, string> LookupKindLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["movies"] = "Movies",
        ["tv"] = "TV Shows",
        ["youtube"] = "YouTube",
        ["music"] = "Music",
        ["audiobooks"] = "Audiobooks",
        ["books"] = "Books",
        ["comics"] = "Comics",
    };

    /// <summary>Reads the library list and builds the lane's preset; the fixed preset when the list cannot be read.</summary>
    /// <param name="fixedTabIds">Tabs of the fixed preset that are not libraries (Listen's Playlists); they stay, after the library tabs.</param>
    public static async Task<LibraryBrowsePreset> LoadAsync(
        IEngineApiClient api,
        LibraryBrowsePreset fixedPreset,
        string area,
        IReadOnlySet<string>? fixedTabIds = null,
        CancellationToken ct = default) =>
        Build(fixedPreset, area, await api.GetBrowseLibrariesAsync(ct), fixedTabIds);

    public static LibraryBrowsePreset Build(
        LibraryBrowsePreset fixedPreset,
        string area,
        IReadOnlyList<BrowseLibraryDto>? libraries,
        IReadOnlySet<string>? fixedTabIds = null)
    {
        if (libraries is null)
        {
            return fixedPreset;
        }

        var inArea = libraries.Where(library => string.Equals(library.Area, area, StringComparison.OrdinalIgnoreCase)).ToList();
        var templates = fixedPreset.Tabs.Where(tab => fixedTabIds is null || !fixedTabIds.Contains(tab.Id)).ToList();
        var tabs = new List<BrowseTabPreset>();
        foreach (var library in inArea.Where(library => library.HasFolders))
        {
            var template = templates.FirstOrDefault(tab => string.Equals(tab.MediaType, library.MediaType, StringComparison.OrdinalIgnoreCase));
            if (template is null)
            {
                continue;
            }

            var isLookupKind = LookupKindLabels.TryGetValue(library.KindKey, out var kindLabel);
            var tab = template with
            {
                Id = isLookupKind ? library.KindKey.ToLowerInvariant() : $"lib-{library.Id:N}",
                Label = isLookupKind ? kindLabel! : library.Name,
                LibraryId = library.Id,
                BehaviourId = template.EffectiveBehaviourId,
                NarrowToLibrary = inArea.Count(other => string.Equals(other.MediaType, library.MediaType, StringComparison.OrdinalIgnoreCase)) > 1,
            };
            if (tabs.All(existing => !string.Equals(existing.Id, tab.Id, StringComparison.OrdinalIgnoreCase)))
            {
                tabs.Add(tab);
            }
        }

        if (tabs.Count == 0)
        {
            return fixedPreset;
        }

        tabs.AddRange(fixedPreset.Tabs.Where(tab => fixedTabIds is not null && fixedTabIds.Contains(tab.Id)));
        return fixedPreset with { Tabs = tabs };
    }
}
