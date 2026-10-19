using MediaEngine.AI.Configuration;
using MediaEngine.AI.Infrastructure;
using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Jobs;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services;

/// <summary>
/// Background service that generates vibe/mood tags for entities
/// using Wikipedia summaries and per-category controlled vocabulary.
/// Runs on a configurable cron schedule (default: 4 AM daily).
/// </summary>
public sealed class VibeBatchService : BackgroundService
{
    private const string FeatureKey = "vibe";
    private const string ModelId = "text_quality";
    private const string PromptVersion = "vibe-v1";
    private readonly AiSettings _settings;
    private readonly AiFeatureGate _featureGate;
    private readonly AiConfigurationService _configurationStore;
    private readonly IVibeTagger _tagger;
    private readonly ICanonicalValueRepository _canonicals;
    private readonly ICanonicalValueArrayRepository _canonicalArrays;
    private readonly IAiFeaturePersistenceRepository _featurePersistence;
    private readonly ILibraryItemRepository _libraryItems;
    private readonly BackgroundAiAdmissionController _admission;
    private readonly ScheduledTaskRunner _runner;
    private readonly ILogger<VibeBatchService> _logger;

    public VibeBatchService(
        AiSettings settings,
        AiFeatureGate featureGate,
        AiConfigurationService configurationStore,
        IVibeTagger tagger,
        ICanonicalValueRepository canonicals,
        ICanonicalValueArrayRepository canonicalArrays,
        IAiFeaturePersistenceRepository featurePersistence,
        ILibraryItemRepository libraryItems,
        BackgroundAiAdmissionController admission,
        ScheduledTaskRunner runner,
        ILogger<VibeBatchService> logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(featureGate);
        ArgumentNullException.ThrowIfNull(configurationStore);
        ArgumentNullException.ThrowIfNull(tagger);
        ArgumentNullException.ThrowIfNull(canonicals);
        ArgumentNullException.ThrowIfNull(canonicalArrays);
        ArgumentNullException.ThrowIfNull(featurePersistence);
        ArgumentNullException.ThrowIfNull(libraryItems);
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _featureGate = featureGate;
        _configurationStore = configurationStore;
        _tagger = tagger;
        _canonicals = canonicals;
        _canonicalArrays = canonicalArrays;
        _featurePersistence = featurePersistence;
        _libraryItems = libraryItems;
        _admission = admission;
        _runner = runner;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay — let the Engine fully start.
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _runner.RunAsync(ScheduledTaskKeys.VibeTags, RunBatchAsync, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "VibeBatchService batch failed");
            }

            var cron = _settings.Scheduling.VibeBatchCron;
            var delay = CronScheduler.UntilNext(cron, TimeSpan.FromHours(24));

