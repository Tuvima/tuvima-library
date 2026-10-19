using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Background worker that processes deferred Pass 2 (Universe Lookup)
/// enrichment requests.
///
/// <list type="bullet">
///   <item><b>Idle detection:</b> queries <see cref="IIdentityJobRepository.CountActiveAsync"/>
///     every <c>pass2_idle_delay_seconds</c>. Processes only when the identity pipeline
///     has no active jobs (count = 0), yielding to Pass 1 ingestion.</item>
///   <item><b>Rate limiting:</b> delays <c>pass2_rate_limit_ms</c> between each item
///     to respect external API rate limits.</item>
///   <item><b>Manual trigger:</b> <see cref="TriggerImmediateProcessingAsync"/> bypasses
///     idle detection for user-initiated processing.</item>
///   <item><b>Nightly sweep:</b> processes stale items (older than
///     <c>pass2_stale_threshold_hours</c>) on a configurable schedule.</item>
/// </list>
///
/// The Engine owns the worker through the hosted-service lifecycle, and the
/// command interface resolves the same singleton instance.
///
/// Spec: §3.24 — Two-Pass Enrichment Architecture.
/// </summary>
public sealed class DeferredEnrichmentService : BackgroundService, IDeferredEnrichmentService
{
    private readonly IDeferredEnrichmentRepository _repo;
    private readonly IHydrationPipelineService _pipeline;
    private readonly IIdentityJobRepository _jobRepo;
    private readonly IConfigurationLoader _config;
    private readonly IEntityTimelineRepository _timelineRepo;
    private readonly ILogger<DeferredEnrichmentService> _logger;
    private readonly IRecurringTaskRecorder? _recorder;

    // Same key as the maintenance schedule and the Scheduled tasks list (ScheduledTaskKeys.NightlyEnrichmentSweep).
    private const string NightlySweepTaskKey = "pass2_nightly_sweep";

    /// <summary>Signalled when the user clicks "Run Pass 2 Now".</summary>
    private readonly ManualResetEventSlim _manualTrigger = new(false);

    /// <summary>Tracks the last nightly sweep execution to avoid re-running within the same hour.</summary>
    private DateTime _lastNightlyRun = DateTime.MinValue;

