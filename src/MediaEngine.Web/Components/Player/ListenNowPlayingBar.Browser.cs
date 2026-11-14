using System.Text.Json;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.MediaTiles;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Components.Player;

public partial class ListenNowPlayingBar
{
    // The browser client: JS registration and the modules it attaches, restoring and persisting the playback state, and the methods the page scripts call back into.

    private IJSObjectReference? _audioCapabilitiesModule;
    private IJSObjectReference? _mediaSessionModule;
    private string? _mediaSessionSubject;

    [JSInvokable]
    public Task AudioCapabilitiesChangedAsync(AudioOutputStateDto state) => InvokeAsync(() =>
    {
        if (!_disposed)
        {
            Playback.SetAudioCapabilities(state.Supported, state.SoftwareVolumeSupported);
        }
    });

    [JSInvokable]
    public Task MediaSessionActionAsync(string action, double? value) => InvokeAsync(async () =>
    {
        if (_disposed || !Playback.HasQueue || Playback.IsVideoMode || Playback.IsDismissed)
        {
            return;
        }

        var shown = Playback.CreateSnapshot();
        var command = action switch
        {
            "play" => ListenPlaybackCommandActions.Play,
            "pause" => ListenPlaybackCommandActions.Pause,
            "previoustrack" => Playback.IsAudiobookMode ? ListenPlaybackCommandActions.PlayPreviousChapter : ListenPlaybackCommandActions.PlayPrevious,
            "nexttrack" => Playback.IsAudiobookMode ? ListenPlaybackCommandActions.PlayNextChapter : ListenPlaybackCommandActions.PlayNext,
            "seekto" or "seekforward" or "seekbackward" => ListenPlaybackCommandActions.Seek,
            _ => null,
        };
        if (command is null)
        {
            return;
        }

        if (command == ListenPlaybackCommandActions.Seek)
        {
            if (value is double number && !double.IsFinite(number))
            {
                return;
            }

            var seconds = action == "seekto" ? value : shown.CurrentTimeSeconds + (action == "seekbackward" ? -1 : 1) * (value ?? (action == "seekbackward" ? shown.SkipBackSeconds : shown.SkipForwardSeconds));
            if (seconds is null || !double.IsFinite(shown.DurationSeconds) || shown.DurationSeconds <= 0)
            {
                return;
            }

            value = Math.Clamp(seconds.Value, 0, shown.DurationSeconds);
        }
        await PanelCommands.SendAsync(shown, new() { Action = command, Value = value });
    });

    [JSInvokable]
    public Task SetAudioPresentationViewport(double width)
    {
        if (_disposed || !double.IsFinite(width) || width <= 0)
        {
            return Task.CompletedTask;
        }
        // The device context tells every listener (the video overlay too) once the width crosses the phone width; the sync below then runs through it.
        DeviceContext.SetViewportWidth(width);
        return InvokeAsync(() => { SyncPhonePresentation(); StateHasChanged(); });
    }

    private async Task RegisterBrowserClientAsync()
    {
        _browserClientRegistering = true;
        _selfReference = DotNetObjectReference.Create(this);
        _audioPresentationModule = await JS.InvokeAsync<IJSObjectReference>("import", "./js/playback-audio-presentation.js");
        if (_disposed)
        {
            return;
        }

        if (_audioPresentationModule is not null)
        {
            await _audioPresentationModule.InvokeVoidAsync("attach", _audioPresentationObserverId, _selfReference);
        }

        await JS.InvokeVoidAsync("listenPlayback.configure", Playback.ClientSettings);
        await ConfigurePlaybackClientContextAsync();
        await JS.InvokeVoidAsync("listenPlayback.registerCommandHandler", _selfReference);
        await JS.InvokeVoidAsync(
            "listenPlayback.registerAudioStateObserver",
            _audioRef,
            _selfReference,
            Playback.ClientSettings.AudioObserverIntervalMilliseconds);
        if (_disposed)
        {
            return;
        }

        _audioCapabilitiesModule = await JS.InvokeAsync<IJSObjectReference>("import", "./js/playback-output.js");
        if (_audioCapabilitiesModule is not null)
        {
            var audioCapabilities = await _audioCapabilitiesModule.InvokeAsync<AudioOutputStateDto>("attach", _selfReference);
            if (audioCapabilities is not null)
            {
                Playback.SetAudioCapabilities(audioCapabilities.Supported, audioCapabilities.SoftwareVolumeSupported);
            }
        }
        _mediaSessionModule = await JS.InvokeAsync<IJSObjectReference>("import", "./js/playback-media-session.js");
        if (_mediaSessionModule is not null)
        {
            await _mediaSessionModule.InvokeVoidAsync("attach", _audioRef, _selfReference);
        }

        _restored = await RestorePlaybackStateStreamAsync();
        if (_disposed)
        {
            return;
        }

        await Playback.SetTransportHostReadyAsync();
    }

    private async Task ConfigurePlaybackClientContextAsync()
    {
        Playback.SetClientContext(PlaybackClientContext.WebDefault);
        await Task.CompletedTask;
    }

