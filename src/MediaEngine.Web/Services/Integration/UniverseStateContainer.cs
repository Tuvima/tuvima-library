using MediaEngine.Contracts.Ingestion;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// Scoped state container (one per Blazor Server circuit) that caches the
/// current Library view and surfaces real-time event data received from
/// the Engine API Intercom SignalR collection.
///
/// <para>
/// <b>Thread safety:</b> SignalR event handlers run on a background thread.
/// Components that subscribe to <see cref="OnStateChanged"/> MUST dispatch
/// re-renders via <c>InvokeAsync(StateHasChanged)</c> — never bare
/// <c>StateHasChanged()</c>.
/// </para>
/// </summary>
public sealed class UniverseStateContainer : IDisposable
{
    private static readonly TimeSpan LiveNotificationThrottle = TimeSpan.FromMilliseconds(250);
    private readonly object _notificationLock = new();
    private CancellationTokenSource? _notificationCts;
    private bool _pendingSnapshotRefresh;
    private List<CollectionViewModel> _collections = [];
    private CollectionViewModel? _selected;
    private UniverseViewModel? _universe;
    private bool _loaded;
    private IngestionProgressEvent? _ingestionProgress;
    private DateTimeOffset? _ingestionProgressReceivedAt;
    private IngestionQueueProgressDto? _ingestionQueueProgress;
    private DateTimeOffset? _ingestionQueueProgressReceivedAt;
    private EngineConnectionState _engineConnectionState = EngineConnectionState.Unknown;
    private readonly Dictionary<Guid, LiveIngestionItemProgress> _ingestionItemProgress = new();
    private UniverseEnrichmentProgressEvent? _universeEnrichmentProgress;
    private DateTimeOffset? _universeEnrichmentProgressReceivedAt;
    private readonly Dictionary<string, LiveModelDownloadProgress> _modelDownloadActivity = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, MediaOperationChangedEvent> _mediaOperationActivity = [];
    private List<IngestionProviderActivityDto> _providerActivity = [];
    private WatchFolderActiveEvent? _latestWatchFolderActivation;
    private string[]? _activeLaneMediaTypes;
    private bool _lastStateChangeRequiresSnapshotRefresh = true;
    private readonly List<PersonEnrichedEvent> _personUpdates = [];
    private readonly List<ActivityEntry> _activityLog = [];
    private const int MaxActivityEntries = 100;
    private const int MaxTrackedIngestionItems = 200;

    // ── Read-only surface ─────────────────────────────────────────────────────

    public IReadOnlyList<CollectionViewModel> Collections => _collections;
    public CollectionViewModel? Selected => _selected;

    /// <summary>
    /// Language code configured in the Engine (e.g. "en", "fr", "de").
    /// Used to build localised Wikipedia links. Defaults to "en" until the
    /// first successful status fetch.
    /// </summary>
    public string Language { get; set; } = "en";

    /// <summary>
    /// Flattened cross-media-type view built by <see cref="UniverseMapper"/>.
    /// Null until the first successful collection load; components should guard with
    /// <c>@if (State.Universe is { } u)</c>.
    /// </summary>
    public UniverseViewModel? Universe => _universe;

    public bool IsLoaded => _loaded;

    /// <summary>
    /// Latest ingestion progress snapshot pushed via SignalR.
    /// Null when no ingestion is in progress or the circuit is freshly created.
    /// </summary>
    public IngestionProgressEvent? IngestionProgress => _ingestionProgress;
    public DateTimeOffset? IngestionProgressReceivedAt => _ingestionProgressReceivedAt;

    /// <summary>The latest live ingestion numbers from the Engine, shown as sent; null until the first snapshot arrives.</summary>
    public IngestionQueueProgressDto? IngestionQueueProgress => _ingestionQueueProgress;

    /// <summary>When the Dashboard last received (pushed or fetched) an ingestion snapshot; the snapshot is only trusted while this is recent.</summary>
    public DateTimeOffset? IngestionQueueProgressReceivedAt => _ingestionQueueProgressReceivedAt;

