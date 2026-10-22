using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services;

/// <summary>
/// Stage 3 universe enrichment coordinator.
///
/// Handles two modes:
/// 1. Inline, coalesced batches queued immediately after quick hydration.
/// 2. Maintenance sweeps for missed or stale items.
///
/// File-level completion is gated by the core Stage 3 work only. Enhancers run
/// after that and do not hold the primary batch queue open.
/// </summary>
public sealed class UniverseEnrichmentService : BackgroundService, IUniverseEnrichmentScheduler
{
    private static readonly TimeSpan InlineSilenceWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LoopInterval = TimeSpan.FromSeconds(5);
    private const int InlineBatchMaxWorks = 50;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ScheduledTaskRunner _runner;
    private readonly ILogger<UniverseEnrichmentService> _logger;
    private readonly ManualResetEventSlim _manualTrigger = new(initialState: false);
    private readonly Dictionary<string, InlineUniverseBatch> _inlineBatches = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _inlineLock = new();

    private DateTimeOffset _maintenanceReadyAtUtc = DateTimeOffset.UtcNow.AddMinutes(5);
    private DateTimeOffset _nextMaintenanceRunUtc = DateTimeOffset.UtcNow.AddMinutes(5);

    public UniverseEnrichmentService(
        IServiceScopeFactory scopeFactory,
        ScheduledTaskRunner runner,
        ILogger<UniverseEnrichmentService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _runner = runner;
        _logger = logger;
    }

    public ValueTask QueueInlineAsync(UniverseEnrichmentRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_inlineLock)
        {
            if (!_inlineBatches.TryGetValue(request.BatchKey, out var batch))
            {
                batch = new InlineUniverseBatch(request.BatchKey);
                _inlineBatches[request.BatchKey] = batch;
            }

            batch.Upsert(request);
        }

