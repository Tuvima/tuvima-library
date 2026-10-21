using MediaEngine.AI.Configuration;
using MediaEngine.AI.EngineRoom;
using MediaEngine.AI.Infrastructure;
using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Jobs;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services;

/// <summary>
/// Infers missing series positions in a bounded, sequential background batch.
/// Model confidence is preserved through the typed inference result and used
/// directly by the publication/review thresholds.
/// </summary>
public sealed class SeriesAlignmentRunner
{
    private const string FeatureKey = "series_alignment";
    private const string ModelId = "text_quality";
    private const string PromptVersion = "series-position-v1";

    private readonly AiSettings _settings;
    private readonly AiFeatureGate _featureGate;
    private readonly ISeriesAligner _aligner;
    private readonly ICanonicalValueRepository _canonicals;
    private readonly IAiFeaturePersistenceRepository _featurePersistence;
    private readonly ILibraryItemRepository _libraryItems;
    private readonly ILogger<SeriesAlignmentRunner> _logger;

    public SeriesAlignmentRunner(
        AiSettings settings,
        AiFeatureGate featureGate,
        ISeriesAligner aligner,
        ICanonicalValueRepository canonicals,
        IAiFeaturePersistenceRepository featurePersistence,
        ILibraryItemRepository libraryItems,
        ILogger<SeriesAlignmentRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(featureGate);
        ArgumentNullException.ThrowIfNull(aligner);
        ArgumentNullException.ThrowIfNull(canonicals);
        ArgumentNullException.ThrowIfNull(featurePersistence);
        ArgumentNullException.ThrowIfNull(libraryItems);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _featureGate = featureGate;
        _aligner = aligner;
        _canonicals = canonicals;
        _featurePersistence = featurePersistence;
        _libraryItems = libraryItems;
        _logger = logger;
    }

    public async Task<TaskRunResult> RunAlignmentAsync(CancellationToken ct)
    {
        if (!_featureGate.CanExecute(AiFeature.SeriesAlignment, MediaEngine.Domain.Enums.AiModelRole.TextQuality))
        {
            _logger.LogDebug("SeriesAlignmentService: feature disabled or selected model unavailable — skipping before scan");
            return TaskRunResult.Skipped("Series order is off");
        }

        _logger.LogInformation("SeriesAlignmentService: scanning for unpositioned series works");
        var page = await _libraryItems.GetPageAsync(
            new LibraryItemQuery(Offset: 0, Limit: 200, Sort: "newest"),
            ct);

        var candidates = new List<(Guid EntityId, string SeriesName, string Title)>();
        foreach (var item in page.Items)
        {
            ct.ThrowIfCancellationRequested();
            var canonicals = await _canonicals.GetByEntityAsync(item.EntityId, ct);
            var series = canonicals.FirstOrDefault(value =>
                string.Equals(value.Key, "series", StringComparison.OrdinalIgnoreCase));
            if (series is null || canonicals.Any(value =>
                    string.Equals(value.Key, "series_position", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var title = canonicals.FirstOrDefault(value =>
                string.Equals(value.Key, "title", StringComparison.OrdinalIgnoreCase));
            candidates.Add((item.EntityId, series.Value, title?.Value ?? item.Title));
        }

        var attempted = 0;
        var batchLimit = Math.Clamp(_settings.EnrichmentBatchSize, 1, 50);
        foreach (var group in candidates.GroupBy(candidate => candidate.SeriesName, StringComparer.OrdinalIgnoreCase))
        {
            var siblingTitles = group.Select(member => member.Title).ToList();
            foreach (var (entityId, seriesName, workTitle) in group)
            {
                ct.ThrowIfCancellationRequested();
                if (attempted >= batchLimit)
                {
                    break;
                }

                var inputFingerprint = AiFeatureFingerprint.Compute(
                    workTitle,
                    seriesName,
                    string.Join('\n', siblingTitles));
                var state = await _featurePersistence.GetAiFeatureStateAsync(entityId, FeatureKey, ct);
                if (state?.IsCurrent(inputFingerprint) == true || state?.CanAttempt(DateTimeOffset.UtcNow) == false)
                {
                    continue;
                }

                attempted++;
                try
                {
                    var inference = await _aligner.InferPositionAsync(
                        workTitle,
                        seriesName,
                        siblingTitles,
                        ct);
                    if (inference is null)
                    {
                        continue;
                    }

                    await _featurePersistence.ReplaceAiFeatureAsync(
                        new AiFeatureWriteRequest(
                            entityId,
                            FeatureKey,
                            new Dictionary<string, IReadOnlyList<string>>(),
                            new Dictionary<string, string?>
                            {
                                ["series_position"] = inference.Position.ToString(),
                            },
                            WellKnownProviders.AiProvider,
                            inference.Confidence,
                            PublishThreshold: 0.80,
                            ReviewThreshold: 0.90,
                            ModelId,
                            PromptVersion,
                            inputFingerprint),
                        ct);
                    _logger.LogDebug(
                        "SeriesAlignmentService: review-gated position {Position} for {Title} in {Series}",
                        inference.Position,
                        workTitle,
                        seriesName);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (AiRequestPreemptedException)
                {
                    _logger.LogInformation(
                        "SeriesAlignmentService: yielding entity {Id} to interactive traffic",
                        entityId);
                    return TaskRunResult.Skipped("Paused to make way for something you were doing");
                }
                catch (AiRequestDeferredException)
                {
                    // The engine room found the machine busy (first-run setup, someone using the app, or low resources).
                    return TaskRunResult.Skipped("Waiting: the machine is busy");
                }
                catch (Exception ex)
                {
                    var failure = await _featurePersistence.RecordAiFeatureFailureAsync(
                        new AiFeatureFailureRequest(
                            entityId,
                            FeatureKey,
                            WellKnownProviders.AiProvider,
                            ModelId,
                            PromptVersion,
                            inputFingerprint,
                            ex.Message,
                            Category: BackgroundJobOutcomeClassifier.Classify(ex, ct)),
                        ct);
                    _logger.LogWarning(ex,
                        "SeriesAlignmentService: failed to infer position for entity {Id}",
                        entityId);
                    if (failure.Status == AiFeatureStatus.Poisoned)
                    {
                        _logger.LogError(
                            "SeriesAlignmentService: quarantined poison entity {Id} after {Attempts} attempts",
                            entityId,
                            failure.Attempts);
                    }
                }
            }

            if (attempted >= batchLimit)
            {
                break;
            }
        }

        _logger.LogInformation(
            "SeriesAlignmentService: attempted {Count} bounded suggestion(s)",
            attempted);
        return TaskRunResult.Succeeded(attempted);
    }
}