    private void HandlePlaybackChanged(PlaybackChangeKind changeKind)
    {
        var notificationVersion = Interlocked.Increment(ref _playbackChangeNotificationVersion);
        _ = InvokeAsync(async () =>
        {
            ObserveLyricsPresentation();
            await StepAsideForNewAudiobookAsync();
            var playbackRequestVersion = Playback.PlaybackRequestVersion;
            if (_settingsMenuOpen && _transientPlaybackRequestVersion != playbackRequestVersion)
            {
                CloseSettingsMenu();
            }
            StateHasChanged();
            _ = TimelineScope.EnsureLoadedAsync(); // a different profile has its own Chapter or Book choice
            _ = RefreshBarMarkersAsync();

            var snapshotJson = Playback.HasQueue
                ? JsonSerializer.Serialize(Playback.CreateSnapshot(), _jsonOptions)
                : null;
            var shouldPauseAudio = Playback.IsVideoMode && _lastAppliedStreamUrl is not null;
            var shouldLoadFavorite = Playback.IsMusicMode && (Playback.CurrentItem?.WorkId != _favoriteSongId || Playback.ActiveProfileId != _favoriteProfileId) &&
                                     changeKind is PlaybackChangeKind.State or PlaybackChangeKind.Queue;

            if (shouldPauseAudio)
            {
                await JS.InvokeVoidAsync("listenPlayback.pauseAudio", _audioRef);
                _lastAppliedPlayingState = false;
            }

            if (_restored && notificationVersion == Volatile.Read(ref _playbackChangeNotificationVersion) &&
                playbackRequestVersion == Playback.PlaybackRequestVersion)
            {
                try
                {
                    if (snapshotJson is not null)
                    {
                        await JS.InvokeVoidAsync("listenPlayback.setState", snapshotJson);
                    }
                    else
                    {
                        await JS.InvokeVoidAsync("listenPlayback.clearState");
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Could not persist listen playback state to browser storage.");
                }
            }

            if (notificationVersion == Volatile.Read(ref _playbackChangeNotificationVersion) &&
                playbackRequestVersion == Playback.PlaybackRequestVersion && shouldLoadFavorite)
            {
                await LoadFavoriteAsync();
            }
        });
    }

    private async Task<bool> RestorePlaybackStateStreamAsync()
    {
        var restoreGeneration = Volatile.Read(ref _playbackChangeNotificationVersion);
        try
        {
            var hasStoredState = await JS.InvokeAsync<bool>("listenPlayback.hasState");
            if (_disposed || restoreGeneration != Volatile.Read(ref _playbackChangeNotificationVersion))
            {
                return _restored;
            }

            if (!hasStoredState)
            {
                _playbackRestoreError = null;
                return true;
            }

            var snapshotStream = await JS.InvokeAsync<IJSStreamReference>("listenPlayback.getStateStream");
            if (_disposed || restoreGeneration != Volatile.Read(ref _playbackChangeNotificationVersion))
            {
                await DisposeUnusedPlaybackStreamAsync(snapshotStream);
                return _restored;
            }

            var snapshot = await PlaybackSnapshotStream.ReadAsync(snapshotStream, _jsonOptions);
            if (_disposed || restoreGeneration != Volatile.Read(ref _playbackChangeNotificationVersion))
            {
                return _restored;
            }

            // A reset makes a new data store with new profiles, but the browser still holds the old queue. Remembered playback that belongs to
            // a profile this data store does not have is dropped, so a fresh library starts with no player (issue 972).
            if (snapshot?.ProfileId is { } savedProfile && Playback.ActiveProfileId is { } activeProfile && savedProfile != activeProfile)
            {
                await JS.InvokeVoidAsync("listenPlayback.clearState");
                _playbackRestoreError = null;
                return true;
            }

            if (snapshot is not null)
            {
                Playback.RestoreInitialState(snapshot);
            }

            _playbackRestoreError = null;
            return true;
        }
        catch (Exception exception)
        {
            if (_disposed || restoreGeneration != Volatile.Read(ref _playbackChangeNotificationVersion))
            {
                return _restored;
            }

            Logger.LogDebug(exception, "Could not restore listen playback state from the browser stream.");
            _playbackRestoreError = L["ListenNowPlayingBar_PlaybackStateCouldNotBe"].Value;
            return false;
        }
    }

    private static async Task DisposeUnusedPlaybackStreamAsync(IJSStreamReference reference)
    {
        try { await reference.DisposeAsync(); }
        catch (Exception) { }
    }

    private async Task RetryPlaybackStateRestoreAsync()
    {
        _restored = await RestorePlaybackStateStreamAsync();
        await InvokeAsync(StateHasChanged);
    }

    private async Task HandleStartFailureAsync()
    {
        var failure = await JS.InvokeAsync<string>("listenPlayback.startFailure", _audioRef);
        if (failure == "AbortError")
        {
            return; // A superseding source/start owns the result.
        }

        if (failure == "NotAllowedError")
        {
            Playback.MarkNeedsUserGestureToStart();
        }
        else
        {
            Playback.MarkCurrentFailed("The media source could not start playback. Retry playback or check the source.");
        }
    }

    [JSInvokable]
    public async Task HandlePlaybackCommand(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("action", out var action)
            || !string.Equals(action.GetString(), "request-state", StringComparison.Ordinal))
        {
            return;
        }

        if (Playback.HasQueue)
        {
            var snapshot = JsonSerializer.Serialize(Playback.CreateSnapshot(), _jsonOptions);
            await JS.InvokeVoidAsync("listenPlayback.setState", snapshot);
        }
        else
        {
            await JS.InvokeVoidAsync("listenPlayback.clearState");
        }
    }
}
