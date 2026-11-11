using System.Text.Json;
using MediaEngine.Contracts.Profiles;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.MediaTiles;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Vault;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Components.Player;

public partial class ListenNowPlayingBar
{
    // Core state, the lifecycle (subscribe on start, unsubscribe and detach on dispose) and the browser hand-over after each render.

    private readonly JsonSerializerOptions _jsonOptions = MediaEngineJson.Web;
    private PlaybackShell? _shell;
    private ElementReference _audioRef;
    private ElementReference _playerShellRef;
    private DotNetObjectReference<ListenNowPlayingBar>? _selfReference;
    private PlaybackFullPlayer? _phoneFullPlayer;
    private bool _nowPlayingFocused;
    private bool _lastPhonePresentation;
    private bool IsPhonePresentation => PlaybackFormFactor.IsPhone(DeviceContext);
    private IJSObjectReference? _audioPresentationModule;
    private readonly string _audioPresentationObserverId = Guid.NewGuid().ToString("D");
    private bool _restored;
    private bool _disposed;
    private bool _browserClientRegistering;
    private string? _playbackRestoreError;
    private string? _lastAppliedStreamUrl;
    private string? _lastSeekedStreamUrl;
    private double? _lastSeekedPositionSeconds;
    private long _lastAppliedStartVersion = -1;
    private bool? _lastAppliedPlayingState;
    private long _playbackChangeNotificationVersion;
    private double? _lastAppliedVolume;
    private bool? _lastAppliedMuteState;
    private double? _lastAppliedPlaybackRate;
    private bool _settingsMenuOpen;
    private string? _settingsMenuTool;
    private string? _settingsMenuSurface;
    private long _transientPlaybackRequestVersion = -1;
    private long _nativeSleepTimerBindVersion;
    private readonly object _nativeSleepTimerBindingGate = new();
    private PendingNativeSleepTimerBinding? _pendingNativeSleepTimerBinding;
    private NativeSleepTimerBindingRequest? _nativeSleepTimerBinding;
    private bool _shortcutsRegistered;

    // The page under the details overlay counts as the current page while it is open.
    [CascadingParameter] public DetailsOverlayState? Overlay { get; set; }

    private bool IsListenRoute => new Uri(RoutedPageAddress.AbsoluteFor(Nav, Overlay)).AbsolutePath.StartsWith("/listen", StringComparison.OrdinalIgnoreCase);

    protected override void OnInitialized()
    {
        SavedItems.Changed += SavedItemsChanged;
        TimelineScope.Changed += TimelineScopeChanged;
        _ = TimelineScope.EnsureLoadedAsync();
        _ = RefreshBarMarkersAsync();
        Playback.SetTransportHostNotReady();
        Playback.Changed += HandlePlaybackChanged;
        AudioSidebar.Changed += HandleAudioPanelChanged;
        _launcher = Services.GetService<NowPlayingLauncher>();
        if (_launcher is not null)
        {
            _launcher.Requested += HandleNowPlayingRequested;
        }

        Playback.TransportCommandRequested += HandleTransportCommandAsync;
        Playback.SleepTimerNativeArmRequested += BindNativeSleepTimerAsync;
        Playback.SleepTimerPauseRequested += PauseNativeSleepTimerAsync;
        Playback.SleepTimerNativeSourcePrepareRequested += PrepareNativeSleepTimerSourceAsync;
        Playback.SleepTimerNativePositionRequested += ReadNativeSleepTimerPositionAsync;
        Nav.LocationChanged += HandleLocationChanged;
        _lastPhonePresentation = IsPhonePresentation;
        DeviceContext.OnChanged += HandleDeviceContextChanged;
    }

    private void HandleDeviceContextChanged() => _ = InvokeAsync(() =>
    {
        if (_disposed)
        {
            return;
        }

        SyncPhonePresentation();
        StateHasChanged();
    });