    public DeferredEnrichmentService(
        IDeferredEnrichmentRepository repo,
        IHydrationPipelineService pipeline,
        IIdentityJobRepository jobRepo,
        IConfigurationLoader config,
        IEntityTimelineRepository timelineRepo,
        ILogger<DeferredEnrichmentService> logger,
        IRecurringTaskRecorder? recorder = null)
    {
        _recorder = recorder;
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _jobRepo = jobRepo ?? throw new ArgumentNullException(nameof(jobRepo));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _timelineRepo = timelineRepo ?? throw new ArgumentNullException(nameof(timelineRepo));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<int> GetPendingCountAsync(CancellationToken ct = default)
    {
        try
        {
            return await _repo.CountPendingAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read deferred enrichment pending count");
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<int> TriggerImmediateProcessingAsync(CancellationToken ct = default)
    {
        var count = await _repo.CountPendingAsync(ct).ConfigureAwait(false);
        if (count > 0)
        {
            _logger.LogInformation("Manual Pass 2 trigger: {Count} pending items", count);
            _manualTrigger.Set();
        }
        return count;
    }

    // ── Background loop ────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Short startup delay to let the rest of the app initialise.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _logger.LogInformation("DeferredEnrichmentService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settings = LoadSettings();

                if (!settings.TwoPassEnabled)
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                // ── Manual trigger check ──────────────────────────────────
                if (_manualTrigger.IsSet)
                {
                    _manualTrigger.Reset();
                    _logger.LogInformation("Processing Pass 2 queue (manual trigger)");
                    await ProcessBatchAsync(settings, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                // ── Nightly sweep check ───────────────────────────────────
                if (IsNightlySweepDue(settings))
                {
                    _logger.LogInformation("Nightly sweep triggered for stale Pass 2 items");
                    _lastNightlyRun = DateTime.Now;
                    if (_recorder is null)
                    {
                        await ProcessStaleAsync(settings, stoppingToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await _recorder.RecordAsync(
                                NightlySweepTaskKey,
                                async token =>
                                {
                                    await ProcessStaleAsync(settings, token).ConfigureAwait(false);
                                    return null;
                                },
                                stoppingToken)
                            .ConfigureAwait(false);
                    }

                    continue;
                }

                // ── Idle check ────────────────────────────────────────────
                if (await _jobRepo.CountActiveAsync(stoppingToken).ConfigureAwait(false) == 0)
                {
                    var pending = await _repo.CountPendingAsync(stoppingToken).ConfigureAwait(false);
                    if (pending > 0)
                    {
                        _logger.LogDebug("Pipeline idle, processing Pass 2 queue ({Count} pending)", pending);
                        await ProcessBatchAsync(settings, stoppingToken).ConfigureAwait(false);
                        continue;
                    }
                }

                // Nothing to do — wait before next check.
                var delay = Math.Max(settings.Pass2IdleDelaySeconds, 1);
                await Task.Delay(TimeSpan.FromSeconds(delay), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DeferredEnrichmentService loop error");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("DeferredEnrichmentService stopped");
    }

    // ── Processing ─────────────────────────────────────────────────────────────

    private async Task ProcessBatchAsync(HydrationSettings settings, CancellationToken ct)
    {
        var batch = await _repo.GetPendingAsync(settings.Pass2BatchSize, ct).ConfigureAwait(false);
        if (batch.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Pass 2 batch: {Count} items", batch.Count);
        var processed = 0;

        foreach (var item in batch)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            // Pause if Pass 1 work arrived (ingestion takes priority).
            if (await _jobRepo.CountActiveAsync(ct).ConfigureAwait(false) > 0 && !_manualTrigger.IsSet)
            {
                _logger.LogInformation(
                    "Pass 2 paused — Pass 1 work arrived ({Processed}/{Total} processed)",
                    processed, batch.Count);
                break;
            }

            await ProcessOneAsync(item, ct).ConfigureAwait(false);
            processed++;

            // Rate limiting between items.
            if (settings.Pass2RateLimitMs > 0 && !ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(settings.Pass2RateLimitMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("Pass 2 batch complete: {Processed}/{Total} processed",
            processed, batch.Count);
    }

    private async Task ProcessStaleAsync(HydrationSettings settings, CancellationToken ct)
    {
        var threshold = TimeSpan.FromHours(Math.Max(settings.Pass2StaleThresholdHours, 1));
        var allStale = await _repo.GetStaleAsync(threshold, settings.Pass2BatchSize, ct).ConfigureAwait(false);

        // Filter out items waiting for down providers — recovery flush handles those.
        var stale = allStale
            .Where(i => i.FailureType != ProviderFailureType.ProviderDown)
            .ToList();

        if (stale.Count == 0)
        {
            _logger.LogDebug("Nightly sweep: no stale items");
            return;
        }

        _logger.LogInformation("Nightly sweep: {Count} stale items", stale.Count);

        foreach (var item in stale)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            await ProcessOneAsync(item, ct).ConfigureAwait(false);

            if (settings.Pass2RateLimitMs > 0 && !ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(settings.Pass2RateLimitMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task ProcessOneAsync(DeferredEnrichmentRequest item, CancellationToken ct)
    {
        // Skip items waiting for a provider that's still down.
        // The ProviderHealthMonitorService handles recovery flush for these.
        if (item.FailureType == ProviderFailureType.ProviderDown
            && !string.IsNullOrEmpty(item.FailedProviderName))
        {
            _logger.LogDebug("Skipping entity {EntityId} — waiting for provider {Provider}",
                item.EntityId, item.FailedProviderName);
            return;
        }

        try
        {
            var hints = DeserializeHints(item.HintsJson);

            var request = new HarvestRequest
            {
                EntityId = item.EntityId,
                EntityType = EntityType.MediaAsset,
                MediaType = item.MediaType,
                Hints = hints,
                PreResolvedQid = item.WikidataQid,
                Pass = HydrationPass.Universe,
                SuppressActivityEntry = true,
            };

            await _pipeline.RunSynchronousAsync(request, ct).ConfigureAwait(false);
            await _repo.MarkProcessedAsync(item.Id, ct).ConfigureAwait(false);

            // Timeline: record the 30-day refresh completion.
            try
            {
                await _timelineRepo.InsertEventAsync(new EntityEvent
                {
                    EntityId = item.EntityId,
                    EntityType = "Work",
                    EventType = "stage2_refresh",
                    Stage = 2,
                    Trigger = "30_day_refresh",
                    ResolvedQid = item.WikidataQid,
                    Detail = $"Pass 2 refresh complete (QID: {item.WikidataQid ?? "none"})",
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Failed to write stage2_refresh timeline event for entity {EntityId}",
                    item.EntityId);
            }

            _logger.LogDebug("Pass 2 complete for entity {EntityId} (QID: {Qid})",
                item.EntityId, item.WikidataQid ?? "none");
        }
        catch (OperationCanceledException)
        {
            // Propagate cancellation — don't swallow it.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pass 2 failed for entity {EntityId}", item.EntityId);
            // Leave as Pending — will be retried on next cycle.
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private HydrationSettings LoadSettings()
    {
        try
        {
            return _config.LoadHydration();
        }
        catch
        {
            return new HydrationSettings();
        }
    }

    /// <summary>
    /// Simple nightly sweep check: parses hour and minute from the cron expression
    /// and checks if the current local time is within a 10-minute window after the
    /// scheduled time, and we haven't already run this hour today.
    /// </summary>
    private bool IsNightlySweepDue(HydrationSettings settings)
    {
        try
        {
            var maintenance = _config.LoadMaintenance();
            var cronExpr = maintenance.Schedules.TryGetValue("pass2_nightly_sweep", out var s)
                ? s
                : "0 2 * * *";
            var parts = cronExpr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                return false;
            }

            if (!int.TryParse(parts[0], out var minute) || !int.TryParse(parts[1], out var hour))
            {
                return false;
            }

            var now = DateTime.Now;

            // Check if we're in the right hour and minute window (within 10 minutes).
            if (now.Hour != hour)
            {
                return false;
            }

            if (now.Minute < minute || now.Minute > minute + 9)
            {
                return false;
            }

            // Don't re-run if we already ran this hour today.
            if (_lastNightlyRun.Date == now.Date && _lastNightlyRun.Hour == now.Hour)
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyDictionary<string, string> DeserializeHints(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (dict is null)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            // Rebuild with case-insensitive comparer.
            return new Dictionary<string, string>(dict, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public override void Dispose()
    {
        _manualTrigger.Dispose();
        base.Dispose();
    }
}
