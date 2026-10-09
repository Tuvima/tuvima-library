using MediaEngine.Contracts.Playback;

namespace MediaEngine.Web.Services.Playback;

public sealed partial class PlaybackSessionController
{
    private readonly Dictionary<Guid, Guid> _persistedQueueEntries = [];
    private Guid[] _lastQueueOrder = [];
    private int _lastQueueCurrentIndex = -1;
    private bool _explicitUpcomingOrder;
    public long QueueRevision { get; private set; }
    public bool OutputSupported { get; private set; }
    public long AudioOutputRevision { get; private set; }
    public bool SoftwareVolumeSupported { get; private set; } = true;

    public void SetAudioCapabilities(bool outputSupported, bool softwareVolumeSupported)
    {
        AudioOutputRevision++;
        OutputSupported = outputSupported;
        SoftwareVolumeSupported = softwareVolumeSupported;
        NotifyChanged();
    }

    private void TrackQueueRevision()
    {
        var order = _queue.Select(item => item.QueueEntryId).ToArray();
        if (_lastQueueOrder.SequenceEqual(order) && _lastQueueCurrentIndex == CurrentIndex)
        {
            return;
        }
        QueueRevision++;
        _lastQueueOrder = order;
        _lastQueueCurrentIndex = CurrentIndex;
        if (CurrentIndex >= _queue.Count - 1)
        {
            _explicitUpcomingOrder = false;
        }
    }

    private void CapturePersistedQueue(PlayerStateDto? state, bool allowNewOccurrences = false)
    {
        if (state is null || state.Queue.Count != _queue.Count
            || _preferences?.ActiveProfileId is Guid profile && state.ProfileId != profile)
        {
            return;
        }
        var available = state.Queue.ToList();
        var mapping = new Dictionary<Guid, Guid>();
        foreach (var item in _queue)
        {
            // Retain established occurrence identities before matching new duplicates.
            var knownId = item.PersistedQueueItemId ?? _persistedQueueEntries.GetValueOrDefault(item.QueueEntryId);
            var match = available.FirstOrDefault(row => knownId == row.QueueItemId);
            if (match is null && allowNewOccurrences)
            {
                match = item.QueueEntryId == CurrentItem?.QueueEntryId
                        ? available.FirstOrDefault(row => row.QueueItemId == state.CurrentQueueItemId)
                            ?? available.FirstOrDefault(row => row.WorkId == item.WorkId && row.AssetId == item.AssetId)
                        : available.FirstOrDefault(row => row.WorkId == item.WorkId && row.AssetId == item.AssetId);
            }
            if (match is null || match.QueueItemId == Guid.Empty || match.WorkId != item.WorkId || match.AssetId != item.AssetId)
            {
                return;
            }
            mapping[item.QueueEntryId] = match.QueueItemId;
            available.Remove(match);
        }
        _persistedQueueEntries.Clear();
        foreach (var entry in mapping)
        {
            _persistedQueueEntries.Add(entry.Key, entry.Value);
        }
        for (var index = 0; index < _queue.Count; index++)
        {
            _queue[index] = _queue[index] with { PersistedQueueItemId = mapping[_queue[index].QueueEntryId] };
        }
    }

    public void ClearMusicHistory()
    {
        if (!IsMusicMode)
        {
            throw new InvalidOperationException("Only this player's music history can be cleared.");
        }
        _history.RemoveAll(item => item.PlaybackExperience == PlaybackExperience.Music);
        NotifyChanged();
    }

    private bool SavedQueueMatches(PlayerStateDto? state) => state is not null
        && (_preferences?.ActiveProfileId is not Guid profile || state.ProfileId == profile)
        && _persistedQueueEntries.Count == _queue.Count
        && state.Queue.Select(row => row.QueueItemId).SequenceEqual(_queue.Select(item => _persistedQueueEntries.GetValueOrDefault(item.QueueEntryId)))
        && state.CurrentQueueItemId == _persistedQueueEntries.GetValueOrDefault(CurrentItem?.QueueEntryId ?? Guid.Empty);

    private async Task ClearUpcomingSavedAsync(CancellationToken ct)
    {
        // Confirm each occurrence independently. A conflict leaves the remaining queue intact.
        var version = PlaybackRequestVersion;
        var profile = _preferences?.ActiveProfileId;
        while (_queue.Count > CurrentIndex + 1)
        {
            if (PlaybackRequestVersion != version || _preferences?.ActiveProfileId != profile || IsDismissed)
            {
                throw new InvalidOperationException("Playback changed before the remaining queue could be cleared.");
            }
            await RemoveUpcomingSavedAsync(_queue.Count - 1, ct);
        }
    }

