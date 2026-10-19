namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// Scoped state for the live Ingestion page. The numbers in its status panel are the Engine's queue snapshot
/// (<see cref="QueueProgress"/>), shown as sent; the presentation snapshot feeds the cards and previews below it.
/// </summary>
public sealed class IngestionLiveDashboardState : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan LiveNotifyThrottle = TimeSpan.FromSeconds(2);

    private readonly IEngineApiClient _api;
    private readonly UIOrchestratorService _orchestrator;
    private readonly UniverseStateContainer _stateContainer;
    private readonly object _snapshotRefreshGate = new();
    private readonly object _liveNotifyGate = new();
    private readonly SemaphoreSlim _snapshotRefreshSignal = new(0, 1);
    private readonly SemaphoreSlim _liveNotifySignal = new(0, 1);
    private CancellationTokenSource? _backgroundCts;
    private Task? _pollTask;
    private Task? _snapshotRefreshTask;
    private Task? _liveNotifyTask;
    private string? _lastPresentationSignature;
    private string? _lastQueueSignature;
    private DateTimeOffset _lastLiveNotifyAt = DateTimeOffset.MinValue;
    private int _loadInProgress;
    private bool _loadAgainRequested;
    private bool _liveNotifyScheduled;
    private bool _signalsDisposed;
    private bool _disposed;
    private bool _initialized;
    // Written by both the snapshot handler and the poll's load, on different threads.
    private volatile bool _queueFetchFailed;
    private volatile string? _error;

    public IngestionLiveDashboardState(
        IEngineApiClient api,
        UIOrchestratorService orchestrator,
        UniverseStateContainer stateContainer)
    {
        _api = api;
        _orchestrator = orchestrator;
        _stateContainer = stateContainer;
    }

    public event Action? OnChanged;

    public IngestionPresentationSnapshotDto? Presentation { get; private set; }

    /// <summary>The Engine's latest queue snapshot, pushed over SignalR and fetched again on every (re)connect and load.</summary>
    public IngestionQueueProgressDto? QueueProgress => _stateContainer.IngestionQueueProgress;

    public bool IsLoading { get; private set; }
    public bool IsScanStarting { get; private set; }
    public string? Error => _error;

    public EngineConnectionState ConnectionState => _orchestrator.EngineConnectionState;

    /// <summary>
    /// True only when the Engine itself cannot be reached: a load failed, the latest queue fetch came back empty after a
    /// request, or the connection is fully offline. Live updates being off (reconnecting, or the live connection refused)
    /// is not "not responding": the page keeps re-reading the numbers on its poll instead. It clears by itself when a
    /// fresh snapshot arrives or a later load succeeds.
    /// </summary>
    public bool IsEngineUnreachable =>
        _error is not null || _queueFetchFailed || ConnectionState == EngineConnectionState.Offline;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (!_initialized)
        {
            _stateContainer.OnStateChanged += OnRealtimeStateChanged;
            _stateContainer.IngestionQueueProgressReceived += OnQueueSnapshotReceived;
            _orchestrator.OnEngineConnectionStateChanged += OnConnectionStateChanged;
            _initialized = true;
        }

        await _orchestrator.StartSignalRAsync(ct);
        await LoadAsync(ct);
        StartBackgroundTasks();
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (System.Threading.Interlocked.Exchange(ref _loadInProgress, 1) == 1)
        {
            _loadAgainRequested = true;
            return;
        }

        try
        {
            do
            {
                _loadAgainRequested = false;
                await LoadSnapshotAsync(ct).ConfigureAwait(false);
            }
            while (_loadAgainRequested && !ct.IsCancellationRequested && !_disposed);
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _loadInProgress, 0);
        }
    }

    private async Task LoadSnapshotAsync(CancellationToken ct)
    {
        IsLoading = Presentation is null;
        _error = null;
        var shouldNotify = IsLoading;
        if (IsLoading)
        {
            Notify();
        }

        try
        {
            var presentationTask = _api.GetIngestionPresentationAsync(ct);
            var queueTask = _api.GetIngestionQueueProgressAsync(ct);
            await Task.WhenAll(presentationTask, queueTask);

            Presentation = MergePresentation(Presentation, presentationTask.Result);

            // The Engine pushes this snapshot itself; fetching it here covers a page opened before the first push and
            // makes Retry recover it. The container never lets an older fetched snapshot replace a newer pushed one.
            // The client reports a failed request as null, so a null here after a request means the Engine did not answer.
            _queueFetchFailed = queueTask.Result is null;
            if (queueTask.Result is { } fetched)
            {
                _stateContainer.PushIngestionQueueProgress(fetched);
            }

            var presentationSignature = BuildPresentationSignature(Presentation);
            var queueSignature = BuildQueueSignature(QueueProgress) + (_queueFetchFailed ? "|unreachable" : string.Empty);
            var changed = !string.Equals(_lastPresentationSignature, presentationSignature, StringComparison.Ordinal)
                || !string.Equals(_lastQueueSignature, queueSignature, StringComparison.Ordinal);
            _lastPresentationSignature = presentationSignature;
            _lastQueueSignature = queueSignature;
            if (changed)
            {
                shouldNotify = true;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _error = $"Ingestion status could not load: {ex.Message}";
            shouldNotify = true;
        }
        finally
        {
            IsLoading = false;
            if (shouldNotify)
            {
                Notify();
            }
        }
    }

    private static string BuildQueueSignature(IngestionQueueProgressDto? value)
    {
        if (value is null)
        {
            return "none";
        }

        return string.Join('|',
            value.State,
            value.FilesFound,
            value.FilesDone,
            value.FilesWaiting,
            value.FilesMoreFound,
            value.FilesUnsorted,
            value.Progress,
            value.EstimatedSecondsRemaining,
            string.Join(',', value.WaitingFor),
            string.Join(',', value.Tally.Select(entry => $"{entry.MediaType}:{entry.Files}")),
            value.UpdatedAt.ToString("O"));
    }

    private static string BuildPresentationSignature(IngestionPresentationSnapshotDto? value)
    {
        if (value is null)
        {
            return "none";
        }

        return string.Join('|',
            value.Status,
            value.LibraryGroups,
            value.ReadyGroups,
            value.FinishingGroups,
            value.ReviewGroups,
            value.ActiveOperations,
            value.QueuedOperations,
            value.RetryWaitingOperations,
            string.Join(',', value.CurrentMedia.Select(item => string.Join(':',
                item.BatchId.ToString("N"),
                item.GroupId.ToString("N"),
                item.Availability,
                item.StatusLabel,
                item.ChildCompleted,
                item.FileCount,
                item.ChildExpected,
                item.CoverUrl,
                FacetSignature(item.People),
                FacetSignature(item.Artwork),
                FacetSignature(item.Metadata),
                FacetSignature(item.Relationships),
                FacetSignature(item.TextTracks),
                item.UpdatedAt.ToString("O")))),
            string.Join(',', value.RecentDays.Select(day => $"{day.Date:O}:{day.TotalCount}")));
    }

    private static string FacetSignature(IngestionFacetStateDto value) => $"{value.State}/{value.Label}";

    private static IngestionPresentationSnapshotDto? MergePresentation(
        IngestionPresentationSnapshotDto? current,
        IngestionPresentationSnapshotDto? next)
    {
        if (next is null)
        {
            return current;
        }

        if (current is null)
        {
            return next;
        }

        // The server orders the bounded preview by current activity. Preserve that order.
        return next;
    }

    public async Task ScanNowAsync(CancellationToken ct = default)
    {
        IsScanStarting = true;
        Notify();
        try
        {
            await _orchestrator.TriggerRescanAsync(ct);
            await LoadAsync(ct);
        }
        finally
        {
            IsScanStarting = false;
            Notify();
        }
    }

    private void StartBackgroundTasks()
    {
        if (_backgroundCts is not null || _disposed)
        {
            return;
        }

        _backgroundCts = new CancellationTokenSource();
        var token = _backgroundCts.Token;
        _pollTask = PollLoopAsync(token);
        _snapshotRefreshTask = SnapshotRefreshLoopAsync(token);
        _liveNotifyTask = LiveNotifyLoopAsync(token);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var delay = Presentation?.IsRunning == true
                ? TimeSpan.FromSeconds(5)
                : TimeSpan.FromSeconds(40);
            try
            {
                await Task.Delay(delay, ct);
                await LoadAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void OnRealtimeStateChanged()
    {
        NotifyLiveThrottled();
        if (_stateContainer.LastStateChangeRequiresSnapshotRefresh)
        {
            DebounceSnapshotRefresh();
        }
    }

    private void OnQueueSnapshotReceived(IngestionQueueProgressDto snapshot)
    {
        // A snapshot only arrives when the Engine is answering, so any earlier failure is over.
        _queueFetchFailed = false;
        _error = null;
    }

    private void OnConnectionStateChanged(EngineConnectionState state)
    {
        Notify();
        if (state == EngineConnectionState.Online)
        {
            DebounceSnapshotRefresh();
        }
    }

    private void DebounceSnapshotRefresh()
    {
        lock (_snapshotRefreshGate)
        {
            if (_disposed || _snapshotRefreshSignal.CurrentCount > 0)
            {
                return;
            }

            _snapshotRefreshSignal.Release();
        }
    }

    private async Task SnapshotRefreshLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _snapshotRefreshSignal.WaitAsync(ct).ConfigureAwait(false);

                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                while (_snapshotRefreshSignal.Wait(0))
                {
                }

                await LoadAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private void Notify()
    {
        if (!_disposed)
        {
            OnChanged?.Invoke();
        }
    }

    private void NotifyLiveThrottled()
    {
        var now = DateTimeOffset.UtcNow;
        TimeSpan delay;
        lock (_liveNotifyGate)
        {
            if (_disposed)
            {
                return;
            }

            if (!_liveNotifyScheduled && now - _lastLiveNotifyAt >= LiveNotifyThrottle)
            {
                _lastLiveNotifyAt = now;
                delay = TimeSpan.Zero;
            }
            else
            {
                if (_liveNotifyScheduled)
                {
                    return;
                }

                delay = LiveNotifyThrottle - (now - _lastLiveNotifyAt);
                if (delay < TimeSpan.Zero)
                {
                    delay = TimeSpan.Zero;
                }

                _liveNotifyScheduled = true;
            }
        }

        if (delay == TimeSpan.Zero)
        {
            Notify();
            return;
        }

        _liveNotifySignal.Release();
    }

    private async Task LiveNotifyLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _liveNotifySignal.WaitAsync(ct).ConfigureAwait(false);

                TimeSpan delay;
                lock (_liveNotifyGate)
                {
                    delay = LiveNotifyThrottle - (DateTimeOffset.UtcNow - _lastLiveNotifyAt);
                    if (delay < TimeSpan.Zero)
                    {
                        delay = TimeSpan.Zero;
                    }
                }

                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }

                lock (_liveNotifyGate)
                {
                    _lastLiveNotifyAt = DateTimeOffset.UtcNow;
                    _liveNotifyScheduled = false;
                }

                Notify();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    public void Stop()
    {
        UnsubscribeAndCancel();
    }

    public async ValueTask StopAsync()
    {
        UnsubscribeAndCancel();

        var tasks = new[] { _pollTask, _snapshotRefreshTask, _liveNotifyTask }
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();
        if (tasks.Length > 0)
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        _backgroundCts?.Dispose();
        _backgroundCts = null;
        _pollTask = null;
        _snapshotRefreshTask = null;
        _liveNotifyTask = null;

        lock (_liveNotifyGate)
        {
            _liveNotifyScheduled = false;
        }

        lock (_snapshotRefreshGate)
        {
            if (!_signalsDisposed)
            {
                while (_snapshotRefreshSignal.Wait(0)) { }
            }
        }

        lock (_liveNotifyGate)
        {
            if (!_signalsDisposed)
            {
                while (_liveNotifySignal.Wait(0)) { }
            }
        }
    }

    private void UnsubscribeAndCancel()
    {
        if (_initialized)
        {
            _stateContainer.OnStateChanged -= OnRealtimeStateChanged;
            _stateContainer.IngestionQueueProgressReceived -= OnQueueSnapshotReceived;
            _orchestrator.OnEngineConnectionStateChanged -= OnConnectionStateChanged;
            _initialized = false;
        }

        _backgroundCts?.Cancel();
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        lock (_snapshotRefreshGate)
        {
            lock (_liveNotifyGate)
            {
                if (!_signalsDisposed)
                {
                    _signalsDisposed = true;
                    _snapshotRefreshSignal.Dispose();
                    _liveNotifySignal.Dispose();
                }
            }
        }
        GC.SuppressFinalize(this);
    }

}
