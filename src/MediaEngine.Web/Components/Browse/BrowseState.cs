using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Components.Browse;

public sealed record BrowseState(
    string ActiveTabId,
    string Grouping,
    string SearchText,
    string SortBy,
    LibraryLayoutMode Layout,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Creators,
    string Status,
    IReadOnlyList<string> Years,
    int? TileSize,
    MediaEngine.Web.Components.Shared.TimelineGrouping Period = MediaEngine.Web.Components.Shared.TimelineGrouping.Year,
    IReadOnlyList<string>? Vibes = null,
    IReadOnlyList<string>? Themes = null,
    bool Unmatched = false,
    Guid? LibraryChip = null);