    private async Task RemoveUpcomingSavedAsync(int index, CancellationToken ct)
    {
        if (index <= CurrentIndex || index < 0 || index >= _queue.Count)
        {
            return;
        }
        var occurrence = _queue[index].QueueEntryId;
        var request = PlaybackRequestVersion;
        var profile = _preferences?.ActiveProfileId;
        var revision = QueueRevision;
        bool Current() => request == PlaybackRequestVersion && profile == _preferences?.ActiveProfileId && revision == QueueRevision && !IsDismissed;
        if (_apiClient is not null)
        {
            var state = await _apiClient.GetPlayerStateAsync(profile, _clientContext.DeviceId, _clientContext.Client, ct);
            if (!Current())
            {
                throw new InvalidOperationException("Playback changed before the removal completed.");
            }
            CapturePersistedQueue(state);
            if (!SavedQueueMatches(state) || !_persistedQueueEntries.TryGetValue(occurrence, out var savedId))
            {
                throw new InvalidOperationException("The saved queue changed. Refresh playback before removing a track.");
            }
            var expectedIds = state!.Queue.Where(row => row.QueueItemId != savedId).Select(row => row.QueueItemId).ToArray();
            await _apiClient.RemovePlayerQueueItemAsync(savedId, new()
            {
                ProfileId = profile,
                DeviceId = _clientContext.DeviceId,
                Client = _clientContext.Client,
                ExpectedStateVersion = state.StateVersion,
                Force = false
            }, ct);
            var confirmed = await _apiClient.GetPlayerStateAsync(profile, _clientContext.DeviceId, _clientContext.Client, ct);
            if (!Current() || confirmed is null || confirmed.ProfileId != state.ProfileId
                || confirmed.CurrentQueueItemId != state.CurrentQueueItemId
                || !confirmed.Queue.Select(row => row.QueueItemId).SequenceEqual(expectedIds))
            {
                throw new InvalidOperationException("The main player could not confirm this queue removal.");
            }
        }
        RemoveUpcomingAt(index);
        _persistedQueueEntries.Remove(occurrence);
    }

    public async Task MoveUpcomingAsync(Guid occurrenceId, int upcomingIndex, long expectedRevision, CancellationToken ct = default)
    {
        TrackQueueRevision();
        if (!IsMusicMode || IsDismissed || expectedRevision != QueueRevision)
        {
            throw new InvalidOperationException("The queue changed. Try again with its current order.");
        }
        var source = _queue.FindIndex(item => item.QueueEntryId == occurrenceId);
        var upcomingCount = _queue.Count - CurrentIndex - 1;
        if (source <= CurrentIndex || upcomingIndex < 0 || upcomingIndex >= upcomingCount)
        {
            throw new InvalidOperationException("Only upcoming tracks can be moved within the upcoming queue.");
        }
        var target = CurrentIndex + 1 + upcomingIndex;
        if (target == source)
        {
            return;
        }
        var reordered = _queue.ToList();
        var moving = reordered[source];
        reordered.RemoveAt(source);
        reordered.Insert(target, moving);
        var version = PlaybackRequestVersion;
        var currentId = CurrentItem?.QueueEntryId;
        var profile = _preferences?.ActiveProfileId;
        bool StillCurrent() => QueueRevision == expectedRevision && PlaybackRequestVersion == version
            && CurrentItem?.QueueEntryId == currentId && _preferences?.ActiveProfileId == profile && !IsDismissed;
        if (_apiClient is not null)
        {
            // Refresh the Engine version because transport/heartbeats also advance it.
            var state = await _apiClient.GetPlayerStateAsync(profile, _clientContext.DeviceId, _clientContext.Client, ct);
            if (!StillCurrent())
            {
                throw new InvalidOperationException("Playback changed before the move completed.");
            }
            CapturePersistedQueue(state);
            if (!SavedQueueMatches(state))
            {
                throw new InvalidOperationException("The saved queue is unavailable or changed. Restart this queue before moving tracks.");
            }
            var ids = reordered.Select(item => _persistedQueueEntries[item.QueueEntryId]).ToArray();
            var result = await _apiClient.ReorderPlayerQueueAsync(new()
            {
                ProfileId = profile,
                DeviceId = _clientContext.DeviceId,
                Client = _clientContext.Client,
                QueueItemIds = ids,
                ExpectedStateVersion = state!.StateVersion,
                Force = false,
            }, ct);
            if (!StillCurrent())
            {
                throw new InvalidOperationException("Playback changed before the move completed.");
            }
            // A lost reply is not permission to retry a mutation. Read its outcome once.
            result ??= await _apiClient.GetPlayerStateAsync(profile, _clientContext.DeviceId, _clientContext.Client, ct);
            if (!StillCurrent() || result is null || result.ProfileId != state.ProfileId
                || result.CurrentQueueItemId != state.CurrentQueueItemId || !result.Queue.Select(row => row.QueueItemId).SequenceEqual(ids))
            {
                throw new InvalidOperationException("The main player could not confirm the saved order. Refresh the queue before trying again.");
            }
            reordered = reordered.Select(item => item with { PersistedQueueItemId = _persistedQueueEntries[item.QueueEntryId] }).ToList();
        }
        _queue.Clear();
        _queue.AddRange(reordered);
        _explicitUpcomingOrder = true;
        NotifyChanged();
    }
}
