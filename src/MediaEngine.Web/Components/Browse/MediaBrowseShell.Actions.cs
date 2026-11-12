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
    // What the last action had to say, shown at the top of the list until the next action or the close button.
    private string? _notice;
    private AppUiTone _noticeTone = AppUiTone.Warning;
    private bool _noticeUrgent;

    // urgent: a warning that says an action just failed is announced at once, like an error.
    private void ShowNotice(string text, AppUiTone tone, bool urgent = false)
    {
        _notice = text;
        _noticeTone = tone;
        _noticeUrgent = urgent;
    }

    private void ClearNotice() => _notice = null;

    private async Task ToggleFavoriteAsync(Guid workId)
    {
        ClearNotice();
        if (!_activeProfileId.HasValue)
        {
            ShowNotice(L["MediaBrowseShell_TheFavoriteCouldNotBe"].Value, AppUiTone.Warning, urgent: true);
            return;
        }
        var kind = IsMusicSongsGrouping ? ProfileEntityKind.Song : ProfileEntityKindForBrowse();
        // The heart is saved-item membership (a song's Favorites, My List for everything else); it never sets or clears Like or Dislike.
        var membership = await SavedItems.ToggleAsync(kind, workId);
        if (membership is null)
        {
            ShowNotice(L["MediaBrowseShell_TheFavoriteCouldNotBe"].Value, AppUiTone.Warning, urgent: true);
            return;
        }

        // The heart on the row or tile shows the new state once the list reloads; that is the confirmation.
        await LoadBrowseContentAsync();
    }

    private ProfileEntityKind ProfileEntityKindForBrowse() => Preset.HeroVariant switch
    {
        BrowseHeroVariant.Watch => string.Equals(CurrentTab.MediaType, "TV", StringComparison.OrdinalIgnoreCase)
            ? ProfileEntityKind.TvShow
            : ProfileEntityKind.Movie,
        BrowseHeroVariant.Read => string.Equals(CurrentTab.MediaType, "Comics", StringComparison.OrdinalIgnoreCase)
            ? ProfileEntityKind.Comic
            : ProfileEntityKind.Book,
        BrowseHeroVariant.Listen => string.Equals(CurrentTab.MediaType, "Audiobooks", StringComparison.OrdinalIgnoreCase)
            ? ProfileEntityKind.Audiobook
            : ProfileEntityKind.Album,
        _ => ProfileEntityKind.Movie,
    };

    private async Task PlaySongTracksAsync(
        IReadOnlyList<WorkViewModel> tracks,
        Guid? startWorkId,
        string? sourceLabel,
        bool shuffle)
    {
        if (tracks.Count == 0)
        {
            return;
        }

        var startIndex = startWorkId.HasValue
            ? Math.Max(0, tracks.ToList().FindIndex(track => track.Id == startWorkId.Value))
            : 0;
        await Playback.ReplaceQueueAsync(tracks, startIndex, sourceLabel ?? "All Songs", shuffle);
    }

    private Task ToggleSongFavoriteAsync(WorkViewModel track) =>
        ToggleFavoriteAsync(track.Id);

    private async Task QueueSongAsync(WorkViewModel track)
    {
        // The queue in the player shows the song; that is the confirmation.
        await Playback.AddToQueueAsync(track);
    }

    private async Task PlayNextSongAsync(WorkViewModel track)
    {
        // The queue in the player shows the song next; that is the confirmation.
        await Playback.InsertNextAsync(track);
    }

    private Task OpenSongAlbumAsync(WorkViewModel track)
    {
        if (track.RootWorkId is not Guid albumId || albumId == Guid.Empty)
        {
            ShowNotice(L["MediaBrowseShell_ThisSongIsNotLinked"].Value, AppUiTone.Info);
            return Task.CompletedTask;
        }

        Nav.NavigateTo($"/details/musicalbum/{albumId:D}?context=listen");
        return Task.CompletedTask;
    }

    private async Task PlayRequestedSongAsync()
    {
        if (!IsMusicSongsGrouping
            || _requestedTrackId is not Guid requestedTrackId
            || _lastAutoPlayedTrackId == requestedTrackId)
        {
            return;
        }

        var tracks = SongTracks;
        if (tracks.All(track => track.Id != requestedTrackId))
        {
            return;
        }

        _lastAutoPlayedTrackId = requestedTrackId;
        await PlaySongTracksAsync(tracks, requestedTrackId, "All Songs", shuffle: false);
    }

    private Task BeginSongDragAsync(Guid workId)
    {
        var track = SongTracks.FirstOrDefault(item => item.Id == workId);
        if (track is not null)
        {
            AudioDrag.BeginDrag([workId], [ListenQueueItemFactory.Create(track)]);
        }

        return Task.CompletedTask;
    }

    private Task EndSongDragAsync()
    {
        AudioDrag.Clear();
        return Task.CompletedTask;
    }

    // ---- The phone Songs header (music table spec D21) -------------------------------------------------------------

    // A sort chosen in the phone header (or remembered for the profile). The browse sort menu still works; choosing there takes over again.
    private SongSortState? _phoneSongSort;

    private bool UsePhoneSongsHeader => DeviceContext.IsPhone && IsMusicSongsGrouping && UseListLayout;

    private SongSortState SongListSort => _phoneSongSort ?? BrowseQueryBuilder.SongListSort(_sortBy);

    private Task OnPhoneSongsSortAsync(SongSortState sort)
    {
        _phoneSongSort = sort;
        return Task.CompletedTask;
    }

    private async Task OnPhoneSongsSearchAsync(string text)
    {
        _searchText = text;
        await OnSearchChanged(text);
    }

    // Play and Shuffle play the list as sorted and filtered on screen: the page's own filters decide the songs, the table's sort rules the order.
    private async Task PlayPhoneSongsAsync(bool shuffle)
    {
        var ordered = SongList.Ordered(SongTracks, SongTablePlace.Songs, SongListSort, SongFavoriteIds);
        await PlaySongTracksAsync(ordered, null, "All Songs", shuffle);
    }
}
