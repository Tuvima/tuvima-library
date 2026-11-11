using System.Globalization;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Profiles;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
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
    // Transport: Play, Pause, skip, seek, volume, next and previous, the commands the session sends the bar, the keyboard shortcuts and closing the player.

    private Task ToggleShuffleFromDockAsync() => Playback.ToggleShuffleAsync();

    private Task HandleBarTransportAsync(PlaybackControlDefinition control) => control.Command switch
    {
        "toggle-play" => TogglePlayPauseAsync(),
        "skip-back" => SkipBackAsync(),
        "skip-forward" => SkipForwardAsync(),
        "play-previous" => PlayPreviousAsync(),
        "play-next" => PlayNextAsync(),
        "play-previous-chapter" => PlayPreviousChapterAsync(),
        "play-next-chapter" => PlayNextChapterAsync(),
        "shuffle" => ToggleShuffleFromDockAsync(),
        "repeat" => CycleRepeatFromDockAsync(),
        _ => Task.CompletedTask,
    };

    private Task HandleBarToolAsync(PlaybackControlActivation activation) => activation.Control.Command switch
    {
        "open-now-playing" => OpenNowPlaying(),
        "close" => ClosePlayerAsync(),
        _ => Task.CompletedTask,
    };
    private Task CycleRepeatFromDockAsync() => Playback.CycleRepeatModeAsync();
    private void OpenCurrentDetails()
    {
        if (Playback.CurrentItem is not { } current)
        {
            return;
        }

        var route = Playback.IsMusicMode && current.CollectionId.HasValue
            ? $"/details/musicalbum/{current.CollectionId.Value:D}?context=listen&track={current.WorkId:D}"
            : $"/details/work/{current.WorkId:D}?context=listen";
        Nav.NavigateTo(route);
    }

    [JSInvokable]
    public async Task HandlePlayerShortcut(string action)
    {
        switch (action)
        {
            case "toggle-play":
                await TogglePlayPauseAsync();
                break;
            case "skip-back":
                await SkipBackAsync();
                break;
            case "skip-forward":
                await SkipForwardAsync();
                break;
            case "volume-up":
                await SetVolumeOnHostAsync(Playback.Volume + Playback.ClientSettings.VolumeStep);
                break;
            case "volume-down":
                await SetVolumeOnHostAsync(Playback.Volume - Playback.ClientSettings.VolumeStep);
                break;
            case "toggle-mute":
                await ToggleMuteOnHostAsync();
                break;
        }
    }

    private async Task TogglePlayPauseAsync()
    {
        if (await ConsumeImmediateToggleAsync())
        {
            return;
        }

        if (Playback.IsPlaying && !Playback.NeedsUserGestureToStart)
        {
            await PauseAudioAsync();
            return;
        }

        if (!string.IsNullOrWhiteSpace(Playback.CurrentBrowserStreamUrl))
        {
            await StartAudioAsync(new PlaybackTransportCommand(
                "start",
                Value: Playback.CurrentTimeSeconds,
                StreamUrl: Playback.CurrentBrowserStreamUrl,
                PositionSeconds: Playback.CurrentTimeSeconds,
                PlaybackRate: Playback.PlaybackRate,
                RequestId: Playback.PlaybackStartVersion));
            return;
        }

        var started = await JS.InvokeAsync<bool>("listenPlayback.playAudio", _audioRef);
        if (started)
        {
            Playback.MarkPlaybackStarted();
        }
        else
        {
            await HandleStartFailureAsync();
        }
    }

    private async Task StartAudioAsync(PlaybackTransportCommand command)
    {
        var requestVersion = Playback.PlaybackRequestVersion;
        var rateSelectionVersion = Playback.PlaybackRateSelectionVersion;
        var streamUrl = StringHelpers.FirstNonBlankOr("Library audio", command.StreamUrl, Playback.CurrentBrowserStreamUrl);
        var positionSeconds = Math.Max(0, command.PositionSeconds ?? command.Value ?? Playback.CurrentTimeSeconds);
        var playbackRate = command.PlaybackRate ?? Playback.PlaybackRate;
        var started = await JS.InvokeAsync<bool>(
            "listenPlayback.startAudio",
            _audioRef,
            new
            {
                streamUrl,
                positionSeconds,
                playbackRate,
                volume = Playback.Volume,
                muted = Playback.IsMuted,
            });

        if (!string.IsNullOrWhiteSpace(streamUrl))
        {
            _lastAppliedStreamUrl = streamUrl;
            _lastSeekedStreamUrl = streamUrl;
        }

        _lastSeekedPositionSeconds = positionSeconds;
        _lastAppliedVolume = Playback.Volume;
        _lastAppliedMuteState = Playback.IsMuted;
        _lastAppliedPlayingState = started;

        if (requestVersion != Playback.PlaybackRequestVersion)
        {
            return;
        }

        if (started)
        {
            Playback.UpdateTransportState(
                currentTimeSeconds: positionSeconds,
                isPlaying: true,
                playbackRate: playbackRate,
                expectedPlaybackRateSelectionVersion: rateSelectionVersion,
                needsUserGestureToStart: false);
            if (rateSelectionVersion == Playback.PlaybackRateSelectionVersion)
            {
                _lastAppliedPlaybackRate = playbackRate;
            }

            Playback.MarkPlaybackStarted();
            await Playback.ReportHeartbeatAsync(force: true);
        }
        else
        {
            await HandleStartFailureAsync();
        }
    }

    private async Task PauseAudioAsync()
    {
        await JS.InvokeVoidAsync("listenPlayback.pauseAudio", _audioRef);
        Playback.UpdateTransportState(isPlaying: false);
        await Playback.ReportHeartbeatAsync(force: true);
    }

    private async Task ApplyPlaybackRateAsync(double rate)
    {
        if (!PlaybackRateOptions.IsValid(rate) || Playback.CurrentItem?.AssetId is not Guid assetId)
        {
            return;
        }

        var playbackRequestVersion = Playback.PlaybackRequestVersion;
        var rateSelectionVersion = Playback.PlaybackRateSelectionVersion;
        await JS.InvokeVoidAsync("listenPlayback.setPlaybackRate", _audioRef, rate);
        if (Playback.PlaybackRateSelectionVersion != rateSelectionVersion
            || !Playback.TrySetPlaybackRateFromHost(rate, assetId, playbackRequestVersion))
        {
            return;
        }

        _lastAppliedPlaybackRate = rate;
        await Playback.ReportHeartbeatAsync(force: true);
    }

    private async Task PlayNextAsync()
    {
        if (Playback.IsAudiobookMode)
        {
            await SkipForwardAsync();
            return;
        }

        await Playback.SkipNextAsync();
    }

    private async Task PlayPreviousAsync()
    {
        if (Playback.IsAudiobookMode)
        {
            await SkipBackAsync();
            return;
        }

        // Music: after a few seconds of play Previous restarts the song; inside the first seconds, or on a second press, it goes back (D3).
        if (Playback.PressMusicPrevious() == PlaybackPreviousAction.Restart)
        {
            await SeekOnHostAsync(0);
            return;
        }

        await Playback.SkipPreviousAsync();
    }

    private Task PlayNextChapterAsync() => Playback.DispatchAsync(new PlaybackCommand(PlaybackCommandKind.PlayNextChapter));

    private Task PlayPreviousChapterAsync() => Playback.DispatchAsync(new PlaybackCommand(PlaybackCommandKind.PlayPreviousChapter));

    private Task SkipBackAsync() => SkipRelativeAsync(-Playback.SkipBackSeconds);

    private Task SkipForwardAsync() => SkipRelativeAsync(Playback.SkipForwardSeconds);

    private async Task SkipRelativeAsync(double deltaSeconds)
    {
        if (await ConsumeImmediateSeekAsync())
        {
            return;
        }

        await SeekRelativeOnHostAsync(deltaSeconds);
    }

    private async Task<bool> ConsumeImmediateToggleAsync()
    {
        try
        {
            if (!await JS.InvokeAsync<bool>("listenPlayback.consumeImmediateToggleHandled"))
            {
                return false;
            }

            await HandleAudioMetricsChangedAsync();
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not consume immediate audio toggle.");
            return false;
        }
    }

    private async Task<bool> ConsumeImmediateSeekAsync()
    {
        try
        {
            if (!await JS.InvokeAsync<bool>("listenPlayback.consumeImmediateSeekHandled"))
            {
                return false;
            }

            await HandleAudioMetricsChangedAsync();
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not consume immediate audio seek.");
            return false;
        }
    }

    private Task SeekRelativeOnHostAsync(double deltaSeconds)
    {
        var max = Playback.DurationSeconds > 0 ? Playback.DurationSeconds : double.MaxValue;
        var next = Math.Clamp(Playback.CurrentTimeSeconds + deltaSeconds, 0, max);
        return SeekOnHostAsync(next);
    }

    private async Task SeekAsync(ChangeEventArgs args)
    {
        if (!double.TryParse(args.Value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return;
        }

        await SeekOnHostAsync(seconds);
    }

    private async Task SeekOnHostAsync(double seconds)
    {
        var next = Math.Max(0, seconds);
        await JS.InvokeVoidAsync("listenPlayback.seekAudio", _audioRef, next);
        _lastSeekedStreamUrl = Playback.CurrentBrowserStreamUrl;
        _lastSeekedPositionSeconds = next;
        Playback.UpdateTransportState(currentTimeSeconds: next);
        await Playback.ReportHeartbeatAsync(force: true);
    }

    private async Task SetVolumeOnHostAsync(double volume)
    {
        await ApplyVolumeOnHostAsync(volume);
    }

    private Task ToggleMuteAsync() => Playback.DispatchAsync(new PlaybackCommand(PlaybackCommandKind.ToggleMute));

    private async Task ApplyVolumeOnHostAsync(double volume)
    {
        var next = Math.Clamp(volume, 0d, 1d);
        var muted = next <= 0d;
        await JS.InvokeVoidAsync("listenPlayback.setVolume", _audioRef, next);
        await JS.InvokeVoidAsync("listenPlayback.setMuted", _audioRef, muted);
        _lastAppliedVolume = next;
        _lastAppliedMuteState = muted;
        Playback.UpdateTransportState(volume: next, isMuted: muted);
        await Playback.ReportHeartbeatAsync(force: true);
    }

    private Task ToggleMuteOnHostAsync() => ApplyVolumeOnHostAsync(Playback.GetMuteToggleTargetVolume());

    public sealed record AudioClosePosition(Guid AssetId, long PlaybackRequestVersion, bool SourceVerified,
        double PositionSeconds, double? DurationSeconds);
    private bool _closeInProgress;
    private async Task ClosePlayerAsync()
    {
        if (_closeInProgress || Playback.IsVideoMode || Playback.CurrentItem is not { } item)
        {
            return;
        }

        var profile = Playback.ActiveProfileId;
        var request = Playback.PlaybackRequestVersion;
        var source = Playback.CurrentBrowserStreamUrl;
        var pendingSource = Playback.Phase == PlaybackPhase.Loading || item.AssetId is null || string.IsNullOrWhiteSpace(source);
        var predecessorSource = _lastAppliedStreamUrl;
        bool Current() => !_disposed && Playback.ActiveProfileId == profile && Playback.PlaybackRequestVersion == request
            && Playback.CurrentItem?.AssetId == item.AssetId && Playback.CurrentItem?.WorkId == item.WorkId && !Playback.IsVideoMode;
        _closeInProgress = true;
        try
        {
            // A receiver that is playing this item is let go first, so the element being released is the browser's own again (unified player v2 D28).
            if (Services.GetService<PlaybackRemoteController>() is { } remote)
            {
                await remote.EndAsync();
            }

            if (!Current())
            {
                return;
            }
            // Pause only the captured native session before waiting on persistence.
            var position = await JS.InvokeAsync<AudioClosePosition?>("listenPlayback.pauseAudioForClose",
                item.AssetId, request, source, predecessorSource, pendingSource);
            if (!Current())
            {
                return;
            }

            if (position is { SourceVerified: true } && position.AssetId == item.AssetId
                && position.PlaybackRequestVersion == request && double.IsFinite(position.PositionSeconds) && position.PositionSeconds >= 0)
            {
                Playback.UpdateTransportState(currentTimeSeconds: position.PositionSeconds,
                    durationSeconds: position.DurationSeconds, isPlaying: false);
            }
            else
            {
                Playback.UpdateTransportState(isPlaying: false);
            }

            await Playback.ReportHeartbeatAsync(force: true, hasPlaybackEnded: false);
            if (!Current())
            {
                return;
            }

            var stopped = await JS.InvokeAsync<bool>("listenPlayback.finalizeAudioClose", item.AssetId, request, source, predecessorSource, pendingSource, profile);
            if (!Current())
            {
                return;
            }

            if (!stopped && !pendingSource)
            {
                return;
            }

            Tools?.Close();
            await AudioSidebar.CloseAsync(Playback);
            if (!Current())
            {
                return;
            }

            CloseSettingsMenu();
            Playback.ClosePlayer();
        }
        finally { _closeInProgress = false; }
    }

    private async Task HandleTransportCommandAsync(PlaybackTransportCommand command)
    {
        if (Playback.IsVideoMode)
        {
            return;
        }

        switch (command.Action)
        {
            case "toggle-play":
                await TogglePlayPauseAsync();
                break;
            case "pause":
                await PauseAudioAsync();
                break;
            case "play-next":
                await PlayNextAsync();
                break;
            case "play-previous":
                await PlayPreviousAsync();
                break;
            case "play-next-chapter":
                await Playback.PlayNextChapterAsync();
                break;
            case "play-previous-chapter":
                await Playback.PlayPreviousChapterAsync();
                break;
            case "skip-back":
                await SkipBackAsync();
                break;
            case "skip-forward":
                await SkipForwardAsync();
                break;
            case "seek" when command.Value.HasValue:
                await SeekOnHostAsync(command.Value.Value);
                break;
            case "start":
                await StartAudioAsync(command);
                break;
            case "set-volume" when command.Value.HasValue:
                await ApplyVolumeOnHostAsync(command.Value.Value);
                break;
            case "set-speed" when command.Value.HasValue:
                await ApplyPlaybackRateAsync(command.Value.Value);
                break;
            case "toggle-mute":
                await ToggleMuteOnHostAsync();
                break;
            case "close-player":
                await ClosePlayerAsync();
                break;
        }
    }
}
