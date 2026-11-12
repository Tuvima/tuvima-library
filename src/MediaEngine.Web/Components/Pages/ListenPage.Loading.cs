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
    private async Task LoadAsync()
    {
        var loadVersion = ++_listenLoadVersion;
        _error = null;
        _loading = true;
        _redirecting = false;
        _albumDetail = null;
        _artistDetail = null;
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

            _favoriteSongOrder = await LoadFavoriteSongIdsAsync(ct);
            _favoriteWorkIds = _favoriteSongOrder.ToHashSet();
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

    private async Task ReloadAsync() => await LoadAsync();

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

    /// <summary>Favorites are the songs the profile saved (its saved items); Like and Dislike are separate reactions. Returned in the profile's saved order.</summary>
    private async Task<List<Guid>> LoadFavoriteSongIdsAsync(CancellationToken ct)
    {
        if (!_activeProfileId.HasValue)
        {
            return [];
        }

        return (await SavedItems.GetListAsync(ct))
            .Where(item => item.EntityKind == ProfileEntityKind.Song)
            .Select(item => item.EntityId)
            .ToList();
    }

    private async Task RefreshReactionStateAsync()
    {
        _favoriteSongOrder = await LoadFavoriteSongIdsAsync(CancellationToken.None);
        _favoriteWorkIds = _favoriteSongOrder.ToHashSet();
        _dislikedWorkIds = (await Reactions.GetDislikedWorkIdsAsync(_activeProfileId)).ToHashSet();
        StateHasChanged();
    }

    // After the songs table's bulk panel saved (universal editor spec D14): read the songs again and leave everything else as it is,
    // so the table keeps its sort, filters, scroll and ticks. LoadAsync would blank the page and drop them.
    private async Task RefreshSongsAsync()
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(LoadTimeout);
            var works = await Orchestrator.GetLibraryWorksAsync(timeoutCts.Token);

            _allWorks.Clear();
            _allWorks.AddRange(works);

            _musicWorks.Clear();
            _musicWorks.AddRange(_allWorks.Where(IsMusicWork).OrderBy(work => work.Artist ?? work.Author).ThenBy(work => work.Album).ThenBy(work => ParseTrackNumber(work.TrackNumber)).ThenBy(DisplayTrackTitle));

            _audiobookWorks.Clear();
            _audiobookWorks.AddRange(_allWorks.Where(IsAudiobookWork).OrderBy(work => work.Author).ThenBy(work => work.Series).ThenBy(work => work.Title));

            _workLookup.Clear();
            foreach (var work in _allWorks.GroupBy(work => work.Id).Select(group => group.First()))
            {
                _workLookup[work.Id] = work;
            }

            _artistDetailCache.Clear();
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
        {
            ShowNotice(L["ListenPage_SongsSavedListNotRefreshed"].Value, AppUiTone.Warning);
        }
    }
}
