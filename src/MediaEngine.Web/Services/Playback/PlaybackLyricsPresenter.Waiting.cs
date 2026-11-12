using MediaEngine.Contracts.Playback;

namespace MediaEngine.Web.Services.Playback;

/// <summary>Where a "Find lyrics" lookup stands, for the lyrics panel to draw (spec music-player-and-listen-fixes D3).</summary>
public enum PlaybackLyricsLookupState
{
    /// <summary>No lookup is being waited for.</summary>
    Idle,

    /// <summary>The queued lookup is running: "Looking for lyrics…".</summary>
    Looking,

    /// <summary>
    /// It is still queued or running after 30 seconds: "Still looking. Lyrics will appear when they are found." The presenter keeps checking
    /// every 10 seconds, for up to 15 minutes, and never turns this into "none" on its own.
    /// </summary>
    StillLooking,

    /// <summary>The lookup finished and the tracks read afterwards hold lyrics.</summary>
    Found,

    /// <summary>The lookup finished and the tracks read afterwards hold none: "No lyrics found".</summary>
    NoneFound,

    /// <summary>The Engine could not look right now (the source failed and will be tried again later): "Lyrics could not be looked up right now".</summary>
    Unreachable,
}

/// <summary>
/// Waiting for a lyrics lookup: the one "Find lyrics" queues, or the one the Library queued itself after ingestion that is still waiting when a song
/// is opened. The Engine only queues it and answers at once; this asks, with the person's own access, how that one lookup is going, so the tracks are
/// read again once the lookup has actually finished rather than straight after it was queued.
/// </summary>
public sealed partial class PlaybackLyricsPresenter
{
    private static readonly TimeSpan FirstLookAfter = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LookEvery = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LookingWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SlowLookEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(15);

    /// <summary>Where the lookup started by Find lyrics stands; cleared when the song changes.</summary>
    public PlaybackLyricsLookupState Lookup { get; private set; }

    private enum QueuedLookupEnd { Finished, CouldNotLook }

    // Asked once per song, when its tracks load empty; a song that already has lyrics never costs the extra request.
    private bool _pendingAsked;

    /// <summary>
    /// A song opened with no lyrics yet may have a lookup the Library queued itself after ingestion that has not run. Asks the Engine once for a lookup
    /// of this song that is still queued or running and, if there is one, follows it exactly like a lookup started by "Find lyrics" (the panel says
    /// "Looking for lyrics…" rather than "No lyrics found"). The following goes on in the background so opening the panel is not held up.
    /// </summary>
    private async Task FollowPendingLookupAsync(PlaybackLyricsIdentity identity, long generation)
    {
        if (_pendingAsked || Refreshing || Lookup != PlaybackLyricsLookupState.Idle)
        {
            return;
        }
        _pendingAsked = true;
        var pending = await api.GetPendingTextTrackRefreshAsync(identity.AssetId, LyricsKind, _reads.Token);
        if (!Current(identity, generation) || pending is null)
        {
            return;
        }
        if (Classify(pending.status) is { } settled)
        {
            // The automatic lookup already failed and waits to be tried again: say so (Try again asks afresh) rather than "No lyrics found".
            if (settled == QueuedLookupEnd.CouldNotLook)
            {
                Lookup = PlaybackLyricsLookupState.Unreachable;
            }
            return;
        }
        Refreshing = true;
        _ = FollowPendingAsync(identity, generation, pending);
    }

    private async Task FollowPendingAsync(PlaybackLyricsIdentity identity, long generation, RefreshTextTracksResponse pending)
    {
        try
        {
            await WaitForQueuedLookupAsync(identity, generation, pending);
        }
        catch (Exception)
        {
            // The song changed or the presenter was disposed (the wait is cancelled), or something unexpected broke: only the second is shown, as the same
            // message "Find lyrics" gives, and only while this song is still the one on screen.
            if (Current(identity, generation))
            {
                Lookup = PlaybackLyricsLookupState.Idle;
                Status = "Lyrics search could not start.";
            }
        }
        finally
        {
            if (Current(identity, generation))
            {
                Refreshing = false; Changed?.Invoke();
            }
        }
    }

    // The Engine's operation statuses. null means the lookup has not finished (pending, queued, leased, running, paused). A lookup whose source failed is stored as
    // retry_waiting (it is tried again later), so that is "could not look right now", never "still looking" and never "none".
    private static QueuedLookupEnd? Classify(string? status) => status?.ToLowerInvariant() switch
    {
        "succeeded" or "no_result" or "blocked" or "failed_terminal" or "cancelled"
            or "missing_confirmed" or "not_applicable" or "skipped" => QueuedLookupEnd.Finished,
        "failed_retryable" or "retry_waiting" or "dead_lettered" => QueuedLookupEnd.CouldNotLook,
        _ => null,
    };

    private async Task WaitForQueuedLookupAsync(PlaybackLyricsIdentity identity, long generation, RefreshTextTracksResponse queued)
    {
        Lookup = PlaybackLyricsLookupState.Looking; Changed?.Invoke();
        var end = Classify(queued.status);
        var stillQueuedOrRunning = false;
        if (end is null && queued.operation_id is { } operationId)
        {
            // The first look is after a second and the rest 2 seconds apart. Once 30 seconds have passed the panel says it is still looking and the
            // looks slow to every 10 seconds, until the lookup finishes, the song changes, the presenter is disposed or 15 minutes have passed.
            var waited = TimeSpan.Zero; var pause = FirstLookAfter;
            while (end is null && waited < GiveUpAfter)
            {
                if (waited >= LookingWindow && Lookup == PlaybackLyricsLookupState.Looking)
                {
                    Refreshing = false; Lookup = PlaybackLyricsLookupState.StillLooking; Changed?.Invoke();
                }
                await _delay(pause, _reads.Token);
                if (!Current(identity, generation))
                {
                    return;
                }
                waited += pause; pause = waited < LookingWindow ? LookEvery : SlowLookEvery;
                var progress = await api.GetTextTrackRefreshStatusAsync(identity.AssetId, operationId, _reads.Token);
                if (!Current(identity, generation))
                {
                    return;
                }
                end = Classify(progress?.status);
            }
            stillQueuedOrRunning = end is null;
        }
        else if (end is null)
        {
            // Nothing to follow, so read what is there once.
            end = QueuedLookupEnd.Finished;
        }

        if (stillQueuedOrRunning)
        {
            // Fifteen minutes and it has still not finished: stop asking, and keep saying it is still looking. Never "none".
            Refreshing = false; Lookup = PlaybackLyricsLookupState.StillLooking; Changed?.Invoke();
            return;
        }

        if (end == QueuedLookupEnd.CouldNotLook)
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
