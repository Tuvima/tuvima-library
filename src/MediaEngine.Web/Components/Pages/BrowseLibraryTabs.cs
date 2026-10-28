using MediaEngine.Contracts.Display;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Components.Pages;

/// <summary>
/// Builds a lane's browse tabs from the Engine's library list (Library setup spec D54): a tab for each library that has folders, in the
/// list's order. A lookup kind (Movies, TV Shows, YouTube, Music, Audiobooks, Books, Comics) has one library, so its tab is named for the
/// kind and keeps the kind's key as its id; a plain kind's named libraries ("Lectures") each get a tab named for the library, with an id
/// built from the library id. Every tab reuses the fixed tab of its media type for grouping and layout and adds its library to the browse
/// query. When the list cannot be read the lane keeps its fixed tabs so browse never breaks; when it was read and holds nothing for the area
/// the lane has no tabs.
/// Watch gathers its YouTube and Other videos libraries into one Videos tab (Library pages D22): a lone library is that tab's library; several
/// give the tab a chip for each (the chips filter on the library each title already belongs to). The list holds only libraries the profile may
/// see, so a library hidden from the profile has neither a chip nor a row.
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

    // The kinds Watch gathers into its one Videos tab, by area. Any other kind keeps a tab of its own.
    private static readonly IReadOnlyDictionary<string, string[]> GatheredKinds = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["watch"] = ["youtube", "other_videos"],
    };

    private const string GatheredTabId = "videos";

    /// <summary>
    /// The lane's preset for the library list the Engine gave. <c>null</c> means the list could not be read, so the fixed tabs stand in;
    /// a list that was read but holds no usable library for the area means the profile has nothing there, so the preset has no tabs at all
    /// (the lane then shows its empty state, never empty tabs).
    /// </summary>
    /// <param name="fixedTabIds">Tabs of the fixed preset that are not libraries (Listen's Playlists); they stay, after the library tabs.</param>
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
        var gathered = GatheredKinds.TryGetValue(area, out var gatheredKeys) ? gatheredKeys : [];
        var gatheredLibraries = inArea
            .Where(library => library.HasFolders && gathered.Contains(library.KindKey, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var gatheredAdded = false;
        foreach (var library in inArea.Where(library => library.HasFolders))
        {
            if (gathered.Contains(library.KindKey, StringComparer.OrdinalIgnoreCase))
            {
                var gatheredTemplate = templates.FirstOrDefault(tab => string.Equals(tab.MediaType, library.MediaType, StringComparison.OrdinalIgnoreCase));
                if (!gatheredAdded && gatheredTemplate is not null)
                {
                    gatheredAdded = true;
                    tabs.Add(GatheredTab(gatheredTemplate, gatheredLibraries));
                }

                continue;
            }

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
            };
            if (tabs.All(existing => !string.Equals(existing.Id, tab.Id, StringComparison.OrdinalIgnoreCase)))
            {
                tabs.Add(tab);
            }
        }

        if (tabs.Count == 0)
        {
            return fixedPreset with { Tabs = [] };
        }

        tabs.AddRange(fixedPreset.Tabs.Where(tab => fixedTabIds is not null && fixedTabIds.Contains(tab.Id)));
        return fixedPreset with { Tabs = tabs };
    }

    private static BrowseTabPreset GatheredTab(BrowseTabPreset template, IReadOnlyList<BrowseLibraryDto> libraries)
    {
        var many = libraries.Count > 1;
        return template with
        {
            Id = GatheredTabId,
            Label = "Videos",
            LibraryId = many ? null : libraries[0].Id,
            LibraryChips = many ? [.. libraries.Select(library => new BrowseLibraryChip(library.Id, library.Name))] : [],
            AlsoKnownAs = ["youtube", .. libraries.Select(library => $"lib-{library.Id:N}")],
            BehaviourId = template.EffectiveBehaviourId,
        };
    }
}