    /// <summary>The Engine connection state, kept here so the menu indicator can read it without the orchestrator.</summary>
    public EngineConnectionState EngineConnectionState => _engineConnectionState;
    public IReadOnlyList<LiveIngestionItemProgress> IngestionItemProgress =>
        _ingestionItemProgress.Values
            .OrderByDescending(item => item.ReceivedAt)
            .ToList();
    public UniverseEnrichmentProgressEvent? UniverseEnrichmentProgress => _universeEnrichmentProgress;
    public DateTimeOffset? UniverseEnrichmentProgressReceivedAt => _universeEnrichmentProgressReceivedAt;
    public IReadOnlyList<LiveModelDownloadProgress> ModelDownloadActivity =>
        _modelDownloadActivity.Values.OrderByDescending(item => item.ReceivedAt).ToList();
    public IReadOnlyList<MediaOperationChangedEvent> MediaOperationActivity =>
        _mediaOperationActivity.Values.OrderByDescending(item => item.UpdatedAt).ToList();
    public IReadOnlyList<IngestionProviderActivityDto> ProviderActivity => _providerActivity;
    public bool LastStateChangeRequiresSnapshotRefresh => _lastStateChangeRequiresSnapshotRefresh;

    public IReadOnlyList<PersonEnrichedEvent> RecentPersonUpdates => _personUpdates;

    /// <summary>
    /// The most recent <c>"WatchFolderActive"</c> event received via SignalR.
    /// Null until the watch folder has been configured or changed in the current circuit.
    /// </summary>
    public WatchFolderActiveEvent? LatestWatchFolderActivation => _latestWatchFolderActivation;

    /// <summary>
    /// Rolling log of plain-English activity entries from SignalR events.
    /// Most recent entries first. Capped at <see cref="MaxActivityEntries"/>.
    /// </summary>
    public IReadOnlyList<ActivityEntry> ActivityLog => _activityLog;

    // ── Lane filter ──────────────────────────────────────────────────────────

    /// <summary>
    /// Collections filtered by the currently active content lane's media types.
    /// Returns all collections when no lane filter is active (Home page).
    /// </summary>
    public IReadOnlyList<CollectionViewModel> FilteredCollections =>
        _activeLaneMediaTypes is null or { Length: 0 }
            ? Collections
            : _collections.Where(h => SplitMediaTypes(h.MediaTypes).Any(mt =>
                _activeLaneMediaTypes.Any(f => string.Equals(f, mt, StringComparison.OrdinalIgnoreCase))))
              .ToList();

