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
    // ---- The playlist's songs on the shared SongTable (spec music-table-and-reader-frame WP5) -------------------------------------

    private IReadOnlySet<string> _playlistFavoriteIds = new HashSet<string>();
    private (IReadOnlySet<Guid>? Source, int Count)? _playlistFavoritesKey;

    // The rows are worked out from the playlist's items at each render, as the old table did; the table keeps this order as given.
    private IReadOnlyList<SongRow> PlaylistSongRows
        => IsFavoritesPlaylist
            ? [.. ActivePlaylistTrackRows.Select(row => SongRowMapper.FromFavorite(row.Work, row.Index))]
            : [.. ActivePlaylistTrackRows.Select(row => SongRowMapper.FromPlaylistItem(row.Item, row.Work, row.Index))];

    private string? PlaylistCurrentRowId => Playback.CurrentItem is { } current ? current.WorkId.ToString("D") : null;

    private IReadOnlySet<string> PlaylistFavoriteIds
    {
        get
        {
            if (_playlistFavoritesKey is not { } known || !ReferenceEquals(known.Source, _favoriteWorkIds) || known.Count != _favoriteWorkIds.Count)
            {
                _playlistFavoriteIds = _favoriteWorkIds.Select(id => id.ToString("D")).ToHashSet(StringComparer.Ordinal);
                _playlistFavoritesKey = (_favoriteWorkIds, _favoriteWorkIds.Count);
            }

            return _playlistFavoriteIds;
        }
    }

    // Reordering is offered only on a playlist this profile owns, and on its own Favorites; the other built-in lists have no items to move.
    // Moving your own Favorites is not library editing, so it follows the same rule as your own hand-made playlist. The Engine still
    // decides who may change a saved order, and a refusal shows the same notice as before.
    private bool CanReorderActivePlaylist
        => IsFavoritesPlaylist
            ? _activeProfileId.HasValue
            : ActivePlaylistCollection is not null && string.IsNullOrWhiteSpace(PlaylistKey) && !CanSaveActivePlaylist;

    // Reorder mode (spec Collections and View fixes, #877): drag, Alt+Up/Down and Move up/down change only the page's copy of the order until
    // Save order; Cancel and Esc put the saved order back. _trackOrderBefore holds each item with the position it was saved at.
    private List<(CollectionItemDto Item, int SortOrder)>? _trackOrderBefore;
    private bool _trackOrderSaving;

    private bool TrackReorderActive => _trackOrderBefore is not null;
    private bool TrackReorderDirty => _trackOrderBefore is { } before && !before.OrderBy(pair => pair.SortOrder).Select(pair => pair.Item.Id)
        .SequenceEqual(_playlistItems.OrderBy(item => item.SortOrder).Select(item => item.Id));

    private void BeginTrackReorder() => _trackOrderBefore = [.. _playlistItems.Select(item => (item, item.SortOrder))];

    private Task CancelTrackReorder()
    {
        if (_trackOrderBefore is { } before)
        {
            foreach (var (item, sortOrder) in before)
            {
                item.SortOrder = sortOrder;
            }
        }

        _trackOrderBefore = null;
        return Task.CompletedTask;
    }

    private async Task<bool> SaveTrackReorderAsync()
    {
        _trackOrderSaving = true;
        try
        {
            var saved = await SavePlaylistTrackOrderAsync([.. _playlistItems.OrderBy(item => item.SortOrder)]);
            if (saved)
            {
                _trackOrderBefore = null;
            }

            return saved;
        }
        finally
        {
            _trackOrderSaving = false;
        }
    }

    private WorkViewModel? PlaylistWorkFor(SongRow row)
        => row.WorkId is { } workId
            ? _workLookup.GetValueOrDefault(workId) ?? ActivePlaylistTracks.FirstOrDefault(work => work.Id == workId)
            : null;

    // Favorites play in the order the table shows (a column may be sorting it); a hand-made playlist plays in its saved order.
    private Task PlayPlaylistRowAsync(SongPlayRequest request)
        => PlayTracksAsync(
            IsFavoritesPlaylist && request.Rows.Count > 0
                ? [.. request.Rows.Select(PlaylistWorkFor).OfType<WorkViewModel>()]
                : ActivePlaylistTracks,
            request.Start.WorkId,
            ActivePlaylistTitle);

    private Task PlayNextPlaylistRowAsync(SongRow row)
        => PlaylistWorkFor(row) is { } work ? PlayNextTrackAsync(work) : Task.CompletedTask;

    private Task QueuePlaylistRowAsync(SongRow row)
        => PlaylistWorkFor(row) is { } work ? QueueTrackAsync(work) : Task.CompletedTask;

    private Task ToggleFavoritePlaylistRowAsync(SongRow row)
        => PlaylistWorkFor(row) is { } work ? ToggleFavoriteAsync(work) : Task.CompletedTask;

    // Edit opens the universal editor over this page with the song ticked (the table only offers it to a desktop that may edit).
    private Task EditPlaylistRowAsync(SongRow row)
        => PlaylistWorkFor(row) is { } work ? EditTrackAsync(work) : Task.CompletedTask;

    private Task OpenPlaylistRowAlbumAsync(SongRow row)
    {
        if (PlaylistWorkFor(row) is { } work && BuildAlbumRouteForTrack(work) is { Length: > 0 } route)
        {
            NavigateTo(route);
        }

        return Task.CompletedTask;
    }

    private Task RemovePlaylistRowAsync(SongRow row)
        => IsFavoritesPlaylist
            ? RemoveFavoriteRowAsync(row)
            : row.PlaylistItemId is { } itemId ? RemovePlaylistTrackAsync(itemId) : Task.CompletedTask;

    // Remove from Favorites takes the heart off, in place (no notice unless it fails); the row leaves because the list is read again.
    private async Task RemoveFavoriteRowAsync(SongRow row)
    {
        if (!_activeProfileId.HasValue || row.WorkId is not { } songId)
        {
            return;
        }

        _notice = null;
        if (!await SavedItems.RemoveAsync(ProfileEntityKind.Song, songId))
        {
            ShowNotice(L["ListenPage_SongRemoveFailed"].Value, AppUiTone.Warning, urgent: true);
            return;
        }

        await RefreshReactionStateAsync();
    }

    // Favorites are moved by song, not by a playlist item. The Engine counts places among the songs this profile sees, so the target place
    // is found in the order the page read from the Engine, not in the rows drawn (a song the page could not resolve never shifts it).
    private async Task ReorderFavoriteRowAsync(SongReorderRequest request)
    {
        if (!CanReorderActivePlaylist || request.Row.WorkId is not { } songId)
        {
            return;
        }

        var shown = ActivePlaylistTrackRows;
        if (request.ToIndex < 0 || request.ToIndex >= shown.Count)
        {
            return;
        }

        var toIndex = _favoriteSongOrder.IndexOf(shown[request.ToIndex].Work.Id);
        var fromIndex = _favoriteSongOrder.IndexOf(songId);
        if (toIndex < 0 || fromIndex < 0 || toIndex == fromIndex)
        {
            return;
        }

        _notice = null;
        if (!await SavedItems.MoveFavoriteSongAsync(songId, toIndex))
        {
            ShowNotice(L["ListenPage_PlaylistOrderSaveFailed"].Value, AppUiTone.Warning, urgent: true);
            return;
        }

        _favoriteSongOrder.RemoveAt(fromIndex);
        _favoriteSongOrder.Insert(toIndex, songId);
    }

    // Drag and the keyboard both arrive here as "this row now sits where that row was". The saved order is worked out on the playlist's own
    // items (so a song the page could not resolve never shifts the result) and written by SavePlaylistTrackOrderAsync, the one write path.
    private async Task ReorderPlaylistRowAsync(SongReorderRequest request)
    {
        if (IsFavoritesPlaylist)
        {
            await ReorderFavoriteRowAsync(request);
            return;
        }

        if (!CanReorderActivePlaylist || request.Row.PlaylistItemId is not { } movedItemId)
        {
            return;
        }

        var shown = ActivePlaylistTrackRows;
        if (request.ToIndex < 0 || request.ToIndex >= shown.Count || shown[request.ToIndex].Item is not { } target)
        {
            return;
        }

        var ordered = _playlistItems.OrderBy(item => item.SortOrder).ToList();
        var sourceIndex = ordered.FindIndex(item => item.Id == movedItemId);
        var targetIndex = ordered.FindIndex(item => item.Id == target.Id);
        if (sourceIndex < 0 || targetIndex < 0)
        {
            return;
        }

        var reordered = ListenPlaylistOrder.MoveOnto(ordered, sourceIndex, targetIndex);
        if (reordered is null)
        {
            return;
        }

        if (TrackReorderActive)
        {
            // Staged only: the order is written by Save order.
            for (var index = 0; index < reordered.Count; index++)
            {
                reordered[index].SortOrder = index + 1;
            }

            return;
        }

        await SavePlaylistTrackOrderAsync(reordered);
    }

    private IReadOnlyList<WorkViewModel> ResolveActivePlaylistTracks()
    {
        if (!string.IsNullOrWhiteSpace(PlaylistKey))
        {
            return PlaylistKey.ToLowerInvariant() switch
            {
                "all-music" => SortedSongs.ToList(),
                // Favorites keep the profile's own order: newest hearted first unless it was moved by hand.
                "favorite-songs" => ResolveFavoriteTracks(),
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

    private IReadOnlyList<WorkViewModel> ResolveFavoriteTracks()
    {
        var music = new Dictionary<Guid, WorkViewModel>();
        foreach (var work in _musicWorks)
        {
            music.TryAdd(work.Id, work);
        }

        return _favoriteSongOrder
            .Select(id => music.GetValueOrDefault(id))
            .OfType<WorkViewModel>()
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

    private async Task<bool> SavePlaylistTrackOrderAsync(List<CollectionItemDto> orderedItems)
    {
        if (ActivePlaylistCollection is null)
        {
            return false;
        }

        _notice = null;
        var saved = await ApiClient.ReorderCollectionItemsAsync(
            ActivePlaylistCollection.Id,
            orderedItems.Select(item => item.Id).ToList(),
            _activeProfileId);

        if (!saved)
        {
            ShowNotice(L["ListenPage_PlaylistOrderSaveFailed"].Value, AppUiTone.Warning, urgent: true);
            return false;
        }

        _playlistItems.Clear();
        for (var index = 0; index < orderedItems.Count; index++)
        {
            orderedItems[index].SortOrder = index + 1;
            _playlistItems.Add(orderedItems[index]);
        }

        return true;
    }

    private async Task RemovePlaylistTrackAsync(Guid itemId)
    {
        if (ActivePlaylistCollection is null)
        {
            return;
        }

        _notice = null;
        var removed = await ApiClient.RemoveCollectionItemAsync(ActivePlaylistCollection.Id, itemId, _activeProfileId);
        if (!removed)
        {
            ShowNotice(L["ListenPage_SongRemoveFailed"].Value, AppUiTone.Warning, urgent: true);
            return;
        }

        _playlistItems.RemoveAll(item => item.Id == itemId);
    }
}
