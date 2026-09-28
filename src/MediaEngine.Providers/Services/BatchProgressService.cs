using System.Collections.Concurrent;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Manages batch counter adjustments and SignalR progress emission.
/// Extracted from HydrationPipelineService for single-responsibility and reuse by pipeline workers.
/// </summary>
public sealed class BatchProgressService
{
    private static readonly TimeSpan MinimumEmitInterval = TimeSpan.FromSeconds(1);

    private readonly IIngestionBatchRepository _batchRepo;
    private readonly IEventPublisher _eventPublisher;
    private readonly ILogger<BatchProgressService> _logger;
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastProgressEmitUtc = new();

    public BatchProgressService(
        IIngestionBatchRepository batchRepo,
        IEventPublisher eventPublisher,
        ILogger<BatchProgressService> logger)
    {
        _batchRepo = batchRepo;
        _eventPublisher = eventPublisher;
        _logger = logger;
    }

    /// <summary>
    /// Refreshes the live batch snapshot after a file settles into review.
    /// </summary>
    public async Task ShiftToReviewAsync(Guid? batchId, CancellationToken ct)
    {
        if (batchId is null)
        {
            return;
        }

        try
        {
            await EmitProgressAsync(batchId.Value, isFinal: false, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Batch review adjustment failed for {BatchId}", batchId);
        }
    }

    /// <summary>
    /// Refreshes the live batch snapshot after a file leaves review.
    /// </summary>
    public async Task ShiftToIdentifiedAsync(Guid? batchId, CancellationToken ct)
    {
        if (batchId is null)
        {
            return;
        }

        try
        {
            await EmitProgressAsync(batchId.Value, isFinal: false, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Batch resolve adjustment failed for {BatchId}", batchId);
        }
    }

    /// <summary>
    /// Fetches the current batch counters and broadcasts a BatchProgress SignalR event.
    /// Best-effort — never throws.
    /// </summary>
    public async Task EmitProgressAsync(Guid batchId, bool isFinal, CancellationToken ct)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (!isFinal
                && _lastProgressEmitUtc.TryGetValue(batchId, out var lastEmit)
                && now - lastEmit < MinimumEmitInterval)
            {
                return;
            }

            _lastProgressEmitUtc[batchId] = now;

            var batch = await _batchRepo.GetByIdAsync(batchId, ct).ConfigureAwait(false);
            if (batch is null)
            {
                return;
            }

            var progress = await GetProgressAsync(batchId, ct).ConfigureAwait(false);
            if (progress is null)
            {
                return;
            }

            var completed = progress.IsComplete;
            if (completed && !string.Equals(batch.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                var finalStatus = progress.FilesFailed >= Math.Max(1, progress.FilesTotal) ? "failed" : "completed";
                await _batchRepo.CompleteAsync(batchId, finalStatus, ct).ConfigureAwait(false);
            }
            await _eventPublisher.PublishAsync(SignalREvents.BatchProgress, progress, ct).ConfigureAwait(false);

            if (isFinal || completed)
            {
                _lastProgressEmitUtc.TryRemove(batchId, out _);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Batch progress emission failed for {BatchId}", batchId);
        }
    }

    public async Task<BatchProgressEvent?> GetProgressAsync(Guid batchId, CancellationToken ct = default)
    {
        var batch = await _batchRepo.GetByIdAsync(batchId, ct).ConfigureAwait(false);
        if (batch is null)
        {
            return null;
        }

        var snapshot = await _batchRepo.GetProgressSnapshotAsync(batchId, ct).ConfigureAwait(false);
        var total = Math.Max(batch.FilesTotal, snapshot.TotalJobs + snapshot.FilesSkipped);
        var failed = snapshot.TotalJobs > 0
            ? Math.Max(0, snapshot.PipelineFailed)
            : Math.Max(0, batch.FilesFailed);
        var ready = snapshot.FilesReady;
        var readyWithoutUniverse = snapshot.FilesReadyWithoutUniverse;
        var identified = ready + readyWithoutUniverse;
        var review = snapshot.FilesReview;
        var noMatch = snapshot.FilesNoMatch;
        var active = snapshot.RetailSearching
            + snapshot.BridgeSearching
            + snapshot.Hydrating
            + snapshot.UniverseEnriching;
        active = Math.Max(active, snapshot.ActiveOperations);
        var terminal = identified + review + noMatch + failed + snapshot.FilesSkipped;
        if (total > 0)
        {
            terminal = Math.Clamp(terminal, 0, total);
            active = Math.Clamp(active, 0, Math.Max(0, total - terminal));
        }

        var queued = snapshot.QueuedJobs + snapshot.RetailMatched + snapshot.QidResolved;
        queued = Math.Max(queued, snapshot.QueuedOperations + snapshot.RetryWaitingOperations);

        // File intake and identity jobs have different units: an audiobook can
        // register many files but intentionally produce only one identity job.
        var progressed = Math.Clamp(Math.Max(batch.FilesProcessed, terminal), 0, total);
        var identitySettled = identified + review + noMatch + failed;
        var intakeComplete = total > 0 && progressed >= total;
        var phaseTotal = intakeComplete ? snapshot.TotalJobs : total;
        var phaseCompleted = intakeComplete ? identitySettled : progressed;
        var pct = phaseTotal > 0 ? (int)Math.Round(Math.Clamp(phaseCompleted * 100d / phaseTotal, 0, 100)) : 0;
        var completed = intakeComplete
            && identitySettled >= snapshot.TotalJobs
            && active == 0
            && snapshot.OutstandingOperations == 0;

        var finishingBackgroundWork = intakeComplete && identitySettled >= snapshot.TotalJobs
            && snapshot.OutstandingOperations > 0;

        int? etaSecs = null;
        if (progressed > 0 && queued > 0)
        {
            var elapsed = (DateTimeOffset.UtcNow - batch.StartedAt).TotalSeconds;
            var rate = elapsed > 0 ? progressed / elapsed : 0;
            if (rate > 0)
            {
                etaSecs = (int)Math.Round(queued / rate);
            }
        }

        var lifecycleStage = ResolveLifecycleStage(snapshot, queued, review, completed);
        var currentStage = finishingBackgroundWork ? "Finishing enrichment" : ResolveStageLabel(lifecycleStage, completed);

        return new BatchProgressEvent(
                batch.Id,
                total,
                progressed,
                identified,
                review,
                noMatch,
                failed,
                pct,
                etaSecs,
                completed,
                CurrentStage: currentStage,
                FilesQueued: queued,
                FilesActive: active,
                FilesReady: ready,
                FilesReadyWithoutUniverse: readyWithoutUniverse,
                CurrentFileTitle: snapshot.CurrentFileTitle,
                LifecycleStage: lifecycleStage,
                WorkUnitsTotal: phaseTotal,
                WorkUnitsCompleted: phaseCompleted,
                ProgressIsIndeterminate: finishingBackgroundWork);
    }

    private static string ResolveLifecycleStage(
        IngestionBatchProgressSnapshot snapshot,
        int queued,
        int review,
        bool completed)
    {
        if (completed)
        {
            return "Complete";
        }

        if (snapshot.UniverseEnriching > 0)
        {
            return "Enriching";
        }

        if (snapshot.OutstandingOperations > 0 && snapshot.QueuedJobs + snapshot.RetailSearching
            + snapshot.RetailMatched + snapshot.BridgeSearching + snapshot.QidResolved + snapshot.Hydrating == 0)
            return "Enriching";

        if (snapshot.Hydrating > 0)
        {
            return "Hydrating";
        }

        if (snapshot.BridgeSearching > 0 || snapshot.QidResolved > 0)
        {
            return "ResolvingUniverse";
        }

        if (snapshot.RetailSearching > 0 || snapshot.RetailMatched > 0 || snapshot.RetailMatchedNeedsReview > 0)
        {
            return "Identifying";
        }

        if (review > 0)
        {
            return "Review";
        }

        if (queued > 0)
        {
            return "Queued";
        }

        return "Processing";
    }

    private static string ResolveStageLabel(string lifecycleStage, bool completed) =>
        completed ? "Complete" :
        lifecycleStage switch
        {
            "ResolvingUniverse" => "Resolving universe",
            "Hydrating" => "Retail Match",
            "Enriching" => "Universe enrichment",
            "Identifying" => "Identifying",
            "Review" => "Review",
            "Queued" => "Queued",
            _ => "Processing",
        };

}
