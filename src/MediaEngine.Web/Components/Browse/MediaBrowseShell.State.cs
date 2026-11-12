using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain.Enums;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Library;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.MediaTiles;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Resources;
using MediaEngine.Web.Services.Branding;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.MediaTiles;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Components.Browse;

public partial class MediaBrowseShell
{
    // The library the browse asks for: the tab's own library, or the chip chosen on a gathering tab.
    private Guid? ActiveLibraryId => _libraryChip ?? CurrentTab.LibraryId;

    // The Folders filter (Libraries v3 D18): offered only when the library in view has two or more folders; null is "All folders".
    private string? _folder;

    private IReadOnlyList<MediaEngine.Contracts.Display.BrowseFolderDto> ActiveFolders =>
        _libraryChip is Guid chip
            ? CurrentTab.LibraryChips.FirstOrDefault(item => item.Id == chip)?.Folders ?? []
            : CurrentTab.Folders;

    private string? ActiveFolderId => ActiveFolders.Any(item => item.Id == _folder) ? _folder : null;

    private IReadOnlyList<AppSelectOption> FolderOptions =>
        [new("", L["MediaBrowseShell_AllFolders"].Value), .. ActiveFolders.Select(item => new AppSelectOption(item.Id, item.Name))];


    private BrowseTabPreset CurrentTab =>
        Preset.Tabs.FirstOrDefault(tab => string.Equals(tab.Id, _activeTabId, StringComparison.OrdinalIgnoreCase))
        ?? Preset.Tabs[0];

    // The fixed tab whose per-tab rules (sort, columns, tiles, links) the active tab follows: a library's own tab follows the tab of its media type.
    private string BehaviourTabId => CurrentTab.EffectiveBehaviourId;

    private string RootClass =>
        $"browse-shell {(Embedded ? "browse-shell--embedded" : string.Empty)}".Trim();
    private bool ShowTabs => ShowTabNavigation && Preset.Tabs.Count > 1;
    private bool ShowHeaderRow => ShowTitleBlock || ShowTabs;
    private int ActiveBrowseTabIndex => GetSelectedIndex(Preset.Tabs.Select(tab => tab.Id), _activeTabId);
    // Unmatched files have no series, show or album to sit in, so the Unmatched filter always lists them flat, whatever the grouping chip says.
    private bool IsContainerGrouping => !_unmatched && BrowseQueryBuilder.IsContainerGrouping(_grouping);
    private bool IsMusicTab => string.Equals(CurrentTab.MediaType, "Music", StringComparison.OrdinalIgnoreCase);
    private bool IsAudiobookTab => string.Equals(CurrentTab.MediaType, "Audiobooks", StringComparison.OrdinalIgnoreCase);
    private bool UsesAlbumTimeline => IsTimelineGrouping
                                      && CurrentTab.TimelineAggregation == TimelineAggregation.Albums;
    private bool UsesTvShowTimeline => IsTimelineGrouping
                                       && CurrentTab.TimelineAggregation == TimelineAggregation.TvShows;
    private bool IsTvNetworksGrouping =>
        string.Equals(BehaviourTabId, "tv", StringComparison.OrdinalIgnoreCase)
        && string.Equals(_grouping, "networks", StringComparison.OrdinalIgnoreCase)
        && string.Equals(CurrentTab.MediaType, "TV", StringComparison.OrdinalIgnoreCase);
    private bool IsPlaylistGrouping => IsMusicTab
                                       && string.Equals(_grouping, "playlists", StringComparison.OrdinalIgnoreCase);
    private bool IsMusicSongsGrouping => IsMusicTab
                                         && _grouping is "tracks" or "songs";
    private bool IsTvShowsGrouping =>
        string.Equals(BehaviourTabId, "tv", StringComparison.OrdinalIgnoreCase)
        && string.Equals(_grouping, "shows", StringComparison.OrdinalIgnoreCase)
        && string.Equals(CurrentTab.MediaType, "TV", StringComparison.OrdinalIgnoreCase);
    private bool SupportsFacetFilters => true;
    private bool UseListLayout => !IsPlaylistGrouping
                                  && (_layout == LibraryLayoutMode.List || _grouping is "tracks" or "songs");
    private bool SupportsLayoutToggle => _grouping is not ("tracks" or "songs" or "playlists");
    private bool IsTimelineGrouping => !_unmatched && string.Equals(_grouping, "timeline", StringComparison.OrdinalIgnoreCase);

