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

    private static string PlaylistRowDomId(Guid itemId) => $"listen-playlist-row-{itemId:N}";

    private static string PlaylistCardSubtitle(WorkViewModel track)
    {
        var artist = StringHelpers.FirstNonBlank(track.Artist, track.Author);
        var album = StringHelpers.FirstNonBlank(track.Album);
        return string.Join(" · ", new[] { artist, album }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    // Menu and keyboard reorder share SavePlaylistTrackOrderAsync with the drag handlers, so there is one write path.
    private async Task MovePlaylistTrackAsync(Guid itemId, int delta, bool keepFocus = false)
    {
        var currentItems = _playlistItems.OrderBy(item => item.SortOrder).ToList();
        var sourceIndex = currentItems.FindIndex(item => item.Id == itemId);
        var orderedItems = ListenPlaylistOrder.Move(currentItems, sourceIndex, delta);
        if (orderedItems is null)
        {
            return;
        }

        var targetIndex = sourceIndex + delta;
        var moved = currentItems[sourceIndex];
        await SavePlaylistTrackOrderAsync(orderedItems);
        var title = _workLookup.GetValueOrDefault(moved.WorkId)?.Title ?? "Song";
        _playlistAnnouncement = $"{title} moved to position {targetIndex + 1} of {orderedItems.Count}";
        if (keepFocus)
        {
            _pendingPlaylistRowFocusId = itemId;
        }
    }

    private Task HandlePlaylistRowKeyDownAsync(KeyboardEventArgs args, CollectionItemDto? item, WorkViewModel? playOnEnter = null)
    {
        if (args.AltKey && item is not null && (args.Key == "ArrowUp" || args.Key == "ArrowDown"))
        {
            return MovePlaylistTrackAsync(item.Id, args.Key == "ArrowUp" ? -1 : 1, keepFocus: true);
        }

        if (playOnEnter is not null && (args.Key == "Enter" || args.Key == " "))
        {
            return PlayTracksAsync(ActivePlaylistTracks, playOnEnter.Id, ActivePlaylistTitle);
        }

        return Task.CompletedTask;
    }

    private IReadOnlyList<AppRowMenuItem> PlaylistRowMenuItems(ListenPlaylistTrackRow row)
    {
        var track = row.Work;
        var item = row.Item;
        var items = new List<AppRowMenuItem>
        {
            new() { Label = "Play next", Icon = AppMaterialIcons.Outlined.PlaylistPlay, OnClick = EventCallback.Factory.Create(this, () => PlayNextTrackAsync(track)) },
            new() { Label = "Add to queue", Icon = AppMaterialIcons.Outlined.PlaylistAdd, OnClick = EventCallback.Factory.Create(this, () => QueueTrackAsync(track)) },
        };

        if (item is not null)
        {
            var lastIndex = _playlistItems.Count - 1;
            items.Add(new() { Label = "Move up", Icon = AppMaterialIcons.Outlined.KeyboardArrowUp, Disabled = row.Index <= 0, OnClick = EventCallback.Factory.Create(this, () => MovePlaylistTrackAsync(item.Id, -1)) });
            items.Add(new() { Label = "Move down", Icon = AppMaterialIcons.Outlined.KeyboardArrowDown, Disabled = row.Index >= lastIndex, OnClick = EventCallback.Factory.Create(this, () => MovePlaylistTrackAsync(item.Id, 1)) });
        }

        var albumRoute = BuildAlbumRouteForTrack(track);
        items.Add(new() { Label = "Go to album", Icon = AppMaterialIcons.Outlined.Album, Disabled = string.IsNullOrWhiteSpace(albumRoute), OnClick = EventCallback.Factory.Create(this, () => { if (!string.IsNullOrWhiteSpace(albumRoute)) { NavigateTo(albumRoute); } }) });
        items.Add(new() { Label = "Go to artist", Icon = AppMaterialIcons.Outlined.Person, Disabled = track.ArtistPersonId is null, OnClick = EventCallback.Factory.Create(this, () => { if (track.ArtistPersonId is { } artistId) { NavigateTo($"/details/person/{artistId:D}"); } }) });
        if (item is not null)
        {
            items.Add(new() { Label = "Remove", Icon = AppMaterialIcons.Outlined.Delete, Destructive = true, OnClick = EventCallback.Factory.Create(this, () => RemovePlaylistTrackAsync(item.Id)) });
        }

        return items;
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