        _logger.LogInformation(
            "[UNIVERSE-ENRICH] Queued inline Stage 3 item {EntityId} ({Title}) in batch {BatchKey}",
            request.EntityId,
            request.WorkTitle ?? request.WorkQid,
            request.BatchKey);

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Signals the background loop to start a maintenance sweep immediately rather than
    /// waiting for the next scheduled slot.
    /// </summary>
    public void TriggerManualSweep()
    {
        _logger.LogInformation("[UNIVERSE-ENRICH] Manual maintenance sweep triggered");
        _manualTrigger.Set();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ReloadMaintenanceSchedule();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessDueInlineBatchesAsync(stoppingToken).ConfigureAwait(false);

                if (ShouldRunMaintenanceSweep())
                {
                    await _runner.RunAsync(ScheduledTaskKeys.UniverseEnrichment, ProcessMaintenanceSweepAsync, stoppingToken)
                        .ConfigureAwait(false);
                    _manualTrigger.Reset();
                    ReloadMaintenanceSchedule();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[UNIVERSE-ENRICH] Background loop failed");
            }

            try
            {
                await Task.Delay(LoopInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProcessDueInlineBatchesAsync(CancellationToken ct)
    {
        List<InlineUniverseBatch> dueBatches;
        lock (_inlineLock)
        {
            var now = DateTimeOffset.UtcNow;
            dueBatches = _inlineBatches.Values
                .Where(batch => batch.IsDue(now))
                .OrderBy(batch => batch.FirstQueuedAtUtc)
                .ToList();

            foreach (var due in dueBatches)
            {
                _inlineBatches.Remove(due.BatchKey);
            }
        }

        foreach (var batch in dueBatches)
        {
            ct.ThrowIfCancellationRequested();
            await ProcessInlineBatchAsync(batch, ct).ConfigureAwait(false);
        }
    }

    private async Task ProcessInlineBatchAsync(InlineUniverseBatch batch, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var services = ResolveServices(scope.ServiceProvider);
        var config = services.ConfigurationLoader.LoadHydration();

        if (!config.Stage3Enabled)
        {
            _logger.LogInformation("[UNIVERSE-ENRICH] Stage 3 disabled; completing inline batch {BatchKey} without core work", batch.BatchKey);

            foreach (var request in batch.Requests)
            {
                await CompleteInlineJobAsync(
                    request,
                    hasUniversePath: false,
                    services.JobRepository,
                    ct).ConfigureAwait(false);
            }

            return;
        }

        _logger.LogInformation(
            "[UNIVERSE-ENRICH] Processing inline batch {BatchKey} with {Count} item(s)",
            batch.BatchKey,
            batch.Requests.Count);

        for (var index = 0; index < batch.Requests.Count; index++)
        {
            var request = batch.Requests[index];
            await ProcessResolvedEntityAsync(
                request,
                Stage3Source.Inline,
                index + 1,
                batch.Requests.Count,
                services,
                ct).ConfigureAwait(false);
        }
    }

    private async Task<TaskRunResult> ProcessMaintenanceSweepAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var services = ResolveServices(scope.ServiceProvider);
        var config = services.ConfigurationLoader.LoadHydration();

        if (!config.Stage3Enabled)
        {
            _logger.LogDebug("[UNIVERSE-ENRICH] Stage 3 disabled; skipping maintenance sweep");
            return TaskRunResult.Skipped("Universe enrichment is off");
        }

        var maxItems = config.Stage3MaxItemsPerSweep > 0 ? config.Stage3MaxItemsPerSweep : 50;
        var refreshDays = config.Stage3RefreshDays > 0 ? config.Stage3RefreshDays : 30;
        var staleThreshold = DateTimeOffset.UtcNow.AddDays(-refreshDays);

        var candidateIds = await services.CanonicalRepository.GetEntitiesNeedingEnrichmentAsync(
            hasField: "wikidata_qid",
            missingField: "__stage3_maintenance_cursor__",
            limit: Math.Max(maxItems * 8, 200),
            ct: ct).ConfigureAwait(false);

        var requests = new List<UniverseEnrichmentRequest>(maxItems);
        foreach (var entityId in candidateIds)
        {
            ct.ThrowIfCancellationRequested();
            if (requests.Count >= maxItems)
            {
                break;
            }

            var canonicals = await services.CanonicalRepository.GetByEntityAsync(entityId, ct).ConfigureAwait(false);
            var lookup = canonicals.ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);

            if (!lookup.TryGetValue("wikidata_qid", out var workQid) || string.IsNullOrWhiteSpace(workQid))
            {
                continue;
            }

            if (lookup.TryGetValue("stage3_enriched_at", out var stage3EnrichedAt)
                && DateTimeOffset.TryParse(stage3EnrichedAt, out var enrichedAt)
                && enrichedAt > staleThreshold)
            {
                if (!HasUniversePath(lookup, narrativeRoot: null))
                {
                    continue;
                }

                var hasLinkedEntities = await HasLinkedFictionalEntitiesAsync(
                    services.FictionalEntityRepository,
                    workQid,
                    ct).ConfigureAwait(false);

                if (hasLinkedEntities)
                {
                    continue;
                }
            }

            var title = GetBestTitle(lookup);
            var mediaType = lookup.TryGetValue("media_type", out var mediaTypeValue) && !string.IsNullOrWhiteSpace(mediaTypeValue)
                ? mediaTypeValue
                : nameof(MediaType.Unknown);

            requests.Add(new UniverseEnrichmentRequest(
                Guid.Empty,
                entityId,
                workQid,
                mediaType,
                $"maintenance:{mediaType}",
                title));
        }

        if (requests.Count == 0)
        {
            _logger.LogDebug("[UNIVERSE-ENRICH] No stale or missing entities found for maintenance sweep");
            return TaskRunResult.Succeeded(0, "Nothing needed refreshing.");
        }

        _logger.LogInformation(
            "[UNIVERSE-ENRICH] Processing maintenance sweep with {Count} item(s)",
            requests.Count);

        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            await ProcessResolvedEntityAsync(
                request,
                Stage3Source.Maintenance,
                index + 1,
                requests.Count,
                services,
                ct).ConfigureAwait(false);
        }

        return TaskRunResult.Succeeded(requests.Count);
    }

