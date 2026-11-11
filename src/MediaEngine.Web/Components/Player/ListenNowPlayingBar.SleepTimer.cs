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
using MediaEngine.Web.Services.Vault;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Components.Player;

public partial class ListenNowPlayingBar
{
    // The audiobook Sleep timer: binding it to the native player, pausing and reading its position, and the selection from the Sleep timer panel.

    private sealed record NativeSleepTimerBindingRequest(
        AudiobookSleepTimerStateDto State,
        Guid AssetId,
        long RequestVersion,
        string? SourceUrl);
    private sealed record PendingNativeSleepTimerBinding(
        NativeSleepTimerBindingRequest Request,
        long Version,
        TaskCompletionSource<bool> Completion);

    private Task<bool> BindNativeSleepTimerAsync(AudiobookSleepTimerStateDto state) =>
        BindNativeSleepTimerCoreAsync(state);

    private async Task<bool> PrepareNativeSleepTimerSourceAsync(AudiobookSleepTimerStateDto state, string streamUrl)
    {
        var requestVersion = Playback.PlaybackRequestVersion;
        var assetId = Playback.CurrentItem?.AssetId;
        if (state.Mode == AudiobookSleepTimerModes.Off
            || !Playback.IsCurrentSleepTimerArm(state.TimerGeneration, state.BoundAssetId, state.PlaybackRequestVersion)
            || state.PlaybackRequestVersion != requestVersion
            || state.BoundAssetId != assetId
            || !string.Equals(streamUrl, Playback.CurrentBrowserStreamUrl, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(streamUrl))
        {
            return false;
        }

        try
        {
            await JS.InvokeVoidAsync("listenPlayback.ensureAudioSource", _audioRef, streamUrl);
            if (!Playback.IsCurrentSleepTimerArm(state.TimerGeneration, state.BoundAssetId, state.PlaybackRequestVersion)
                || Playback.PlaybackRequestVersion != requestVersion
                || Playback.CurrentItem?.AssetId != assetId
                || !string.Equals(streamUrl, Playback.CurrentBrowserStreamUrl, StringComparison.Ordinal))
            {
                return false;
            }

            _lastAppliedStreamUrl = streamUrl;
            _lastSeekedStreamUrl = null;
            _lastSeekedPositionSeconds = null;
            _lastAppliedPlayingState = null;
            return true;
        }
        catch (JSDisconnectedException)
        {
            return false;
        }
        catch (InvalidOperationException exception)
        {
            Logger.LogDebug(exception, "The audiobook source could not be prepared for its active sleep timer.");
            return false;
        }
    }

    private async Task<bool> PauseNativeSleepTimerAsync(AudiobookSleepTimerStateDto state)
    {
        if (state.Mode == AudiobookSleepTimerModes.Off
            || !Playback.IsCurrentSleepTimerArm(state.TimerGeneration, state.BoundAssetId, state.PlaybackRequestVersion))
        {
            return false;
        }

        try
        {
            var paused = await JS.InvokeAsync<bool>(
                "listenPlayback.pauseAudioForSleepTimer",
                _audioRef,
                state.TimerGeneration,
                state.BoundAssetId,
                state.PlaybackRequestVersion);
            var accepted = paused
                && Playback.IsCurrentSleepTimerArm(state.TimerGeneration, state.BoundAssetId, state.PlaybackRequestVersion);
            return accepted;
        }
        catch (JSDisconnectedException)
        {
            return false;
        }
        catch (InvalidOperationException exception)
        {
            Logger.LogDebug(exception, "The audiobook sleep timer could not pause playback through the native player.");
            return false;
        }
    }

    private async Task<double?> ReadNativeSleepTimerPositionAsync(Guid expectedAssetId, long expectedPlaybackRequestVersion)
    {
        var item = Playback.CurrentItem;
        var profileId = PlaybackPreferences.ActiveProfileId;
        var workId = item?.AudiobookWorkId ?? item?.WorkId;
        var sourceUrl = Playback.CurrentBrowserStreamUrl;
        if (!Playback.IsAudiobookMode
            || item?.AssetId != expectedAssetId
            || Playback.PlaybackRequestVersion != expectedPlaybackRequestVersion
            || string.IsNullOrWhiteSpace(sourceUrl))
        {
            return null;
        }
        if (profileId is not Guid expectedProfileId || expectedProfileId == Guid.Empty
            || workId is not Guid expectedWorkId || expectedWorkId == Guid.Empty)
        {
            return null;
        }

        try
        {
            var currentTime = await JS.InvokeAsync<double?>(
                "listenPlayback.readAudioPositionForSleepTimer",
                _audioRef,
                expectedAssetId,
                expectedPlaybackRequestVersion,
                sourceUrl);
            var current = Playback.CurrentItem;
            if (PlaybackPreferences.ActiveProfileId != expectedProfileId
                || !Playback.IsAudiobookMode
                || current is null
                || current.AssetId != expectedAssetId
                || (current.AudiobookWorkId ?? current.WorkId) != expectedWorkId
                || Playback.PlaybackRequestVersion != expectedPlaybackRequestVersion
                || !string.Equals(Playback.CurrentBrowserStreamUrl, sourceUrl, StringComparison.Ordinal)
                || currentTime is not { } position
                || !double.IsFinite(position)
                || position < 0)
            {
                return null;
            }

            return currentTime;
        }
        catch (JSDisconnectedException)
        {
            return null;
        }
        catch (InvalidOperationException exception)
        {
            Logger.LogDebug(exception, "The current audiobook position could not be read for its sleep timer.");
            return null;
        }
    }

    private bool IsSleepTimerBindingCurrent(AudiobookSleepTimerStateDto state, string? sourceUrl) =>
        _nativeSleepTimerBinding is { } binding
        && binding.State == state
        && (state.Mode == AudiobookSleepTimerModes.Off
            || (binding.AssetId == state.BoundAssetId
                && binding.RequestVersion == state.PlaybackRequestVersion
                && string.Equals(binding.SourceUrl, sourceUrl, StringComparison.Ordinal)
                && Playback.CurrentItem?.AssetId == state.BoundAssetId
                && Playback.PlaybackRequestVersion == state.PlaybackRequestVersion));

    private async Task SyncNativeSleepTimerAsync()
    {
        if (_selfReference is null || Playback.SleepTimerRegistrationInProgress)
        {
            return;
        }

        var state = Playback.SleepTimerState;
        if (IsSleepTimerBindingCurrent(state, Playback.CurrentBrowserStreamUrl))
        {
            return;
        }

        await BindNativeSleepTimerCoreAsync(state);
    }

    private Task<bool> BindNativeSleepTimerCoreAsync(AudiobookSleepTimerStateDto state)
    {
        var requestVersion = Playback.PlaybackRequestVersion;
        var assetId = Playback.CurrentItem?.AssetId;
        var sourceUrl = Playback.CurrentBrowserStreamUrl;
        var request = new NativeSleepTimerBindingRequest(state, assetId ?? Guid.Empty, requestVersion, sourceUrl);

        // Reject stale or invalid passive callbacks before advancing the bind
        // version; an invalid request must not cancel a valid source transition.
        if (!IsNativeSleepTimerBindingRequestCurrent(request))
        {
            return Task.FromResult(false);
        }

        lock (_nativeSleepTimerBindingGate)
        {
            if (!IsNativeSleepTimerBindingRequestCurrent(request))
            {
                return Task.FromResult(false);
            }

            if (_pendingNativeSleepTimerBinding is { } pending && pending.Request == request)
            {
                return pending.Completion.Task;
            }

            var version = Interlocked.Increment(ref _nativeSleepTimerBindVersion);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var next = new PendingNativeSleepTimerBinding(request, version, completion);
            var previous = _pendingNativeSleepTimerBinding;
            _pendingNativeSleepTimerBinding = next;
            previous?.Completion.TrySetResult(false);
            _ = CompleteNativeSleepTimerBindingAsync(next);
            return completion.Task;
        }
    }

    private async Task CompleteNativeSleepTimerBindingAsync(PendingNativeSleepTimerBinding pending)
    {
        var request = pending.Request;
        var state = request.State;
        var accepted = false;
        try
        {
            var registered = await JS.InvokeAsync<bool>(
                "listenPlayback.setAudiobookSleepTimer",
                _audioRef,
                state,
                request.SourceUrl);
            accepted = registered
                && pending.Version == Volatile.Read(ref _nativeSleepTimerBindVersion)
                && IsNativeSleepTimerBindingRequestCurrent(request);
            if (accepted)
            {
                _nativeSleepTimerBinding = request;
            }
        }
        catch (JSDisconnectedException)
        {
        }
        catch (InvalidOperationException exception)
        {
            Logger.LogDebug(exception, "The audiobook sleep timer could not be bound before the audio element became interactive.");
        }
        catch (JSException exception)
        {
            Logger.LogDebug(exception, "The audiobook sleep timer registration was rejected by the native player.");
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "The audiobook sleep timer registration failed unexpectedly.");
        }
        finally
        {
            pending.Completion.TrySetResult(accepted);
            lock (_nativeSleepTimerBindingGate)
            {
                if (ReferenceEquals(_pendingNativeSleepTimerBinding, pending))
                {
                    _pendingNativeSleepTimerBinding = null;
                }
            }
        }
    }