            _logger.LogInformation("VibeBatchService: next run in {Delay}", delay);
            await _configurationStore.WaitForDelayOrChangeAsync(delay, stoppingToken);
        }
    }

    private async Task<TaskRunResult> RunBatchAsync(CancellationToken ct)
    {
        if (!_featureGate.CanExecute(AiFeature.VibeTags, MediaEngine.Domain.Enums.AiModelRole.TextQuality))
        {
            _logger.LogDebug("VibeBatchService: feature disabled or selected model unavailable — skipping before scan");
            return TaskRunResult.Skipped("Vibe tags is off");
        }

        _logger.LogInformation("VibeBatchService: scanning for entities needing vibe tags");

        // Page through the libraryItem to find entities (up to 200 candidates per batch).
        // We load a larger set then filter in-memory for those missing vibe tags.
        var page = await _libraryItems.GetPageAsync(
            new LibraryItemQuery(Offset: 0, Limit: 200, Sort: "newest"), ct);

        int tagged = 0;
        var batchLimit = Math.Clamp(_settings.EnrichmentBatchSize, 1, 50);

        foreach (var item in page.Items)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            if (tagged >= batchLimit)
            {
                break;
            }

            var canonicals = await _canonicals.GetByEntityAsync(item.EntityId, ct);
            var arrays = await _canonicalArrays.GetAllByEntityAsync(item.EntityId, ct);

            // Need a description to generate vibes.
            var description = canonicals.FirstOrDefault(c =>
                string.Equals(c.Key, "description", StringComparison.OrdinalIgnoreCase));
            if (description is null)
            {
                continue;
            }

            var genres = arrays.GetValueOrDefault("genre")?.Select(value => value.Value).ToList() ?? [];

            var mediaType = canonicals.FirstOrDefault(c =>
                string.Equals(c.Key, "media_type", StringComparison.OrdinalIgnoreCase));

            var title = canonicals.FirstOrDefault(c =>
                string.Equals(c.Key, "title", StringComparison.OrdinalIgnoreCase));
            var inputFingerprint = AiFeatureFingerprint.Compute(
                title?.Value,
                description.Value,
                mediaType?.Value,
                string.Join('\n', genres));
            var state = await _featurePersistence.GetAiFeatureStateAsync(item.EntityId, FeatureKey, ct);
            if (state?.IsCurrent(inputFingerprint) == true || state?.CanAttempt(DateTimeOffset.UtcNow) == false)
            {
                continue;
            }

            if (state is null && arrays.GetValueOrDefault("vibe")?.Count > 0)
            {
                continue;
            }

            using var admission = _admission.TryAcquire(
                MediaEngine.Domain.Enums.AiModelRole.TextQuality,
                ct);
            if (admission is null)
            {
                return TaskRunResult.Skipped("Waiting: the machine is busy");
            }

            try
            {
                var tags = await _tagger.TagAsync(
                    title?.Value ?? "Unknown",
                    description.Value,
                    genres,
                    mediaType?.Value ?? "books",
                    admission.Token);

                var result = await _featurePersistence.ReplaceAiFeatureAsync(
                    new AiFeatureWriteRequest(
                        item.EntityId,
                        FeatureKey,
                        new Dictionary<string, IReadOnlyList<string>> { ["vibe"] = tags },
                        new Dictionary<string, string?>(),
                        WellKnownProviders.AiProvider,
                        ClaimConfidence.AiDescription,
                        PublishThreshold: 0.65,
                        ReviewThreshold: 0.75,
                        ModelId,
                        PromptVersion,
                        inputFingerprint),
                    ct);
                if (tags.Count > 0 && result.Status is AiFeatureStatus.Published or AiFeatureStatus.ReviewRequired)
                {
                    tagged++;

                    _logger.LogDebug(
                        "VibeBatchService: tagged entity {EntityId} with {Count} vibe tag(s)",
                        item.EntityId, tags.Count);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (admission.WasPreempted)
            {
                _logger.LogInformation("VibeBatchService: yielding to interactive traffic");
                return TaskRunResult.Skipped("Paused to make way for something you were doing");
            }
            catch (Exception ex)
            {
                var failure = await _featurePersistence.RecordAiFeatureFailureAsync(
                    new AiFeatureFailureRequest(
                        item.EntityId,
                        FeatureKey,
                        WellKnownProviders.AiProvider,
                        ModelId,
                        PromptVersion,
                        inputFingerprint,
                        ex.Message,
                        Category: BackgroundJobOutcomeClassifier.Classify(ex, ct)),
                    ct);
                _logger.LogWarning(ex, "VibeBatchService: failed to tag entity {Id}", item.EntityId);
                if (failure.Status == AiFeatureStatus.Poisoned)
                {
                    _logger.LogError("VibeBatchService: quarantined poison entity {Id} after {Attempts} attempts", item.EntityId, failure.Attempts);
                }
            }
        }

        _logger.LogInformation("VibeBatchService: tagged {Count} entities", tagged);
        return TaskRunResult.Succeeded(tagged);
    }
}