    /// <summary>
    /// Distinct set of media type strings across all collections in the library.
    /// Used by lane pages for content-aware display.
    /// </summary>
    public HashSet<string> AvailableMediaTypes =>
        new HashSet<string>(
            _collections.SelectMany(h => SplitMediaTypes(h.MediaTypes)),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Splits a comma-separated MediaTypes string into individual trimmed values.</summary>
    private static string[] SplitMediaTypes(string mediaTypes) =>
        string.IsNullOrWhiteSpace(mediaTypes)
            ? []
            : mediaTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Sets the active lane media-type filter. Pass null or empty to show all (Home).
    /// Called by lane pages when they mount/navigate.
    /// </summary>
    public void SetLaneFilter(string[]? mediaTypes)
    {
        _activeLaneMediaTypes = mediaTypes;
        NotifyStateChanged();
    }

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fires whenever the collection list, selected collection, universe view, or
    /// ingestion progress changes.  May fire from a SignalR background thread —
    /// use <c>InvokeAsync(StateHasChanged)</c> in component handlers.
    /// </summary>
    public event Action? OnStateChanged;
    public event Action<MediaAddedEvent>? MediaAddedReceived;

    /// <summary>Raised with each live ingestion snapshot the Engine publishes (the Engine's counting, shown as-is).</summary>
    public event Action<IngestionQueueProgressDto>? IngestionQueueProgressReceived;

    // ── Collection-list mutations ────────────────────────────────────────────────────

    /// <summary>
    /// Replaces the cached collection list and rebuilds the flattened
    /// <see cref="UniverseViewModel"/> via <see cref="UniverseMapper"/>.
    /// </summary>
    public void SetCollections(IEnumerable<CollectionViewModel> collections)
    {
        _collections = collections.ToList();
        _universe = UniverseMapper.MapFromCollections(_collections);
        _loaded = true;
        NotifyStateChanged();
    }

    public void SelectCollection(CollectionViewModel? collection)
    {
        _selected = collection;
        NotifyStateChanged();
    }

    /// <summary>
    /// Clears all cached data.  The next call to
    /// <c>UIOrchestratorService.GetCollectionsAsync()</c> will trigger a fresh API fetch.
    /// </summary>
    public void Invalidate(bool requiresSnapshotRefresh = true)
    {
        _collections = [];
        _selected = null;
        _universe = null;
        _loaded = false;
        _ingestionProgress = null;
        _ingestionProgressReceivedAt = null;
        // Note: _activeLaneMediaTypes is NOT cleared on invalidate
        // so the user's lane selection persists across data refreshes.
        NotifyStateChanged(requiresSnapshotRefresh);
    }

    // ── Real-time event sinks (called by UIOrchestratorService) ───────────────

    /// <summary>
    /// Called when an <c>"IngestionProgress"</c> event arrives on the Intercom collection.
    /// Updates the progress indicator and notifies subscribed components.
    /// </summary>
    public void PushIngestionProgress(IngestionProgressEvent ev)
    {
        var complete = string.Equals(ev.Stage, "Complete", StringComparison.OrdinalIgnoreCase);
        _ingestionProgress = complete ? null : ev;
        _ingestionProgressReceivedAt = complete ? null : DateTimeOffset.UtcNow;

        // Only log stage transitions (Scanning/Complete), not every tick.
        if (ev.Stage is "Scanning" or "Complete")
        {
            var summary = ev.Stage == "Complete"
                ? $"Ingestion complete — {ev.ProcessedCount} files processed."
                : $"Scanning files… ({ev.ProcessedCount} found so far)";
            PushActivity(new ActivityEntry(
                DateTimeOffset.UtcNow, ActivityKind.IngestionProgress,
                "sync", summary));
        }

        NotifyStateChanged(requiresSnapshotRefresh: complete, throttle: !complete);
    }

    /// <summary>
    /// Called when a <c>"IngestionItemProgress"</c> event arrives on the Intercom collection.
    /// Keeps item-level stage progress for live ingestion dashboards.
    /// </summary>
    public void PushIngestionItemProgress(IngestionItemProgressEvent ev)
    {
        _ingestionItemProgress[ev.LogEntryId] = new LiveIngestionItemProgress(ev, DateTimeOffset.UtcNow);
        PruneIngestionItemProgress();
        NotifyStateChanged(requiresSnapshotRefresh: false, throttle: true);
    }

    /// <summary>
    /// Called when an <c>"IngestionQueueProgress"</c> event arrives, and by the fetch on connect; hands the Engine's snapshot
    /// on unchanged. A snapshot older than the one already held is ignored, so a slow fetch never overwrites a newer push.
    /// </summary>
    public void PushIngestionQueueProgress(IngestionQueueProgressDto snapshot) =>
        PushIngestionQueueProgress(snapshot, DateTimeOffset.UtcNow);

    internal void PushIngestionQueueProgress(IngestionQueueProgressDto snapshot, DateTimeOffset receivedAt)
    {
        if (_ingestionQueueProgress is { } current && snapshot.UpdatedAt < current.UpdatedAt)
        {
            return;
        }

        _ingestionQueueProgress = snapshot;
        _ingestionQueueProgressReceivedAt = receivedAt;
        IngestionQueueProgressReceived?.Invoke(snapshot);
        NotifyStateChanged(requiresSnapshotRefresh: false, throttle: true);
    }

    /// <summary>Called by the orchestrator whenever the Engine connection state changes.</summary>
    public void SetEngineConnectionState(EngineConnectionState state)
    {
        if (_engineConnectionState == state)
        {
            return;
        }

        _engineConnectionState = state;
        NotifyStateChanged(requiresSnapshotRefresh: false);
    }

    /// <summary>
    /// Called when a <c>"UniverseEnrichmentProgress"</c> event arrives on the Intercom collection.
    /// Keeps a live Stage 3 snapshot and adds a rolling feed entry per file-stage transition.
    /// </summary>
    public void PushUniverseEnrichmentProgress(UniverseEnrichmentProgressEvent ev)
    {
        _universeEnrichmentProgress = ev;
        _universeEnrichmentProgressReceivedAt = DateTimeOffset.UtcNow;

        PushActivity(new ActivityEntry(
            DateTimeOffset.UtcNow,
            ActivityKind.MetadataUpdated,
            "auto_awesome",
            $"{ev.CurrentStep}: {ev.WorkTitle}",
            $"{ev.ProcessedCount} of {ev.TotalCount} in Stage 3"));

        NotifyStateChanged(requiresSnapshotRefresh: false, throttle: true);
    }

    public void PushModelDownloadProgress(ModelDownloadProgressEvent ev)
    {
        if (ev.Percent >= 100)
        {
            _modelDownloadActivity.Remove(ev.Role);
        }
        else
        {
            _modelDownloadActivity[ev.Role] = new LiveModelDownloadProgress(ev, DateTimeOffset.UtcNow);
        }

        NotifyStateChanged(requiresSnapshotRefresh: false, throttle: true);
    }

    public void PushModelStateChanged(ModelStateChangedEvent ev)
    {
        if (!ev.NewState.Equals("Downloading", StringComparison.OrdinalIgnoreCase))
        {
            _modelDownloadActivity.Remove(ev.Role);
        }

        NotifyStateChanged(requiresSnapshotRefresh: false, throttle: true);
    }

    public void PushMediaOperationChanged(MediaOperationChangedEvent ev)
    {
        if (IsActiveOperationStatus(ev.Status))
        {
            _mediaOperationActivity[ev.Id] = ev;
        }
        else
        {
            _mediaOperationActivity.Remove(ev.Id);
        }

        NotifyStateChanged(requiresSnapshotRefresh: false, throttle: true);
    }

    public void SetMediaOperationActivity(IEnumerable<MediaOperationChangedEvent> operations)
    {
        _mediaOperationActivity.Clear();
        foreach (var operation in operations.Where(operation => IsActiveOperationStatus(operation.Status)))
        {
            _mediaOperationActivity[operation.Id] = operation;
        }

        NotifyStateChanged(requiresSnapshotRefresh: false, throttle: true);
    }

    /// <summary>
    /// Called when a <c>"MediaAdded"</c> event arrives on the Intercom collection.
    /// Logs the event and invalidates the collection cache so the next navigation
    /// triggers a fresh load with the new Work included.
    /// </summary>
    public void PushMediaAdded(MediaAddedEvent ev)
    {
        PushActivity(new ActivityEntry(
            DateTimeOffset.UtcNow, ActivityKind.MediaAdded,
            "library_add",
            $"New {ev.MediaType.ToLowerInvariant()} added: \"{ev.Title}\"",
            ev.CollectionId is { } collectionId ? $"Assigned to Collection {collectionId:N}" : "Standalone (no Collection)"));
        MediaAddedReceived?.Invoke(ev);
        Invalidate(requiresSnapshotRefresh: false);
    }

    /// <summary>
    /// Called when an <c>"IngestionCompleted"</c> event arrives on the Intercom collection.
    /// Logs the event and invalidates the collection cache so the next navigation
    /// triggers a fresh load with the newly ingested file included.
    /// </summary>
    public void PushIngestionCompleted(IngestionCompletedEvent ev)
    {
        var fileName = Path.GetFileName(ev.FilePath);
        PushActivity(new ActivityEntry(
            DateTimeOffset.UtcNow, ActivityKind.MediaAdded,
            "library_add",
            $"Ingested {ev.MediaType.ToLowerInvariant()}: \"{fileName}\""));
        Invalidate(requiresSnapshotRefresh: false);
    }

    /// <summary>
    /// Called when a <c>"PersonEnriched"</c> event arrives on the Intercom collection.
    /// Keeps a rolling buffer of the 50 most recent person updates.
    /// </summary>
    public void PushPersonEnriched(PersonEnrichedEvent ev)
    {
        _personUpdates.Add(ev);
        if (_personUpdates.Count > 50)
        {
            _personUpdates.RemoveAt(0);
        }

        PushActivity(new ActivityEntry(
            DateTimeOffset.UtcNow, ActivityKind.PersonEnriched,
            "person",
            $"Enriched person: {ev.Name}",
            ev.HeadshotUrl is not null ? "Portrait found on Wikimedia Commons" : null));

        NotifyStateChanged();
    }

    /// <summary>
    /// Called when a <c>"MetadataHarvested"</c> event arrives on the Intercom collection.
    /// Logs the event and invalidates the cache.
    /// </summary>
    public void PushMetadataHarvested(MetadataHarvestedEvent ev)
    {
        var fields = string.Join(", ", ev.UpdatedFields);
        PushActivity(new ActivityEntry(
            DateTimeOffset.UtcNow, ActivityKind.MetadataUpdated,
            "auto_awesome",
            $"Metadata updated by {ev.ProviderName}",
            $"Fields enriched: {fields}"));
        Invalidate(requiresSnapshotRefresh: false);
    }

    public void PushProviderActivity(ProviderActivityEvent ev)
    {
        _providerActivity = ev.Providers.Select(provider => new IngestionProviderActivityDto
        {
            ProviderName = provider.ProviderName,
            ActiveRequests = provider.ActiveRequests,
            WaitingRequests = provider.WaitingRequests,
            RequestsTotal = provider.RequestsTotal,
            RequestsLastMinute = provider.RequestsLastMinute,
            MaxActiveLastMinute = provider.MaxActiveLastMinute,
            ErrorsTotal = provider.ErrorsTotal,
            ErrorsLastMinute = provider.ErrorsLastMinute,
            ThrottleWaitMsTotal = provider.ThrottleWaitMsTotal,
            WaitMsLastMinute = provider.WaitMsLastMinute,
            AverageWaitMs = provider.AverageWaitMs,
            AverageLatencyMs = provider.AverageLatencyMs,
            LastSuccessAt = provider.LastSuccessAt,
            LastRequestAt = provider.LastRequestAt,
            LastError = provider.LastError,
        }).ToList();

        NotifyStateChanged(requiresSnapshotRefresh: false, throttle: true);
    }

    /// <summary>
    /// Called when a <c>"WatchFolderActive"</c> event arrives on the Intercom collection.
    /// Updates <see cref="LatestWatchFolderActivation"/> so components can react
    /// to a watch folder change without a page reload.
    /// </summary>
    public void PushWatchFolderActive(WatchFolderActiveEvent ev)
    {
        _latestWatchFolderActivation = ev;

        PushActivity(new ActivityEntry(
            DateTimeOffset.UtcNow, ActivityKind.WatchFolderChanged,
            "folder_open",
            $"Watch folder updated: {ev.WatchDirectory}"));

        NotifyStateChanged();
    }

    // ── Activity log helpers ────────────────────────────────────────────────

    /// <summary>Inserts an entry at position 0 (most recent first) and trims overflow.</summary>
    private void PushActivity(ActivityEntry entry)
    {
        _activityLog.Insert(0, entry);
        if (_activityLog.Count > MaxActivityEntries)
        {
            _activityLog.RemoveAt(_activityLog.Count - 1);
        }
    }

    /// <summary>Adds a startup entry — called once by the orchestrator on connection.</summary>
    public void PushServerStarted()
    {
        PushActivity(new ActivityEntry(
            DateTimeOffset.UtcNow, ActivityKind.ServerStatus,
            "power_settings_new",
            "Dashboard connected to the Engine."));
        NotifyStateChanged();
    }

    private void NotifyStateChanged(bool requiresSnapshotRefresh = true, bool throttle = false)
    {
        if (!throttle)
        {
            lock (_notificationLock)
            {
                requiresSnapshotRefresh |= _pendingSnapshotRefresh;
                _pendingSnapshotRefresh = false;
                _notificationCts?.Cancel();
                _notificationCts?.Dispose();
                _notificationCts = null;
            }

            _lastStateChangeRequiresSnapshotRefresh = requiresSnapshotRefresh;
            OnStateChanged?.Invoke();
            return;
        }

        lock (_notificationLock)
        {
            _pendingSnapshotRefresh |= requiresSnapshotRefresh;
            // Consumers inspect this flag synchronously after accepting a live event.
            // Keep it current even when the actual render notification is coalesced.
            _lastStateChangeRequiresSnapshotRefresh = _pendingSnapshotRefresh;
            if (_notificationCts is not null)
            {
                return;
            }

            _notificationCts = new CancellationTokenSource();
            _ = FlushNotificationAsync(_notificationCts);
        }
    }

    private async Task FlushNotificationAsync(CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(LiveNotificationThrottle, source.Token).ConfigureAwait(false);
            bool requiresSnapshotRefresh;
            lock (_notificationLock)
            {
                if (!ReferenceEquals(_notificationCts, source))
                {
                    return;
                }

                requiresSnapshotRefresh = _pendingSnapshotRefresh;
                _pendingSnapshotRefresh = false;
                _notificationCts = null;
            }

            source.Dispose();
            _lastStateChangeRequiresSnapshotRefresh = requiresSnapshotRefresh;
            OnStateChanged?.Invoke();
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        lock (_notificationLock)
        {
            _notificationCts?.Cancel();
            _notificationCts?.Dispose();
            _notificationCts = null;
        }
    }

    private void PruneIngestionItemProgress()
    {
        if (_ingestionItemProgress.Count <= MaxTrackedIngestionItems)
        {
            return;
        }

        foreach (var stale in _ingestionItemProgress
            .OrderBy(pair => IsFileIngestionTerminal(pair.Value.Event) ? 0 : 1)
            .ThenBy(pair => pair.Value.ReceivedAt)
            .Take(_ingestionItemProgress.Count - MaxTrackedIngestionItems)
            .Select(pair => pair.Key)
            .ToList())
        {
            _ingestionItemProgress.Remove(stale);
        }
    }

    private static bool IsFileIngestionTerminal(IngestionItemProgressEvent ev) =>
        ev.IsTerminal
        || ev.Stage.Equals("queued_identity", StringComparison.OrdinalIgnoreCase)
        || ev.Stage.Equals("complete", StringComparison.OrdinalIgnoreCase)
        || ev.Stage.Equals("needs_review", StringComparison.OrdinalIgnoreCase)
        || ev.Stage.Equals("duplicate", StringComparison.OrdinalIgnoreCase)
        || ev.Stage.Equals("same_path_redetected", StringComparison.OrdinalIgnoreCase)
        || ev.Stage.Equals("missing", StringComparison.OrdinalIgnoreCase)
        || ev.Stage.Equals("skipped_non_media", StringComparison.OrdinalIgnoreCase)
        || ev.Stage.Equals("failed", StringComparison.OrdinalIgnoreCase);

    private static bool IsActiveOperationStatus(string status) =>
        status.Equals("leased", StringComparison.OrdinalIgnoreCase)
        || status.Equals("running", StringComparison.OrdinalIgnoreCase);
}

public sealed record LiveIngestionItemProgress(
    IngestionItemProgressEvent Event,
    DateTimeOffset ReceivedAt);

public sealed record LiveModelDownloadProgress(
    ModelDownloadProgressEvent Event,
    DateTimeOffset ReceivedAt);