    // The grouping the Engine is asked for: the Unmatched filter is always a flat list of files (see IsContainerGrouping).
    private string BrowseGrouping => _unmatched ? "all" : _grouping;
    private bool IsInProgressOnly => string.Equals(_status, "in-progress", StringComparison.OrdinalIgnoreCase);
    private bool IsSecondaryQuickFilterSelected => string.Equals(_status, SecondaryQuickFilterValue, StringComparison.OrdinalIgnoreCase);
    private bool IsFinishedOnly => string.Equals(_status, "finished", StringComparison.OrdinalIgnoreCase);
    private string SecondaryQuickFilterValue => CurrentTab.MediaType == "Music" ? "favorite" : "not-started";
    private string SecondaryQuickFilterLabel => CurrentTab.MediaType switch
    {
        "Movies" or "TV" or "Videos" => "Unwatched",
        "Music" => "Favorites",
        "Audiobooks" => "Not started",
        _ => "Unread",
    };
    private IReadOnlyList<string> GenreFacets => _facets?.Genres ?? [];
    private IReadOnlyList<string> CreatorFacets => _facets?.Creators ?? [];
    private IReadOnlyList<string> YearFacets => _facets?.Years ?? [];
    private IReadOnlyList<string> VibeFacets => _facets?.Vibes ?? [];
    private IReadOnlyList<string> ThemeFacets => _facets?.Themes ?? [];
    private IReadOnlyList<string> SelectedVibes => _selectedVibes.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
    private IReadOnlyList<string> SelectedThemes => _selectedThemes.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
    private IReadOnlyList<string> SelectedGenres => _selectedGenres.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
    private IReadOnlyList<string> SelectedCreators => _selectedCreators.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
    private IReadOnlyList<string> SelectedYears => _selectedYears.OrderByDescending(value => value, StringComparer.OrdinalIgnoreCase).ToList();
    private bool HasActiveFilters => !string.IsNullOrWhiteSpace(_searchText)
                                     || _selectedGenres.Count > 0
                                     || _selectedCreators.Count > 0
                                     || !string.IsNullOrWhiteSpace(_status)
                                     || _selectedYears.Count > 0
                                     || _selectedVibes.Count > 0
                                     || _selectedThemes.Count > 0;
    private string CreatorFilterLabel => CurrentTab.MediaType switch
    {
        "Books" or "Comics" => "Author / creator",
        "Music" or "Audiobooks" => "Artist / author",
        "TV" => "Creator / network",
        _ => "Director / creator",
    };
    private string AllCreatorLabel => CurrentTab.MediaType switch
    {
        "Books" or "Comics" => "All authors",
        "Music" or "Audiobooks" => "All artists",
        _ => "All creators",
    };
    private string CreatorSearchPlaceholder => CurrentTab.MediaType switch
    {
        "Books" or "Comics" or "Audiobooks" => "Search authors...",
        "Music" => "Search artists...",
        "TV" => "Search creators or networks...",
        _ => "Search directors or creators...",
    };
    private string SearchPlaceholder => (CurrentTab.MediaType, _grouping) switch
    {
        ("Music", "songs" or "tracks") => "Search songs, artists, or albums...",
        ("Music", "artists") => "Search artists...",
        ("Music", "playlists") => "Search playlists...",
        ("Music", _) => "Search albums or artists...",
        ("Books", _) => "Search books or authors...",
        ("Comics", _) => "Search comics or creators...",
        ("Movies", _) => "Search movies or directors...",
        ("TV", _) => "Search TV shows or creators...",
        ("Audiobooks", _) => "Search audiobooks or authors...",
        _ => $"Search {CurrentTab.Label.ToLowerInvariant()}...",
    };
    private string ContinueTitle => Preset.HeroVariant switch
    {
        BrowseHeroVariant.Read => "Continue Reading",
        BrowseHeroVariant.Listen => "Continue Listening",
        _ => "Continue Watching",
    };
    private string ResultCountLabel => _totalCount == 1 ? "1 item" : $"{_totalCount} items";
    private IReadOnlyList<ActiveFilterChip> ActiveFilterChips =>
        [
            .. _selectedGenres.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .Select(value => new ActiveFilterChip($"genre:{value}", value)),
            .. _selectedCreators.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .Select(value => new ActiveFilterChip($"person:{value}", value)),
            .. _selectedYears.OrderByDescending(value => value, StringComparer.OrdinalIgnoreCase)
                .Select(value => new ActiveFilterChip($"year:{value}", value)),
            .. _selectedVibes.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .Select(value => new ActiveFilterChip($"vibe:{value}", value)),
            .. _selectedThemes.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .Select(value => new ActiveFilterChip($"theme:{value}", value)),
            .. string.IsNullOrWhiteSpace(_status)
                ? []
                : new[] { new ActiveFilterChip("status", StatusDisplayLabel) },
            .. _unmatched
                ? new[] { new ActiveFilterChip("unmatched", "Needs review") }
                : [],
        ];
    private string StatusDisplayLabel => _status switch
    {
        "in-progress" => "In progress",
        "not-started" => SecondaryQuickFilterLabel,
        "favorite" => "Favorites",
        "finished" => "Finished",
        _ => _status,
    };
    private string EmptyStateTitle =>
        _unmatched
            ? "Nothing needs review here."
        : HasQualifyingFilters
            ? $"No {ActiveGroupingLabel.ToLowerInvariant()} match these filters"
            : IsContainerGrouping
                ? $"No {ActiveGroupingLabel.ToLowerInvariant()} yet"
                : $"No {CurrentTab.Label.ToLowerInvariant()} in your library yet";

