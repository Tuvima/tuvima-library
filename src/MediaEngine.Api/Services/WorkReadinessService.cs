using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services;

/// <summary>
/// Keeps the stored Ready mark (<c>works.ready_at</c>) in step with the one Ready rule and announces each Work
/// the moment it becomes Ready. "Added" means Ready: the <see cref="SignalREvents.MediaAdded"/> event is
/// published from here and nowhere else in the ingestion path.
///
/// It re-checks only the Works whose inputs changed (identity jobs, review rows, artwork values) every couple of
/// seconds, and re-checks every Work every few minutes to catch inputs that carry no timestamp (curator
/// changes, parent placement, files that moved). Startup backfills the mark for the existing library without
/// announcing anything, because those items are already known.
/// </summary>
public sealed class WorkReadinessService : BackgroundService
{
    // How often to look for Works whose readiness inputs changed.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    // How often to re-check every Work, to catch changes that leave no timestamp behind.
    private static readonly TimeSpan FullSweepInterval = TimeSpan.FromMinutes(10);

    private readonly IWorkReadinessRepository _repository;
    private readonly IIdentityBypassLibraries _bypassLibraries;
    private readonly IEventPublisher _publisher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WorkReadinessService> _logger;

    // First time this service saw a Work matched, placed and not in review but still waiting for artwork.
    // Only the service's single loop touches it.
    private readonly Dictionary<Guid, DateTimeOffset> _matchedAt = [];

    private WorkReadinessWatermark _watermark = WorkReadinessWatermark.Empty;

    public WorkReadinessService(
        IWorkReadinessRepository repository,
        IIdentityBypassLibraries bypassLibraries,
        IEventPublisher publisher,
        TimeProvider timeProvider,
        ILogger<WorkReadinessService> logger)
    {
        _repository = repository;
        _bypassLibraries = bypassLibraries;
        _publisher = publisher;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the host finish starting the other services before the first full pass.
        await Task.Yield();

        using var timer = new PeriodicTimer(PollInterval, _timeProvider);
        var lastSweepAt = _timeProvider.GetUtcNow();
        var started = false;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!started)
                    {
                        await BackfillAsync(stoppingToken).ConfigureAwait(false);
                        started = true;
                        lastSweepAt = _timeProvider.GetUtcNow();
                    }
                    else
                    {
                        await PollAsync(stoppingToken).ConfigureAwait(false);
                        if (_timeProvider.GetUtcNow() - lastSweepAt >= FullSweepInterval)
                        {
                            await SweepAsync(stoppingToken).ConfigureAwait(false);
                            lastSweepAt = _timeProvider.GetUtcNow();
                        }
                    }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Work readiness check failed; it will be retried on the next pass.");
                }

                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }
    }

    /// <summary>
    /// Establishes the starting point for change detection, then backfills the Ready mark for every Work.
    /// Nothing is announced: the library's existing items are already known to clients.
    /// </summary>
    public async Task BackfillAsync(CancellationToken ct)
    {
        // Take the starting point first so anything that changes during the full pass is seen by the next poll.
        var (_, next) = await _repository.FindTouchedWorkIdsAsync(WorkReadinessWatermark.Empty, ct).ConfigureAwait(false);
        var changes = await _repository.RefreshAllAsync(_bypassLibraries.GetLibraryIds(), ct).ConfigureAwait(false);
        _watermark = next;

        var ready = changes.Count(change => change.IsReady);
        var notReady = changes.Count - ready;
        _logger.LogInformation(
            "Work readiness backfilled at startup: {Ready} marked ready, {NotReady} cleared.", ready, notReady);
    }

    /// <summary>One pass over the Works whose readiness inputs changed since the last pass.</summary>
    public async Task PollAsync(CancellationToken ct)
    {
        var (workIds, next) = await _repository.FindTouchedWorkIdsAsync(_watermark, ct).ConfigureAwait(false);
        if (workIds.Count > 0)
        {
            // Read each pass so a changed library policy applies from the next pass on. If the configuration cannot
            // be read the pass fails and is retried; guessing an empty list would wrongly clear local-only works.
            var bypassLibraryIds = _bypassLibraries.GetLibraryIds();
            await NoteMatchedAwaitingArtworkAsync(workIds, bypassLibraryIds, ct).ConfigureAwait(false);
            var changes = await _repository.RefreshAsync(workIds, bypassLibraryIds, ct).ConfigureAwait(false);
            await ReportAsync(changes, ct).ConfigureAwait(false);
        }

        _watermark = next;
    }

    /// <summary>Re-checks every Work, catching inputs that carry no timestamp.</summary>
    public async Task SweepAsync(CancellationToken ct)
    {
        var changes = await _repository.RefreshAllAsync(_bypassLibraries.GetLibraryIds(), ct).ConfigureAwait(false);
        await ReportAsync(changes, ct).ConfigureAwait(false);
    }

    private async Task NoteMatchedAwaitingArtworkAsync(
        IReadOnlyCollection<Guid> workIds, IReadOnlyCollection<string> bypassLibraryIds, CancellationToken ct)
    {
        var awaiting = await _repository.FindMatchedAwaitingArtworkAsync(workIds, bypassLibraryIds, ct).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        foreach (var workId in awaiting)
        {
            _matchedAt.TryAdd(workId, now);
        }
    }

    private async Task ReportAsync(IReadOnlyList<WorkReadinessChange> changes, CancellationToken ct)
    {
        if (changes.Count == 0)
        {
            return;
        }

        var becameReady = changes.Where(change => change.IsReady).ToList();
        var details = becameReady.Count == 0
            ? new Dictionary<Guid, WorkReadinessDetails>()
            : (await _repository.GetDetailsAsync(becameReady.Select(change => change.WorkId).ToList(), ct)
                .ConfigureAwait(false)).ToDictionary(pair => pair.Key, pair => pair.Value);

        foreach (var change in changes)
        {
            if (!change.IsReady)
            {
                _matchedAt.Remove(change.WorkId);
                _logger.LogInformation("Work {WorkId} is no longer ready ({MediaType})", change.WorkId, change.MediaType);
                continue;
            }

            if (_matchedAt.Remove(change.WorkId, out var matchedAt))
            {
                _logger.LogInformation(
                    "Work {WorkId} is ready ({MediaType}) {Seconds:0.0}s after it was matched",
                    change.WorkId, change.MediaType, Math.Max(0, (change.At - matchedAt).TotalSeconds));
            }
            else
            {
                _logger.LogInformation("Work {WorkId} is ready ({MediaType})", change.WorkId, change.MediaType);
            }

            details.TryGetValue(change.WorkId, out var detail);
            await PublishAddedAsync(change, detail, ct).ConfigureAwait(false);
        }
    }

    private async Task PublishAddedAsync(WorkReadinessChange change, WorkReadinessDetails? detail, CancellationToken ct)
    {
        try
        {
            await _publisher.PublishAsync(
                SignalREvents.MediaAdded,
                new MediaAddedEvent(
                    change.WorkId,
                    detail?.CollectionId,
                    detail?.MediaType ?? change.MediaType,
                    detail?.Title ?? string.Empty,
                    detail?.AssetId),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "MediaAdded event for ready work {WorkId} could not be published.", change.WorkId);
        }
    }
}
