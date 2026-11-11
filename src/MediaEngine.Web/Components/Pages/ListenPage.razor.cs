using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Components.Browse;
using MediaEngine.Web.Components.Library;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.MediaTiles;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Ui;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Components.Pages;

public partial class ListenPage
{
    private const string MusicHomeRoute = ListenNavigation.MusicHomeRoute;
    private const string AlbumsRoute = ListenNavigation.AlbumsRoute;
    private const string ArtistsRoute = ListenNavigation.ArtistsRoute;
    private const string SongsRoute = ListenNavigation.SongsRoute;
    private const string PlaylistsRoute = ListenNavigation.PlaylistsRoute;
    private const string AudiobooksRoute = ListenNavigation.AudiobooksRoute;
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(45);

    [Inject] private IEngineApiClient ApiClient { get; set; } = default!;
    [Inject] private UIOrchestratorService Orchestrator { get; set; } = default!;
    [Inject] private PlaybackSessionController Playback { get; set; } = default!;
    [Inject] private ListenAudioDragService AudioDrag { get; set; } = default!;
    [Inject] private MediaReactionService Reactions { get; set; } = default!;
    [Inject] private SavedItemService SavedItems { get; set; } = default!;
    [Inject] private IServiceProvider Services { get; set; } = default!;
    [Inject] private ListEditLauncher EditLauncher { get; set; } = default!;
    [Inject] private MediaEngine.Web.Services.Theming.DeviceContextService DeviceContext { get; set; } = default!;
    [Inject] private CollectionEditorLauncherService CollectionEditorLauncher { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IAppDialogService Dialogs { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    [Inject] private ILogger<ListenPage> Logger { get; set; } = default!;

    [Parameter] public string? Section { get; set; }
    [Parameter] public Guid? CollectionId { get; set; }
    [Parameter] public string? AlbumKey { get; set; }
    [Parameter] public string? ArtistKey { get; set; }
    [Parameter] public string? PlaylistKey { get; set; }
    [Parameter] public Guid? WorkId { get; set; }

    // The address this page was routed with (see RoutedPageQuery): the page under the details overlay keeps its own query.
    [CascadingParameter(Name = RoutedPageQuery.CascadingName)] public string? RoutedUri { get; set; }

    [CascadingParameter] public DetailsOverlayState? Overlay { get; set; }

    private RoutedPageQuery Query => RoutedPageQuery.From(RoutedUri ?? Nav.Uri);
    public Guid? Track => Query.Guid("track");
    public string? AlbumArtist => Query.String("artist");
    public string? BrowseQuery => Query.String("browse");

    private readonly List<WorkViewModel> _allWorks = [];
    private readonly List<WorkViewModel> _musicWorks = [];
    private readonly List<JourneyItemViewModel> _musicJourney = [];
    private readonly List<WorkViewModel> _audiobookWorks = [];
    private readonly List<DisplayCardDto> _audiobookCards = [];
    private readonly List<JourneyItemViewModel> _audiobookJourney = [];
    private readonly List<ContentGroupViewModel> _albumGroups = [];
    private readonly List<ContentGroupViewModel> _artistGroups = [];
    private readonly List<ContentGroupViewModel> _audiobookSeriesGroups = [];
    private readonly List<ContentGroupViewModel> _audiobookAuthorPersonGroups = [];
    private readonly List<ContentGroupViewModel> _audiobookNarratorPersonGroups = [];
    private readonly List<ManagedCollectionViewModel> _managedCollections = [];
    private readonly List<CollectionItemDto> _playlistItems = [];
    private readonly Dictionary<Guid, WorkViewModel> _workLookup = [];
    private readonly Dictionary<string, CollectionGroupDetailViewModel?> _artistDetailCache = new(StringComparer.OrdinalIgnoreCase);

    private CollectionGroupDetailViewModel? _albumDetail;
    private CollectionGroupDetailViewModel? _artistDetail;
    private DiscoveryPageViewModel _listenHubPage = new() { Key = "listen" };
    private ProfileViewModel? _activeProfile;
    private Guid? _activeProfileId;
    private HashSet<Guid> _favoriteWorkIds = [];
    private HashSet<Guid> _dislikedWorkIds = [];
    private bool _loading = true;
    private bool _redirecting;
    private bool _uiStateLoaded;
    private string? _error;
    private string _songSortColumn = "dateAdded";
    private bool _songSortDescending = true;
    private string _songSearch = string.Empty;
    private string _albumSearch = string.Empty;
    private string _albumSort = "recent";
    private string _albumArtistFilter = "all";
    private string _albumGenreFilter = "all";
    private bool _albumFavoriteOnly;
    private bool _albumArtOnly;
    private string _artistSearch = string.Empty;
    private string _artistSort = "name";
    private string _audiobookSearch = string.Empty;
    private string _audiobookView = "all";
    private string _audiobookSort = "recent";
    private string _audiobookLengthFilter = "all";
    private bool _audiobookInProgressOnly;
    private bool _audiobookUnreadOnly;
    private LibraryLayoutMode _audiobookLayout = LibraryLayoutMode.Card;
    private string? _audiobookSeriesFocus;
    private string? _lastHandledTrackContext;
    private string? _selectedArtistName;
    private string? _restoredArtistName;
    private string? _restoredMode;
    private string? _lastPersistedMode;
    private string? _lastPersistedArtistName;
    private HashSet<Guid> _selectedTrackIds = [];
    private HashSet<Guid> _draggingTrackIds = [];
    private bool _playlistsExpanded = true;
    private string? _playlistNavigationConfig;
    private List<Guid> _playlistOrder = [];
    private Guid? _draggingPlaylistId;
    private Guid? _pendingPlaylistRowFocusId;
    private string _playlistAnnouncement = string.Empty;
    private Guid? _draggingPlaylistTrackItemId;
    private Guid? _playlistTrackDropTargetItemId;
    private bool _playlistTrackDropAfter;
    private bool _activePlaylistSaved;
    private bool _creatingPlaylistInline;
    private string _newPlaylistName = string.Empty;
    private Guid? _renamingPlaylistId;
    private string _renamePlaylistName = string.Empty;
    private readonly HashSet<string> _playlistHiddenColumns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _playlistColumnWidths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["song"] = 360,
        ["favorite"] = 96,
        ["artist"] = 180,
        ["album"] = 220,
        ["time"] = 88,
    };
    private readonly Dictionary<string, int> _songColumnWidths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["song"] = 360,
        ["favorite"] = 96,
        ["time"] = 88,
        ["artist"] = 180,
        ["album"] = 220,
        ["genre"] = 150,
        ["plays"] = 92,
        ["dateAdded"] = 150,
    };
    private string? _resizingColumnSet;
    private string? _resizingColumnKey;
    private double _resizeStartX;
    private int _resizeStartWidth;
    private int _listenLoadVersion;

