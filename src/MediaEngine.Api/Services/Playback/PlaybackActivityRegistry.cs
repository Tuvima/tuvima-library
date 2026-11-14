using System.Collections.Concurrent;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Playback;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// The Engine's live list of what is being watched and prepared (playing-now spec D4). It lives in memory only: nothing is stored, and a
/// restart empties it (an encode cannot survive a restart anyway). Telemetry feeds the watching sessions, the packager and the offline
/// encode queue feed the encodes, and every encode owns a cancel handle so Stop ends it at once.
/// </summary>
public sealed class PlaybackActivityRegistry(TimeProvider clock)
{
    /// <summary>A playing player sends a heartbeat every few seconds; a playing session that has been silent this long is lost.</summary>
    internal static readonly TimeSpan PlayingStaleAfter = TimeSpan.FromSeconds(45);

    /// <summary>A player fetches stream pieces every few seconds, so a read this recent counts as someone watching.</summary>
    internal static readonly TimeSpan ReadFreshFor = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<Guid, EncodeEntry> _encodes = new();
    private readonly ConcurrentDictionary<Guid, WatchEntry> _watches = new();
    private readonly ConcurrentDictionary<Guid, long> _lastReadByAsset = new();

    /// <summary>Raised after any change, so a listener can refresh. Never throws into the caller.</summary>
    public event Action? Changed;

    /// <summary>Starts tracking an encode (waiting for a slot until <see cref="EncodeEntry.WaitingForSlot"/> is cleared).</summary>
    public EncodeEntry BeginEncode(Guid id, string kind, Guid assetId, Func<Task> stop, string? quality = null, string? requestedBy = null, string? title = null)
    {
        var now = clock.GetUtcNow();
        var entry = new EncodeEntry(id, kind, assetId, stop, now) { WaitingForSlot = true, Quality = quality, RequestedBy = requestedBy, Title = title };
        _encodes[id] = entry;
        Changed?.Invoke();
        return entry;
    }

    public void EndEncode(Guid id)
    {
        if (_encodes.TryRemove(id, out _))
        {
            Changed?.Invoke();
        }
    }

    /// <summary>A viewer's player read a piece of the stream for this video.</summary>
    public void NoteRead(Guid assetId) => _lastReadByAsset[assetId] = clock.GetUtcNow().UtcTicks;

    /// <summary>A player session was seen (or changed state). The newest facts replace the old ones.</summary>
    public void ObserveWatch(WatchEntry watch)
    {
        _watches[watch.PlayerSessionId] = watch;
        Changed?.Invoke();
    }

    /// <summary>A player session ended (stopped, closed, replaced).</summary>
    public void CloseWatch(Guid playerSessionId)
    {
        if (_watches.TryRemove(playerSessionId, out _))
        {
            Changed?.Invoke();
        }
    }

    public IReadOnlyList<EncodeEntry> Encodes => [.. _encodes.Values];

    public IReadOnlyList<WatchEntry> Watches => [.. _watches.Values];

    public EncodeEntry? FindEncode(Guid id) => _encodes.TryGetValue(id, out var entry) ? entry : null;

    /// <summary>True while a player is playing this video, is paused within the allowed time, or has just read its stream.</summary>
    public bool HasViewer(Guid assetId, DateTimeOffset now, TimeSpan pausedFor)
    {
        if (_lastReadByAsset.TryGetValue(assetId, out var ticks) && now - new DateTimeOffset(ticks, TimeSpan.Zero) < ReadFreshFor)
        {
            return true;
        }

        foreach (var watch in _watches.Values)
        {
            if (watch.AssetId == assetId && IsLive(watch, now, pausedFor))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a session still counts as watching: playing within a short silence, or paused within the paused allowance.</summary>
    public static bool IsLive(WatchEntry watch, DateTimeOffset now, TimeSpan pausedFor) =>
        watch.State == PlaybackTelemetryStates.Paused
            ? now - watch.LastObservedAt < pausedFor
            : watch.State == PlaybackTelemetryStates.Playing && now - watch.LastObservedAt < PlayingStaleAfter;

    /// <summary>
    /// Updates who is watching each playback encode and returns the ones whose last viewer left more than the grace period ago
    /// (D6). An encode starts with its start time as the last viewer time, so a player that is still opening is given the grace period.
    /// </summary>
    public IReadOnlyList<EncodeEntry> Evaluate(DateTimeOffset now, TimeSpan grace, TimeSpan pausedFor)
    {
        var due = new List<EncodeEntry>();
        foreach (var encode in _encodes.Values)
        {
            if (encode.Kind != PlayingNowKinds.PlaybackEncode)
            {
                continue;
            }

            if (HasViewer(encode.AssetId, now, pausedFor))
            {
                encode.LastViewerAt = now;
            }
            else if (now - encode.LastViewerAt >= grace && !encode.IsStopping)
            {
                due.Add(encode);
            }
        }

        return due;
    }

    /// <summary>Stops one encode. False when it is not tracked (already finished or stopped).</summary>
    public async Task<bool> StopAsync(Guid id)
    {
        if (!_encodes.TryGetValue(id, out var entry))
        {
            return false;
        }

        await StopEntryAsync(entry).ConfigureAwait(false);
        return true;
    }

    internal async Task StopEntryAsync(EncodeEntry entry)
    {
        entry.IsStopping = true;
        await entry.Stop().ConfigureAwait(false);
        Changed?.Invoke();
    }

    /// <summary>Stops every playback encode nobody is watching. Offline copies are never touched.</summary>
    public async Task<int> StopUnwatchedAsync(TimeSpan pausedFor)
    {
        var now = clock.GetUtcNow();
        var stopped = 0;
        foreach (var encode in _encodes.Values)
        {
            if (encode.Kind == PlayingNowKinds.PlaybackEncode && !encode.IsStopping && !HasViewer(encode.AssetId, now, pausedFor))
            {
                await StopEntryAsync(encode).ConfigureAwait(false);
                stopped++;
            }
        }

        return stopped;
    }

    internal void RaiseChanged() => Changed?.Invoke();
}

/// <summary>One encode being prepared or waiting. Written by the packager, read by the list.</summary>
public sealed class EncodeEntry(Guid id, string kind, Guid assetId, Func<Task> stop, DateTimeOffset startedAt)
{
    public Guid Id { get; } = id;
    public string Kind { get; } = kind;
    public Guid AssetId { get; } = assetId;
    public Func<Task> Stop { get; } = stop;
    public DateTimeOffset StartedAt { get; } = startedAt;
    public DateTimeOffset LastViewerAt { get; internal set; } = startedAt;
    public volatile bool WaitingForSlot;
    public volatile bool IsStopping;
    public string Step { get; set; } = PlayingNowSteps.Preparing;
    public string RanOn { get; set; } = PlaybackEncodePlaces.Processor;
    public string? Heaviness { get; set; }
    public string? Quality { get; init; }
    public string? RequestedBy { get; init; }
    public string? Title { get; init; }

    /// <summary>How much is prepared (0 to 100), or null; read each time the list is built.</summary>
    public Func<int?>? Percent { get; set; }
}

/// <summary>What the Engine knows about one open player session, captured when it opened.</summary>
public sealed record WatchEntry(
    Guid PlayerSessionId,
    Guid AssetId,
    Guid AccountId,
    Guid ProfileId,
    Guid? DeviceId,
    string? DeviceName,
    string Title,
    string? Subtitle,
    string? CoverUrl,
    bool IsVault,
    string State,
    double PositionSeconds,
    double? DurationSeconds,
    DateTimeOffset StartedAt,
    DateTimeOffset LastObservedAt);
