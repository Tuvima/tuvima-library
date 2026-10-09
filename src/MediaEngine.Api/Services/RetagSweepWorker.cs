using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion.Services;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services;

/// <summary>
/// Background service that walks the library and re-applies file-level tag
/// metadata whenever <c>writeback-fields.json</c> (or a tagger version) has
/// rotated the per-media-type writeback hash.
///
/// <para>Triggers:</para>
/// <list type="bullet">
///   <item>Startup (after a short delay) — catches any drift that happened while the app was down.</item>
///   <item>Cron (default 3 AM daily) — periodic re-scan.</item>
///   <item><see cref="WritebackConfigState.PendingApplied"/> — immediate sweep after the user clicks Apply on a staged diff.</item>
/// </list>
///
/// <para>Uses <see cref="RetagFailureClassifier"/> to decide between retry
/// (transient) and Action Center routing (terminal). Transient failures are
/// re-scheduled for the next off-hours window; after
/// <see cref="RetagSweepSettings.MaxRetryAttempts"/> attempts they are promoted
/// to terminal and a <see cref="ReviewTrigger.WritebackFailed"/> review item
/// is created.</para>
/// </summary>
public sealed class RetagSweepWorker : BackgroundService
{
    private readonly IMediaAssetRepository _assetRepo;
    private readonly IWriteBackService _writeBackService;
    private readonly IReviewQueueRepository _reviewRepo;
    private readonly IConfigurationLoader _configLoader;
    private readonly IEventPublisher _eventPublisher;
    private readonly WritebackConfigState _hashState;
    private readonly ILogger<RetagSweepWorker> _logger;

    /// <summary>Cron expression fallback when <c>maintenance.json</c> is missing or unparseable.</summary>
    private const string DefaultSchedule = "0 3 * * *";