    private string EmptyStateMessage =>
        HasQualifyingFilters
            ? "Remove a filter or clear all filters to broaden the results."
            : IsPlaylistGrouping
                ? "Create a playlist or smart playlist to organize music for later."
            : IsContainerGrouping
                ? $"{ActiveGroupingLabel} appear when matching library metadata is available."
                : $"Once supported {CurrentTab.Label.ToLowerInvariant()} files are ingested, they will appear here.";

    private bool HasQualifyingFilters =>
        !string.IsNullOrWhiteSpace(_searchText)
        || _selectedGenres.Count > 0
        || _selectedCreators.Count > 0
        || _selectedYears.Count > 0
        || _selectedVibes.Count > 0
        || _selectedThemes.Count > 0
        || !string.IsNullOrWhiteSpace(_status)
        || _unmatched;

    private string ActiveGroupingLabel => CurrentTab.GroupingOptions
        .FirstOrDefault(option => string.Equals(option.Value, _grouping, StringComparison.OrdinalIgnoreCase))?.Label
        ?? CurrentTab.Label;


    private string LayoutToggleClass(LibraryLayoutMode mode) =>
        $"browse-shell__layout-button {(_layout == mode ? "is-active" : string.Empty)}";

    private string GroupingChipClass(string value) =>
        $"browse-shell__grouping-button {(string.Equals(_grouping, value, StringComparison.OrdinalIgnoreCase) ? "is-active" : string.Empty)}";

    private List<LibraryColumnDef> ActiveColumns =>
        IsContainerGrouping
            ? LibraryColumnDefinitions.GetColumnsByTab(BehaviourTabId, _grouping)
            : LibraryColumnDefinitions.GetColumnsByTab(BehaviourTabId);

    private List<string> VisibleColumnKeys =>
        ActiveColumns
            .Where(column => column.DefaultVisible && !string.Equals(column.Key, "checkbox", StringComparison.OrdinalIgnoreCase))
            .Select(column => column.Key)
            .ToList();

    private List<LibraryItemViewModel> ActiveTableItems =>
        IsContainerGrouping ? BuildContainerDisplayItems() : _items;
    private IReadOnlyList<WorkViewModel> SongTracks =>
        ActiveTableItems.Select(ToSongWork).ToList();
    private IReadOnlySet<Guid> SongFavoriteIds =>
        ActiveTableItems.Where(item => item.IsFavorite).Select(item => item.EntityId).ToHashSet();


    private static string ResolveGrouping(string? requestedGrouping, BrowseTabPreset tab) =>
        BrowseQueryBuilder.ResolveGrouping(requestedGrouping, tab);

    private static LibraryLayoutMode ResolveLayout(string? requestedLayout, BrowseTabPreset tab, string grouping) =>
        BrowseQueryBuilder.ResolveLayout(requestedLayout, tab, grouping);

    private string ResolveSort(string? requestedSort) =>
        BrowseQueryBuilder.ResolveSort(requestedSort, BehaviourTabId, _grouping);
}
