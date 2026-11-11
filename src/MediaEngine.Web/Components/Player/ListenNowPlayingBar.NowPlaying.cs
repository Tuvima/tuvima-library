using System.Text.Json;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Profiles;
using MediaEngine.Domain.Enums;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.MediaTiles;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Components.Player;

public partial class ListenNowPlayingBar
{
    // Now playing: opening and leaving the Now playing page or the phone's expanded player, stepping a details window aside for a new audiobook, and the music Favorite.

    private Guid? _favoriteSongId, _favoriteProfileId;
    private long _favoriteLoadGeneration;
    private bool _isFavorite;

    private void SavedItemsChanged() => _ = InvokeAsync(async () => { await LoadFavoriteAsync(); StateHasChanged(); });

    private NowPlayingLauncher? _launcher;

    // The navbar icon asks the bar, which alone knows whether it is drawn for a phone: a phone toggles its expanded player, a desktop opens or leaves the page.
    private void HandleNowPlayingRequested() => _ = InvokeAsync(async () =>
    {
        if (_disposed || !Playback.HasQueue || Playback.IsVideoMode)
        {
            return;
        }

        if (IsPhonePresentation && Playback.PresentationSurface == PlaybackPresentationSurface.NowPlaying)
        {
            await CloseNowPlaying();
        }
        else
        {
            await OpenNowPlaying();
        }
    });

    // The Lyrics panel's Open Now playing button is offered only where it has somewhere to go; with the phone's Now playing already open it would only close the sheet.
    private EventCallback LyricsOpenNowPlaying =>
        IsPhonePresentation && Playback.PresentationSurface == PlaybackPresentationSurface.NowPlaying ? default : EventCallback.Factory.Create(this, OpenNowPlaying);

    private async Task OpenNowPlaying()
    {
        Tools?.Close(); CloseSettingsMenu();
        if (IsNowPlayingPage) { await CloseNowPlaying(); return; }
        if (!IsPhonePresentation)
        {
            // A desktop opens the Now playing page and remembers where the person was, so Back to library and Close return there.
            OpenNowPlayingPage(returnTo: null);
            await LoadFavoriteAsync();
            return;
        }
        Playback.SetPresentationSurface(PlaybackPresentationSurface.NowPlaying);
        await LoadFavoriteAsync();
    }

    private void OpenNowPlayingPage(string? returnTo)
    {
        // A stale phone overlay (the window was widened while it was open) must not stay behind the page.
        if (Playback.PresentationSurface == PlaybackPresentationSurface.NowPlaying)
        {
            Playback.SetPresentationSurface(PlaybackPresentationSurface.Docked);
        }

        var from = returnTo ?? DetailsOverlayState.ToAppPath(Nav, RoutedPageAddress.AbsoluteFor(Nav, Overlay));
        Nav.NavigateTo(NowPlayingRoute.Build(from));
    }

    private async Task LoadFavoriteAsync()
    {
        var generation = Interlocked.Increment(ref _favoriteLoadGeneration);
        var item = Playback.CurrentItem;
        var request = Playback.PlaybackRequestVersion;
        var profileId = Playback.ActiveProfileId;
        _isFavorite = false;
        _favoriteSongId = null;
        _favoriteProfileId = null;
        if (!Playback.IsMusicMode || item?.WorkId is not { } workId || workId == Guid.Empty)
        {
            return;
        }

        var profile = await Orchestrator.GetActiveProfileAsync();
        bool Current() => !_disposed && generation == Volatile.Read(ref _favoriteLoadGeneration)
            && Playback.IsMusicMode && Playback.PlaybackRequestVersion == request
            && Playback.CurrentItem?.WorkId == workId && Playback.ActiveProfileId == profileId;
        if (!Current() || profile?.Id != profileId)
        {
            return;
        }

        var membership = await SavedItems.GetMembershipAsync(ProfileEntityKind.Song, workId);
        if (!Current())
        {
            return;
        }

        _favoriteSongId = workId;
        _favoriteProfileId = profileId;
        _isFavorite = membership.IsSaved;
        await InvokeAsync(StateHasChanged);
    }

    private async Task ToggleFavoriteAsync()
    {
        var request = Playback.PlaybackRequestVersion;
        var profileId = Playback.ActiveProfileId;
        if (!Playback.IsMusicMode || Playback.CurrentItem?.WorkId is not { } workId || workId == Guid.Empty)
        {
            return;
        }

        bool Current() => !_disposed && Playback.IsMusicMode && Playback.PlaybackRequestVersion == request
            && Playback.CurrentItem?.WorkId == workId && Playback.ActiveProfileId == profileId;
        var profile = await Orchestrator.GetActiveProfileAsync();
        if (!Current())
        {
            return;
        }

        if (profile is null || profile.Id != profileId)
        {
            _barNotice = L["Common_FavoritesRequireAnActiveProfile"].Value;
            return;
        }
        _barNotice = null;
        if (_favoriteSongId != workId || _favoriteProfileId != profileId)
        {
            await LoadFavoriteAsync();
        }

        if (!Current())
        {
            return;
        }
        // Favorite is membership in the profile's saved items; it never sets or clears Like or Dislike.
        await SavedItems.ToggleAsync(ProfileEntityKind.Song, workId);
        if (Current())
        {
            await LoadFavoriteAsync();
        }
    }

    // Unified player D6: an audiobook started from a details window must not stay under it. The window is a modal dialog, so it would hide the
    // bar and block the player; step it aside, show Now Playing, and open the same details page again when the person closes Now Playing.
    // Optional: a host rendered without the details overlay (some tests, the pop-out window) simply never steps one aside.
    private DetailsOverlayState? DetailsOverlay => Services.GetService<DetailsOverlayState>();
    private long _lastAudiobookRequestVersion = -1;
    private string? _returnToDetailsUri;

    private async Task StepAsideForNewAudiobookAsync()
    {
        var version = Playback.PlaybackRequestVersion;
        if (!Playback.IsAudiobookMode || Playback.IsVideoMode || version == _lastAudiobookRequestVersion)
        {
            return;
        }

        _lastAudiobookRequestVersion = version;
        if (DetailsOverlay is not { IsOpen: true } overlay || overlay.Current.OverlayUri is not { } detailsUri)
        {
            return;
        }

        if (!await overlay.DismissAsync())
        {
            return;
        }

        if (!IsPhonePresentation)
        {
            // The page remembers the details window as the place to come back to.
            OpenNowPlayingPage(returnTo: detailsUri);
            return;
        }
        _returnToDetailsUri = detailsUri;
        Playback.SetPresentationSurface(PlaybackPresentationSurface.NowPlaying);
    }

    private void ReturnToDetailsAfterNowPlaying()
    {
        if (_returnToDetailsUri is not { } uri)
        {
            return;
        }

        _returnToDetailsUri = null;
        if (DetailsOverlay is { IsOpen: false })
        {
            Nav.NavigateTo(Nav.ToAbsoluteUri(uri).ToString());
        }
    }

    private async Task CloseNowPlaying()
    {
        Tools?.Close(); CloseSettingsMenu();
        Playback.SetPresentationSurface(PlaybackPresentationSurface.Docked);
        // Leaving the page is going back to where the person came from; playback keeps going.
        if (IsNowPlayingPage)
        {
            Nav.NavigateTo(NowPlayingRoute.ReturnTarget(Nav, Overlay));
        }

        ReturnToDetailsAfterNowPlaying();
        if (_shell?.ArtworkElement is { } artwork)
        {
            await artwork.FocusAsync();
        }
    }
}
