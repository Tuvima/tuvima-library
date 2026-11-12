using MediaEngine.Contracts.Playback;

namespace MediaEngine.Web.Services.Playback;

/// <summary>Where a "Find lyrics" lookup stands, for the lyrics panel to draw (spec music-player-and-listen-fixes D3).</summary>
public enum PlaybackLyricsLookupState
{
    /// <summary>No lookup is being waited for.</summary>
    Idle,

    /// <summary>The queued lookup is running: "Looking for lyrics…".</summary>
    Looking,

    /// <summary>It is still running after 30 seconds: "Still looking. Lyrics will appear when they are found."</summary>
    StillLooking,

    /// <summary>The lookup finished and the tracks read afterwards hold lyrics.</summary>
    Found,

    /// <summary>The lookup finished and the tracks read afterwards hold none: "No lyrics found".</summary>
    NoneFound,

    /// <summary>The Engine could not look right now (the source failed and will be tried again later): "Lyrics could not be looked up right now".</summary>
    Unreachable,
}

/// <summary>
/// Waiting for the lookup that "Find lyrics" queues. The Engine only queues it and answers at once; this asks, with the person's own access,
/// how that one lookup is going, so the tracks are read again once the lookup has actually finished rather than straight after it was queued.
/// </summary>
public sealed partial class PlaybackLyricsPresenter
{
    private static readonly TimeSpan FirstLookAfter = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LookEvery = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LookingWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StillLookingWindow = TimeSpan.FromSeconds(30);

    /// <summary>Where the lookup started by Find lyrics stands; cleared when the song changes.</summary>
    public PlaybackLyricsLookupState Lookup { get; private set; }

    private enum QueuedLookupEnd { Finished, CouldNotLook }

    // The Engine's operation statuses. null means the lookup has not finished (queued, leased, running, waiting to retry, paused).
    private static QueuedLookupEnd? Classify(string? status) => status?.ToLowerInvariant() switch
    {
        "succeeded" or "no_result" or "blocked" or "failed_terminal" or "cancelled"
            or "missing_confirmed" or "not_applicable" or "skipped" => QueuedLookupEnd.Finished,
        "failed_retryable" or "dead_lettered" => QueuedLookupEnd.CouldNotLook,
        _ => null,
    };

    private async Task WaitForQueuedLookupAsync(PlaybackLyricsIdentity identity, long generation, RefreshTextTracksResponse queued)
    {
        Lookup = PlaybackLyricsLookupState.Looking; Changed?.Invoke();
        var end = Classify(queued.status);
        if (end is null && queued.operation_id is { } operationId)
        {
            var waited = TimeSpan.Zero; var pause = FirstLookAfter;
            while (end is null && waited < LookingWindow)
            {
                await _delay(pause, _reads.Token);
                if (!Current(identity, generation))
                {
                    return;
                }
                waited += pause; pause = LookEvery;
                var progress = await api.GetTextTrackRefreshStatusAsync(identity.AssetId, operationId, _reads.Token);
                if (!Current(identity, generation))
                {
                    return;
                }
                end = Classify(progress?.status);
            }
        }
        else if (end is null)
        {
            // Nothing to follow, so read what is there once.
            end = QueuedLookupEnd.Finished;
        }

        if (end is null)
        {
            // Still going after 30 seconds: say so, leave Add a lyrics file usable, and read the tracks once more after another 30 seconds.
            Refreshing = false; Lookup = PlaybackLyricsLookupState.StillLooking; Changed?.Invoke();
            await _delay(StillLookingWindow, _reads.Token);
            if (!Current(identity, generation))
            {
                return;
            }
        }
        else if (end == QueuedLookupEnd.CouldNotLook)
        {
            Lookup = PlaybackLyricsLookupState.Unreachable;
            return;
        }

        _tracksLoaded = false; _contentLoaded = false;
        if (_commands is not null)
        {
            await EnsureAsync(_snapshot, _commands);
        }
        if (Current(identity, generation))
        {
            Lookup = Lines.Count > 0 ? PlaybackLyricsLookupState.Found : PlaybackLyricsLookupState.NoneFound;
        }
    }
}
