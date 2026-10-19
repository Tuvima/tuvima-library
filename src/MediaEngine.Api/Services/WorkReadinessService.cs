using System.Text.Json;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

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

    // How many times a failed announcement is tried before giving up.
    private const int MaxAnnouncementAttempts = 5;

    // How often to re-check every Work, to catch changes that leave no timestamp behind.
    private static readonly TimeSpan FullSweepInterval = TimeSpan.FromMinutes(10);

    private readonly IWorkReadinessRepository _repository;
    private readonly IIdentityBypassLibraries _bypassLibraries;
    private readonly IEventPublisher _publisher;
    private readonly ISystemActivityRepository _activity;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WorkReadinessService> _logger;

    // First time this service saw a Work matched, placed and not in review but still waiting for artwork.
    // Only the service's single loop touches it.
    private readonly Dictionary<Guid, DateTimeOffset> _matchedAt = [];

    // Works that became Ready for the first time but whose announcement failed, with the attempts made so far.
    private readonly Dictionary<Guid, int> _pendingAnnouncements = [];

    // Works whose "Added" activity row is already written but whose announcement has not finished, so a retried
    // announcement never writes a second row.
    private readonly HashSet<Guid> _activityWritten = [];

    private WorkReadinessWatermark _watermark = WorkReadinessWatermark.Empty;

    public WorkReadinessService(
        IWorkReadinessRepository repository,
        IIdentityBypassLibraries bypassLibraries,
        IEventPublisher publisher,
        ISystemActivityRepository activity,
        TimeProvider timeProvider,
        ILogger<WorkReadinessService> logger)
    {
        _repository = repository;
        _bypassLibraries = bypassLibraries;
        _publisher = publisher;
        _activity = activity;
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
        await RetryPendingAnnouncementsAsync(ct).ConfigureAwait(false);

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
        await RetryPendingAnnouncementsAsync(ct).ConfigureAwait(false);

        var bypassLibraryIds = _bypassLibraries.GetLibraryIds();
        var changes = await _repository.RefreshAllAsync(bypassLibraryIds, ct).ConfigureAwait(false);
        await ReportAsync(changes, ct).ConfigureAwait(false);

        // Forget works that are no longer waiting for artwork (they left review, were removed, or became ready some
        // other way), so the timing table does not grow without bound.
        if (_matchedAt.Count > 0)
        {
            var stillWaiting = (await _repository.FindMatchedAwaitingArtworkAsync(_matchedAt.Keys.ToList(), bypassLibraryIds, ct)
                .ConfigureAwait(false)).ToHashSet();
            foreach (var workId in _matchedAt.Keys.Where(id => !stillWaiting.Contains(id)).ToList())
            {
                _matchedAt.Remove(workId);
            }
        }
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
        }

        // A Work is announced as added once, the first time it is ever Ready; returning to Ready after a review is
        // not announced again.
        var firstReady = changes.Where(change => change.IsReady && change.IsFirstReady).Select(change => change.WorkId).ToList();
        await AnnounceAsync(firstReady, ct).ConfigureAwait(false);
    }

    // Announces the given works. Anything that cannot be announced (the details look-up or the publish failed) is kept
    // and retried on the following passes, up to a limit, so a brief failure does not lose the announcement.
    private async Task AnnounceAsync(IReadOnlyCollection<Guid> workIds, CancellationToken ct)
    {
        if (workIds.Count == 0)
        {
            return;
        }

        IReadOnlyDictionary<Guid, WorkReadinessDetails> details;
        try
        {
            details = await _repository.GetDetailsAsync(workIds, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "The details of {Count} ready works could not be read; announcing them will be retried.", workIds.Count);
            foreach (var workId in workIds)
            {
                Defer(workId);
            }

            return;
        }

        foreach (var workId in workIds)
        {
            if (!details.TryGetValue(workId, out var detail))
            {
                // The work is gone; there is nothing to announce.
                _pendingAnnouncements.Remove(workId);
                _activityWritten.Remove(workId);
                continue;
            }

            // The activity row goes first and is written once; a failed write is deferred like a failed publish.
            if (!await RecordAddedAsync(detail, ct).ConfigureAwait(false))
            {
                Defer(workId);
                continue;
            }

            if (await PublishAddedAsync(detail, ct).ConfigureAwait(false))
            {
                _pendingAnnouncements.Remove(workId);
                _activityWritten.Remove(workId);
            }
            else
            {
                Defer(workId);
            }
        }
    }

    private async Task RetryPendingAnnouncementsAsync(CancellationToken ct)
    {
        if (_pendingAnnouncements.Count > 0)
        {
            await AnnounceAsync(_pendingAnnouncements.Keys.ToList(), ct).ConfigureAwait(false);
        }
    }

    private void Defer(Guid workId)
    {
        var attempts = _pendingAnnouncements.GetValueOrDefault(workId) + 1;
        if (attempts >= MaxAnnouncementAttempts)
        {
            _pendingAnnouncements.Remove(workId);
            _activityWritten.Remove(workId);
            _logger.LogWarning("Work {WorkId} could not be announced after {Attempts} attempts; giving up.", workId, attempts);
            return;
        }

        _pendingAnnouncements[workId] = attempts;
    }

    private async Task<bool> RecordAddedAsync(WorkReadinessDetails detail, CancellationToken ct)
    {
        if (_activityWritten.Contains(detail.WorkId))
        {
            return true;
        }

        try
        {
            await _activity.LogAsync(new SystemActivityEntry
            {
                ActionType = SystemActionType.MediaAdded,
                CollectionName = detail.Title,
                EntityId = detail.WorkId,
                EntityType = "Work",
                ChangesJson = JsonSerializer.Serialize(new { media_type = detail.MediaType }),
                Detail = "Added",
            }, ct).ConfigureAwait(false);
            _activityWritten.Add(detail.WorkId);
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "The Added activity entry for ready work {WorkId} could not be written.", detail.WorkId);
            return false;
        }
    }

    private async Task<bool> PublishAddedAsync(WorkReadinessDetails detail, CancellationToken ct)
    {
        try
        {
            await _publisher.PublishAsync(
                SignalREvents.MediaAdded,
                new MediaAddedEvent(detail.WorkId, detail.CollectionId, detail.MediaType, detail.Title, detail.AssetId),
                ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "MediaAdded event for ready work {WorkId} could not be published.", detail.WorkId);
            return false;
        }
    }
}
