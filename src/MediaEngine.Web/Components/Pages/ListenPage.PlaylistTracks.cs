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
        => [.. ActivePlaylistTrackRows.Select(row => SongRowMapper.FromPlaylistItem(row.Item, row.Work, row.Index))];

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

    // Reordering is offered only on a playlist this profile owns; the built-in lists have no items to move. The Engine still decides
    // who may change a saved order, and a refusal shows the same notice as before.
    private bool CanReorderActivePlaylist
        => ActivePlaylistCollection is not null && string.IsNullOrWhiteSpace(PlaylistKey) && !CanSaveActivePlaylist;

    private WorkViewModel? PlaylistWorkFor(SongRow row)
        => row.WorkId is { } workId
            ? _workLookup.GetValueOrDefault(workId) ?? ActivePlaylistTracks.FirstOrDefault(work => work.Id == workId)
            : null;

    private Task PlayPlaylistRowAsync(SongPlayRequest request)
        => PlayTracksAsync(ActivePlaylistTracks, request.Start.WorkId, ActivePlaylistTitle);

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
        => row.PlaylistItemId is { } itemId ? RemovePlaylistTrackAsync(itemId) : Task.CompletedTask;

    // Drag and the keyboard both arrive here as "this row now sits where that row was". The saved order is worked out on the playlist's own
    // items (so a song the page could not resolve never shifts the result) and written by SavePlaylistTrackOrderAsync, the one write path.
    private async Task ReorderPlaylistRowAsync(SongReorderRequest request)
    {
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

        await SavePlaylistTrackOrderAsync(reordered);
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

        _notice = null;
        var saved = await ApiClient.ReorderCollectionItemsAsync(
            ActivePlaylistCollection.Id,
            orderedItems.Select(item => item.Id).ToList(),
            _activeProfileId);

        if (!saved)
        {
            ShowNotice(L["ListenPage_PlaylistOrderSaveFailed"].Value, AppUiTone.Warning, urgent: true);
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