    /// <summary>Signal raised when <see cref="WritebackConfigState.PendingApplied"/> fires.</summary>
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);

    public RetagSweepWorker(
        IMediaAssetRepository assetRepo,
        IWriteBackService writeBackService,
        IReviewQueueRepository reviewRepo,
        IConfigurationLoader configLoader,
        IEventPublisher eventPublisher,
        WritebackConfigState hashState,
        ILogger<RetagSweepWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(assetRepo);
        ArgumentNullException.ThrowIfNull(writeBackService);
        ArgumentNullException.ThrowIfNull(reviewRepo);
        ArgumentNullException.ThrowIfNull(configLoader);
        ArgumentNullException.ThrowIfNull(eventPublisher);
        ArgumentNullException.ThrowIfNull(hashState);
        ArgumentNullException.ThrowIfNull(logger);

        _assetRepo = assetRepo;
        _writeBackService = writeBackService;
        _reviewRepo = reviewRepo;
        _configLoader = configLoader;
        _eventPublisher = eventPublisher;
        _hashState = hashState;
        _logger = logger;

        _hashState.PendingApplied += OnPendingApplied;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RetagSweepWorker started");

        // Let the rest of the app finish starting before the first scan.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSweepAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RetagSweepWorker: sweep pass failed; will retry on next cycle");
            }

            // Wait until the next cron tick OR an Apply signal, whichever comes first.
            var schedule = LoadSchedule();
            var cronDelay = CronScheduler.UntilNext(schedule, TimeSpan.FromHours(24));
            _logger.LogInformation("Next retag sweep at {NextRun}", DateTimeOffset.Now.Add(cronDelay));

            var waitTask = _wakeSignal.WaitAsync(stoppingToken);
            var cronTask = Task.Delay(cronDelay, stoppingToken);
            var finished = await Task.WhenAny(waitTask, cronTask);
            if (finished == waitTask)
            {
                _logger.LogInformation("RetagSweepWorker: woken by Apply signal");
            }
        }
    }

    // ── Internals ──────────────────────────────────────────────────────────

    private async Task RunSweepAsync(CancellationToken ct)
    {
        var settings = LoadSettings();

        if (!settings.Enabled)
        {
            _logger.LogDebug("RetagSweepWorker: sweep disabled in settings — skipping");
            return;
        }

        // While a pending diff is awaiting Apply, the current hashes still
        // reflect the prior field list and there's nothing new to sweep.
        if (_hashState.HasPendingDiff)
        {
            _logger.LogDebug("RetagSweepWorker: pending diff is unapproved — waiting for Apply");
            return;
        }

        var expected = _hashState.CurrentHashes;
        if (expected.Count == 0)
        {
            _logger.LogDebug("RetagSweepWorker: no current writeback hashes — skipping");
            return;
        }

        int totalProcessed = 0, totalVerified = 0, totalCompletedUnverified = 0, totalTransient = 0, totalTerminal = 0;
        Guid? afterAssetId = null;

        while (!ct.IsCancellationRequested)
        {
            var nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var batch = await _assetRepo.GetStaleForRetagAsync(expected, settings.BatchSize, nowEpoch, ct,
                afterAssetId);
            if (batch.Count == 0)
            {
                _logger.LogDebug("RetagSweepWorker: no stale assets remain");
                break;
            }

            _logger.LogInformation("RetagSweepWorker: processing batch of {Count} stale asset(s)", batch.Count);

            foreach (var stale in batch)
            {
                ct.ThrowIfCancellationRequested();
                // Advance even if policy or missing data makes the service skip
                // this asset. Such a skip must never hot-loop in one pass.
                afterAssetId = stale.AssetId;
                totalProcessed++;

                // Skip files still in staging — writeback is meaningless there
                // and creates blocking reviews that prevent promotion.
                if (stale.FilePathRoot?.Contains(".data/staging", StringComparison.OrdinalIgnoreCase) == true
                    || stale.FilePathRoot?.Contains(".data\\staging", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _logger.LogDebug("RetagSweepWorker: skipping staged asset {Id} at {Path}", stale.AssetId, stale.FilePathRoot);
                    continue;
                }

                try
                {
                    await _writeBackService.WriteMetadataAsync(stale.AssetId, "config_change", ct);
                    // A completed adapter call lacks complete metadata read-back;
                    // it cannot resolve a failure review or count as verified.
                    totalCompletedUnverified++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (ex is NotSupportedException or FormatException)
                    {
                        if (expected.TryGetValue(stale.MediaType, out var expectedHash))
                        {
                            await _assetRepo.MarkWritebackUnsupportedAsync(stale.AssetId, expectedHash, ex.Message, ct);
                        }
                        totalTerminal++;
                        _logger.LogInformation("RetagSweepWorker: unsupported write-back request for {Path}: {Reason}",
                            stale.FilePathRoot, ex.Message);
                        continue;
                    }
                    var outcome = RetagFailureClassifier.Classify(ex);
                    var attempts = stale.Attempts + 1;

                    if (RetagFailureClassifier.IsTransient(outcome) && attempts < settings.MaxRetryAttempts)
                    {
                        var nextRetry = ComputeNextRetryEpoch(settings);
                        await _assetRepo.ScheduleRetagRetryAsync(stale.AssetId, nextRetry, ex.Message, ct);
                        totalTransient++;
                        _logger.LogDebug(
                            "RetagSweepWorker: transient failure ({Outcome}) for {Path} — retry {Attempt}/{Max} at {NextRun}",
                            outcome, stale.FilePathRoot, attempts, settings.MaxRetryAttempts,
                            DateTimeOffset.FromUnixTimeSeconds(nextRetry));
                    }
                    else
                    {
                        await _assetRepo.MarkRetagFailedAsync(stale.AssetId, ex.Message, ct);
                        await InsertReviewItemAsync(stale, ex.Message, outcome, ct);
                        totalTerminal++;
                        _logger.LogWarning(ex,
                            "RetagSweepWorker: terminal failure ({Outcome}) for {Path} — routed to Action Center",
                            outcome, stale.FilePathRoot);
                    }
                }

                // Small delay between writes so we don't flood disk IO.
                if (settings.BatchDelayMs > 0)
                {
                    try { await Task.Delay(settings.BatchDelayMs, ct); }
                    catch (OperationCanceledException) { throw; }
                }

                // Progress ping every 10 files so the Dashboard can show a live counter.
                if (totalProcessed % 10 == 0)
                {
                    await EmitProgressAsync(totalProcessed, totalVerified, totalTransient, totalTerminal, isFinal: false, ct);
                }
            }
        }

        await EmitProgressAsync(totalProcessed, totalVerified, totalTransient, totalTerminal, isFinal: true, ct);

        if (totalProcessed > 0)
        {
            _logger.LogInformation(
                    "RetagSweepWorker: pass complete — processed {Processed}, completed without verification {Unverified}, verified {Verified}, retry {Retry}, unsupported/failed {Terminal}",
                    totalProcessed, totalCompletedUnverified, totalVerified, totalTransient, totalTerminal);
        }

        try
        {
            await _eventPublisher.PublishAsync(
                SignalREvents.RetagSweepCompleted,
                new RetagSweepCompletedEvent(totalProcessed, totalVerified, totalTransient, totalTerminal),
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "RetagSweepWorker: completed event publish failed");
        }
    }

    private async Task EmitProgressAsync(int processed, int succeeded, int transient, int terminal, bool isFinal, CancellationToken ct)
    {
        try
        {
            await _eventPublisher.PublishAsync(
                SignalREvents.RetagSweepProgress,
                new RetagSweepProgressEvent(processed, succeeded, transient, terminal, isFinal),
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "RetagSweepWorker: progress event publish failed");
        }
    }

    private async Task ResolveWritebackReviewsAsync(Guid assetId, CancellationToken ct)
    {
        var pending = await _reviewRepo.GetPendingByEntityAsync(assetId, ct);
        foreach (var entry in pending.Where(item => item.Trigger == ReviewTrigger.WritebackFailed))
        {
            await _reviewRepo.UpdateStatusAsync(entry.Id, ReviewStatus.Resolved, "writeback worker", ct);
            await _eventPublisher.PublishAsync(
                SignalREvents.ReviewItemResolved,
                new ReviewItemResolvedSupplementaryEvent(entry.Id, assetId, ReviewStatus.Resolved),
                ct);
        }
    }

    private async Task InsertReviewItemAsync(StaleRetagAsset stale, string error, RetagFailureClassifier.Outcome outcome, CancellationToken ct)
    {
        var entry = new ReviewQueueEntry
        {
            Id = Guid.NewGuid(),
            EntityId = stale.AssetId,
            EntityType = "MediaAsset",
            Trigger = ReviewTrigger.WritebackFailed,
            Status = ReviewStatus.Pending,
            Detail = $"Re-tag failed ({outcome}): {error}",
            CreatedAt = DateTimeOffset.UtcNow,
            ReviewReadyAt = DateTimeOffset.UtcNow,
            AutomationCompletedAt = DateTimeOffset.UtcNow,
        };

        try
        {
            await _reviewRepo.InsertAsync(entry, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "RetagSweepWorker: could not insert review item for {AssetId}", stale.AssetId);
        }
    }

    /// <summary>
    /// Next off-hours window start, expressed as a unix epoch. If we're already
    /// inside the window, the retry fires in 30 minutes (so sibling locked
    /// files don't all retry at the same instant).
    /// </summary>
    private static long ComputeNextRetryEpoch(RetagSweepSettings settings)
    {
        if (!TimeSpan.TryParse(settings.OffHoursStart, out var start))
        {
            start = new TimeSpan(2, 0, 0);
        }

        if (!TimeSpan.TryParse(settings.OffHoursEnd, out var end))
        {
            end = new TimeSpan(6, 0, 0);
        }

        var now = DateTime.Now;
        var todayStart = now.Date + start;
        var todayEnd = now.Date + end;

        if (now >= todayStart && now <= todayEnd)
        {
            return new DateTimeOffset(now.AddMinutes(30)).ToUnixTimeSeconds();
        }

        var next = now < todayStart ? todayStart : todayStart.AddDays(1);
        return new DateTimeOffset(next).ToUnixTimeSeconds();
    }

    private RetagSweepSettings LoadSettings()
    {
        try
        {
            return _configLoader.LoadMaintenance().RetagSweep ?? new RetagSweepSettings();
        }
        catch
        {
            return new RetagSweepSettings();
        }
    }

    private string LoadSchedule()
    {
        try
        {
            var maintenance = _configLoader.LoadMaintenance();
            var cron = maintenance.Schedules.GetValueOrDefault("retag_sweep", DefaultSchedule);
            return string.IsNullOrWhiteSpace(cron) ? DefaultSchedule : cron;
        }
        catch
        {
            return DefaultSchedule;
        }
    }

    private void OnPendingApplied()
    {
        // Non-blocking release — if the semaphore is already signaled we drop the new one.
        try { _wakeSignal.Release(); }
        catch (SemaphoreFullException) { /* already signaled */ }
    }

    public override void Dispose()
    {
        _hashState.PendingApplied -= OnPendingApplied;
        _wakeSignal.Dispose();
        base.Dispose();
    }
}