    private bool IsNativeSleepTimerBindingRequestCurrent(NativeSleepTimerBindingRequest request)
    {
        var state = request.State;
        if (!Playback.CanBindNativeSleepTimerState(state))
        {
            return false;
        }

        if (state.Mode == AudiobookSleepTimerModes.Off)
        {
            var currentOff = Playback.SleepTimerState;
            if (currentOff.Mode == AudiobookSleepTimerModes.Off
                && currentOff.TimerGeneration == state.TimerGeneration)
            {
                // An authority invalidation clears the former source binding
                // with the committed Off generation, even after the live source
                // has moved on.
                return true;
            }

            // A user-selected Off is a staged candidate; keep it scoped to the
            // exact live source that authorized the selection.
            return Playback.PlaybackRequestVersion == request.RequestVersion
                && (Playback.CurrentItem?.AssetId ?? Guid.Empty) == request.AssetId
                && string.Equals(Playback.CurrentBrowserStreamUrl, request.SourceUrl, StringComparison.Ordinal);
        }

        var item = Playback.CurrentItem;
        var profileId = PlaybackPreferences.ActiveProfileId;
        var workId = item?.AudiobookWorkId ?? item?.WorkId;
        var liveScopeMatches = Playback.IsAudiobookMode
            && item?.AssetId == request.AssetId
            && Playback.PlaybackRequestVersion == request.RequestVersion
            && state.BoundAssetId == request.AssetId
            && state.PlaybackRequestVersion == request.RequestVersion
            && state.ProfileId != Guid.Empty
            && profileId == state.ProfileId
            && workId == state.WorkId
            && !string.IsNullOrWhiteSpace(request.SourceUrl)
            && string.Equals(Playback.CurrentBrowserStreamUrl, request.SourceUrl, StringComparison.Ordinal);
        if (!liveScopeMatches)
        {
            return false;
        }

        return true;
    }

