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
    [Inject] private MediaEditorLauncherService MediaEditorLauncher { get; set; } = default!;
    [Inject] private CollectionEditorLauncherService CollectionEditorLauncher { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private IAppToastService Snackbar { get; set; } = default!;
    [Inject] private ILogger<ListenPage> Logger { get; set; } = default!;

    [Parameter] public string? Section { get; set; }
    [Parameter] public Guid? CollectionId { get; set; }
    [Parameter] public string? AlbumKey { get; set; }
    [Parameter] public string? ArtistKey { get; set; }
    [Parameter] public string? PlaylistKey { get; set; }
    [Parameter] public Guid? WorkId { get; set; }
    [SupplyParameterFromQuery(Name = "track")] public Guid? Track { get; set; }
    [SupplyParameterFromQuery(Name = "edit")] public bool Edit { get; set; }
    [SupplyParameterFromQuery(Name = "artist")] public string? AlbumArtist { get; set; }
    [SupplyParameterFromQuery(Name = "browse")] public string? BrowseQuery { get; set; }

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
    private bool _artistLoading;
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
    private int _albumTileSizePx = 136;
    private string _artistSearch = string.Empty;
    private string _artistSort = "name";
    private string _audiobookSearch = string.Empty;
    private string _audiobookView = "all";
    private string _audiobookSort = "recent";
    private string _audiobookLengthFilter = "all";
    private bool _audiobookInProgressOnly;
    private bool _audiobookUnreadOnly;
    private LibraryLayoutMode _audiobookLayout = LibraryLayoutMode.Card;
    private int _audiobookTileSizePx = 160;
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
    private Guid? _draggingPlaylistTrackItemId;
    private Guid? _playlistTrackDropTargetItemId;
    private bool _playlistTrackDropAfter;
    private bool _activePlaylistSaved;
    private bool _creatingPlaylistInline;
    private string _newPlaylistName = string.Empty;
    private Guid? _renamingPlaylistId;
    private string _renamePlaylistName = string.Empty;
    private Guid? _deletingPlaylistId;
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

    private string CurrentPath => Nav.ToAbsoluteUri(Nav.Uri).AbsolutePath;
    private string NormalizedSection => string.IsNullOrWhiteSpace(Section) ? string.Empty : Section.Trim().ToLowerInvariant();
    private bool IsDefaultEntry => string.Equals(CurrentPath, "/listen", StringComparison.OrdinalIgnoreCase);
    private bool IsListenHub => IsDefaultEntry;
    private bool IsAudiobooksView => string.Equals(CurrentPath, AudiobooksRoute, StringComparison.OrdinalIgnoreCase);
    private bool IsAudiobookDetail => WorkId.HasValue && CurrentPath.StartsWith("/listen/audiobook/", StringComparison.OrdinalIgnoreCase);
    private bool IsAudiobooksMode => IsAudiobooksView || IsAudiobookDetail;
    private bool IsMusicMode => !IsAudiobooksMode;
    private bool IsMusicTimelineView => IsMusicMode
        && string.Equals(CurrentPath, MusicHomeRoute, StringComparison.OrdinalIgnoreCase)
        && string.Equals(BrowseQuery, "timeline", StringComparison.OrdinalIgnoreCase);
    private bool IsMusicBrowseEntry => IsMusicMode
        && !IsListenHub
        && string.Equals(CurrentPath, MusicHomeRoute, StringComparison.OrdinalIgnoreCase);
    private bool IsAlbumsView => IsMusicMode
        && !IsMusicTimelineView
        && !CollectionId.HasValue
        && (IsMusicBrowseEntry || string.Equals(NormalizedSection, "albums", StringComparison.OrdinalIgnoreCase));
    private bool IsAlbumDetail => IsMusicMode
        && (CollectionId.HasValue || !string.IsNullOrWhiteSpace(AlbumKey))
        && CurrentPath.StartsWith("/listen/music/albums/", StringComparison.OrdinalIgnoreCase);
    private bool IsAudioDetailSurface => IsAlbumDetail || IsAudiobookDetail;
    private bool IsSongsView => IsMusicMode && string.Equals(NormalizedSection, "songs", StringComparison.OrdinalIgnoreCase);
    private bool IsPlaylistsView => IsMusicMode && !CollectionId.HasValue && string.IsNullOrWhiteSpace(PlaylistKey) && string.Equals(NormalizedSection, "playlists", StringComparison.OrdinalIgnoreCase);
    private bool IsPlaylistSurface => IsMusicMode && ((CollectionId.HasValue && CurrentPath.StartsWith("/listen/music/playlists/", StringComparison.OrdinalIgnoreCase)) || !string.IsNullOrWhiteSpace(PlaylistKey));
    private bool IsArtistsSurface => IsMusicMode && (string.Equals(NormalizedSection, "artists", StringComparison.OrdinalIgnoreCase) || CurrentPath.StartsWith("/listen/music/artists/", StringComparison.OrdinalIgnoreCase));
    private string SelectedArtistName => _selectedArtistName ?? string.Empty;

    private string PageTitleText => IsAlbumDetail && _albumDetail is not null
        ? $"{_albumDetail.DisplayName} - Listen"
        : IsPlaylistSurface
            ? $"{ActivePlaylistTitle} - Listen"
            : IsArtistsSurface && !string.IsNullOrWhiteSpace(SelectedArtistName)
                ? $"{SelectedArtistName} - Listen"
                : IsDefaultEntry
                    ? "Listen - Tuvima Library"
                : IsMusicBrowseEntry
                    ? "Music - Listen"
                : IsAudiobooksMode
                    ? "Audiobooks - Listen"
                    : "Listen - Tuvima Library";

    private IReadOnlyList<WorkViewModel> SortedSongs => SortSongs(_musicWorks);
    private IReadOnlyList<WorkViewModel> FilteredSongs => ApplySongFilters(SortedSongs);
    private IReadOnlyList<WorkViewModel> RecentlyAddedTracks => _musicWorks.OrderByDescending(work => work.CreatedAt).ToList();
    private IReadOnlyList<WorkViewModel> AlbumTracks => _albumDetail is null
        ? []
        : ResolveGroupWorks(_albumDetail.Works)
            .OrderBy(work => ParseTrackNumber(work.TrackNumber))
            .ThenBy(DisplayTrackTitle, StringComparer.OrdinalIgnoreCase)
            .ToList();
    private IReadOnlyList<CollectionGroupSeasonDto> ArtistAlbums => _artistDetail?.Seasons ?? [];
    private IReadOnlyList<WorkViewModel> ArtistTracks => ResolveArtistTracks();
    private IReadOnlyList<WorkViewModel> ActivePlaylistTracks => ResolveActivePlaylistTracks();
    private IReadOnlyList<ListenPlaylistTrackRow> ActivePlaylistTrackRows => ResolveActivePlaylistTrackRows();
    private IReadOnlyList<PlaylistColumnDefinition> PlaylistColumns => BuildPlaylistColumns();
    private string PlaylistGridTemplate => BuildPlaylistGridTemplate();
    private string SongsGridTemplate => BuildSongsGridTemplate();
    private IReadOnlyList<ContentGroupViewModel> FilteredAlbumGroups => ApplyAlbumFilters();
    private IReadOnlyList<ContentGroupViewModel> FilteredArtistGroups => ApplyArtistFilters();
    private IReadOnlyList<MediaTileViewModel> ArtistPersonTiles => FilteredArtistGroups
        .Select(group => ToContentPersonTile(group, "Artist"))
        .ToList();
    private string ListenPageClass => IsAudioDetailSurface
        ? "listen-page listen-page--detail"
        : IsAudiobooksMode
            ? "listen-page listen-page--audiobooks"
            : "listen-page";
    private IReadOnlyList<AudiobookListenItem> AudiobookContinueCards => BuildAudiobookContinueCards();
    private IReadOnlyList<AudiobookDisplayItem> AudiobookDisplayItems => BuildAudiobookDisplayItems();
    private IReadOnlyList<AudiobookDisplayItem> FilteredAudiobookItems => ApplyAudiobookFilters();
    private IReadOnlyList<AudiobookDisplayItem> AudiobookBrowseItems => BuildAudiobookBrowseItems();
    private IReadOnlyList<MediaTileViewModel> AudiobookBrowseTiles => AudiobookBrowseItems.Select(ToAudiobookTile).ToList();
    private IReadOnlyList<ContentGroupViewModel> AudiobookSeriesGroups => ApplyAudiobookSeriesFilters();
    private IReadOnlyList<ContentGroupViewModel> FeaturedAudiobookSeries => _audiobookSeriesGroups
        .Where(group => group.WorkCount >= 2)
        .OrderByDescending(group => group.WorkCount)
        .ThenBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase)
        .Take(3)
        .ToList();
    private IReadOnlyList<AudiobookAuthorGroup> AudiobookAuthorGroups => BuildAudiobookAuthorGroups(FilteredAudiobookItems);
    private IReadOnlyList<AudiobookAuthorGroup> AudiobookNarratorGroups => BuildAudiobookNarratorGroups(FilteredAudiobookItems);
    private IReadOnlyList<MediaTileViewModel> AudiobookAuthorPersonTiles => AudiobookAuthorGroups
        .Select(group => ToAudiobookPersonTile(group, "Author", _audiobookAuthorPersonGroups))
        .ToList();
    private IReadOnlyList<MediaTileViewModel> AudiobookNarratorPersonTiles => AudiobookNarratorGroups
        .Select(group => ToAudiobookPersonTile(group, "Narrator", _audiobookNarratorPersonGroups))
        .ToList();
    private IReadOnlyList<MediaTileViewModel> MusicTimelineTiles => AlbumTiles;
    private string AudiobookBrowseTitle => _audiobookView switch
    {
        "series" => "Series",
        "authors" => "Authors",
        "narrators" => "Narrators",
        "timeline" => "Timeline",
        _ when !string.IsNullOrWhiteSpace(_audiobookSeriesFocus) => _audiobookSeriesFocus,
        _ => "Browse All",
    };
    private string AudiobookBrowseSummary => _audiobookView switch
    {
        "series" => AudiobookSeriesGroups.Count == 1 ? "1 series" : $"{AudiobookSeriesGroups.Count} series",
        "authors" => Pluralize(AudiobookAuthorGroups.Count, "author"),
        "narrators" => Pluralize(AudiobookNarratorGroups.Count, "narrator"),
        "timeline" => Pluralize(FilteredAudiobookItems.Count, "audiobook"),
        _ => Pluralize(FilteredAudiobookItems.Count, "audiobook"),
    };
    private string AudiobookBooksLayoutClass => _audiobookLayout == LibraryLayoutMode.List
        ? "audiobooks-books audiobooks-books--list"
        : "audiobooks-books";
    private IReadOnlyList<string> PlaylistCoverUrls => ActivePlaylistTracks.Select(track => track.CoverUrl).Where(url => !string.IsNullOrWhiteSpace(url)).Distinct().Cast<string>().Take(4).ToList();
    private IReadOnlyList<ManagedCollectionViewModel> PlaylistCollections => _managedCollections
        .Where(MediaTileComposerService.IsUserVisiblePlaylist)
        .OrderBy(GetPlaylistOrderIndex)
        .ThenBy(collection => collection.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();
    private IReadOnlyList<ManagedCollectionViewModel> ManualPlaylistCollections => PlaylistCollections
        .Where(CanAcceptPlaylistItems)
        .ToList();
    private bool HasPlaylistCollections => PlaylistCollections.Count > 0;
    private IReadOnlyList<MediaTileViewModel> HomePlaylistTiles => PlaylistCollections
        .Take(6)
        .Select(MediaTileComposerService.FromPlaylist)
        .ToList();
    private IReadOnlyList<MediaTileViewModel> AlbumTiles => FilteredAlbumGroups
        .Select(ToAlbumTile)
        .ToList();
    private IReadOnlyList<MediaTileViewModel> ArtistAlbumTiles => ArtistAlbums
        .Select(ToArtistAlbumTile)
        .ToList();
    private IReadOnlyList<MediaTileViewModel> PlaylistTiles => PlaylistCollections
        .Select(MediaTileComposerService.FromPlaylist)
        .ToList();
    private IReadOnlyList<string> SongGenres => _musicWorks
        .SelectMany(work => work.Genres)
        .Where(genre => !string.IsNullOrWhiteSpace(genre))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(genre => genre, StringComparer.OrdinalIgnoreCase)
        .ToList();
    private IReadOnlyList<string> SongArtists => _musicWorks
        .Select(work => StringHelpers.FirstNonBlankOr("-", work.Artist, work.Author, null))
        .Where(name => !string.IsNullOrWhiteSpace(name) && !string.Equals(name, "-", StringComparison.Ordinal))
        .Cast<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToList();
    private IReadOnlyList<string> SongAlbums => _musicWorks
        .Select(work => work.Album)
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Cast<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToList();
    private IReadOnlyList<string> AlbumArtists => _albumGroups
        .Select(album => album.Creator)
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Cast<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToList();
    private bool HasTrackSelection => _selectedTrackIds.Count > 0;
    private int SelectedTrackCount => _selectedTrackIds.Count;
    private string TrackSelectionStatusText => HasTrackSelection
        ? $"{SelectedTrackCount} selected"
        : "Single-click to select, ctrl/shift-click to add more, and use the play icon to start playback.";

    private static readonly IReadOnlyList<MediaHubModeViewModel> ListenHubModes =
    [
        new("all", "Discover", "/listen"),
        new("music", "Music", MusicHomeRoute),
        new("audiobooks", "Audiobooks", AudiobooksRoute),
    ];

    private static readonly IReadOnlyList<BrowseGroupingOption> MusicBrowseOptions =
    [
        new("albums", "Albums", AppMaterialIcons.Outlined.Album),
        new("artists", "Artists", AppMaterialIcons.Outlined.PersonOutline),
        new("songs", "Songs", AppMaterialIcons.Outlined.MusicNote),
        new("playlists", "Playlists", AppMaterialIcons.Outlined.QueueMusic),
        new("timeline", "Timeline", AppMaterialIcons.Outlined.Timeline),
    ];

    private static readonly IReadOnlyList<BrowseGroupingOption> AudiobookBrowseOptions =
    [
        new("all", "Audiobooks", AppMaterialIcons.Outlined.Headphones),
        new("authors", "Authors", AppMaterialIcons.Outlined.PersonOutline),
        new("series", "Series", AppMaterialIcons.Outlined.CollectionsBookmark),
        new("narrators", "Narrators", AppMaterialIcons.Outlined.RecordVoiceOver),
        new("timeline", "Timeline", AppMaterialIcons.Outlined.Timeline),
    ];

    private string MusicBrowseMode => IsMusicTimelineView
        ? "timeline"
        : IsArtistsSurface ? "artists"
        : IsSongsView ? "songs"
        : IsPlaylistsView || IsPlaylistSurface ? "playlists"
        : "albums";

    private bool IsMusicBrowseNavigationVisible => IsMusicMode && !IsAudioDetailSurface && !IsAlbumDetail;

    private string ActiveListenMode => IsListenHub
        ? "all"
        : IsAudiobooksMode
            ? "audiobooks"
            : "music";

    private IReadOnlyList<MediaHubShelfViewModel> ListenHubShelves
    {
        get
        {
            var shelves = _listenHubPage.Shelves
                .Where(shelf => shelf.Kind != MediaTileShelfKind.Collections)
                .Select(shelf => MediaHubShelfViewModel.FromShelf(shelf))
                .ToList();

            if (HomePlaylistTiles.Count > 0)
            {
                shelves.Add(new MediaHubShelfViewModel
                {
                    Key = "listen-playlists",
                    Title = "Playlists",
                    Subtitle = "Saved playlists and mixes from your library",
                    Items = HomePlaylistTiles,
                    SeeAllRoute = PlaylistsRoute,
                });
            }

            return shelves;
        }
    }

    private ManagedCollectionViewModel? ActivePlaylistCollection => CollectionId.HasValue
        ? PlaylistCollections.FirstOrDefault(collection => collection.Id == CollectionId.Value)
        : null;

    private bool CanSaveActivePlaylist => ActivePlaylistCollection is { } playlist
        && (!_activeProfileId.HasValue || playlist.ProfileId != _activeProfileId);

    private string ActivePlaylistTitle => !string.IsNullOrWhiteSpace(PlaylistKey)
        ? PlaylistKey.ToLowerInvariant() switch
        {
            "all-music" => "All Music",
            "favorite-songs" => "Favorite Songs",
            "recently-added" => "Recently Added",
            _ => "Playlist",
        }
        : ActivePlaylistCollection?.Name ?? "Playlist";

    private string ActivePlaylistTypeLabel => !string.IsNullOrWhiteSpace(PlaylistKey)
        ? "System Playlist"
        : ActivePlaylistCollection?.TypeLabel is { Length: > 0 } label
            ? $"{label} Playlist"
            : "Playlist";

    private string? ActivePlaylistDescription => !string.IsNullOrWhiteSpace(PlaylistKey)
        ? PlaylistKey.ToLowerInvariant() switch
        {
            "all-music" => "Every song in your library in one utility view.",
            "favorite-songs" => "Songs this profile has marked as Favorites.",
            "recently-added" => "Fresh music sorted by arrival time.",
            _ => null,
        }
        : ActivePlaylistCollection?.Description;

    private string? ActivePlaylistMeta => !string.IsNullOrWhiteSpace(PlaylistKey)
        ? null
        : ActivePlaylistCollection is null
            ? null
            : $"{ActivePlaylistCollection.ItemCount} items";

    private IReadOnlyList<ListenNavigationItem> ListenLibraryItems =>
    [
        new("Discover", "/listen", AppMaterialIcons.Outlined.Explore, null),
    ];

    private IReadOnlyList<ListenNavigationItem> ListenFormatItems =>
    [
        new("Audiobooks", AudiobooksRoute, AppMaterialIcons.Outlined.Headphones, _audiobookWorks.Count.ToString(CultureInfo.InvariantCulture)),
        new("Music", MusicHomeRoute, AppMaterialIcons.Outlined.MusicNote, _musicWorks.Count.ToString(CultureInfo.InvariantCulture)),
    ];

    private IReadOnlyList<ListenNavigationItem> ListenYourLibraryItems =>
    [
        new("Recently Added", "/listen/music/playlists/system/recently-added", AppMaterialIcons.Outlined.Schedule, RecentlyAddedTracks.Count.ToString(CultureInfo.InvariantCulture)),
    ];

    private IReadOnlyList<ListenNavigationItem> ListenPinnedPlaylistItems =>
    [
        new("Favorites", "/for-me?view=favorites&area=listen", AppMaterialIcons.Outlined.FavoriteBorder),
        new("All Playlists", PlaylistsRoute, AppMaterialIcons.Outlined.QueueMusic),
    ];

    protected override async Task OnParametersSetAsync()
    {
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
    }

    private async Task LoadAsync()
    {
        var loadVersion = ++_listenLoadVersion;
        _error = null;
        _loading = true;
        _redirecting = false;
        _albumDetail = null;
        _artistDetail = null;
        _artistLoading = false;
        _activePlaylistSaved = false;
        _playlistItems.Clear();
        _selectedTrackIds.Clear();
        CloseTrackContextMenu();
        _draggingTrackIds.Clear();
        AudioDrag.Clear();
        StateHasChanged();

        try
        {
            using var timeoutCts = new CancellationTokenSource(LoadTimeout);
            var ct = timeoutCts.Token;
            var profile = await Orchestrator.GetActiveProfileAsync(ct);
            if (!IsCurrentLoad(loadVersion))
            {
                return;
            }

            _activeProfile = profile;
            _activeProfileId = profile?.Id;
            _playlistNavigationConfig = profile?.NavigationConfig;
            _playlistOrder = ListenPlaylistOrderState.Read(profile?.NavigationConfig);

            var shouldLoadListenHub = IsListenHub;
            var shouldLoadMusicBrowseData = IsMusicMode && !shouldLoadListenHub;
            var shouldLoadJourney = shouldLoadListenHub || IsMusicMode || IsAudiobooksMode;
            var worksTask = Orchestrator.GetLibraryWorksAsync(ct);
            var journeyTask = shouldLoadJourney
                ? Orchestrator.GetJourneyAsync(_activeProfileId, 48, ct: ct)
                : Task.FromResult(new List<JourneyItemViewModel>());
            var listenHubTask = shouldLoadListenHub
                ? ApiClient.GetDisplayBrowseAsync(
                    lane: "listen",
                    grouping: "all",
                    includeCatalog: false,
                    profileId: _activeProfileId,
                    ct: ct)
                : Task.FromResult<DisplayPageDto?>(null);
            var albumGroupsTask = shouldLoadMusicBrowseData
                ? ApiClient.GetSystemViewGroupsAsync(mediaType: "Music", groupField: "album", ct: ct)
                : Task.FromResult(new List<ContentGroupViewModel>());
            var artistGroupsTask = shouldLoadMusicBrowseData
                ? ApiClient.GetSystemViewGroupsAsync(mediaType: "Music", groupField: "artist", ct: ct)
                : Task.FromResult(new List<ContentGroupViewModel>());
            var audiobookBrowseTask = IsAudiobooksMode
                ? ApiClient.GetDisplayBrowseAsync(
                    mediaType: "Audiobooks",
                    grouping: "all",
                    offset: 0,
                    limit: 240,
                    profileId: _activeProfileId,
                    ct: ct)
                : Task.FromResult<DisplayPageDto?>(null);
            var audiobookSeriesGroupsTask = IsAudiobooksMode
                ? ApiClient.GetSystemViewGroupsAsync(mediaType: "Audiobooks", groupField: "series", ct: ct)
                : Task.FromResult(new List<ContentGroupViewModel>());
            var audiobookAuthorGroupsTask = IsAudiobooksMode
                ? ApiClient.GetSystemViewGroupsAsync(mediaType: "Audiobooks", groupField: "author", ct: ct)
                : Task.FromResult(new List<ContentGroupViewModel>());
            var audiobookNarratorGroupsTask = IsAudiobooksMode
                ? ApiClient.GetSystemViewGroupsAsync(mediaType: "Audiobooks", groupField: "narrator", ct: ct)
                : Task.FromResult(new List<ContentGroupViewModel>());
            var collectionsTask = _activeProfileId.HasValue
                ? ApiClient.GetManagedCollectionsAsync(_activeProfileId.Value, ct)
                : Task.FromResult(new List<ManagedCollectionViewModel>());

            await Task.WhenAll(
                worksTask,
                journeyTask,
                listenHubTask,
                albumGroupsTask,
                artistGroupsTask,
                audiobookBrowseTask,
                audiobookSeriesGroupsTask,
                audiobookAuthorGroupsTask,
                audiobookNarratorGroupsTask,
                collectionsTask);
            if (!IsCurrentLoad(loadVersion))
            {
                return;
            }

            _allWorks.Clear();
            _allWorks.AddRange(worksTask.Result);

            _musicWorks.Clear();
            _musicWorks.AddRange(_allWorks.Where(IsMusicWork).OrderBy(work => work.Artist ?? work.Author).ThenBy(work => work.Album).ThenBy(work => ParseTrackNumber(work.TrackNumber)).ThenBy(DisplayTrackTitle));

            _audiobookWorks.Clear();
            _audiobookWorks.AddRange(_allWorks.Where(IsAudiobookWork).OrderBy(work => work.Author).ThenBy(work => work.Series).ThenBy(work => work.Title));

            _audiobookCards.Clear();
            if (audiobookBrowseTask.Result is not null)
            {
                _audiobookCards.AddRange(audiobookBrowseTask.Result.Catalog);
            }

            _musicJourney.Clear();
            _musicJourney.AddRange(journeyTask.Result.Where(item => IsMusicWork(item.MediaType)));

            _audiobookJourney.Clear();
            _audiobookJourney.AddRange(journeyTask.Result.Where(item => IsAudiobookWork(item.MediaType)));

            _workLookup.Clear();
            foreach (var work in _allWorks.GroupBy(work => work.Id).Select(group => group.First()))
            {
                _workLookup[work.Id] = work;
            }

            _albumGroups.Clear();
            var albumGroups = shouldLoadMusicBrowseData
                ? albumGroupsTask.Result.OrderBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase).ToList()
                : BuildMusicGroupSummaries(_musicWorks, "album");
            _albumGroups.AddRange(albumGroups.Count > 0 ? albumGroups : BuildMusicGroupSummaries(_musicWorks, "album"));

            _artistGroups.Clear();
            var artistGroups = shouldLoadMusicBrowseData
                ? artistGroupsTask.Result.OrderBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase).ToList()
                : BuildMusicGroupSummaries(_musicWorks, "artist");
            _artistGroups.AddRange(artistGroups.Count > 0 ? artistGroups : BuildMusicGroupSummaries(_musicWorks, "artist"));

            _audiobookSeriesGroups.Clear();
            _audiobookSeriesGroups.AddRange(audiobookSeriesGroupsTask.Result
                .Where(group => group.WorkCount >= 2)
                .OrderBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase));

            _audiobookAuthorPersonGroups.Clear();
            _audiobookAuthorPersonGroups.AddRange(audiobookAuthorGroupsTask.Result);

            _audiobookNarratorPersonGroups.Clear();
            _audiobookNarratorPersonGroups.AddRange(audiobookNarratorGroupsTask.Result);

            _managedCollections.Clear();
            _managedCollections.AddRange(collectionsTask.Result);

            _favoriteWorkIds = (await Reactions.GetFavoriteWorkIdsAsync(_activeProfileId, ct)).ToHashSet();
            _dislikedWorkIds = (await Reactions.GetDislikedWorkIdsAsync(_activeProfileId, ct)).ToHashSet();
            if (!IsCurrentLoad(loadVersion))
            {
                return;
            }

            if (shouldLoadListenHub)
            {
                _listenHubPage = MediaTileComposerService.FromDisplayPage(
                    listenHubTask.Result ?? throw new InvalidOperationException("Display API did not return the listen hub page."));
            }

            if (CollectionId.HasValue && IsPlaylistSurface)
            {
                _playlistItems.AddRange(await ApiClient.GetCollectionItemsAsync(CollectionId.Value, 1000, _activeProfileId, ct));
                if (CanSaveActivePlaylist)
                {
                    _activePlaylistSaved = (await SavedItems.GetMembershipAsync(
                        ProfileEntityKind.Playlist,
                        CollectionId.Value,
                        ct)).IsSaved;
                }
                if (!IsCurrentLoad(loadVersion))
                {
                    return;
                }
            }

            _loading = false;

            if (IsArtistsSurface)
            {
                await EnsureArtistSelectionAsync(ct);
            }

            await HandleTrackQueryAsync();
        }
        catch (OperationCanceledException)
        {
            if (!IsCurrentLoad(loadVersion))
            {
                return;
            }

            _error = "Listen took too long to load. Try again after the current background work settles.";
            _loading = false;
        }
        catch (Exception ex)
        {
            if (!IsCurrentLoad(loadVersion))
            {
                return;
            }

            _error = ex.Message;
            _loading = false;
        }
    }

    private async Task ToggleActivePlaylistSavedAsync()
    {
        if (ActivePlaylistCollection is null || !CanSaveActivePlaylist)
        {
            return;
        }

        var result = await SavedItems.ToggleAsync(ProfileEntityKind.Playlist, ActivePlaylistCollection.Id);
        if (result is null)
        {
            Snackbar.Add("The Playlist could not be updated in My List.", AppSeverity.Error);
            return;
        }

        _activePlaylistSaved = result.IsSaved;
        Snackbar.Add(result.IsSaved ? "Playlist added to My List." : "Playlist removed from My List.", AppSeverity.Success);
    }

    private bool IsCurrentLoad(int loadVersion)
        => loadVersion == _listenLoadVersion;

    private async Task RestoreUiStateAsync()
    {
        _uiStateLoaded = true;

        try
        {
            _restoredMode = await JS.InvokeAsync<string?>("listenUi.getMode");
            _restoredArtistName = await JS.InvokeAsync<string?>("listenUi.getSelectedArtist");
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not restore listen UI state from browser storage.");
        }

        _lastPersistedMode = _restoredMode;
        _lastPersistedArtistName = _restoredArtistName;

        if (IsDefaultEntry)
        {
            return;
        }

        if (IsArtistsSurface && !_loading && string.IsNullOrWhiteSpace(ArtistKey) && !string.IsNullOrWhiteSpace(_restoredArtistName))
        {
            await SelectArtistAsync(_restoredArtistName, persistSelection: false);
        }
    }

    private async Task PersistUiStateAsync()
    {
        try
        {
            var mode = IsAudiobooksMode ? "audiobooks" : "music";
            if (!string.Equals(_lastPersistedMode, mode, StringComparison.OrdinalIgnoreCase))
            {
                await JS.InvokeVoidAsync("listenUi.setMode", mode);
                _lastPersistedMode = mode;
            }

            if (!string.IsNullOrWhiteSpace(_selectedArtistName)
                && !string.Equals(_lastPersistedArtistName, _selectedArtistName, StringComparison.OrdinalIgnoreCase))
            {
                await JS.InvokeVoidAsync("listenUi.setSelectedArtist", _selectedArtistName);
                _lastPersistedArtistName = _selectedArtistName;
                _restoredArtistName = _selectedArtistName;
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not persist listen UI state to browser storage.");
        }
    }

    private int GetPlaylistOrderIndex(ManagedCollectionViewModel playlist)
    {
        var index = _playlistOrder.IndexOf(playlist.Id);
        return index < 0 ? int.MaxValue : index;
    }

    private async Task MovePlaylistAsync(Guid playlistId, int direction)
    {
        var orderedIds = PlaylistCollections.Select(playlist => playlist.Id).ToList();
        if (!ListenPlaylistOrderState.TryMoveByOffset(orderedIds, playlistId, direction, out var updatedOrder))
        {
            return;
        }

        _playlistOrder = updatedOrder;
        await SavePlaylistOrderAsync();
    }

    private void BeginPlaylistReorder(Guid playlistId)
    {
        _draggingPlaylistId = playlistId;
    }

    private void EndPlaylistReorder()
    {
        _draggingPlaylistId = null;
    }

    private async Task DropPlaylistReorderAsync(Guid targetPlaylistId)
    {
        if (!_draggingPlaylistId.HasValue || _draggingPlaylistId.Value == targetPlaylistId)
        {
            return;
        }

        var orderedIds = PlaylistCollections.Select(playlist => playlist.Id).ToList();
        if (!ListenPlaylistOrderState.TryMoveBefore(
                orderedIds,
                _draggingPlaylistId.Value,
                targetPlaylistId,
                out var updatedOrder))
        {
            return;
        }

        _playlistOrder = updatedOrder;
        _draggingPlaylistId = null;
        await SavePlaylistOrderAsync();
    }

    private void BeginPlaylistInlineCreate()
    {
        _creatingPlaylistInline = true;
        _newPlaylistName = string.Empty;
    }

    private async Task CreatePlaylistOnKeyDownAsync(KeyboardEventArgs args)
    {
        if (string.Equals(args.Key, "Escape", StringComparison.Ordinal))
        {
            _creatingPlaylistInline = false;
            _newPlaylistName = string.Empty;
            return;
        }
        if (!string.Equals(args.Key, "Enter", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(_newPlaylistName))
        {
            return;
        }

        if (_activeProfileId is null)
        {
            Snackbar.Add("An active profile is required to create playlists.", AppSeverity.Warning);
            return;
        }
        var name = _newPlaylistName.Trim();
        var createdId = await ApiClient.CreateCollectionWithItemsAsync(
            name,
            null,
            AppMaterialIcons.Outlined.QueueMusic,
            "Playlist",
            new CollectionRuleDefinitionViewModel(),
            "title",
            "asc",
            "private",
            [],
            _activeProfileId);
        if (createdId is null)
        {
            Snackbar.Add("The Playlist could not be created.", AppSeverity.Error);
            return;
        }
        _creatingPlaylistInline = false;
        _newPlaylistName = string.Empty;
        Snackbar.Add($"Created {name}.", AppSeverity.Success);
        await LoadAsync();
    }

    private void BeginPlaylistRename(ManagedCollectionViewModel playlist)
    {
        _deletingPlaylistId = null;
        _renamingPlaylistId = playlist.Id;
        _renamePlaylistName = playlist.Name;
    }

    private async Task RenamePlaylistOnKeyDownAsync(ManagedCollectionViewModel playlist, KeyboardEventArgs args)
    {
        if (string.Equals(args.Key, "Escape", StringComparison.Ordinal))
        {
            _renamingPlaylistId = null;
            return;
        }
        if (!string.Equals(args.Key, "Enter", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(_renamePlaylistName))
        {
            return;
        }

        if (!await ApiClient.UpdateCollectionAsync(
                playlist.Id,
                _renamePlaylistName.Trim(),
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                _activeProfileId))
        {
            Snackbar.Add("The Playlist could not be renamed.", AppSeverity.Error);
            return;
        }
        _renamingPlaylistId = null;
        await LoadAsync();
    }

    private void BeginPlaylistDelete(ManagedCollectionViewModel playlist)
    {
        _renamingPlaylistId = null;
        _deletingPlaylistId = playlist.Id;
    }

    private async Task DeletePlaylistAsync(ManagedCollectionViewModel playlist)
    {
        if (!await ApiClient.DeleteCollectionAsync(playlist.Id, _activeProfileId))
        {
            Snackbar.Add("The Playlist could not be deleted.", AppSeverity.Error);
            return;
        }
        _deletingPlaylistId = null;
        Snackbar.Add($"Deleted {playlist.Name}.", AppSeverity.Success);
        if (CollectionId == playlist.Id)
        {
            Nav.NavigateTo("/listen/playlists");
            return;
        }
        await LoadAsync();
    }

    private async Task OpenPlaylistCreateDialog(string kind)
    {
        if (_activeProfileId is null)
        {
            Snackbar.Add("An active profile is required to create playlists.", AppSeverity.Warning);
            return;
        }

        var isSmart = string.Equals(kind, "Smart", StringComparison.OrdinalIgnoreCase);
        var changed = await CollectionEditorLauncher.OpenAsync(new CollectionEditorLaunchRequest
        {
            ActiveProfileId = _activeProfileId,
            Kind = ContainerEditorKind.Playlist,
            InitialMembershipMode = isSmart ? "Smart" : "Manual",
            InitialTitle = string.Empty,
        });

        if (changed)
        {
            await LoadAsync();
        }
    }
    private async Task SavePlaylistOrderAsync()
    {
        if (_activeProfile is null)
        {
            return;
        }

        var updatedConfig = ListenPlaylistOrderState.Write(_playlistNavigationConfig, _playlistOrder);
        var saved = await Orchestrator.UpdateProfileAsync(
            _activeProfile.Id,
            _activeProfile.DisplayName,
            _activeProfile.AvatarColor,
            updatedConfig);

        if (saved)
        {
            _playlistNavigationConfig = updatedConfig;
            Snackbar.Add("Playlist order saved.", AppSeverity.Success);
        }
        else
        {
            Snackbar.Add("Playlist order could not be saved.", AppSeverity.Warning);
        }
    }

    private void TogglePlaylistColumn(string key)
    {
        if (!_playlistHiddenColumns.Add(key))
        {
            _playlistHiddenColumns.Remove(key);
        }
    }

    private bool IsPlaylistColumnVisible(string key) => !_playlistHiddenColumns.Contains(key);

    private IReadOnlyList<PlaylistColumnDefinition> BuildPlaylistColumns()
    {
        var columns = new List<PlaylistColumnDefinition>
        {
            new("favorite", "Favorite"),
            new("artist", "Artist"),
            new("album", "Album"),
            new("time", "Time"),
        };

        return columns;
    }

    private string BuildPlaylistGridTemplate()
    {
        var tracks = new List<string> { $"{_playlistColumnWidths["song"]}px" };
        if (IsPlaylistColumnVisible("favorite"))
        {
            tracks.Add($"{_playlistColumnWidths["favorite"]}px");
        }

        if (IsPlaylistColumnVisible("artist"))
        {
            tracks.Add($"{_playlistColumnWidths["artist"]}px");
        }

        if (IsPlaylistColumnVisible("album"))
        {
            tracks.Add($"{_playlistColumnWidths["album"]}px");
        }

        if (IsPlaylistColumnVisible("time"))
        {
            tracks.Add($"{_playlistColumnWidths["time"]}px");
        }

        tracks.Add("118px");
        return string.Join(" ", tracks);
    }

    private string BuildSongsGridTemplate()
    {
        var tracks = new[]
        {
            $"{_songColumnWidths["song"]}px",
            $"{_songColumnWidths["favorite"]}px",
            $"{_songColumnWidths["time"]}px",
            $"{_songColumnWidths["artist"]}px",
            $"{_songColumnWidths["album"]}px",
            $"{_songColumnWidths["genre"]}px",
            $"{_songColumnWidths["plays"]}px",
            $"{_songColumnWidths["dateAdded"]}px",
            "52px",
        };

        return string.Join(" ", tracks);
    }

    private void BeginColumnResize(string set, string key, double clientX)
    {
        var widths = string.Equals(set, "songs", StringComparison.OrdinalIgnoreCase)
            ? _songColumnWidths
            : _playlistColumnWidths;

        if (!widths.TryGetValue(key, out var width))
        {
            return;
        }

        _resizingColumnSet = set;
        _resizingColumnKey = key;
        _resizeStartX = clientX;
        _resizeStartWidth = width;
    }

    private void ResizeColumn(double clientX)
    {
        if (string.IsNullOrWhiteSpace(_resizingColumnSet) || string.IsNullOrWhiteSpace(_resizingColumnKey))
        {
            return;
        }

        var widths = string.Equals(_resizingColumnSet, "songs", StringComparison.OrdinalIgnoreCase)
            ? _songColumnWidths
            : _playlistColumnWidths;

        if (!widths.ContainsKey(_resizingColumnKey))
        {
            return;
        }

        widths[_resizingColumnKey] = Math.Clamp(_resizeStartWidth + (int)Math.Round(clientX - _resizeStartX), 64, 560);
    }

    private void EndColumnResize()
    {
        _resizingColumnSet = null;
        _resizingColumnKey = null;
    }

    private void BeginPlaylistTrackReorder(Guid itemId)
    {
        _draggingPlaylistTrackItemId = itemId;
        _playlistTrackDropTargetItemId = null;
        _playlistTrackDropAfter = false;
    }

    private Task BeginPlaylistTrackReorderAsync(Guid itemId)
    {
        BeginPlaylistTrackReorder(itemId);
        return Task.CompletedTask;
    }

    private void EndPlaylistTrackReorder()
    {
        _draggingPlaylistTrackItemId = null;
        _playlistTrackDropTargetItemId = null;
        _playlistTrackDropAfter = false;
    }

    private Task SetPlaylistTrackDropTargetAsync(Guid targetItemId, bool after)
    {
        if (_draggingPlaylistTrackItemId.HasValue && _draggingPlaylistTrackItemId.Value != targetItemId)
        {
            _playlistTrackDropTargetItemId = targetItemId;
            _playlistTrackDropAfter = after;
            StateHasChanged();
        }

        return Task.CompletedTask;
    }

    private Task SetPlaylistTrackEndDropTargetAsync()
    {
        if (!_draggingPlaylistTrackItemId.HasValue)
        {
            return Task.CompletedTask;
        }

        _playlistTrackDropTargetItemId = null;
        _playlistTrackDropAfter = true;
        StateHasChanged();
        return Task.CompletedTask;
    }

    private async Task DropPlaylistTrackReorderAsync(Guid targetItemId, bool after = false)
    {
        if (!_draggingPlaylistTrackItemId.HasValue || _draggingPlaylistTrackItemId.Value == targetItemId)
        {
            return;
        }

        var orderedItems = _playlistItems.OrderBy(item => item.SortOrder).ToList();
        var sourceIndex = orderedItems.FindIndex(item => item.Id == _draggingPlaylistTrackItemId.Value);
        var targetIndex = orderedItems.FindIndex(item => item.Id == targetItemId);
        if (sourceIndex < 0 || targetIndex < 0)
        {
            return;
        }

        var moved = orderedItems[sourceIndex];
        orderedItems.RemoveAt(sourceIndex);
        if (sourceIndex < targetIndex)
        {
            targetIndex--;
        }

        if (after)
        {
            targetIndex++;
        }

        targetIndex = Math.Clamp(targetIndex, 0, orderedItems.Count);
        orderedItems.Insert(targetIndex, moved);
        await SavePlaylistTrackOrderAsync(orderedItems);
        EndPlaylistTrackReorder();
    }

    private async Task DropPlaylistTrackAtEndAsync()
    {
        if (!_draggingPlaylistTrackItemId.HasValue)
        {
            return;
        }

        var orderedItems = _playlistItems.OrderBy(item => item.SortOrder).ToList();
        var sourceIndex = orderedItems.FindIndex(item => item.Id == _draggingPlaylistTrackItemId.Value);
        if (sourceIndex < 0 || sourceIndex == orderedItems.Count - 1)
        {
            return;
        }

        var moved = orderedItems[sourceIndex];
        orderedItems.RemoveAt(sourceIndex);
        orderedItems.Add(moved);
        await SavePlaylistTrackOrderAsync(orderedItems);
        EndPlaylistTrackReorder();
    }

    private async Task ReloadAsync() => await LoadAsync();

    private async Task EnsureArtistSelectionAsync(CancellationToken ct = default)
    {
        if (_artistGroups.Count == 0)
        {
            _selectedArtistName = null;
            _artistDetail = null;
            return;
        }

        var desiredArtist = ResolveDesiredArtistName();
        if (string.IsNullOrWhiteSpace(desiredArtist))
        {
            _selectedArtistName = null;
            _artistDetail = null;
            return;
        }

        await SelectArtistAsync(desiredArtist, persistSelection: false, ct);
    }

    private string? ResolveDesiredArtistName()
    {
        if (!string.IsNullOrWhiteSpace(ArtistKey))
        {
            return CanonicalArtistName(Uri.UnescapeDataString(ArtistKey));
        }

        if (!string.IsNullOrWhiteSpace(_selectedArtistName) && ArtistExists(_selectedArtistName))
        {
            return CanonicalArtistName(_selectedArtistName);
        }

        if (!string.IsNullOrWhiteSpace(_restoredArtistName) && ArtistExists(_restoredArtistName))
        {
            return CanonicalArtistName(_restoredArtistName);
        }

        return _artistGroups.FirstOrDefault()?.DisplayName;
    }

    private bool ArtistExists(string artistName)
        => _artistGroups.Any(artist => string.Equals(artist.DisplayName, artistName, StringComparison.OrdinalIgnoreCase));

    private string CanonicalArtistName(string artistName)
        => _artistGroups.FirstOrDefault(artist => string.Equals(artist.DisplayName, artistName, StringComparison.OrdinalIgnoreCase))?.DisplayName
           ?? artistName;

    private async Task SelectArtistAsync(string artistName, bool persistSelection = true, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artistName))
        {
            return;
        }

        artistName = CanonicalArtistName(artistName);
        if (string.Equals(_selectedArtistName, artistName, StringComparison.OrdinalIgnoreCase) && _artistDetail is not null)
        {
            if (persistSelection)
            {
                await PersistArtistSelectionAsync(artistName);
            }

            return;
        }

        _selectedArtistName = artistName;
        _artistLoading = true;
        _artistDetail = null;
        StateHasChanged();

        try
        {
            if (!_artistDetailCache.TryGetValue(artistName, out var detail))
            {
                detail = await ApiClient.GetArtistDetailByNameAsync(artistName, ct);
                _artistDetailCache[artistName] = detail;
            }

            if (string.Equals(_selectedArtistName, artistName, StringComparison.OrdinalIgnoreCase))
            {
                _artistDetail = detail;
            }
        }
        catch (Exception ex)
        {
            if (string.Equals(_selectedArtistName, artistName, StringComparison.OrdinalIgnoreCase))
            {
                Snackbar.Add($"Could not load {artistName}: {ex.Message}", AppSeverity.Error);
            }
        }
        finally
        {
            if (string.Equals(_selectedArtistName, artistName, StringComparison.OrdinalIgnoreCase))
            {
                _artistLoading = false;

                if (persistSelection)
                {
                    await PersistArtistSelectionAsync(artistName);
                }

                StateHasChanged();
            }
        }
    }

    private async Task PersistArtistSelectionAsync(string artistName)
    {
        if (!_uiStateLoaded)
        {
            return;
        }

        try
        {
            await JS.InvokeVoidAsync("listenUi.setSelectedArtist", artistName);
            _lastPersistedArtistName = artistName;
            _restoredArtistName = artistName;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not persist selected listen artist {ArtistName}.", artistName);
        }
    }

    private async Task HandleTrackQueryAsync()
    {
        if (!Track.HasValue)
        {
            _lastHandledTrackContext = null;
            return;
        }

        var context = $"{CurrentPath}|{Track.Value}|{Edit}";
        if (string.Equals(_lastHandledTrackContext, context, StringComparison.Ordinal))
        {
            return;
        }

        _lastHandledTrackContext = context;

        if (!_workLookup.TryGetValue(Track.Value, out var requestedTrack))
        {
            return;
        }

        _selectedTrackIds = [requestedTrack.Id];

        if (Edit)
        {
            await OpenTrackEditorAsync(requestedTrack, navigateBackToTrackContext: true);
            return;
        }
    }

    private void NavigateTo(string route, bool forceLoad = false)
    {
        CloseTrackContextMenu();
        _draggingTrackIds.Clear();
        AudioDrag.Clear();
        Nav.NavigateTo(route, forceLoad: forceLoad);
    }

    private void NavigateRailLink(string route)
        => NavigateTo(route, forceLoad: true);

    private async Task<CollectionGroupDetailViewModel?> LoadAlbumDetailAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(AlbumKey))
        {
            var albumName = Uri.UnescapeDataString(AlbumKey);
            return await ApiClient.GetSystemViewGroupDetailAsync(
                "album",
                albumName,
                mediaType: "Music",
                artistName: string.IsNullOrWhiteSpace(AlbumArtist) ? null : Uri.UnescapeDataString(AlbumArtist),
                ct: ct);
        }

        if (!CollectionId.HasValue)
        {
            return null;
        }

        var selectedAlbum = _albumGroups.FirstOrDefault(group => group.CollectionId == CollectionId.Value);
        if (selectedAlbum is not null)
        {
            return await ApiClient.GetSystemViewGroupDetailAsync(
                "album",
                selectedAlbum.DisplayName,
                mediaType: "Music",
                artistName: selectedAlbum.Creator,
                ct: ct);
        }

        return await ApiClient.GetCollectionGroupDetailAsync(CollectionId.Value, ct);
    }

    private DetailPageViewModel? BuildAlbumDetailModel(CollectionGroupDetailViewModel? detail)
    {
        if (detail is null)
        {
            return null;
        }

        var id = detail.RootWorkId ?? detail.CollectionId;
        var artworkUrl = StringHelpers.FirstNonBlank(detail.CoverUrl, detail.HeroUrl, detail.BackgroundUrl);
        var artist = StringHelpers.FirstNonBlank(detail.Creator, detail.Writer, detail.Director);
        var tracks = detail.Works
            .OrderBy(work => ParseTrackNumber(StringHelpers.FirstNonBlank(work.TrackNumber, work.Ordinal?.ToString(CultureInfo.InvariantCulture))))
            .ThenBy(work => work.Title, StringComparer.OrdinalIgnoreCase)
            .Select(work => new MediaGroupingItemViewModel
            {
                Id = work.WorkId.ToString("D"),
                EntityType = DetailEntityType.Work,
                Title = string.IsNullOrWhiteSpace(work.Title) ? "Untitled track" : work.Title,
                Subtitle = artist,
                ArtworkUrl = StringHelpers.FirstNonBlank(work.CoverUrl, artworkUrl),
                TrackNumber = StringHelpers.FirstNonBlank(work.TrackNumber, work.Ordinal?.ToString(CultureInfo.InvariantCulture)),
                Duration = work.Duration,
                DurationSeconds = work.DurationSeconds,
                Artist = artist,
                AssetId = work.AssetId?.ToString("D"),
                Metadata = string.IsNullOrWhiteSpace(work.Duration) ? [] : [new MetadataPill { Label = work.Duration!, Kind = "duration" }],
                Actions = work.WorkId == Guid.Empty ? [] : [new DetailAction { Key = "play-track", Label = "Play", Icon = "play_arrow" }],
                IsOwned = work.IsOwned,
                ProgressState = work.IsOwned ? LibraryProgressState.Unstarted : LibraryProgressState.Missing,
            })
            .ToList();

        IReadOnlyList<CreditGroupViewModel> contributors = string.IsNullOrWhiteSpace(artist)
            ? []
            : new List<CreditGroupViewModel>
            {
                new()
                {
                    Title = "Artist",
                    GroupType = CreditGroupType.MusicCredits,
                    Credits =
                    [
                        new EntityCreditViewModel
                        {
                            EntityType = RelatedEntityType.Person,
                            DisplayName = artist!,
                            FallbackInitials = Initials(artist!),
                            PrimaryRole = "Artist",
                            IsPrimary = true,
                        },
                    ],
                },
            };

        return new DetailPageViewModel
        {
            Id = id.ToString("D"),
            EntityType = DetailEntityType.MusicAlbum,
            PresentationContext = DetailPresentationContext.Listen,
            EditorTarget = id == Guid.Empty
                ? null
                : new DetailEditorTarget
                {
                    EntityId = id.ToString("D"),
                    EntityKind = detail.RootWorkId.HasValue ? "Work" : "Collection",
                    ContainerMode = detail.RootWorkId.HasValue ? "Canonical" : "Curated",
                    InitialTab = "tracks",
                },
            Title = detail.DisplayName,
            Subtitle = artist,
            Description = detail.Description,
            Tagline = detail.Tagline,
            Artwork = new ArtworkSet
            {
                CoverUrl = artworkUrl,
                PosterUrl = artworkUrl,
                BackdropUrl = StringHelpers.FirstNonBlank(detail.BackgroundUrl, detail.HeroUrl, detail.BannerUrl),
                BannerUrl = detail.BannerUrl,
                LogoUrl = detail.LogoUrl,
                DominantColors = detail.DominantColors,
                PrimaryColor = detail.PrimaryColor,
                SecondaryColor = detail.SecondaryColor,
                AccentColor = detail.AccentColor,
                HeroArtwork = new HeroArtworkViewModel
                {
                    Url = StringHelpers.FirstNonBlank(detail.BackgroundUrl, detail.HeroUrl, artworkUrl),
                    Mode = HeroArtworkMode.ArtworkFallback,
                    HasImage = !string.IsNullOrWhiteSpace(StringHelpers.FirstNonBlank(detail.BackgroundUrl, detail.HeroUrl, artworkUrl)),
                },
                PresentationMode = ArtworkPresentationMode.ColorGradientFromArtwork,
            },
            Metadata = BuildAlbumDetailMetadata(detail, tracks.Count),
            PrimaryActions =
            [
                new DetailAction { Key = "play-album", Label = "Listen", Icon = "headphones", IsPrimary = true },
            ],
            SecondaryActions =
            [
                new DetailAction
                {
                    Key = "my-list",
                    Label = _favoriteWorkIds.Contains(id) ? "In My List" : "My List",
                    Icon = _favoriteWorkIds.Contains(id) ? "playlist_add_check" : "playlist_add",
                    Tooltip = _favoriteWorkIds.Contains(id) ? "Remove from My List" : "Add to My List",
                    DisplayStyle = "icon",
                    IsSelected = _favoriteWorkIds.Contains(id),
                },
                new DetailAction
                {
                    Key = "reaction-menu",
                    Label = "Rate",
                    Icon = "thumb_up",
                    Tooltip = "Rate this title",
                    DisplayStyle = "icon",
                    Children =
                    [
                        new DetailAction { Key = "reaction-dislike", Label = "Not for me", Icon = "thumb_down" },
                        new DetailAction { Key = "reaction-like", Label = "I like this", Icon = "thumb_up" },
                        new DetailAction { Key = "reaction-love", Label = "I love this", Icon = "favorite" },
                    ],
                },
                new DetailAction { Key = "shuffle", Label = "Shuffle", Icon = "shuffle", DisplayStyle = "icon" },
            ],
            OverflowActions = [],
            ContributorGroups = contributors,
            PreviewContributors = contributors.SelectMany(group => group.Credits).Take(3).ToList(),
            Tabs =
            [
                new DetailTab { Key = "tracks", Label = "Tracks" },
                new DetailTab { Key = "overview", Label = "Overview" },
                new DetailTab { Key = "credits", Label = "Credits" },
                new DetailTab { Key = "related", Label = "Related" },
                new DetailTab { Key = "details", Label = "Details" },
            ],
            MediaGroups =
            [
                new MediaGroupingViewModel
                {
                    Key = "tracks",
                    Title = "Tracks",
                    Items = tracks,
                    OwnedCount = tracks.Count(track => track.IsOwned),
                    TotalCount = tracks.Count,
                    MissingCount = tracks.Count(track => !track.IsOwned),
                    CompletionPercent = tracks.Count == 0
                        ? 0
                        : Math.Round((double)tracks.Count(track => track.IsOwned) / tracks.Count * 100d, 1),
                },
            ],
            IdentityStatus = CanonicalIdentityStatus.ProviderMatched,
            LibraryStatus = MediaEngine.Contracts.Details.LibraryStatus.Owned,
        };
    }

    private static IReadOnlyList<MetadataPill> BuildAlbumDetailMetadata(CollectionGroupDetailViewModel detail, int trackCount)
    {
        var values = new List<MetadataPill>();

        AddMetadata(values, StringHelpers.FirstNonBlank(detail.YearRange, detail.ReleaseDate), "year");
        AddMetadata(values, Pluralize(trackCount > 0 ? trackCount : detail.TotalItems, "track"), "track_count");
        AddMetadata(values, detail.TotalDuration, "duration");
        AddMetadata(values, detail.Genre, "genre");
        return values;
    }

    private static void AddMetadata(List<MetadataPill> values, string? label, string kind)
    {
        if (!string.IsNullOrWhiteSpace(label))
        {
            values.Add(new MetadataPill { Label = label, Kind = kind });
        }
    }

    private static string Initials(string value)
        => string.Join(
            string.Empty,
            value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Take(2)
                .Select(part => char.ToUpperInvariant(part[0])));

    private string BuildAlbumDetailRoute(string? albumName, string? artistName = null, Guid? trackId = null, bool edit = false)
    {
        var album = _albumGroups.FirstOrDefault(group =>
            string.Equals(group.DisplayName, albumName, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(artistName)
                || string.Equals(group.Creator, artistName, StringComparison.OrdinalIgnoreCase)));
        if (album is not null)
        {
            var trackQuery = trackId.HasValue ? $"&track={trackId.Value:D}" : string.Empty;
            return $"/details/musicalbum/{album.CollectionId:D}?context=listen{trackQuery}";
        }

        return trackId.HasValue
            ? $"/listen/music?browse=songs&track={trackId.Value:D}"
            : AlbumsRoute;
    }

    private bool IsRouteActive(string route)
    {
        if (string.Equals(route, "/listen", StringComparison.OrdinalIgnoreCase))
        {
            return IsDefaultEntry;
        }

        var routeUri = Nav.ToAbsoluteUri(route);
        var currentUri = Nav.ToAbsoluteUri(Nav.Uri);
        if (!string.IsNullOrWhiteSpace(routeUri.Query))
        {
            return string.Equals(CurrentPath, routeUri.AbsolutePath, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(currentUri.Query, routeUri.Query, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(BrowseQuery)
            && string.Equals(CurrentPath, routeUri.AbsolutePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(CurrentPath, routeUri.AbsolutePath, StringComparison.OrdinalIgnoreCase)
               || CurrentPath.StartsWith(routeUri.AbsolutePath.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsRouteActiveExact(string route)
        => string.Equals(route, "/listen", StringComparison.OrdinalIgnoreCase)
            ? IsDefaultEntry
            : string.Equals(CurrentPath, route, StringComparison.OrdinalIgnoreCase);

    private async Task PlaySingleWorkAsync(WorkViewModel work, string sourceLabel)
    {
        await Playback.PlayWorkAsync(work, sourceLabel);
        Snackbar.Add($"{work.Title} added to the queue", AppSeverity.Success);
    }

    private async Task PlayTracksAsync(IReadOnlyList<WorkViewModel> works, Guid? startWorkId, string? sourceLabel, bool shuffle = false)
    {
        if (works.Count == 0)
        {
            return;
        }

        var startIndex = 0;
        if (startWorkId.HasValue)
        {
            for (var index = 0; index < works.Count; index++)
            {
                if (works[index].Id == startWorkId.Value)
                {
                    startIndex = index;
                    break;
                }
            }
        }

        var items = works.Select(ListenQueueItemFactory.Create).ToList();
        if (IsPlaylistSurface && ActivePlaylistCollection is { } playlist)
        {
            items = items.Select(item => item with { PlaylistId = playlist.Id }).ToList();
        }

        await Playback.ReplaceQueueItemsAsync(items, startIndex, sourceLabel, shuffle);
    }

    private async Task QueueTrackAsync(WorkViewModel work)
    {
        await Playback.AddToQueueAsync(work);
        Snackbar.Add($"{work.Title} added to the queue", AppSeverity.Success);
    }

    private async Task EditTrackAsync(WorkViewModel work)
    {
        var albumRoute = BuildAlbumRouteForTrack(work, edit: true);
        if (!string.IsNullOrWhiteSpace(albumRoute))
        {
            NavigateTo(albumRoute);
            return;
        }

        await OpenTrackEditorAsync(work, navigateBackToTrackContext: false);
    }

    private async Task DeleteTrackAsync(WorkViewModel work)
    {
        var confirmed = await JS.InvokeAsync<bool>("confirm", $"Delete {work.Title} from the library?");
        if (!confirmed)
        {
            return;
        }

        var response = await ApiClient.BatchDeleteLibraryCatalogItemsAsync([work.Id]);
        if (response is null)
        {
            Snackbar.Add("Delete failed.", AppSeverity.Error);
            return;
        }

        CloseTrackContextMenu();
        await LoadAsync();
        Snackbar.Add($"{work.Title} deleted.", AppSeverity.Success);
    }

    private async Task OpenTrackEditorAsync(WorkViewModel work, bool navigateBackToTrackContext)
    {
        var applied = await MediaEditorLauncher.OpenAsync(new MediaEditorLaunchRequest
        {
            EntityIds = [work.Id],
            LaunchEntityId = work.Id,
            LaunchEntityKind = "Work",
            Mode = SharedMediaEditorMode.Normal,
            MediaType = work.MediaType,
            HeaderTitle = DisplayTrackTitle(work),
            HeaderSubtitle = StringHelpers.FirstNonBlankOr("-", work.Artist, work.Author, work.Album, work.Series),
            CoverUrl = work.CoverUrl,
            PreviewItems =
            [
                new MediaEditorPreviewItem
                {
                    EntityId = work.Id,
                    Title = DisplayTrackTitle(work),
                    CoverUrl = work.CoverUrl,
                    MediaType = work.MediaType,
                }
            ],
        });

        if (applied)
        {
            await LoadAsync();
        }

        var trackContextRoute = BuildAlbumRouteForTrack(work);
        if (navigateBackToTrackContext && !string.IsNullOrWhiteSpace(trackContextRoute))
        {
            Nav.NavigateTo(trackContextRoute, replace: true);
        }
    }

    private string? BuildAlbumRouteForTrack(WorkViewModel work, bool edit = false)
    {
        if (work.CollectionId.HasValue)
        {
            return $"/details/musicalbum/{work.CollectionId.Value:D}?context=listen&track={work.Id:D}";
        }

        if (string.IsNullOrWhiteSpace(work.Album))
        {
            return null;
        }

        return BuildAlbumDetailRoute(work.Album, StringHelpers.FirstNonBlank(work.Artist, work.Author), work.Id, edit);
    }

    private async Task AddToPlaylistAsync(WorkViewModel work, ManagedCollectionViewModel collection)
        => await AddTracksToPlaylistAsync([work], collection);

    private async Task ToggleFavoriteAsync(WorkViewModel work)
    {
        var current = _favoriteWorkIds.Contains(work.Id) ? MediaReaction.Like : MediaReaction.Neutral;
        await Reactions.SetReactionAsync(
            work.Id,
            current == MediaReaction.Neutral ? MediaReaction.Like : MediaReaction.Neutral,
            _activeProfileId,
            ProfileEntityKind.Song);
        await RefreshReactionStateAsync();
    }

    private async Task ToggleDislikeAsync(WorkViewModel work)
    {
        var nextReaction = _dislikedWorkIds.Contains(work.Id) ? MediaReaction.Neutral : MediaReaction.Dislike;
        await Reactions.SetReactionAsync(work.Id, nextReaction, _activeProfileId, ProfileEntityKind.Song);
        await RefreshReactionStateAsync();
    }

    private IReadOnlyList<WorkViewModel> ApplySongFilters(IEnumerable<WorkViewModel> works)
    {
        var filtered = works;

        if (!string.IsNullOrWhiteSpace(_songSearch))
        {
            filtered = filtered.Where(work =>
                DisplayTrackTitle(work).Contains(_songSearch, StringComparison.OrdinalIgnoreCase)
                || (work.Artist?.Contains(_songSearch, StringComparison.OrdinalIgnoreCase) ?? false)
                || (work.Album?.Contains(_songSearch, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        return filtered.ToList();
    }

    private IReadOnlyList<ContentGroupViewModel> ApplyAlbumFilters()
    {
        IEnumerable<ContentGroupViewModel> filtered = _albumGroups;

        if (!string.IsNullOrWhiteSpace(_albumSearch))
        {
            filtered = filtered.Where(group =>
                group.DisplayName.Contains(_albumSearch, StringComparison.OrdinalIgnoreCase)
                || (group.Creator?.Contains(_albumSearch, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        if (!string.Equals(_albumArtistFilter, "all", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(group => string.Equals(group.Creator, _albumArtistFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.Equals(_albumGenreFilter, "all", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(group => AlbumMatchesGenre(group, _albumGenreFilter));
        }

        if (_albumFavoriteOnly)
        {
            filtered = filtered.Where(AlbumHasFavorites);
        }

        if (_albumArtOnly)
        {
            filtered = filtered.Where(group => !string.IsNullOrWhiteSpace(group.CoverUrl));
        }

        filtered = _albumSort switch
        {
            "title" => filtered.OrderBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase),
            "artist" => filtered.OrderBy(group => group.Creator, StringComparer.OrdinalIgnoreCase).ThenBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase),
            "year" => filtered.OrderByDescending(group => ParseYear(group.Year)).ThenBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase),
            _ => filtered.OrderByDescending(group => group.CreatedAt).ThenBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase),
        };

        return filtered.ToList();
    }

    private IReadOnlyList<ContentGroupViewModel> ApplyArtistFilters()
    {
        IEnumerable<ContentGroupViewModel> filtered = _artistGroups;

        if (!string.IsNullOrWhiteSpace(_artistSearch))
        {
            filtered = filtered.Where(group => group.DisplayName.Contains(_artistSearch, StringComparison.OrdinalIgnoreCase));
        }

        filtered = _artistSort switch
        {
            "tracks" => filtered.OrderByDescending(group => group.WorkCount).ThenBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase),
            _ => filtered.OrderBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase),
        };

        return filtered.ToList();
    }

    private bool AlbumMatchesGenre(ContentGroupViewModel album, string genre)
        => AlbumWorksFor(album).Any(work => work.Genres.Any(item => string.Equals(item, genre, StringComparison.OrdinalIgnoreCase)));

    private bool AlbumHasFavorites(ContentGroupViewModel album)
        => AlbumWorksFor(album).Any(work => _favoriteWorkIds.Contains(work.Id));

    private IReadOnlyList<WorkViewModel> AlbumWorksFor(ContentGroupViewModel album)
        => _musicWorks
            .Where(work => AlbumMatchesWork(album, work))
            .ToList();

    private static bool AlbumMatchesWork(ContentGroupViewModel album, WorkViewModel work)
    {
        if (work.CollectionId == album.CollectionId)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(work.Album)
            || !string.Equals(work.Album, album.DisplayName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(album.Creator))
        {
            return true;
        }

        var workCreator = StringHelpers.FirstNonBlank(work.Artist, work.Author);
        return string.Equals(workCreator, album.Creator, StringComparison.OrdinalIgnoreCase);
    }

    private async Task RefreshReactionStateAsync()
    {
        _favoriteWorkIds = (await Reactions.GetFavoriteWorkIdsAsync(_activeProfileId)).ToHashSet();
        _dislikedWorkIds = (await Reactions.GetDislikedWorkIdsAsync(_activeProfileId)).ToHashSet();
        StateHasChanged();
    }

    private List<LibraryItemViewModel> BuildTrackGridItems(IEnumerable<WorkViewModel> works) =>
        works.Select(MapTrackToLibraryItem).ToList();

    private LibraryItemViewModel MapTrackToLibraryItem(WorkViewModel work)
    {
        var durationSeconds = GetTrackDurationSeconds(work);
        var artist = StringHelpers.FirstNonBlankOr("-", work.Artist, work.Author, null);
        var comments = GetCanonicalValue(work, "comment", "comments", "description");
        var releaseDate = ParseCanonicalDate(GetCanonicalValue(work, "release_date", "date_released"));
        var lastPlayedAt = ParseCanonicalDate(GetCanonicalValue(work, "last_played", "played_at"));
        var dateModified = ParseCanonicalDate(GetCanonicalValue(work, "date_modified", "modified_at", "updated_at"));

        return new LibraryItemViewModel
        {
            EntityId = work.Id,
            Title = DisplayTrackTitle(work),
            OriginalTitle = work.OriginalTitle,
            Artist = string.Equals(artist, "-", StringComparison.Ordinal) ? null : artist,
            Album = work.Album,
            AlbumArtist = GetCanonicalValue(work, "album_artist"),
            Composer = GetCanonicalValue(work, "composer"),
            Comments = comments,
            Genre = work.Genres.FirstOrDefault() ?? work.Genre,
            Duration = LibraryHelpers.FormatDuration(durationSeconds, fallback: "0:00"),
            DurationSeconds = durationSeconds,
            TrackNumber = work.TrackNumber,
            DiscNumber = GetCanonicalValue(work, "disc_number"),
            Rating = GetCanonicalValue(work, "rating", "album_rating"),
            Year = work.Year,
            SortTitle = GetCanonicalValue(work, "sort_title") ?? DisplayTrackTitle(work),
            SortArtist = GetCanonicalValue(work, "sort_artist") ?? artist,
            SortAlbum = GetCanonicalValue(work, "sort_album") ?? work.Album,
            Kind = GetCanonicalValue(work, "kind") ?? "Audio File",
            MediaType = "Music",
            CoverUrl = work.CoverUrl,
            CoverThumbUrl = work.CoverUrl,
            CreatedAt = work.CreatedAt,
            DateModified = dateModified,
            LastPlayedAt = lastPlayedAt,
            ReleaseDate = releaseDate,
            PlayCount = GetPlayCount(work),
            IsFavorite = _favoriteWorkIds.Contains(work.Id),
        };
    }

    private IReadOnlyList<WorkViewModel> CurrentTrackSurfaceTracks =>
        IsAlbumDetail ? AlbumTracks :
        IsArtistsSurface ? ArtistTracks :
        IsPlaylistSurface ? ActivePlaylistTracks :
        FilteredSongs;

    private string CurrentTrackSurfaceLabel =>
        IsAlbumDetail && _albumDetail is not null ? _albumDetail.DisplayName :
        IsArtistsSurface && _artistDetail is not null ? _artistDetail.DisplayName :
        IsPlaylistSurface ? ActivePlaylistTitle :
        "All Music";

    private IReadOnlyList<WorkViewModel> SelectedTrackWorks =>
        CurrentTrackSurfaceTracks
            .Where(work => _selectedTrackIds.Contains(work.Id))
            .ToList();

    private async Task OnTrackSelectionChanged(HashSet<Guid> selected)
    {
        _selectedTrackIds = selected.ToHashSet();
        CloseTrackContextMenu();
        await InvokeAsync(StateHasChanged);
    }

    private async Task OnTrackRowActivated(Guid entityId)
    {
        var target = CurrentTrackSurfaceTracks.FirstOrDefault(work => work.Id == entityId);
        if (target is null)
        {
            return;
        }

        await PlayTracksAsync(CurrentTrackSurfaceTracks, target.Id, CurrentTrackSurfaceLabel);
    }

    private void ToggleSongSort(string column)
    {
        column = NormalizeSongSortColumn(column);
        if (string.Equals(NormalizeSongSortColumn(_songSortColumn), column, StringComparison.OrdinalIgnoreCase))
        {
            _songSortDescending = !_songSortDescending;
            return;
        }

        _songSortColumn = column;
        _songSortDescending = column is "dateAdded" or "plays" or "time" or "favorite";
    }

    private string SongSortIndicator(string column)
    {
        column = NormalizeSongSortColumn(column);
        if (!string.Equals(NormalizeSongSortColumn(_songSortColumn), column, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return _songSortDescending ? AppMaterialIcons.Outlined.ArrowDownward : AppMaterialIcons.Outlined.ArrowUpward;
    }

    private async Task OnTrackFavoriteToggled(Guid entityId)
    {
        var target = CurrentTrackSurfaceTracks.FirstOrDefault(work => work.Id == entityId);
        if (target is null)
        {
            return;
        }

        await ToggleFavoriteAsync(target);
    }

    private Task OnTrackDragStarted(Guid entityId)
    {
        _draggingTrackIds = _selectedTrackIds.Contains(entityId)
            ? _selectedTrackIds.ToHashSet()
            : [entityId];

        var draggingItems = CurrentTrackSurfaceTracks
            .Where(work => _draggingTrackIds.Contains(work.Id))
            .Select(ListenQueueItemFactory.Create)
            .ToList();
        AudioDrag.BeginDrag(_draggingTrackIds, draggingItems);

        return Task.CompletedTask;
    }

    private Task OnTrackDragEnded()
    {
        _draggingTrackIds.Clear();
        AudioDrag.Clear();
        return Task.CompletedTask;
    }

    private static void CloseTrackContextMenu()
    {
    }

    private async Task PlaySelectedTracksAsync()
    {
        if (SelectedTrackWorks.Count == 0)
        {
            return;
        }

        await PlayTracksAsync(SelectedTrackWorks, SelectedTrackWorks[0].Id, "Selected Tracks");
        _selectedTrackIds.Clear();
    }

    private async Task QueueSelectedTracksAsync(bool next)
    {
        if (SelectedTrackWorks.Count == 0)
        {
            return;
        }

        foreach (var track in SelectedTrackWorks)
        {
            if (next)
            {
                await Playback.InsertNextAsync(track);
            }
            else
            {
                await Playback.AddToQueueAsync(track);
            }
        }

        Snackbar.Add(next ? "Selected songs will play next." : "Selected songs added to the queue.", AppSeverity.Success);
        CloseTrackContextMenu();
    }

    private async Task EditSelectedTrackAsync()
    {
        var target = SelectedTrackWorks.FirstOrDefault();
        if (target is null)
        {
            return;
        }

        await EditTrackAsync(target);
    }

    private async Task DeleteSelectedTracksAsync()
    {
        if (SelectedTrackWorks.Count == 0)
        {
            return;
        }

        var deleteCount = SelectedTrackWorks.Count;

        var confirmed = await JS.InvokeAsync<bool>("confirm", $"Delete {deleteCount} selected song(s) from the library?");
        if (!confirmed)
        {
            return;
        }

        var response = await ApiClient.BatchDeleteLibraryCatalogItemsAsync(SelectedTrackWorks.Select(track => track.Id).ToArray());
        if (response is null)
        {
            Snackbar.Add("Delete failed.", AppSeverity.Error);
            return;
        }

        _selectedTrackIds.Clear();
        CloseTrackContextMenu();
        await LoadAsync();
        Snackbar.Add($"{deleteCount} songs deleted.", AppSeverity.Success);
    }

    private async Task AddSelectedTracksToPlaylistAsync(Guid? collectionId = null)
    {
        if (SelectedTrackWorks.Count == 0)
        {
            return;
        }

        if (collectionId.HasValue)
        {
            var collection = PlaylistCollections.FirstOrDefault(item => item.Id == collectionId.Value);
            if (collection is null)
            {
                return;
            }

            await AddTracksToPlaylistAsync(SelectedTrackWorks, collection);
            return;
        }

        await CreatePlaylistAndAddTracksAsync(SelectedTrackWorks);
    }

    private async Task AddTracksToPlaylistAsync(IEnumerable<WorkViewModel> works, ManagedCollectionViewModel collection)
    {
        if (!_activeProfileId.HasValue)
        {
            Snackbar.Add("Choose an active profile before saving to playlists.", AppSeverity.Warning);
            return;
        }

        var added = 0;
        foreach (var work in works)
        {
            if (await ApiClient.AddCollectionItemAsync(collection.Id, work.Id, _activeProfileId.Value))
            {
                added++;
            }
        }

        if (added == 0)
        {
            Snackbar.Add($"Could not add songs to {collection.Name}.", AppSeverity.Error);
            return;
        }

        Snackbar.Add($"Added {added} song(s) to {collection.Name}.", AppSeverity.Success);
        await LoadAsync();
    }

    private async Task CreatePlaylistAndAddTracksAsync(IEnumerable<WorkViewModel> works)
    {
        if (!_activeProfileId.HasValue)
        {
            Snackbar.Add("Choose an active profile before creating playlists.", AppSeverity.Warning);
            return;
        }

        var timestamp = DateTimeOffset.Now.ToString("M-d HHmm", CultureInfo.InvariantCulture);
        var created = await ApiClient.CreateCollectionAsync(
            name: $"New Playlist {timestamp}",
            description: "Created from the Listen grid.",
            iconName: AppMaterialIcons.Outlined.QueueMusic,
            collectionType: "Playlist",
            definition: new CollectionRuleDefinitionViewModel(),
            sortField: null,
            sortDirection: "asc",
            visibility: "private",
            profileId: _activeProfileId.Value);

        if (!created)
        {
            Snackbar.Add("Could not create a playlist for the dropped songs.", AppSeverity.Error);
            return;
        }

        await LoadAsync();
        var createdPlaylist = PlaylistCollections.OrderByDescending(collection => collection.CreatedAt).FirstOrDefault();
        if (createdPlaylist is null)
        {
            Snackbar.Add("Playlist was created, but it could not be loaded yet.", AppSeverity.Warning);
            return;
        }

        await AddTracksToPlaylistAsync(works, createdPlaylist);
    }

    private async Task HandlePlaylistDropAsync(Guid? collectionId = null)
    {
        var draggingWorkIds = _draggingTrackIds.Count > 0
            ? _draggingTrackIds.ToHashSet()
            : AudioDrag.WorkIds.ToHashSet();

        if (draggingWorkIds.Count == 0)
        {
            return;
        }

        var draggedWorks = draggingWorkIds
            .Select(id => _workLookup.GetValueOrDefault(id))
            .Where(work => work is not null)
            .Cast<WorkViewModel>()
            .ToList();

        if (draggedWorks.Count == 0)
        {
            _draggingTrackIds.Clear();
            AudioDrag.Clear();
            return;
        }

        _selectedTrackIds = draggingWorkIds;
        if (collectionId.HasValue)
        {
            var collection = PlaylistCollections.FirstOrDefault(item => item.Id == collectionId.Value);
            if (collection is not null)
            {
                await AddTracksToPlaylistAsync(draggedWorks, collection);
            }
        }
        else
        {
            await CreatePlaylistAndAddTracksAsync(draggedWorks);
        }

        _draggingTrackIds.Clear();
        AudioDrag.Clear();
    }

    private void OpenArtist(string artistName)
    {
        var identity = _artistGroups.FirstOrDefault(group =>
            string.Equals(group.DisplayName, artistName, StringComparison.OrdinalIgnoreCase));
        NavigateTo(MediaPersonGroupTileComposer.NavigationUrl(
            identity?.PersonId ?? identity?.ArtistPersonId,
            artistName));
    }

    private Task OnTrackArtistClicked(string artistName)
    {
        if (!string.IsNullOrWhiteSpace(artistName))
        {
            OpenArtist(artistName);
        }

        return Task.CompletedTask;
    }

    private IReadOnlyList<WorkViewModel> ResolveGroupWorks(IEnumerable<CollectionGroupWorkDto> works)
        => works
            .Select(groupWork => _workLookup.GetValueOrDefault(groupWork.WorkId))
            .Where(work => work is not null)
            .Cast<WorkViewModel>()
            .ToList();

    private IReadOnlyList<WorkViewModel> ResolveArtistTracks()
    {
        if (_artistDetail is null)
        {
            return [];
        }

        var directWorks = ResolveGroupWorks(_artistDetail.Works);
        if (directWorks.Count > 0)
        {
            return directWorks;
        }

        return _artistDetail.Seasons
            .SelectMany(season => season.Episodes)
            .Select(episode => _workLookup.GetValueOrDefault(episode.WorkId))
            .Where(work => work is not null)
            .Cast<WorkViewModel>()
            .ToList();
    }

    private IReadOnlyList<WorkViewModel> ResolveActivePlaylistTracks()
    {
        if (!string.IsNullOrWhiteSpace(PlaylistKey))
        {
            return PlaylistKey.ToLowerInvariant() switch
            {
                "all-music" => SortedSongs.ToList(),
                "favorite-songs" => _musicWorks
                    .Where(work => _favoriteWorkIds.Contains(work.Id))
                    .OrderBy(work => work.Artist ?? work.Author, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(work => work.Album, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(work => ParseTrackNumber(work.TrackNumber))
                    .ThenBy(DisplayTrackTitle, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                "recently-added" => RecentlyAddedTracks.ToList(),
                _ => [],
            };
        }

        return _playlistItems
            .OrderBy(item => item.SortOrder)
            .Select(item => _workLookup.GetValueOrDefault(item.WorkId))
            .Where(work => work is not null)
            .Cast<WorkViewModel>()
            .ToList();
    }

    private IReadOnlyList<ListenPlaylistTrackRow> ResolveActivePlaylistTrackRows()
    {
        if (!string.IsNullOrWhiteSpace(PlaylistKey))
        {
            return ActivePlaylistTracks
                .Select((work, index) => new ListenPlaylistTrackRow(null, work, index))
                .ToList();
        }

        return _playlistItems
            .OrderBy(item => item.SortOrder)
            .Select((item, index) =>
            {
                var work = _workLookup.GetValueOrDefault(item.WorkId);
                return work is null ? null : new ListenPlaylistTrackRow(item, work, index);
            })
            .Where(row => row is not null)
            .Cast<ListenPlaylistTrackRow>()
            .ToList();
    }

    private async Task SavePlaylistTrackOrderAsync(List<CollectionItemDto> orderedItems)
    {
        if (ActivePlaylistCollection is null)
        {
            return;
        }

        var saved = await ApiClient.ReorderCollectionItemsAsync(
            ActivePlaylistCollection.Id,
            orderedItems.Select(item => item.Id).ToList(),
            _activeProfileId);

        if (!saved)
        {
            Snackbar.Add("Playlist order could not be saved.", AppSeverity.Warning);
            return;
        }

        _playlistItems.Clear();
        for (var index = 0; index < orderedItems.Count; index++)
        {
            orderedItems[index].SortOrder = index + 1;
            _playlistItems.Add(orderedItems[index]);
        }
    }

    private async Task RemovePlaylistTrackAsync(Guid itemId)
    {
        if (ActivePlaylistCollection is null)
        {
            return;
        }

        var removed = await ApiClient.RemoveCollectionItemAsync(ActivePlaylistCollection.Id, itemId, _activeProfileId);
        if (!removed)
        {
            Snackbar.Add("Song could not be removed from the playlist.", AppSeverity.Warning);
            return;
        }

        _playlistItems.RemoveAll(item => item.Id == itemId);
        Snackbar.Add("Song removed from playlist.", AppSeverity.Success);
    }

    private IReadOnlyList<WorkViewModel> SortSongs(IEnumerable<WorkViewModel> works)
    {
        var ordered = NormalizeSongSortColumn(_songSortColumn) switch
        {
            "title" => _songSortDescending
                ? works.OrderByDescending(DisplayTrackTitle, StringComparer.OrdinalIgnoreCase)
                : works.OrderBy(DisplayTrackTitle, StringComparer.OrdinalIgnoreCase),
            "artist" => _songSortDescending
                ? works.OrderByDescending(work => work.Artist ?? work.Author, StringComparer.OrdinalIgnoreCase).ThenByDescending(DisplayTrackTitle, StringComparer.OrdinalIgnoreCase)
                : works.OrderBy(work => work.Artist ?? work.Author, StringComparer.OrdinalIgnoreCase).ThenBy(DisplayTrackTitle, StringComparer.OrdinalIgnoreCase),
            "album" => _songSortDescending
                ? works.OrderByDescending(work => work.Album, StringComparer.OrdinalIgnoreCase).ThenByDescending(work => ParseTrackNumber(work.TrackNumber))
                : works.OrderBy(work => work.Album, StringComparer.OrdinalIgnoreCase).ThenBy(work => ParseTrackNumber(work.TrackNumber)),
            "genre" => _songSortDescending
                ? works.OrderByDescending(work => work.Genres.FirstOrDefault(), StringComparer.OrdinalIgnoreCase).ThenByDescending(DisplayTrackTitle, StringComparer.OrdinalIgnoreCase)
                : works.OrderBy(work => work.Genres.FirstOrDefault(), StringComparer.OrdinalIgnoreCase).ThenBy(DisplayTrackTitle, StringComparer.OrdinalIgnoreCase),
            "favorite" => _songSortDescending
                ? works.OrderByDescending(work => _favoriteWorkIds.Contains(work.Id)).ThenBy(DisplayTrackTitle, StringComparer.OrdinalIgnoreCase)
                : works.OrderBy(work => _favoriteWorkIds.Contains(work.Id)).ThenBy(DisplayTrackTitle, StringComparer.OrdinalIgnoreCase),
            "plays" => _songSortDescending
                ? works.OrderByDescending(GetPlayCount).ThenByDescending(DisplayTrackTitle, StringComparer.OrdinalIgnoreCase)
                : works.OrderBy(GetPlayCount).ThenBy(DisplayTrackTitle, StringComparer.OrdinalIgnoreCase),
            "time" => _songSortDescending
                ? works.OrderByDescending(GetDurationSortKey).ThenByDescending(DisplayTrackTitle, StringComparer.OrdinalIgnoreCase)
                : works.OrderBy(GetDurationSortKey).ThenBy(DisplayTrackTitle, StringComparer.OrdinalIgnoreCase),
            _ => _songSortDescending
                ? works.OrderByDescending(work => work.CreatedAt).ThenByDescending(DisplayTrackTitle, StringComparer.OrdinalIgnoreCase)
                : works.OrderBy(work => work.CreatedAt).ThenBy(DisplayTrackTitle, StringComparer.OrdinalIgnoreCase),
        };

        return ordered.ToList();
    }

    private static string NormalizeSongSortColumn(string? column) =>
        (column ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "date_added" => "dateAdded",
            "track_number" => "title",
            _ => string.IsNullOrWhiteSpace(column) ? "dateAdded" : column,
        };

    private static bool IsMusicWork(WorkViewModel work)
    {
        var mediaType = work.MediaType ?? string.Empty;
        return mediaType.Contains("Music", StringComparison.OrdinalIgnoreCase)
               || string.Equals(mediaType, "Audio", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMusicWork(string? mediaType)
        => (mediaType ?? string.Empty).Contains("Music", StringComparison.OrdinalIgnoreCase)
           || string.Equals(mediaType, "Audio", StringComparison.OrdinalIgnoreCase);

    private static bool IsAudiobookWork(WorkViewModel work)
        => (work.MediaType ?? string.Empty).Contains("Audiobook", StringComparison.OrdinalIgnoreCase)
           || string.Equals(work.MediaType, "M4B", StringComparison.OrdinalIgnoreCase);

    private static bool IsAudiobookWork(string? mediaType)
        => (mediaType ?? string.Empty).Contains("Audiobook", StringComparison.OrdinalIgnoreCase)
           || string.Equals(mediaType, "M4B", StringComparison.OrdinalIgnoreCase);

    private static int ParseTrackNumber(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : int.MaxValue;

    private static int ParseYear(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static int GetDurationSortKey(WorkViewModel work)
    {
        var durationSeconds = GetTrackDurationSeconds(work);
        return durationSeconds > 0 ? (int)Math.Min(durationSeconds, int.MaxValue) : 0;
    }

    private static string GetTrackDuration(WorkViewModel work)
        => LibraryHelpers.FormatDuration(GetTrackDurationSeconds(work), fallback: "0:00");

    private static string DisplayTrackTitle(WorkViewModel work)
        => StringHelpers.FirstNonBlank(
               IsUntitled(work.Title) ? null : work.Title,
               GetCanonicalValue(work, "track_title"),
               GetCanonicalValue(work, "track_name"),
               GetCanonicalValue(work, "song_title"),
               GetCanonicalValue(work, "name"),
               GetCanonicalValue(work, "file_title"),
               GetCanonicalValue(work, "file_name"))
           ?? work.Title;

    private static long GetTrackDurationSeconds(WorkViewModel work)
        => LibraryHelpers.NormalizeDurationSeconds(
            GetCanonicalValue(work, "duration_sec"),
            GetCanonicalValue(work, "duration_seconds"),
            GetCanonicalValue(work, "duration"),
            GetCanonicalValue(work, "runtime")) ?? 0;

    private static string? GetCanonicalValue(WorkViewModel work, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = work.CanonicalValues.FirstOrDefault(item =>
                string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;

            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static DateTimeOffset? ParseCanonicalDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
        {
            return parsed;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out parsed))
        {
            return parsed;
        }

        return null;
    }

    private static bool IsUntitled(string? value)
        => string.IsNullOrWhiteSpace(value)
           || value.Trim().StartsWith("Untitled", StringComparison.OrdinalIgnoreCase);

    private static string FormatDateAdded(DateTimeOffset createdAt)
        => createdAt == default ? "-" : createdAt.LocalDateTime.ToString("M/d/yyyy", CultureInfo.CurrentCulture);

    private static int GetPlayCount(WorkViewModel work)
    {
        var value = work.CanonicalValues.FirstOrDefault(item =>
            string.Equals(item.Key, "play_count", StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Key, "plays", StringComparison.OrdinalIgnoreCase))?.Value;

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var plays) ? plays : 0;
    }

    private static string GetPlayCountDisplay(WorkViewModel work)
        => GetPlayCount(work) > 0 ? GetPlayCount(work).ToString(CultureInfo.InvariantCulture) : "-";

    private static string PlaylistIconFor(ManagedCollectionViewModel collection)
        => collection.CollectionType switch
        {
            "Smart" => AppMaterialIcons.Outlined.AutoAwesomeMotion,
            "PlaylistFolder" => AppMaterialIcons.Outlined.Folder,
            "Mix" => AppMaterialIcons.Outlined.GraphicEq,
            "System" => AppMaterialIcons.Outlined.SettingsSuggest,
            _ => AppMaterialIcons.Outlined.QueueMusic,
        };

    private static IReadOnlyList<ContentGroupViewModel> BuildMusicGroupSummaries(IEnumerable<WorkViewModel> works, string groupField)
    {
        var normalizedGroupField = groupField.Trim().ToLowerInvariant();
        return works
            .Select(work => new
            {
                Work = work,
                Key = normalizedGroupField == "artist"
                    ? StringHelpers.FirstNonBlank(work.Artist, work.Author)
                    : StringHelpers.FirstNonBlank(work.Album),
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .GroupBy(item => item.Key!, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var groupWorks = group.Select(item => item.Work).ToList();
                var first = groupWorks.First();
                var creator = normalizedGroupField == "album"
                    ? StringHelpers.FirstNonBlank(first.Artist, first.Author)
                    : null;

                return new ContentGroupViewModel
                {
                    CollectionId = GenerateDeterministicGuid($"music:{normalizedGroupField}:{group.Key}"),
                    DisplayName = group.Key,
                    PrimaryMediaType = "Music",
                    WorkCount = groupWorks.Count,
                    CoverUrl = StringHelpers.FirstNonBlank(first.SquareUrl, first.CoverUrl),
                    BackgroundUrl = first.BackgroundUrl,
                    BannerUrl = first.BannerUrl,
                    Creator = creator,
                    ArtistPhotoUrl = normalizedGroupField == "artist" ? StringHelpers.FirstNonBlank(first.SquareUrl, first.CoverUrl) : null,
                    CreatedAt = groupWorks.Max(work => work.CreatedAt),
                };
            })
            .OrderBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void ApplyAudiobookQueryState()
    {
        if (!IsAudiobooksView)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(BrowseQuery))
        {
            _audiobookView = "all";
            _audiobookSeriesFocus = null;
            return;
        }

        var requestedView = BrowseQuery.Trim().ToLowerInvariant();
        if (requestedView is "all" or "series" or "authors" or "narrators" or "timeline")
        {
            _audiobookView = requestedView;
            _audiobookSeriesFocus = null;
        }
    }

    private IReadOnlyList<AudiobookListenItem> BuildAudiobookContinueCards()
    {
        var continueItems = _audiobookJourney
            .Where(item => item.ProgressPct is > 0 and < 99.5)
            .OrderByDescending(item => item.LastAccessed)
            .Select(item => new AudiobookListenItem(ResolveAudiobookItem(item.WorkId, item), item))
            .Take(3)
            .ToList();

        if (continueItems.Count > 0)
        {
            return continueItems;
        }

        return AudiobookDisplayItems
            .OrderByDescending(AudiobookSortTimestamp)
            .Take(3)
            .Select(item => new AudiobookListenItem(item, null))
            .ToList();
    }

    private IReadOnlyList<AudiobookDisplayItem> BuildAudiobookDisplayItems()
    {
        if (_audiobookCards.Count > 0)
        {
            return _audiobookCards
                .Select(card => new AudiobookDisplayItem(card, ResolveAudiobookWork(card.WorkId)))
                .ToList();
        }

        return _audiobookWorks
            .Select(work => new AudiobookDisplayItem(null, work))
            .ToList();
    }

    private AudiobookDisplayItem ResolveAudiobookItem(Guid workId, JourneyItemViewModel? journey)
    {
        var card = _audiobookCards.FirstOrDefault(candidate => candidate.WorkId == workId);
        var work = ResolveAudiobookWork(workId);
        if (card is not null || work is not null)
        {
            return new AudiobookDisplayItem(card, work);
        }

        var fallback = new DisplayCardDto(
            Id: workId,
            WorkId: workId,
            AssetId: null,
            CollectionId: null,
            MediaType: "Audiobook",
            GroupingType: "work",
            Title: journey?.Title ?? "Audiobook",
            Subtitle: journey?.Author,
            Facts: [],
            Artwork: new DisplayArtworkDto(
                CoverUrl: journey?.CoverUrl,
                CoverSmallUrl: null,
                CoverMediumUrl: null,
                CoverLargeUrl: null,
                SquareUrl: null,
                SquareSmallUrl: null,
                SquareMediumUrl: null,
                SquareLargeUrl: null,
                BannerUrl: null,
                BannerSmallUrl: null,
                BannerMediumUrl: null,
                BannerLargeUrl: null,
                BackgroundUrl: null,
                BackgroundSmallUrl: null,
                BackgroundMediumUrl: null,
                BackgroundLargeUrl: null,
                LogoUrl: null,
                CoverWidthPx: null,
                CoverHeightPx: null,
                SquareWidthPx: null,
                SquareHeightPx: null,
                BannerWidthPx: null,
                BannerHeightPx: null,
                BackgroundWidthPx: null,
                BackgroundHeightPx: null,
                AccentColor: null),
            PreferredShape: "cover",
            Presentation: "work",
            TileTextMode: "caption",
            PreviewPlacement: "smart",
            Progress: journey is null ? null : new DisplayProgressDto(journey.ProgressPct, journey.ProgressDisplay, journey.LastAccessed, null),
            Actions: [new DisplayActionDto("openWork", "Open", WorkId: workId, WebUrl: $"/details/work/{workId:D}?context=listen")],
            Flags: new DisplayCardFlagsDto(true, false, false, false),
            SortTimestamp: journey?.LastAccessed ?? DateTimeOffset.MinValue);

        return new AudiobookDisplayItem(fallback, null);
    }

    private WorkViewModel? ResolveAudiobookWork(Guid? workId)
        => workId.HasValue && _workLookup.TryGetValue(workId.Value, out var work) && IsAudiobookWork(work)
            ? work
            : null;

    private IReadOnlyList<AudiobookDisplayItem> BuildAudiobookBrowseItems()
        => FilteredAudiobookItems;

    private IReadOnlyList<AudiobookDisplayItem> ApplyAudiobookFilters()
    {
        IEnumerable<AudiobookDisplayItem> items = AudiobookDisplayItems;

        if (!string.IsNullOrWhiteSpace(_audiobookSeriesFocus))
        {
            items = items.Where(item => AudiobookMatchesSeries(item, _audiobookSeriesFocus));
        }

        var search = _audiobookSearch.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            items = items.Where(item => AudiobookMatchesSearch(item, search));
        }

        if (_audiobookInProgressOnly)
        {
            items = items.Where(item => AudiobookProgressPercent(item) is > 0 and < 99.5);
        }

        if (_audiobookUnreadOnly)
        {
            items = items.Where(item => AudiobookProgressPercent(item) <= 0);
        }

        items = _audiobookLengthFilter switch
        {
            "short" => items.Where(item => AudiobookDurationSeconds(item) is > 0 and <= 21600),
            "medium" => items.Where(item => AudiobookDurationSeconds(item) is > 21600 and <= 50400),
            "long" => items.Where(item => AudiobookDurationSeconds(item) > 50400),
            _ => items,
        };

        return _audiobookSort switch
        {
            "title" => items.OrderBy(AudiobookTitle, StringComparer.OrdinalIgnoreCase).ToList(),
            "author" => items.OrderBy(AudiobookCreatorLine, StringComparer.OrdinalIgnoreCase)
                .ThenBy(AudiobookTitle, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            "length" => items.OrderByDescending(AudiobookDurationSeconds)
                .ThenBy(AudiobookTitle, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            _ => items.OrderByDescending(AudiobookSortTimestamp)
                .ThenBy(AudiobookTitle, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    private IReadOnlyList<ContentGroupViewModel> ApplyAudiobookSeriesFilters()
    {
        IEnumerable<ContentGroupViewModel> groups = _audiobookSeriesGroups;
        var search = _audiobookSearch.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            groups = groups.Where(group =>
                ContainsSearch(group.DisplayName, search)
                || ContainsSearch(group.Creator, search)
                || ContainsSearch(group.Description, search)
                || ContainsSearch(group.Tagline, search));
        }

        return _audiobookSort switch
        {
            "title" or "author" => groups.OrderBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => groups.OrderByDescending(group => group.WorkCount).ThenBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    private static bool AudiobookMatchesSearch(AudiobookDisplayItem item, string search)
    {
        var card = item.Card;
        var work = item.Work;
        return ContainsSearch(card?.Title, search)
               || ContainsSearch(card?.Subtitle, search)
               || (card?.Facts.Any(fact => ContainsSearch(fact, search)) ?? false)
               || ContainsSearch(work?.Title, search)
               || ContainsSearch(work?.Author, search)
               || ContainsSearch(work?.Artist, search)
               || ContainsSearch(work?.Narrator, search)
               || ContainsSearch(work?.Series, search)
               || (work?.Genres.Any(genre => ContainsSearch(genre, search)) ?? false);
    }

    private static bool AudiobookMatchesSeries(AudiobookDisplayItem item, string series)
    {
        return ContainsSearch(item.Work?.Series, series)
               || (item.Card?.Facts.Any(fact => string.Equals(fact, series, StringComparison.OrdinalIgnoreCase)) ?? false);
    }

    private static bool ContainsSearch(string? value, string search)
        => !string.IsNullOrWhiteSpace(value)
           && value.Contains(search, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<AudiobookAuthorGroup> BuildAudiobookAuthorGroups(IEnumerable<AudiobookDisplayItem> items)
        => items
            .SelectMany(item =>
            {
                var authors = AudiobookAuthorNames(item);
                return authors.Select(author => new { Author = author, Item = item });
            })
            .GroupBy(item => item.Author, StringComparer.OrdinalIgnoreCase)
            .Select(group => new AudiobookAuthorGroup(
                group.Key,
                group.Select(item => item.Item)
                    .OrderByDescending(AudiobookSortTimestamp)
                    .ThenBy(AudiobookTitle, StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IReadOnlyList<string> AudiobookAuthorNames(AudiobookDisplayItem item)
    {
        if (item.Work?.Authors.Count > 0)
        {
            return item.Work.Authors;
        }

        var creator = AudiobookCreatorLine(item);
        return string.IsNullOrWhiteSpace(creator) || string.Equals(creator, "Unknown Author", StringComparison.OrdinalIgnoreCase)
            ? ["Unknown Author"]
            : [creator];
    }

    private static IReadOnlyList<AudiobookAuthorGroup> BuildAudiobookNarratorGroups(IEnumerable<AudiobookDisplayItem> items)
        => items
            .Where(item => !string.IsNullOrWhiteSpace(item.Work?.Narrator))
            .GroupBy(item => item.Work!.Narrator!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new AudiobookAuthorGroup(
                group.Key,
                group.OrderByDescending(AudiobookSortTimestamp)
                    .ThenBy(AudiobookTitle, StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private JourneyItemViewModel? AudiobookJourneyFor(AudiobookDisplayItem item)
    {
        var workId = AudiobookWorkId(item);
        return workId.HasValue
            ? _audiobookJourney
                .Where(journey => journey.WorkId == workId.Value)
                .OrderByDescending(journey => journey.LastAccessed)
                .FirstOrDefault()
            : null;
    }

    private static Guid? AudiobookWorkId(AudiobookDisplayItem item)
        => item.Card?.WorkId ?? item.Work?.Id;

    private static string AudiobookTitle(AudiobookDisplayItem item)
        => StringHelpers.FirstNonBlank(item.Card?.Title, item.Work?.Title, "Audiobook") ?? "Audiobook";

    private static string? AudiobookCoverUrl(AudiobookDisplayItem item)
        => StringHelpers.FirstNonBlank(
            item.Card?.Artwork.CoverUrl,
            item.Card?.Artwork.CoverMediumUrl,
            item.Card?.Artwork.CoverSmallUrl,
            item.Card?.Artwork.SquareUrl,
            item.Card?.Artwork.BackgroundUrl,
            item.Work?.CoverUrl,
            item.Work?.CoverUrlMedium,
            item.Work?.CoverUrlSmall,
            item.Work?.SquareUrl);

    private static DateTimeOffset AudiobookSortTimestamp(AudiobookDisplayItem item)
        => item.Card?.SortTimestamp ?? item.Work?.CreatedAt ?? DateTimeOffset.MinValue;

    private double AudiobookProgressPercent(AudiobookDisplayItem item)
        => item.Card?.Progress?.Percent ?? AudiobookJourneyFor(item)?.ProgressPct ?? 0;

    private double AudiobookProgressPercent(AudiobookListenItem item)
        => item.Journey?.ProgressPct ?? AudiobookProgressPercent(item.Item);

    private string AudiobookProgressLabel(AudiobookDisplayItem item)
    {
        var progress = AudiobookProgressPercent(item);
        return progress switch
        {
            >= 99.5 => "Complete",
            > 0 => $"{Math.Max(1, progress):F0}%",
            _ => "Unread",
        };
    }

    private string AudiobookProgressLabel(AudiobookListenItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Journey?.ProgressDisplay))
        {
            return item.Journey.ProgressDisplay;
        }

        var progress = AudiobookProgressPercent(item);
        return progress switch
        {
            >= 99.5 => "Complete",
            > 0 => $"{Math.Max(1, progress):F0}%",
            _ => "Ready to start",
        };
    }

    private string AudiobookRemainingLabel(AudiobookListenItem item)
    {
        var durationSeconds = AudiobookDurationSeconds(item.Item);
        if (durationSeconds <= 0)
        {
            return item.Journey is null ? "Length unknown" : item.Journey.ProgressDisplay;
        }

        var progress = Math.Clamp(AudiobookProgressPercent(item), 0, 100);
        if (progress <= 0)
        {
            return FormatAudiobookDuration(durationSeconds);
        }

        if (progress >= 99.5)
        {
            return "Complete";
        }

        var remainingSeconds = (long)Math.Round(durationSeconds * ((100 - progress) / 100d));
        return $"{FormatAudiobookDuration(remainingSeconds)} left";
    }

    private static long AudiobookDurationSeconds(AudiobookDisplayItem item)
        => item.Work is null ? 0 : GetTrackDurationSeconds(item.Work);

    private static string AudiobookDurationLabel(AudiobookDisplayItem item)
    {
        var duration = FormatAudiobookDuration(AudiobookDurationSeconds(item));
        if (!string.Equals(duration, "Length unknown", StringComparison.OrdinalIgnoreCase))
        {
            return duration;
        }

        return item.Card?.Facts.FirstOrDefault(IsDurationFact) ?? duration;
    }

    private static bool IsDurationFact(string value)
        => value.Contains('h', StringComparison.OrdinalIgnoreCase)
           || value.Contains("min", StringComparison.OrdinalIgnoreCase)
           || value.Contains("hour", StringComparison.OrdinalIgnoreCase);

    private static string FormatAudiobookDuration(long seconds)
    {
        if (seconds <= 0)
        {
            return "Length unknown";
        }

        var duration = TimeSpan.FromSeconds(seconds);
        var hours = (int)Math.Floor(duration.TotalHours);
        var minutes = duration.Minutes;

        if (hours <= 0)
        {
            return $"{Math.Max(1, minutes)}m";
        }

        return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
    }

    private static string AudiobookCreatorLine(AudiobookDisplayItem item)
        => StringHelpers.FirstNonBlank(item.Card?.Subtitle, item.Work?.Author, item.Work?.Artist, "Unknown Author") ?? "Unknown Author";

    private static string AudiobookSecondaryLine(AudiobookDisplayItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Work?.Narrator))
        {
            return $"Narrated by {item.Work.Narrator}";
        }

        var creator = AudiobookCreatorLine(item);
        var fact = item.Card?.Facts.FirstOrDefault(candidate =>
            !string.Equals(candidate, creator, StringComparison.OrdinalIgnoreCase)
            && !IsDurationFact(candidate));
        return string.IsNullOrWhiteSpace(fact) ? "Audiobook" : fact;
    }

    private static string AudiobookBadge(AudiobookDisplayItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Work?.SeriesPosition))
        {
            return $"Book {item.Work.SeriesPosition}";
        }

        if (!string.IsNullOrWhiteSpace(item.Work?.Series)
            || string.Equals(item.Card?.Presentation, "audiobookSeries", StringComparison.OrdinalIgnoreCase))
        {
            return "Series";
        }

        return item.Card?.Badges.FirstOrDefault()?.Label ?? "Audiobook";
    }

    private static string AudiobookSeriesMeta(ContentGroupViewModel series)
    {
        var count = series.WorkCount == 1 ? "1 book" : $"{series.WorkCount} books";
        return string.IsNullOrWhiteSpace(series.Creator) ? count : $"{count} - {series.Creator}";
    }

    private static string AudiobookSeriesDescription(ContentGroupViewModel series)
        => StringHelpers.FirstNonBlank(series.Description, series.Tagline)
           ?? $"{series.DisplayName} includes {Pluralize(series.WorkCount, "audiobook")} in your library.";

    private static string AudiobookAuthorMeta(AudiobookAuthorGroup author)
        => author.Items.Count == 1 ? "1 audiobook" : $"{author.Items.Count} audiobooks";

    private static string AudiobookRoute(AudiobookDisplayItem item)
        => item.Card?.Actions.FirstOrDefault(action => !string.IsNullOrWhiteSpace(action.WebUrl))?.WebUrl
           ?? (item.Work is null ? "/" : MediaNavigation.ForWork(item.Work));

    private static string AudiobookSeriesRoute(ContentGroupViewModel series)
        => $"/details/collection/{series.CollectionId:D}?context=listen";

    private static string AudiobookTabClass(string view, string activeView)
        => string.Equals(view, activeView, StringComparison.OrdinalIgnoreCase)
            ? "audiobooks-tab is-active"
            : "audiobooks-tab";

    private string AudiobookLayoutToggleClass(LibraryLayoutMode mode)
        => _audiobookLayout == mode
            ? "audiobooks-layout-toggle__button is-active"
            : "audiobooks-layout-toggle__button";

    private void SetAudiobookView(string view)
    {
        _audiobookView = view;
        _audiobookSeriesFocus = null;
        Nav.NavigateTo(AudiobookBrowseRoute(view));
    }

    private void SetMusicBrowseMode(string view) => Nav.NavigateTo(MusicBrowseRoute(view));

    private static string MusicBrowseRoute(string view) => view switch
    {
        "artists" => ArtistsRoute,
        "songs" => SongsRoute,
        "playlists" => PlaylistsRoute,
        "timeline" => $"{MusicHomeRoute}?browse=timeline",
        _ => AlbumsRoute,
    };

    private static string AudiobookBrowseRoute(string view) => view == "all"
        ? AudiobooksRoute
        : $"{AudiobooksRoute}?browse={Uri.EscapeDataString(view)}";

    private string? MusicBrowseCount(string view) => view switch
    {
        "albums" => _albumGroups.Count.ToString(CultureInfo.InvariantCulture),
        "artists" => _artistGroups.Count.ToString(CultureInfo.InvariantCulture),
        "songs" => _musicWorks.Count.ToString(CultureInfo.InvariantCulture),
        "playlists" => PlaylistCollections.Count.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    private string? AudiobookBrowseCount(string view) => view switch
    {
        "all" => _audiobookWorks.Count.ToString(CultureInfo.InvariantCulture),
        "authors" => AudiobookAuthorGroups.Count.ToString(CultureInfo.InvariantCulture),
        "series" => _audiobookSeriesGroups.Count.ToString(CultureInfo.InvariantCulture),
        "narrators" => AudiobookNarratorGroups.Count.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    private void UpdateAudiobookSearch(ChangeEventArgs args)
    {
        _audiobookSearch = args.Value?.ToString() ?? string.Empty;
    }

    private void UpdateAlbumTileSize(ChangeEventArgs args)
    {
        if (int.TryParse(args.Value?.ToString(), out var requestedSize))
        {
            _albumTileSizePx = Math.Clamp(requestedSize, 96, 208);
        }
    }

    private void UpdateAudiobookTileSize(ChangeEventArgs args)
    {
        if (int.TryParse(args.Value?.ToString(), out var requestedSize))
        {
            _audiobookTileSizePx = Math.Clamp(requestedSize, 128, 248);
        }
    }

    private void SetAudiobookInProgress(bool selected)
    {
        _audiobookInProgressOnly = selected;
        if (_audiobookInProgressOnly)
        {
            _audiobookUnreadOnly = false;
        }
    }

    private void SetAudiobookUnread(bool selected)
    {
        _audiobookUnreadOnly = selected;
        if (_audiobookUnreadOnly)
        {
            _audiobookInProgressOnly = false;
        }
    }

    private void ViewAudiobookSeries(ContentGroupViewModel series)
        => Nav.NavigateTo(AudiobookSeriesRoute(series));

    private void ViewAudiobookAuthor(AudiobookAuthorGroup author)
    {
        _audiobookView = "all";
        _audiobookSearch = author.Name;
        _audiobookSeriesFocus = null;
    }

    private void ViewAudiobookNarrator(AudiobookAuthorGroup narrator)
    {
        _audiobookView = "all";
        _audiobookSearch = narrator.Name;
        _audiobookSeriesFocus = null;
        Nav.NavigateTo(AudiobooksRoute);
    }

    private void ClearAudiobookSeriesFocus()
    {
        _audiobookSeriesFocus = null;
    }

    private Task PlayAudiobookItemAsync(AudiobookDisplayItem item)
    {
        if (item.Work is not null)
        {
            return PlaySingleWorkAsync(item.Work, "Audiobooks");
        }

        Nav.NavigateTo(AudiobookRoute(item));
        return Task.CompletedTask;
    }

    private static bool CanAcceptPlaylistItems(ManagedCollectionViewModel collection)
        => string.Equals(collection.CollectionType, "Playlist", StringComparison.OrdinalIgnoreCase);

    private static string Pluralize(int count, string singular)
        => count == 1 ? $"1 {singular}" : $"{count} {singular}s";

    private MediaTileViewModel ToAudiobookTile(AudiobookDisplayItem item)
    {
        if (item.Card is not null)
        {
            return MediaTileComposerService.FromDisplayCard(item.Card);
        }

        var work = item.Work;
        var cover = work?.CoverUrl ?? work?.CoverUrlMedium ?? work?.CoverUrlSmall;
        var small = MediaTileArtworkUrl.Sized(cover, "s");
        var medium = MediaTileArtworkUrl.Sized(cover, "m");
        var backgroundMedium = MediaTileArtworkUrl.Sized(work?.BackgroundUrl, "m");
        var backgroundLarge = MediaTileArtworkUrl.Sized(work?.BackgroundUrl, "l");
        var route = AudiobookRoute(item);
        var facts = new[]
            {
                AudiobookCreatorLine(item),
                work?.Year,
                AudiobookDurationLabel(item),
                work is null ? null : GetCanonicalValue(work, "content_rating", "certification"),
                work is null ? null : GetCanonicalValue(work, "rating"),
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();

        return new MediaTileViewModel
        {
            Id = work?.Id ?? item.Card?.Id ?? Guid.NewGuid(),
            WorkId = work?.Id,
            Title = AudiobookTitle(item),
            Subtitle = AudiobookCreatorLine(item),
            CoverUrl = cover,
            BackgroundUrl = work?.BackgroundUrl,
            LogoUrl = work?.LogoUrl,
            HoverFacts = facts,
            MediaKind = "Audiobook",
            Shape = MediaTileShape.Square,
            HoverArtworkShape = string.IsNullOrWhiteSpace(backgroundMedium)
                ? MediaTileShape.Portrait
                : MediaTileShape.Landscape,
            Presentation = MediaTilePresentation.Default,
            SurfaceKind = MediaTileSurfaceKind.CoverSquare,
            HoverLayout = string.IsNullOrWhiteSpace(backgroundMedium)
                ? MediaTileHoverLayout.ArtOnlyPopover
                : MediaTileHoverLayout.BannerPopover,
            HoverMode = MediaTileHoverMode.Expanded,
            TileTextMode = MediaTileTextMode.CoverOnly,
            TileImageUrl = small,
            TileImageSrcSet = MediaTileArtworkUrl.SrcSet(small, medium),
            HoverImageUrl = backgroundMedium ?? medium,
            HoverImageSrcSet = MediaTileArtworkUrl.SrcSet(backgroundMedium ?? medium, backgroundLarge),
            TileImageFitMode = MediaTileImageFitMode.Contain,
            HoverImageFitMode = string.IsNullOrWhiteSpace(backgroundMedium)
                ? MediaTileImageFitMode.Contain
                : MediaTileImageFitMode.Fill,
            NavigationUrl = route,
            DetailsNavigationUrl = route,
            PrimaryNavigationUrl = route,
            PrimaryActionLabel = AudiobookProgressPercent(item) > 0 ? "Resume" : "Play",
            ProgressPct = AudiobookProgressPercent(item),
            ProgressLabel = AudiobookProgressLabel(item),
            AccentColor = "var(--tl-media-audio)",
            SortTimestamp = AudiobookSortTimestamp(item),
            SortYear = int.TryParse(work?.Year, out var audiobookYear) ? audiobookYear : 0,
        };
    }

    private MediaTileViewModel ToContentPersonTile(ContentGroupViewModel group, string defaultRole)
    {
        var personId = group.PersonId ?? group.ArtistPersonId;
        var roles = group.PersonRoles.Count > 0 ? group.PersonRoles : [defaultRole];
        var artworkItems = BuildContentGroupArtwork(group);
        return MediaPersonGroupTileComposer.Compose(
            personId ?? GenerateDeterministicGuid($"{defaultRole}:{group.DisplayName}"),
            group.DisplayName,
            personId,
            group.PersonPhotoUrl ?? group.ArtistPhotoUrl,
            roles,
            group.PrimaryMediaType,
            group.WorkCount,
            group.EarliestYear,
            group.LatestYear,
            group.CreatedAt,
            artworkItems,
            MediaPersonGroupTileComposer.NavigationUrl(personId, group.DisplayName),
            group.MediaTypeColor);
    }

    private MediaTileViewModel ToAudiobookPersonTile(
        AudiobookAuthorGroup personGroup,
        string role,
        IReadOnlyList<ContentGroupViewModel> identityGroups)
    {
        var identity = identityGroups.FirstOrDefault(group =>
            PersonNamesMatch(group.DisplayName, personGroup.Name));
        var personId = identity?.PersonId ?? identity?.ArtistPersonId;
        var roles = identity?.PersonRoles.Count > 0 ? identity.PersonRoles : [role];
        var artworkItems = personGroup.Items
            .Select(item =>
            {
                var workId = AudiobookWorkId(item);
                return new ArtworkStackItem
                {
                    Id = workId?.ToString("D") ?? GenerateDeterministicGuid($"{personGroup.Name}:{AudiobookTitle(item)}").ToString("D"),
                    WorkId = workId,
                    Title = AudiobookTitle(item),
                    ImageUrl = AudiobookCoverUrl(item) ?? string.Empty,
                    MediaType = "Audiobooks",
                    NavigationUrl = AudiobookRoute(item),
                    Shape = ArtworkShape.Portrait,
                };
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.ImageUrl))
            .Take(4)
            .ToList();
        var years = personGroup.Items
            .Select(AudiobookPublicationYear)
            .Where(year => year.HasValue)
            .Select(year => year!.Value)
            .ToList();
        var latestTimestamp = personGroup.Items.Count == 0
            ? DateTimeOffset.MinValue
            : personGroup.Items.Max(AudiobookSortTimestamp);

        return MediaPersonGroupTileComposer.Compose(
            personId ?? GenerateDeterministicGuid($"{role}:{personGroup.Name}"),
            personGroup.Name,
            personId,
            identity?.PersonPhotoUrl ?? identity?.ArtistPhotoUrl,
            roles,
            "Audiobooks",
            personGroup.Items.Count,
            years.Count == 0 ? null : years.Min(),
            years.Count == 0 ? null : years.Max(),
            latestTimestamp,
            artworkItems,
            MediaPersonGroupTileComposer.NavigationUrl(personId, personGroup.Name),
            "var(--tl-media-audio)");
    }

    private static IReadOnlyList<ArtworkStackItem> BuildContentGroupArtwork(ContentGroupViewModel group)
    {
        var artworkItems = group.PreviewItems
            .Where(item => !string.IsNullOrWhiteSpace(item.ImageUrl))
            .Select(item => new ArtworkStackItem
            {
                Id = item.WorkId.ToString("D"),
                WorkId = item.WorkId,
                Title = item.Title,
                ImageUrl = item.ImageUrl,
                MediaType = group.PrimaryMediaType,
                Shape = item.Shape.Trim().ToLowerInvariant() switch
                {
                    "square" => ArtworkShape.Square,
                    "wide" or "landscape" => ArtworkShape.Wide,
                    _ => ArtworkShape.Portrait,
                },
                Position = item.Position,
                Description = item.Description,
                Facts = item.Facts ?? [],
            })
            .Take(4)
            .ToList();

        if (artworkItems.Count == 0 && !string.IsNullOrWhiteSpace(group.CoverUrl))
        {
            artworkItems.Add(new ArtworkStackItem
            {
                Id = group.CollectionId.ToString("D"),
                Title = group.DisplayName,
                ImageUrl = group.CoverUrl,
                MediaType = group.PrimaryMediaType,
                Shape = ArtworkShape.Portrait,
            });
        }

        return artworkItems;
    }

    private static int? AudiobookPublicationYear(AudiobookDisplayItem item)
    {
        if (int.TryParse(item.Work?.Year, NumberStyles.Integer, CultureInfo.InvariantCulture, out var workYear))
        {
            return workYear;
        }

        foreach (var fact in item.Card?.Facts ?? [])
        {
            if (fact.Length == 4
                && int.TryParse(fact, NumberStyles.Integer, CultureInfo.InvariantCulture, out var factYear))
            {
                return factYear;
            }
        }

        return null;
    }

    private static bool PersonNamesMatch(string left, string right) =>
        string.Equals(
            NormalizePersonName(left),
            NormalizePersonName(right),
            StringComparison.Ordinal);

    private static string NormalizePersonName(string value) =>
        new(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    private MediaTileViewModel ToAlbumTile(ContentGroupViewModel album) =>
        SquareTile(
            id: album.CollectionId,
            title: album.DisplayName,
            subtitle: StringHelpers.FirstNonBlankOr("-", album.Creator, album.Year, Pluralize(album.WorkCount, "track")),
            imageUrl: album.CoverUrl ?? album.ArtistPhotoUrl,
            backgroundUrl: album.BackgroundUrl ?? album.HeroUrl,
            bannerUrl: album.BannerUrl,
            logoUrl: album.LogoUrl,
            description: album.Description,
            facts: string.IsNullOrWhiteSpace(album.Year) ? [] : [album.Year],
            route: $"/details/musicalbum/{album.CollectionId:D}?context=listen",
            presentation: MediaTilePresentation.Album,
            primaryActionLabel: "Open",
            sortYear: int.TryParse(album.Year, out var albumYear) ? albumYear : 0);

    private MediaTileViewModel ToArtistAlbumTile(CollectionGroupSeasonDto album)
    {
        var title = album.SeasonLabel ?? $"Album {album.SeasonNumber}";
        var route = album.AlbumCollectionId.HasValue
            ? $"/details/musicalbum/{album.AlbumCollectionId.Value}?context=listen"
            : BuildAlbumDetailRoute(album.SeasonLabel, _artistDetail?.DisplayName);

        return SquareTile(
            id: album.AlbumCollectionId ?? GenerateDeterministicGuid($"{_artistDetail?.DisplayName}:{title}"),
            title: title,
            subtitle: StringHelpers.FirstNonBlankOr("-", album.Year, Pluralize(album.Episodes.Count, "track")),
            imageUrl: album.CoverUrl,
            route: route,
            presentation: MediaTilePresentation.Album,
            primaryActionLabel: "Open");
    }

    private static MediaTileViewModel SquareTile(
        Guid id,
        string title,
        string? subtitle,
        string? imageUrl,
        string route,
        MediaTilePresentation presentation,
        string primaryActionLabel,
        string? backgroundUrl = null,
        string? bannerUrl = null,
        string? logoUrl = null,
        string? description = null,
        IReadOnlyList<string>? facts = null,
        int sortYear = 0)
    {
        var small = MediaTileArtworkUrl.Sized(imageUrl, "s");
        var medium = MediaTileArtworkUrl.Sized(imageUrl, "m");
        var backgroundMedium = MediaTileArtworkUrl.Sized(backgroundUrl, "m");
        var backgroundLarge = MediaTileArtworkUrl.Sized(backgroundUrl, "l");
        var bannerMedium = MediaTileArtworkUrl.Sized(bannerUrl, "m");
        var bannerLarge = MediaTileArtworkUrl.Sized(bannerUrl, "l");
        var hoverImage = StringHelpers.FirstNonBlank(backgroundMedium, bannerMedium, medium);
        return new MediaTileViewModel
        {
            Id = id,
            Title = title,
            Subtitle = subtitle,
            Description = description,
            CoverUrl = imageUrl,
            BackgroundUrl = backgroundUrl,
            BannerUrl = bannerUrl,
            LogoUrl = logoUrl,
            HoverFacts = facts ?? [],
            MediaKind = "Music",
            Shape = MediaTileShape.Square,
            HoverArtworkShape = !string.IsNullOrWhiteSpace(backgroundMedium) || !string.IsNullOrWhiteSpace(bannerMedium)
                ? MediaTileShape.Landscape
                : MediaTileShape.Square,
            Presentation = presentation,
            SurfaceKind = MediaTileSurfaceKind.CoverSquare,
            HoverLayout = !string.IsNullOrWhiteSpace(backgroundMedium) || !string.IsNullOrWhiteSpace(bannerMedium)
                ? MediaTileHoverLayout.BannerPopover
                : MediaTileHoverLayout.ArtOnlyPopover,
            HoverMode = MediaTileHoverMode.Expanded,
            TileTextMode = MediaTileTextMode.CoverOnly,
            TileImageUrl = small,
            TileImageSrcSet = MediaTileArtworkUrl.SrcSet(small, medium),
            HoverImageUrl = hoverImage,
            HoverImageSrcSet = MediaTileArtworkUrl.SrcSet(hoverImage, StringHelpers.FirstNonBlank(backgroundLarge, bannerLarge, medium)),
            TileImageFitMode = MediaTileImageFitMode.Contain,
            HoverImageFitMode = !string.IsNullOrWhiteSpace(backgroundMedium) || !string.IsNullOrWhiteSpace(bannerMedium)
                ? MediaTileImageFitMode.Fill
                : MediaTileImageFitMode.Contain,
            NavigationUrl = route,
            DetailsNavigationUrl = route,
            PrimaryNavigationUrl = route,
            PrimaryActionLabel = primaryActionLabel,
            AccentColor = "var(--tl-media-audio)",
            SortTimestamp = DateTimeOffset.UtcNow,
            SortYear = sortYear,
        };
    }

    private static Guid GenerateDeterministicGuid(string value)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes);
    }

    private sealed record ListenPlaylistTrackRow(CollectionItemDto? Item, WorkViewModel Work, int Index);
    private sealed record PlaylistColumnDefinition(string Key, string Label);
    private sealed record AudiobookDisplayItem(DisplayCardDto? Card, WorkViewModel? Work);
    private sealed record AudiobookListenItem(AudiobookDisplayItem Item, JourneyItemViewModel? Journey);
    private sealed record AudiobookAuthorGroup(string Name, IReadOnlyList<AudiobookDisplayItem> Items);
}