    private async Task ProcessResolvedEntityAsync(
        UniverseEnrichmentRequest request,
        Stage3Source source,
        int processedCount,
        int totalCount,
        ResolvedServices services,
        CancellationToken ct)
    {
        // A work in a library that never looks anything up (a plain kind) is never enriched from Wikidata, by inline batch or sweep (Library setup D38).
        if (await services.LookupGuard.RefusesAsync(request.EntityId, _logger, "Universe enrichment", ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var stage3Scope = await ResolveStage3ScopeAsync(
                request.EntityId,
                services.WorkRepository,
                services.CanonicalRepository,
                ct).ConfigureAwait(false);

            var lookup = stage3Scope.Canonicals.ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
            var narrativeRoot = await services.NarrativeRootResolver.ResolveAsync(stage3Scope.CanonicalEntityId, ct).ConfigureAwait(false);
            await WriteAdaptationEdgesAsync(services.WorkAdaptationEdges, request, stage3Scope, ct).ConfigureAwait(false);
            var hasUniversePath = HasUniversePath(lookup, narrativeRoot);
            var workTitle = request.WorkTitle ?? GetBestTitle(lookup) ?? request.WorkQid;

            await PublishUniverseProgressAsync(
                services.EventPublisher,
                request.WorkQid,
                workTitle,
                processedCount,
                totalCount,
                "Work relationships",
                ct).ConfigureAwait(false);

            await services.Enrichment.RunWorkScopedPassAsync(request.EntityId, request.WorkQid, ct).ConfigureAwait(false);

            if (!hasUniversePath)
            {
                await PublishUniverseProgressAsync(
                    services.EventPublisher,
                    request.WorkQid,
                    workTitle,
                    processedCount,
                    totalCount,
                    "No universe path",
                    ct).ConfigureAwait(false);
            }
            else
            {
                await PublishUniverseProgressAsync(
                    services.EventPublisher,
                    request.WorkQid,
                    workTitle,
                    processedCount,
                    totalCount,
                    "Core universe",
                    ct).ConfigureAwait(false);
            }

            if (hasUniversePath)
            {
                await services.Enrichment.RunUniverseCorePassAsync(request.EntityId, request.WorkQid, ct).ConfigureAwait(false);
            }

            var stage3Settled = !hasUniversePath;
            if (hasUniversePath)
            {
                stage3Settled = await HasLinkedFictionalEntitiesAsync(
                    services.FictionalEntityRepository,
                    request.WorkQid,
                    ct).ConfigureAwait(false);

                if (!stage3Settled)
                {
                    _logger.LogWarning(
                        "[UNIVERSE-ENRICH] Stage 3 core completed for {Qid} ({EntityId}) but linked no fictional entities; leaving item retryable",
                        request.WorkQid,
                        request.EntityId);
                }
            }

            if (stage3Settled)
            {
                await MarkStage3SettledAsync(services.CanonicalRepository, request.EntityId, ct).ConfigureAwait(false);
            }
            else
            {
                await ClearStage3SettledAsync(services.CanonicalRepository, request.EntityId, ct).ConfigureAwait(false);
            }

            await PublishUniverseProgressAsync(
                services.EventPublisher,
                request.WorkQid,
                workTitle,
                processedCount,
                totalCount,
                "Enhancers",
                ct).ConfigureAwait(false);

            try
            {
                await services.Enrichment.RunUniverseEnhancerPassAsync(request.EntityId, request.WorkQid, ct).ConfigureAwait(false);
                await RunPluginLorePassAsync(lookup, narrativeRoot, services.PluginUniverseLore, ct).ConfigureAwait(false);
                await MarkStage3EnhancedAsync(services.CanonicalRepository, request.EntityId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(
                    ex,
                    "[UNIVERSE-ENRICH] Enhancer phase failed for {Qid} ({EntityId}); leaving item retryable",
                    request.WorkQid,
                    request.EntityId);

                return;
            }

            if (source == Stage3Source.Inline)
            {
                await CompleteInlineJobAsync(
                    request,
                    hasUniversePath && stage3Settled,
                    services.JobRepository,
                    ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[UNIVERSE-ENRICH] Failed to process {Qid} ({EntityId}) for {Source}",
                request.WorkQid,
                request.EntityId,
                source);

            if (source == Stage3Source.Inline && request.JobId != Guid.Empty)
            {
                await services.JobRepository.UpdateStateAsync(
                    request.JobId,
                    IdentityJobState.Failed,
                    ex.Message,
                    ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task CompleteInlineJobAsync(
        UniverseEnrichmentRequest request,
        bool hasUniversePath,
        IIdentityJobRepository jobRepository,
        CancellationToken ct)
    {
        if (request.JobId != Guid.Empty)
        {
            await jobRepository.UpdateStateAsync(
                request.JobId,
                hasUniversePath ? IdentityJobState.Ready : IdentityJobState.ReadyWithoutUniverse,
                ct: ct).ConfigureAwait(false);
        }

    }

    private void ReloadMaintenanceSchedule()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var configLoader = scope.ServiceProvider.GetRequiredService<IConfigurationLoader>();
            var maintenance = configLoader.LoadMaintenance();
            var cron = maintenance.Schedules.TryGetValue("universe_enrichment", out var configuredCron)
                ? configuredCron
                : "0 3 * * *";

            _nextMaintenanceRunUtc = CronScheduler.NextRunAt(string.IsNullOrWhiteSpace(cron) ? "0 3 * * *" : cron, TimeSpan.FromHours(24)).ToUniversalTime();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load the Universe enrichment schedule; next sweep in 24 hours");
            _nextMaintenanceRunUtc = DateTimeOffset.UtcNow.AddHours(24);
        }
    }

    private bool ShouldRunMaintenanceSweep()
    {
        var now = DateTimeOffset.UtcNow;
        return now >= _maintenanceReadyAtUtc
            && (_manualTrigger.IsSet || now >= _nextMaintenanceRunUtc);
    }

    private static async Task MarkStage3SettledAsync(
        ICanonicalValueRepository canonicalRepository,
        Guid entityId,
        CancellationToken ct)
    {
        await canonicalRepository.UpsertBatchAsync(
        [
            new CanonicalValue
            {
                EntityId = entityId,
                Key = "stage3_enriched_at",
                Value = DateTimeOffset.UtcNow.ToString("o"),
                LastScoredAt = DateTimeOffset.UtcNow,
                WinningProviderId = Guid.Empty,
            }
        ], ct).ConfigureAwait(false);
    }

    private static Task ClearStage3SettledAsync(
        ICanonicalValueRepository canonicalRepository,
        Guid entityId,
        CancellationToken ct)
        => canonicalRepository.DeleteByKeyAsync(entityId, "stage3_enriched_at", ct);

    private static async Task MarkStage3EnhancedAsync(
        ICanonicalValueRepository canonicalRepository,
        Guid entityId,
        CancellationToken ct)
    {
        await canonicalRepository.UpsertBatchAsync(
        [
            new CanonicalValue
            {
                EntityId = entityId,
                Key = "stage3_enhanced_at",
                Value = DateTimeOffset.UtcNow.ToString("o"),
                LastScoredAt = DateTimeOffset.UtcNow,
                WinningProviderId = Guid.Empty,
            }
        ], ct).ConfigureAwait(false);
    }

    private static async Task<bool> HasLinkedFictionalEntitiesAsync(
        IFictionalEntityRepository fictionalEntityRepository,
        string workQid,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workQid))
        {
            return false;
        }

        var linkedEntities = await fictionalEntityRepository.GetByWorkQidAsync(workQid, ct).ConfigureAwait(false);
        return linkedEntities.Count > 0;
    }

    private static async Task PublishUniverseProgressAsync(
        IEventPublisher eventPublisher,
        string workQid,
        string workTitle,
        int processedCount,
        int totalCount,
        string currentStep,
        CancellationToken ct)
    {
        await eventPublisher.PublishAsync(
            SignalREvents.UniverseEnrichmentProgress,
            new
            {
                WorkQid = workQid,
                WorkTitle = workTitle,
                ProcessedCount = processedCount,
                TotalCount = totalCount,
                CurrentStep = currentStep,
            },
            ct).ConfigureAwait(false);
    }

    private static async Task<Stage3Scope> ResolveStage3ScopeAsync(
        Guid entityId,
        IWorkRepository workRepository,
        ICanonicalValueRepository canonicalRepository,
        CancellationToken ct)
    {
        var lineage = await workRepository.GetLineageByAssetAsync(entityId, ct).ConfigureAwait(false);
        var canonicalEntityId = lineage?.TargetForParentScope ?? entityId;
        var canonicals = await canonicalRepository.GetByEntityAsync(canonicalEntityId, ct).ConfigureAwait(false);

        return new Stage3Scope(canonicalEntityId, canonicals, lineage is not null && lineage.WorkId != lineage.RootParentWorkId);
    }

    // Adaptation edges are best effort: a failure here is logged and must not stop the rest of stage 3 for this Work.
    // The claims are Parent-scope, so for an episode the fallback is the show's identity (read from the show's own values by
    // the edge service), never the episode's.
    private async Task WriteAdaptationEdgesAsync(
        IWorkAdaptationEdgeService edges,
        UniverseEnrichmentRequest request,
        Stage3Scope scope,
        CancellationToken ct)
    {
        try
        {
            await edges.WriteAsync(scope.CanonicalEntityId, scope.RequestIsChild ? null : request.WorkQid, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Adaptation edges could not be written for Work {WorkQid}; stage 3 continues.", request.WorkQid);
        }
    }

    private static bool HasUniversePath(
        IReadOnlyDictionary<string, string> canonicalLookup,
        NarrativeRoot? narrativeRoot)
    {
        if (narrativeRoot is not null && !string.IsNullOrWhiteSpace(narrativeRoot.Qid))
        {
            return true;
        }

        return canonicalLookup.ContainsKey("fictional_universe_qid")
            || canonicalLookup.ContainsKey("franchise_qid")
            || canonicalLookup.ContainsKey("series_qid")
            || canonicalLookup.ContainsKey("characters_qid")
            || canonicalLookup.ContainsKey("narrative_location_qid");
    }

    private static string? GetBestTitle(IReadOnlyDictionary<string, string> lookup)
    {
        if (lookup.TryGetValue(MetadataFieldConstants.Title, out var title) && !string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        if (lookup.TryGetValue(MetadataFieldConstants.ShowName, out var showName) && !string.IsNullOrWhiteSpace(showName))
        {
            return showName;
        }

        return null;
    }

    private async Task RunPluginLorePassAsync(
        IReadOnlyDictionary<string, string> lookup,
        NarrativeRoot? narrativeRoot,
        PluginUniverseLoreService pluginUniverseLore,
        CancellationToken ct)
    {
        var universeQid = ResolveUniverseQid(lookup, narrativeRoot);
        if (string.IsNullOrWhiteSpace(universeQid))
        {
            return;
        }

        try
        {
            var discovered = await pluginUniverseLore.DiscoverSourcesAsync(universeQid, ct).ConfigureAwait(false);
            if (discovered is null)
            {
                _logger.LogDebug("[UNIVERSE-ENRICH] Skipped plugin lore for {UniverseQid}: the Library does not hold it as a Universe", universeQid);
                return;
            }

            var summary = await pluginUniverseLore.EnrichUniverseAsync(universeQid, ct).ConfigureAwait(false);
            if (summary is { SourcesEnriched: > 0 })
            {
                _logger.LogInformation(
                    "[UNIVERSE-ENRICH] Plugin lore enriched {EntityCount} supplemental entities from {SourceCount} source(s) for {UniverseQid}",
                    summary.EntitiesWritten,
                    summary.SourcesEnriched,
                    universeQid);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[UNIVERSE-ENRICH] Plugin lore pass failed for {UniverseQid}", universeQid);
        }
    }

    private static string? ResolveUniverseQid(
        IReadOnlyDictionary<string, string> lookup,
        NarrativeRoot? narrativeRoot)
    {
        if (!string.IsNullOrWhiteSpace(narrativeRoot?.Qid))
        {
            return narrativeRoot.Qid;
        }

        foreach (var key in new[]
                 {
                     "fictional_universe_qid",
                     "franchise_qid",
                     "series_qid",
                     "characters_qid",
                     "narrative_location_qid",
                 })
        {
            if (lookup.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static ResolvedServices ResolveServices(IServiceProvider services)
        => new(
            services.GetRequiredService<IConfigurationLoader>(),
            services.GetRequiredService<ICanonicalValueRepository>(),
            services.GetRequiredService<IWorkRepository>(),
            services.GetRequiredService<IEnrichmentService>(),
            services.GetRequiredService<IFictionalEntityRepository>(),
            services.GetRequiredService<INarrativeRootResolver>(),
            services.GetRequiredService<IWorkAdaptationEdgeService>(),
            services.GetRequiredService<IIdentityJobRepository>(),
            services.GetRequiredService<IEventPublisher>(),
            services.GetRequiredService<PluginUniverseLoreService>(),
            services.GetService<IExternalLookupGuard>());

    private sealed class InlineUniverseBatch
    {
        private readonly Dictionary<Guid, UniverseEnrichmentRequest> _requests = new();

        public InlineUniverseBatch(string batchKey)
        {
            BatchKey = batchKey;
            FirstQueuedAtUtc = DateTimeOffset.UtcNow;
            LastQueuedAtUtc = FirstQueuedAtUtc;
        }

        public string BatchKey { get; }
        public DateTimeOffset FirstQueuedAtUtc { get; }
        public DateTimeOffset LastQueuedAtUtc { get; private set; }
        public IReadOnlyList<UniverseEnrichmentRequest> Requests => _requests.Values.ToList();

        public void Upsert(UniverseEnrichmentRequest request)
        {
            _requests[request.EntityId] = request;
            LastQueuedAtUtc = DateTimeOffset.UtcNow;
        }

        public bool IsDue(DateTimeOffset now)
            => _requests.Count >= InlineBatchMaxWorks
                || LastQueuedAtUtc <= now - InlineSilenceWindow;
    }

    private sealed record ResolvedServices(
        IConfigurationLoader ConfigurationLoader,
        ICanonicalValueRepository CanonicalRepository,
        IWorkRepository WorkRepository,
        IEnrichmentService Enrichment,
        IFictionalEntityRepository FictionalEntityRepository,
        INarrativeRootResolver NarrativeRootResolver,
        IWorkAdaptationEdgeService WorkAdaptationEdges,
        IIdentityJobRepository JobRepository,
        IEventPublisher EventPublisher,
        PluginUniverseLoreService PluginUniverseLore,
        IExternalLookupGuard? LookupGuard);

    private sealed record Stage3Scope(
        Guid CanonicalEntityId,
        IReadOnlyList<CanonicalValue> Canonicals,
        bool RequestIsChild);

    private enum Stage3Source
    {
        Inline,
        Maintenance,
    }
}