    [JSInvokable]
    public Task HandleNativeSleepTimerExpired(
        long timerGeneration,
        Guid assetId,
        long playbackRequestVersion,
        double actualCurrentTimeSeconds,
        bool nativeEndOfFileConfirmed = false) =>
        Playback.HandleNativeSleepTimerExpiredAsync(
            timerGeneration,
            assetId,
            playbackRequestVersion,
            actualCurrentTimeSeconds,
            nativeEndOfFileConfirmed);

    [JSInvokable]
    public Task HandleNativeAudioEnded(
        Guid assetId,
        long playbackRequestVersion,
        long timerGeneration,
        double actualCurrentTimeSeconds) =>
        Playback.HandleNativeAudioEndedAsync(
            assetId,
            playbackRequestVersion,
            timerGeneration,
            actualCurrentTimeSeconds);

    private async Task SetAudiobookSleepTimerSelectionAsync(AudiobookSleepTimerSelectionDto selection)
    {
        if (!Playback.IsAudiobookMode || Playback.CurrentItem is not { AssetId: { } assetId } item
            || PlaybackPreferences.ActiveProfileId is not { } profileId)
        {
            return;
        }

        try
        {
            await Playback.SetAudiobookSleepTimerAsync(
                profileId,
                item.AudiobookWorkId ?? item.WorkId,
                assetId,
                Playback.PlaybackRequestVersion,
                selection);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            Logger.LogInformation(exception, "The audiobook sleep timer selection no longer matched the active playback session.");
            _barNotice = exception.Message;
        }
    }
}
