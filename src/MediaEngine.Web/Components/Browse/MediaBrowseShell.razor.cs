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
    [Parameter] public string? Tab { get; set; }
    [Parameter, EditorRequired] public LibraryBrowsePreset Preset { get; set; } = default!;
    [Parameter] public bool Embedded { get; set; }
    [Parameter] public bool ShowTitleBlock { get; set; } = true;
    [Parameter] public bool ShowTabNavigation { get; set; } = true;

    private const int PageSize = 72;
    private TimelineGrouping _timelineGrouping;
    private IReadOnlyList<MediaEngine.Contracts.Display.DisplayTimelinePeriodDto>? _timelineIndex;
    private int _timelineOffset;
    private int _displayRequestVersion;
    private CancellationTokenSource? _browseRequestCts;

    private readonly HashSet<Guid> _selectedItems = [];
    private readonly List<MediaTileViewModel> _cards = [];

    private string _activeTabId = string.Empty;
    private string _grouping = "all";
    private string _searchText = string.Empty;
    private string? _openFacetMenuKey;
    private int _searchInputVersion;
    private string _sortBy = "newest";
    private readonly HashSet<string> _selectedGenres = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedCreators = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedYears = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedVibes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedThemes = new(StringComparer.OrdinalIgnoreCase);
    private string _status = string.Empty;
    // Unmatched is its own value, not a status: the Unmatched files of the tab's library (Library setup spec D64).
    private bool _unmatched;
    // The library chosen on a gathering tab (Watch's Videos); null is "All".
    private Guid? _libraryChip;
    private DisplayBrowseFacetsDto? _facets;
    private int _totalCount;
    private readonly List<MediaTileViewModel> _continueCards = [];
    private bool _continueLoading = true;
    private LibraryLayoutMode _layout = LibraryLayoutMode.Card;
    private bool _loading;
    private bool _hasLoadedOnce;
    private bool _hasMore;
    private bool _navigatingInternally;

    /// <summary>The details overlay state, cascaded by the route view; null where the shell is shown on its own.</summary>
    [CascadingParameter] public DetailsOverlayState? Overlay { get; set; }

    // The routed page's own address: while the details overlay is open the address bar names the title, not this page.
    private RoutedPageAddress? _address;
    private bool _loadFailed;
    private string? _loadError;
    private Guid? _activeProfileId;
    private bool _activeProfileResolved;

    private List<LibraryItemViewModel> _items = [];
    private List<ContentGroupViewModel> _groups = [];
    private static readonly StreamingServiceLogoResolver StreamingServiceLogos = new();

    private Guid? _requestedTrackId;
    private Guid? _lastAutoPlayedTrackId;
    private CancellationTokenSource? _mediaAddedReloadCts;
    private int _pendingMediaAddedCount;
    private string? _lastParameterQueryKey;

    protected override void OnInitialized()
    {
        _address = new RoutedPageAddress(Nav, Overlay);
        Nav.LocationChanged += OnLocationChanged;
        StateContainer.MediaAddedReceived += OnMediaAdded;
        Orchestrator.OnLibraryItemsRemoved += OnLibraryItemsRemoved;
        Playback.Changed += OnPlaybackChanged;
        Editor.Closed += OnEditorClosed;
    }

    private void OnPlaybackChanged(PlaybackChangeKind kind)
    {
        if (kind != PlaybackChangeKind.TransportTick)
        {
            _ = InvokeAsync(StateHasChanged);
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (!_activeProfileResolved)
        {
            _activeProfileId = (await Orchestrator.GetActiveProfileAsync())?.Id;
            _activeProfileResolved = true;
        }

        // The Router renders before this shell's own location handler runs, so a same-page change (a new tab with a new
        // query) must be picked up here, or the new tab would be read with the old query. A detail address is ignored.
        _address!.Accept(Nav.Uri);
        ReadUrlState(_address.Absolute);
        var queryKey = BuildCurrentQueryKey();
        if (string.Equals(queryKey, _lastParameterQueryKey, StringComparison.Ordinal))
        {
            return;
        }

        _lastParameterQueryKey = queryKey;
        await LoadBrowseContentAsync();
    }

    private async void OnLocationChanged(object? sender, Microsoft.AspNetCore.Components.Routing.LocationChangedEventArgs e)
    {
        try
        {
            if (_navigatingInternally)
            {
                _navigatingInternally = false;
                _address!.Accept(e.Location);
                return;
            }

            // The details overlay opening, swapping titles or closing leaves this page where it was: no re-read, no reload.
            if (!_address!.Accept(e.Location))
            {
                return;
            }

            ReadUrlState(_address.Absolute);
            await InvokeAsync(async () =>
            {
                if (_lastParameterQueryKey == BuildCurrentQueryKey()) { StateHasChanged(); return; }
                _lastParameterQueryKey = BuildCurrentQueryKey();
                await LoadBrowseContentAsync();
                StateHasChanged();
            });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to handle browse route change");
        }
    }

    private void ReadUrlState(string address)
    {
        var state = BrowseQueryBuilder.Read(Preset, Tab, address);
        _activeTabId = state.ActiveTabId;
        _grouping = state.Grouping;
        _layout = state.Layout;
        if (!string.Equals(_searchText, state.SearchText, StringComparison.Ordinal))
        {
            _searchInputVersion++;
        }
        _searchText = state.SearchText;
        _sortBy = state.SortBy;
        _timelineGrouping = state.Period;
        _selectedGenres.Clear();
        _selectedGenres.UnionWith(state.Genres);
        _selectedCreators.Clear();
        _selectedCreators.UnionWith(state.Creators);
        _status = state.Status;
        _unmatched = state.Unmatched;
        _libraryChip = state.LibraryChip;
        _selectedYears.Clear();
        _selectedYears.UnionWith(state.Years);
        _selectedVibes.Clear();
        _selectedVibes.UnionWith(state.Vibes ?? []);
        _selectedThemes.Clear();
        _selectedThemes.UnionWith(state.Themes ?? []);
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(address).Query);
        _requestedTrackId = Guid.TryParse(query["track"], out var trackId) ? trackId : null;
        ClearActiveGroupState();
    }

    public void Dispose()
    {
        Nav.LocationChanged -= OnLocationChanged;
        StateContainer.MediaAddedReceived -= OnMediaAdded;
        Orchestrator.OnLibraryItemsRemoved -= OnLibraryItemsRemoved;
        Playback.Changed -= OnPlaybackChanged;
        Editor.Closed -= OnEditorClosed;
        _mediaAddedReloadCts?.Cancel();
        _mediaAddedReloadCts?.Dispose();
        _browseRequestCts?.Cancel();
        _browseRequestCts?.Dispose();
        _browseRequestCts = null;
    }

    private static int GetSelectedIndex(IEnumerable<string> keys, string activeKey)
    {
        var index = 0;
        foreach (var key in keys)
        {
            if (string.Equals(key, activeKey, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }

            index++;
        }

        return 0;
    }
}
