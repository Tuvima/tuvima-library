using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Components.Browse;

public static class BrowseQueryBuilder
{
    public static BrowseState Read(
        LibraryBrowsePreset preset,
        string? requestedTab,
        string absoluteUri)
    {
        var normalizedTab = string.IsNullOrWhiteSpace(requestedTab) ? null : requestedTab.Trim().ToLowerInvariant();
        var fallbackTab = preset.Tabs[0];
        var resolvedTab = preset.Tabs.FirstOrDefault(tab =>
            string.Equals(tab.Id, normalizedTab, StringComparison.OrdinalIgnoreCase)) ?? fallbackTab;

        var query = System.Web.HttpUtility.ParseQueryString(new Uri(absoluteUri).Query);
        var grouping = ResolveGrouping(query["browse"], resolvedTab);
        var layout = ResolveLayout(query["view"], resolvedTab, grouping);
        var sortBy = ResolveSort(query["sort"], resolvedTab.Id, grouping);

        return new BrowseState(
            resolvedTab.Id,
            grouping,
            query["q"] ?? string.Empty,
            sortBy,
            layout,
            ParseList(query.GetValues("genre")),
            ParseList(query.GetValues("person")),
            query["status"] ?? string.Empty,
            ParseList(query.GetValues("year")),
            int.TryParse(query["tile"], out var tileSize) ? tileSize : null,
            query["period"] == "decade" ? MediaEngine.Web.Components.Shared.TimelineGrouping.Decade : MediaEngine.Web.Components.Shared.TimelineGrouping.Year);
    }

    public static string ResolveGrouping(string? requestedGrouping, BrowseTabPreset tab)
    {
        if (string.IsNullOrWhiteSpace(requestedGrouping))
        {
            return tab.DefaultGrouping;
        }

        return tab.GroupingOptions.Any(option => string.Equals(option.Value, requestedGrouping, StringComparison.OrdinalIgnoreCase))
            ? requestedGrouping
            : tab.DefaultGrouping;
    }

    public static LibraryLayoutMode ResolveLayout(string? requestedLayout, BrowseTabPreset tab, string grouping)
    {
        if (grouping == "timeline")
        {
            return LibraryLayoutMode.Card;
        }
        if (grouping is "tracks" or "songs")
        {
            return LibraryLayoutMode.List;
        }

        return Enum.TryParse<LibraryLayoutMode>(requestedLayout, ignoreCase: true, out var parsed)
            ? parsed
            : tab.DefaultLayout;
    }

    public static string ResolveSort(string? requestedSort, string activeTabId, string grouping)
    {
        var allowed = GetSortOptions(activeTabId, grouping).Select(option => option.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(requestedSort) && allowed.Contains(requestedSort))
        {
            return requestedSort;
        }

        if (grouping == "timeline")
        {
            return "newest";
        }

        if (string.Equals(activeTabId, "music", StringComparison.OrdinalIgnoreCase))
        {
            return grouping == "artists" ? "title" : "newest";
        }

        return IsContainerGrouping(grouping) ? "featured" : "newest";
    }

    public static IReadOnlyList<(string Value, string Label)> GetSortOptions(string activeTabId, string grouping) =>
        string.Equals(activeTabId, "music", StringComparison.OrdinalIgnoreCase)
        && grouping == "albums"
            ? [
                ("newest", "Recently added"),
                ("recently-played", "Recently played"),
                ("title", "Title A-Z"),
                ("creator", "Artist"),
                ("year", "Release year"),
            ]
        : string.Equals(activeTabId, "music", StringComparison.OrdinalIgnoreCase)
          && grouping == "artists"
            ? [("title", "A-Z"), ("featured", "Most albums"), ("newest", "Recently added")]
        : string.Equals(activeTabId, "music", StringComparison.OrdinalIgnoreCase)
          && grouping == "playlists"
            ? [("newest", "Recently updated"), ("title", "Title A-Z"), ("oldest", "Oldest")]
        : grouping == "timeline"
            ? [("oldest", "Oldest first"), ("newest", "Newest first")]
            : IsContainerGrouping(grouping)
            ? [("featured", "Top"), ("title", "A-Z"), ("newest", "Newest")]
            : [("newest", "Newest"), ("title", "A-Z"), ("oldest", "Oldest"), ("creator", "Creator"), ("year", "Year")];

    public static bool IsContainerGrouping(string grouping) =>
        grouping is "series" or "shows" or "albums" or "artists" or "authors" or "creators"
            or "publishers" or "collections" or "directors" or "networks" or "playlists" or "narrators";

    public static string? GetSystemViewGroupField(string activeTabId, string grouping) => (activeTabId, grouping) switch
    {
        ("music", "artists") => "artist",
        ("music", "albums") => "album",
        ("music", "playlists") => "playlist",
        ("books", "series") => "series",
        ("books", "authors") => "author",
        ("audiobooks", "series") => "series",
        ("audiobooks", "authors") => "author",
        ("audiobooks", "narrators") => "narrator",
        ("comics", "creators") => "author",
        ("comics", "publishers") => "publisher",
        ("movies", "directors") => "director",
        ("tv", "networks") => "network",
        _ => null,
    };

    private static IReadOnlyList<string> ParseList(string[]? values) =>
        values is null
            ? []
            : values.SelectMany(value => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

}
