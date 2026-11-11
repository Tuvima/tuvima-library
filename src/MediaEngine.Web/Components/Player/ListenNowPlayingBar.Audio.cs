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
    // The native audio element: keeping it in step with the session (source, volume, mute, rate, position, play and pause) and reading its events back.

    private async Task SyncAudioAsync()
    {
        var browserStreamUrl = Playback.CurrentBrowserStreamUrl;
        var targetPositionSeconds = Playback.CurrentTimeSeconds;
        var targetPlaybackRate = Playback.PlaybackRate;
        var hasAppliedStartVersion = _lastAppliedStartVersion >= 0;
        var startVersionChanged = hasAppliedStartVersion
            && _lastAppliedStartVersion != Playback.PlaybackStartVersion;
        if (!hasAppliedStartVersion || startVersionChanged)
        {
            _lastAppliedStartVersion = Playback.PlaybackStartVersion;
            _lastSeekedStreamUrl = null;
            _lastSeekedPositionSeconds = null;
            _lastAppliedPlayingState = null;
        }

        await PrepareAudioSourceAsync();

        if (_lastAppliedVolume != Playback.Volume)
        {
            await JS.InvokeVoidAsync("listenPlayback.setVolume", _audioRef, Playback.Volume);
            _lastAppliedVolume = Playback.Volume;
        }

        if (_lastAppliedMuteState != Playback.IsMuted)
        {
            await JS.InvokeVoidAsync("listenPlayback.setMuted", _audioRef, Playback.IsMuted);
            _lastAppliedMuteState = Playback.IsMuted;
        }

        if (!_lastAppliedPlaybackRate.HasValue || Math.Abs(_lastAppliedPlaybackRate.Value - targetPlaybackRate) >= 0.01d)
        {
            await JS.InvokeVoidAsync("listenPlayback.setPlaybackRate", _audioRef, targetPlaybackRate);
            _lastAppliedPlaybackRate = targetPlaybackRate;
        }

        var streamChanged = !string.Equals(_lastSeekedStreamUrl, browserStreamUrl, StringComparison.Ordinal);
        var shouldSeek = targetPositionSeconds > 0 && streamChanged
            || startVersionChanged;
        if (shouldSeek)
        {
            await JS.InvokeVoidAsync("listenPlayback.seekAudio", _audioRef, targetPositionSeconds);
            _lastSeekedStreamUrl = browserStreamUrl;
            _lastSeekedPositionSeconds = targetPositionSeconds;
        }

        if (_lastAppliedPlayingState != Playback.IsPlaying)
        {
            if (Playback.IsPlaying)
            {
                if (Playback.IsAudiobookMode
                    && Playback.SleepTimerState.Mode != AudiobookSleepTimerModes.Off
                    && !IsSleepTimerBindingCurrent(Playback.SleepTimerState, browserStreamUrl))
                {
                    return;
                }

                var started = await JS.InvokeAsync<bool>("listenPlayback.playAudio", _audioRef);
                if (!started)
                {
                    await HandleStartFailureAsync();
                }
            }
            else
            {
                await JS.InvokeVoidAsync("listenPlayback.pauseAudio", _audioRef);
            }

            _lastAppliedPlayingState = Playback.IsPlaying;
        }
    }

    private async Task HandleAudioMetricsChangedAsync()
    {
        var rateSelectionVersion = Playback.PlaybackRateSelectionVersion;
        var state = await JS.InvokeAsync<AudioMetrics>("listenPlayback.readAudioState", _audioRef);
        await ApplyAudioMetricsAsync(state, rateSelectionVersion);
    }

    [JSInvokable]
    public Task HandleObservedAudioState(AudioMetrics state) =>
        ApplyAudioMetricsAsync(state, expectedRateSelectionVersion: null);

    private async Task ApplyAudioMetricsAsync(AudioMetrics state, long? expectedRateSelectionVersion)
    {
        if (Playback.IsVideoMode)
        {
            return;
        }

        Playback.UpdateTransportState(
            currentTimeSeconds: state.CurrentTime,
            durationSeconds: state.Duration,
            isPlaying: state.Paused && Playback.Phase == PlaybackPhase.Loading ? null : !state.Paused,
            volume: state.Volume,
            isMuted: state.Muted,
            playbackRate: state.PlaybackRate,
            expectedPlaybackRateSelectionVersion: expectedRateSelectionVersion);
        await Playback.ReportHeartbeatAsync();
    }

    private async Task PrepareAudioSourceAsync()
    {
        var browserStreamUrl = Playback.CurrentBrowserStreamUrl;
        if (string.IsNullOrWhiteSpace(browserStreamUrl)
            || string.Equals(_lastAppliedStreamUrl, browserStreamUrl, StringComparison.Ordinal))
        {
            return;
        }

        _lastAppliedStreamUrl = browserStreamUrl;
        _lastSeekedStreamUrl = null;
        _lastSeekedPositionSeconds = null;
        _lastAppliedPlayingState = null;
        await JS.InvokeVoidAsync("listenPlayback.ensureAudioSource", _audioRef, browserStreamUrl);
    }

    private async Task HandleAudioStarted()
    {
        if (Playback.IsVideoMode)
        {
            return;
        }

        Playback.MarkPlaybackStarted();
        await Playback.ReportHeartbeatAsync(force: true);
    }

    private async Task HandleAudioPaused()
    {
        if (Playback.IsVideoMode || Playback.Phase == PlaybackPhase.Loading)
        {
            return;
        }

        Playback.UpdateTransportState(isPlaying: false);
        await Playback.ReportHeartbeatAsync(force: true);
    }

    private Task HandleAudioErrorAsync()
    {
        if (Playback.IsVideoMode)
        {
            return Task.CompletedTask;
        }

        Playback.MarkCurrentFailed("This audio source failed to load or play.");
        return Task.CompletedTask;
    }
}
