using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Components.Pages;

/// <summary>
/// Derives lane navigation from the same typed browse preset consumed by the
/// filter toolbar, so route tabs and persistent navigation cannot drift apart.
/// </summary>
public static class MediaLaneConfigurationBuilder
{
    public static IReadOnlyList<MediaHubModeViewModel> BuildModes(LibraryBrowsePreset preset) =>
    [
        new("all", "Discover", preset.RouteBase),
        .. preset.Tabs.Select(tab => new MediaHubModeViewModel(
            tab.Id,
            tab.Label,
            $"{preset.RouteBase}/{tab.Id}")),
    ];

    /// <summary>
    /// The Collections group of the Watch and Read rails: a link to every collection, and a "+" that opens the New collection flow.
    /// Only drawn where the person can create (an administrator on a desktop).
    /// </summary>
    public static MediaSectionNavigationGroup BuildCollectionsGroup(string area) =>
        new("Collections",
        [
            new("All collections", "/collections", AppMaterialIcons.Outlined.Collections, Exact: true),
        ],
        new MediaSectionNavigationCreateOptions(
            "New collection",
            "New collection",
            "Collection name",
            new CollectionFlowCreateTarget(area),
            new CollectionFlowCreateTarget(area),
            OpensCollectionFlow: true));

    public static MediaSectionNavigationGroup BuildLibraryGroup(
        LibraryBrowsePreset preset,
        IReadOnlySet<string>? tabIds = null) =>
        new(
            "Library",
            [
                new MediaSectionNavigationItem(
                    "Discover",
                    preset.RouteBase,
                    AppMaterialIcons.Outlined.Explore,
                    Exact: true),
                .. preset.Tabs
                    .Where(tab => tabIds is null || tabIds.Contains(tab.Id))
                    .Select(tab => new MediaSectionNavigationItem(
                    tab.Label,
                    $"{preset.RouteBase}/{tab.Id}",
                    ResolveTabIcon(tab))),
            ]);

    private static string ResolveTabIcon(BrowseTabPreset tab) =>
        tab.GroupingOptions.FirstOrDefault(option =>
            string.Equals(option.Value, tab.DefaultGrouping, StringComparison.OrdinalIgnoreCase))?.Icon
        ?? tab.GroupingOptions.FirstOrDefault()?.Icon
        ?? AppMaterialIcons.Outlined.Folder;
}
