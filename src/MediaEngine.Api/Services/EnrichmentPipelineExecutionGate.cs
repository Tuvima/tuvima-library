namespace MediaEngine.Api.Services;

/// <summary>
/// Coordinates destructive development resets with background enrichment workers.
/// A pause prevents new work from starting and completes only after every active
/// worker has left its database-sensitive section.
/// </summary>
public sealed class EnrichmentPipelineExecutionGate
{
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<long, ActiveWorker> _activeWorkers = [];
    private long _nextLeaseId;
    private bool _paused;
    private CancellationTokenSource _activePeriodCancellation = new();
    private TaskCompletionSource _resumeSignal = CreateCompletedSignal();
    private TaskCompletionSource? _drainedSignal;

    public EnrichmentPipelineExecutionGate()
        : this(TimeProvider.System)
    {
    }

    public EnrichmentPipelineExecutionGate(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Waits until no pause is in force, then registers <paramref name="owner"/> as active until the lease is disposed.
    /// The owner's name is what a timed-out pause reports, so a stuck worker can be found.
    /// </summary>
    public async ValueTask<ExecutionLease> EnterAsync(string owner, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        while (true)
        {
            Task resumeTask;
            lock (_sync)
            {
                if (!_paused)
                {
                    var leaseId = ++_nextLeaseId;
                    _activeWorkers[leaseId] = new ActiveWorker(owner, _timeProvider.GetUtcNow());
                    return new ExecutionLease(this, leaseId, _activePeriodCancellation.Token);
                }

                resumeTask = _resumeSignal.Task;
            }

            await resumeTask.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops new work from starting, asks active workers to stop, and waits for them to leave.
    /// </summary>
    /// <exception cref="TimeoutException">
    /// Thrown when a worker is still active after <paramref name="drainTimeout"/>; the message names each one.
    /// The gate stays paused, so the caller must still call <see cref="Resume"/>.
    /// </exception>
    public async Task PauseAndDrainAsync(TimeSpan drainTimeout, CancellationToken ct = default)
    {
        Task drainedTask;
        CancellationTokenSource? activePeriodCancellation = null;
        lock (_sync)
        {
            if (!_paused)
            {
                _paused = true;
                _resumeSignal = CreateSignal();
                activePeriodCancellation = _activePeriodCancellation;
            }

            drainedTask = _activeWorkers.Count == 0
                ? Task.CompletedTask
                : (_drainedSignal ??= CreateSignal()).Task;
        }

        activePeriodCancellation?.Cancel();
        try
        {
            await drainedTask.WaitAsync(drainTimeout, _timeProvider, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"Background enrichment did not stop within {drainTimeout.TotalSeconds:0} seconds. Still running: {DescribeActiveWorkers()}.");
        }
    }

    public void Resume()
    {
        TaskCompletionSource? resumeSignal;
        CancellationTokenSource? completedPeriodCancellation;
        lock (_sync)
        {
            if (!_paused)
            {
                return;
            }

            _paused = false;
            _drainedSignal = null;
            resumeSignal = _resumeSignal;
            completedPeriodCancellation = _activePeriodCancellation;
            _activePeriodCancellation = new CancellationTokenSource();
        }

        completedPeriodCancellation.Dispose();
        resumeSignal.TrySetResult();
    }

    /// <summary>The active workers, longest-running first, as "Owner (active for N s)".</summary>
    public string DescribeActiveWorkers()
    {
        var now = _timeProvider.GetUtcNow();
        lock (_sync)
        {
            if (_activeWorkers.Count == 0)
            {
                return "none";
            }

            return string.Join(
                ", ",
                _activeWorkers.Values
                    .OrderBy(worker => worker.Since)
                    .Select(worker => $"{worker.Owner} (active for {(now - worker.Since).TotalSeconds:0} s)"));
        }
    }

    private void Exit(long leaseId)
    {
        TaskCompletionSource? drainedSignal = null;
        lock (_sync)
        {
            if (!_activeWorkers.Remove(leaseId))
            {
                throw new InvalidOperationException("An enrichment pipeline execution lease was released more than once.");
            }

            if (_paused && _activeWorkers.Count == 0)
            {
                drainedSignal = _drainedSignal;
            }
        }

        drainedSignal?.TrySetResult();
    }

    private static TaskCompletionSource CreateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource CreateCompletedSignal()
    {
        var signal = CreateSignal();
        signal.TrySetResult();
        return signal;
    }

    private sealed record ActiveWorker(string Owner, DateTimeOffset Since);

    public sealed class ExecutionLease : IDisposable
    {
        private EnrichmentPipelineExecutionGate? _owner;
        private readonly long _leaseId;

        internal ExecutionLease(
            EnrichmentPipelineExecutionGate owner,
            long leaseId,
            CancellationToken pauseCancellationToken)
        {
            _owner = owner;
            _leaseId = leaseId;
            PauseCancellationToken = pauseCancellationToken;
        }

        public CancellationToken PauseCancellationToken { get; }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Exit(_leaseId);
    }
}