    // The player is drawn for a phone by one rule (PlaybackFormFactor). When the device stops being a phone while the phone's expanded player is open, the
    // expanded player closes with it, so the surface is never left at Now playing with nothing to draw it.
    private void SyncPhonePresentation()
    {
        var isPhone = IsPhonePresentation;
        if (_lastPhonePresentation == isPhone)
        {
            return;
        }

        _lastPhonePresentation = isPhone;
        CloseSettingsMenu(); Tools?.Close();
        if (!isPhone && Playback.PresentationSurface == PlaybackPresentationSurface.NowPlaying && !IsNowPlayingPage)
        {
            Playback.SetPresentationSurface(PlaybackPresentationSurface.Docked);
        }
    }

    private void HandleLocationChanged(object? sender, Microsoft.AspNetCore.Components.Routing.LocationChangedEventArgs args) =>
        _ = InvokeAsync(StateHasChanged);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // A full reload of a deep URL can mount the bar and dispose it again while its first render is still
        // awaiting the browser; the self reference is gone by then, so stop instead of using it.
        if (_disposed)
        {
            return;
        }

        try
        {
            await RunAfterRenderAsync(firstRender);
        }
        catch (JSDisconnectedException)
        {
            // The browser connection is gone, so there is nothing left to synchronise.
        }
        catch (Exception exception) when (_disposed && exception is ObjectDisposedException or JSException or TaskCanceledException or InvalidOperationException)
        {
            Logger.LogDebug(exception, "The playback bar was disposed while its browser calls were still running.");
        }
    }

    private async Task RunAfterRenderAsync(bool firstRender)
    {
        if (_openCompactToolPending && _compactContext is not null)
        {
            _openCompactToolPending = false;
            await _compactContext.OpenAsync(true);
        }
        if (firstRender)
        {
            try
            {
                await RegisterBrowserClientAsync();
            }
            catch (JSDisconnectedException)
            {
                return;
            }
            catch (Exception exception) when (_disposed && exception is ObjectDisposedException or JSException or TaskCanceledException)
            {
                Logger.LogDebug(exception, "The playback bar was disposed while it was connecting to the browser.");
            }

            if (_disposed)
            {
                // Disposal ran before registration finished and could not undo what registration added afterwards.
                await DetachBrowserClientAsync();
                _selfReference?.Dispose();
                _selfReference = null;
                return;
            }
        }

        if (Playback.HasQueue && !Playback.IsVideoMode && !_shortcutsRegistered && _selfReference is not null)
        {
            await JS.InvokeVoidAsync("listenPlayback.registerPlayerShortcuts", _playerShellRef, _selfReference);
            _shortcutsRegistered = true;
        }

        if (!_closeInProgress && !Playback.IsVideoMode
            && Playback.CurrentItem is not null
            && !string.IsNullOrWhiteSpace(Playback.CurrentBrowserStreamUrl))
        {
            await PrepareAudioSourceAsync();
            await SyncNativeSleepTimerAsync();
            await SyncAudioAsync();
        }

        var vaultLocked = Services.GetService<NsfwModeState>() is not { Unlocked: true };
        var mediaSubject = $"{Playback.ActiveProfileId}:{Playback.CurrentItem?.WorkId}:{Playback.CurrentItem?.AssetId}:{Playback.PlaybackRequestVersion}:{Playback.IsDismissed}:{Playback.IsVideoMode}:{Playback.CurrentChapter?.Title}:{Playback.SkipBackSeconds}:{Playback.SkipForwardSeconds}:{vaultLocked}";
        if (_mediaSessionModule is not null && _mediaSessionSubject != mediaSubject)
        {
            _mediaSessionSubject = mediaSubject;
            var current = Playback.CurrentItem;
            // One builder serves the audio bar and the video player (unified player v2 D27): same lines as the bar, the actions that work, no metadata for a locked Vault item.
            var metadata = current is null || Playback.IsDismissed || Playback.IsVideoMode
                ? null
                : PlaybackSessionMetadata.Create(new PlaybackSessionContext(BarMedia, current, new Uri(Nav.BaseUri),
                    ChapterTitle: Playback.CurrentChapter?.Title, SkipBackSeconds: Playback.SkipBackSeconds, SkipForwardSeconds: Playback.SkipForwardSeconds, VaultLocked: vaultLocked));
            await _mediaSessionModule.InvokeVoidAsync("metadata", metadata);
        }

        var nowPlayingOpen = Playback.PresentationSurface == PlaybackPresentationSurface.NowPlaying && IsPhonePresentation;
        if (nowPlayingOpen && !_nowPlayingFocused)
        {
            await JS.InvokeVoidAsync("playbackTools.capturePlayerFocus", _phoneFullPlayer?.Element ?? default);
            _nowPlayingFocused = true;
        }
        else if (!nowPlayingOpen && _nowPlayingFocused)
        {
            await JS.InvokeVoidAsync("playbackTools.restorePlayerFocus");
            _nowPlayingFocused = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        SavedItems.Changed -= SavedItemsChanged;
        TimelineScope.Changed -= TimelineScopeChanged;
        _disposed = true;
        Playback.SetTransportHostNotReady();
        Playback.Changed -= HandlePlaybackChanged;
        AudioSidebar.Changed -= HandleAudioPanelChanged;
        if (_launcher is not null)
        {
            _launcher.Requested -= HandleNowPlayingRequested;
        }

        Playback.TransportCommandRequested -= HandleTransportCommandAsync;
        Playback.SleepTimerNativeArmRequested -= BindNativeSleepTimerAsync;
        Playback.SleepTimerPauseRequested -= PauseNativeSleepTimerAsync;
        Playback.SleepTimerNativeSourcePrepareRequested -= PrepareNativeSleepTimerSourceAsync;
        Playback.SleepTimerNativePositionRequested -= ReadNativeSleepTimerPositionAsync;
        Nav.LocationChanged -= HandleLocationChanged;
        DeviceContext.OnChanged -= HandleDeviceContextChanged;

        await DetachBrowserClientAsync();

        _selfReference?.Dispose();
    }

    // Safe to call more than once and before the browser client was ever registered.
    private async Task DetachBrowserClientAsync()
    {
        if (!_browserClientRegistering)
        {
            return;
        }

        var mediaSession = _mediaSessionModule;
        var audioCapabilities = _audioCapabilitiesModule;
        var audioPresentation = _audioPresentationModule;
        _mediaSessionModule = null;
        _audioCapabilitiesModule = null;
        _audioPresentationModule = null;
        try
        {
            if (mediaSession is not null)
            {
                await mediaSession.InvokeVoidAsync("detach", _audioRef);
                await mediaSession.DisposeAsync();
            }
            if (audioCapabilities is not null)
            {
                await audioCapabilities.InvokeVoidAsync("detach");
                await audioCapabilities.DisposeAsync();
            }
            if (audioPresentation is not null)
            {
                await audioPresentation.InvokeVoidAsync("detach", _audioPresentationObserverId);
                await audioPresentation.DisposeAsync();
            }
            await JS.InvokeVoidAsync("listenPlayback.unregisterAudioStateObserver", _audioRef);
            await JS.InvokeVoidAsync("listenPlayback.unregisterPlayerShortcuts", _playerShellRef);
            await JS.InvokeVoidAsync("listenPlayback.unregisterCommandHandler", _selfReference);
        }
        catch (JSDisconnectedException)
        {
            // The browser connection is gone, so the handlers went with it.
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or JSException or TaskCanceledException)
        {
            Logger.LogDebug(ex, "Failed to unregister listen playback command handler.");
        }
    }

    public sealed class AudioMetrics
    {
        public double CurrentTime { get; set; }
        public double Duration { get; set; }
        public double Volume { get; set; }
        public bool Muted { get; set; }
        public bool Paused { get; set; }
        public double PlaybackRate { get; set; } = 1d;
    }
}
