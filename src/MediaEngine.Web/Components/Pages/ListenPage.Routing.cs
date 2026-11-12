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
    private string CurrentPath => new Uri(RoutedPageAddress.AbsoluteFor(Nav, Overlay)).AbsolutePath;
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
                    ? "Listen - Library"
                : IsMusicBrowseEntry
                    ? "Music - Listen"
                : IsAudiobooksMode
                    ? "Audiobooks - Listen"
                    : "Listen - Library";

    private static readonly IReadOnlyList<MediaHubModeViewModel> ListenHubModes =
    [
        new("all", "Discover", "/listen", AppMaterialIcons.Outlined.Explore),
        new("music", "Music", MusicHomeRoute, AppMaterialIcons.Outlined.MusicNote),
        new("audiobooks", "Audiobooks", AudiobooksRoute, AppMaterialIcons.Outlined.Headphones),
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

    // Favorites open inside Listen as a playlist of the songs this profile hearted (Listen music spec D11), not in the For Me view.
    private const string FavoriteSongsRoute = "/listen/music/playlists/system/favorite-songs";

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
        new("Favorites", FavoriteSongsRoute, AppMaterialIcons.Outlined.FavoriteBorder),
        new("All Playlists", PlaylistsRoute, AppMaterialIcons.Outlined.QueueMusic),
    ];

    private async Task HandleTrackQueryAsync()
    {
        if (!Track.HasValue)
        {
            _lastHandledTrackContext = null;
            return;
        }

        var context = $"{CurrentPath}|{Track.Value}";
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

    private bool IsRouteActive(string route)
    {
        if (string.Equals(route, "/listen", StringComparison.OrdinalIgnoreCase))
        {
            return IsDefaultEntry;
        }

        var routeUri = Nav.ToAbsoluteUri(route);
        var currentUri = new Uri(RoutedPageAddress.AbsoluteFor(Nav, Overlay));
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
}
