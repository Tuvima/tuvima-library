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
    private async Task PlaySingleWorkAsync(WorkViewModel work, string sourceLabel)
    {
        await Playback.PlayWorkAsync(work, sourceLabel);
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
    }

    private async Task PlayNextTrackAsync(WorkViewModel work)
    {
        await Playback.InsertNextAsync(work);
    }

    // A song's Edit (the playlist's Edit button): the pop-over editor opens over this page with the song ticked and the Edit panel open
    // (universal editor v3 D6, D19). Nothing navigates away, and the Engine's own answer says whether the song may be edited.
    private async Task EditTrackAsync(WorkViewModel work)
    {
        var row = new BulkEditRow(work.Id, DisplayTrackTitle(work), DetailEntityType.Work, work.CollectionId, "Music");
        _notice = null;
        if (!await EditLauncher.OpenRowAsync(row, DetailPresentationContext.Listen) && EditLauncher.LastNotice is { } why)
        {
            ShowNotice(why, AppUiTone.Warning);
        }
    }

    private string? BuildAlbumRouteForTrack(WorkViewModel work)
    {
        if (work.CollectionId.HasValue)
        {
            return $"/details/musicalbum/{work.CollectionId.Value:D}?context=listen&track={work.Id:D}";
        }

        if (string.IsNullOrWhiteSpace(work.Album))
        {
            return null;
        }

        return BuildAlbumDetailRoute(work.Album, StringHelpers.FirstNonBlank(work.Artist, work.Author), work.Id);
    }

    private async Task AddToPlaylistAsync(WorkViewModel work, ManagedCollectionViewModel collection)
        => await AddTracksToPlaylistAsync([work], collection);

    private async Task ToggleFavoriteAsync(WorkViewModel work)
    {
        if (!_activeProfileId.HasValue)
        {
            return;
        }

        // Favorite is saved-item membership; it never sets or clears Like or Dislike.
        await SavedItems.ToggleAsync(ProfileEntityKind.Song, work.Id);
        await RefreshReactionStateAsync();
    }

    private async Task ToggleDislikeAsync(WorkViewModel work)
    {
        var nextReaction = _dislikedWorkIds.Contains(work.Id) ? MediaReaction.Neutral : MediaReaction.Dislike;
        await Reactions.SetReactionAsync(work.Id, nextReaction, _activeProfileId, ProfileEntityKind.Song);
        await RefreshReactionStateAsync();
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

    private async Task OnTrackFavoriteToggled(Guid entityId)
    {
        var target = CurrentTrackSurfaceTracks.FirstOrDefault(work => work.Id == entityId);
        if (target is null)
        {
            return;
        }

        await ToggleFavoriteAsync(target);
    }

    // The list tells which songs are being dragged (every ticked song when a ticked one is dragged); the playlist drop itself is the song
    // table's and the drop service's. This only lets the queue panel take the same songs.
    private Task OnTrackDragStarted(IReadOnlyList<Guid> entityIds)
    {
        _draggingTrackIds = entityIds.ToHashSet();

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

        CloseTrackContextMenu();
    }
}