    private string? _noticeSection;

    protected override async Task OnParametersSetAsync()
    {
        // A notice belongs to the section it was raised in: moving to another Listen section starts clean.
        if (!string.Equals(_noticeSection, Section, StringComparison.OrdinalIgnoreCase))
        {
            _noticeSection = Section;
            _notice = null;
        }

        ApplyAudiobookQueryState();
        await LoadAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_uiStateLoaded)
        {
            await RestoreUiStateAsync();
            if (IsDefaultEntry)
            {
                return;
            }
        }

        if (_uiStateLoaded && !_loading && !_redirecting && !IsDefaultEntry)
        {
            await PersistUiStateAsync();
        }

        if (_pendingPlaylistRowFocusId is { } focusId)
        {
            _pendingPlaylistRowFocusId = null;
            await JS.InvokeVoidAsync("tuvimaFocusById", PlaylistRowDomId(focusId));
        }
    }

    private sealed record ListenPlaylistTrackRow(CollectionItemDto? Item, WorkViewModel Work, int Index);
    private sealed record PlaylistColumnDefinition(string Key, string Label);
    private sealed record AudiobookDisplayItem(DisplayCardDto? Card, WorkViewModel? Work);
    private sealed record AudiobookListenItem(AudiobookDisplayItem Item, JourneyItemViewModel? Journey);
    private sealed record AudiobookAuthorGroup(string Name, IReadOnlyList<AudiobookDisplayItem> Items);
}
