using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Components.Shared;
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
