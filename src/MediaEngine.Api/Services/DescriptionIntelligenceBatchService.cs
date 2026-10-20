using MediaEngine.AI.Configuration;
using MediaEngine.AI.EngineRoom;
using MediaEngine.AI.Infrastructure;
using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Jobs;
using MediaEngine.Domain.Models;
using MediaEngine.Intelligence.Contracts;
using MediaEngine.Providers.Adapters;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services;

/// <summary>
/// Background service that runs Description Intelligence (LLM-powered structured
/// analysis of descriptions) for entities that have a description or plot summary
/// but have not yet been enriched with themes, mood, and other vocabulary.
///
/// Runs on a configurable cron schedule (default: every 15 minutes).
/// Initial delay of 3 minutes to let ingestion and hydration settle first.
/// </summary>
public sealed class DescriptionIntelligenceBatchService : BackgroundService
{
    private const string FeatureKey = "description_intelligence";
    private const string PromptVersion = "description-intelligence-v1";
    private readonly AiSettings _settings;
    private readonly IConfigurationLoader _configLoader;
    private readonly ResourceMonitorService _resourceMonitor;
    private readonly AiFeatureGate _featureGate;
    private readonly AiConfigurationService _configurationStore;
    private readonly ICanonicalValueRepository _canonicals;
    private readonly IAiFeaturePersistenceRepository _featurePersistence;
    private readonly IWorkRepository _works;
    private readonly IDescriptionIntelligenceService _descriptionIntelligence;
    private readonly IPersonReconciliationService _personReconciliation;
    private readonly IMetadataClaimRepository _claims;
    private readonly IScoringEngine _scoring;
    private readonly ICanonicalValueArrayRepository _canonicalArrays;
    private readonly ISearchIndexRepository _searchIndex;
    private readonly IReadOnlyList<IExternalMetadataProvider> _providers;
    private readonly IRecursiveIdentityService _identityService;
    private readonly IMetadataHarvestingService _harvesting;
    private readonly ScheduledTaskRunner _runner;
    private readonly ILogger<DescriptionIntelligenceBatchService> _logger;

    public DescriptionIntelligenceBatchService(
        AiSettings settings,
        IConfigurationLoader configLoader,
        ResourceMonitorService resourceMonitor,
        AiFeatureGate featureGate,
        AiConfigurationService configurationStore,
        ICanonicalValueRepository canonicals,
        IAiFeaturePersistenceRepository featurePersistence,
        IWorkRepository works,
        IDescriptionIntelligenceService descriptionIntelligence,
        IPersonReconciliationService personReconciliation,
        IMetadataClaimRepository claims,
        IScoringEngine scoring,
        ICanonicalValueArrayRepository canonicalArrays,
        ISearchIndexRepository searchIndex,
        IEnumerable<IExternalMetadataProvider> providers,
        IRecursiveIdentityService identityService,
        IMetadataHarvestingService harvesting,
        ScheduledTaskRunner runner,
        ILogger<DescriptionIntelligenceBatchService> logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(configLoader);
        ArgumentNullException.ThrowIfNull(resourceMonitor);
        ArgumentNullException.ThrowIfNull(featureGate);
        ArgumentNullException.ThrowIfNull(configurationStore);
        ArgumentNullException.ThrowIfNull(canonicals);
        ArgumentNullException.ThrowIfNull(featurePersistence);
        ArgumentNullException.ThrowIfNull(works);
        ArgumentNullException.ThrowIfNull(descriptionIntelligence);
        ArgumentNullException.ThrowIfNull(personReconciliation);
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(scoring);
        ArgumentNullException.ThrowIfNull(canonicalArrays);
        ArgumentNullException.ThrowIfNull(searchIndex);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(identityService);
        ArgumentNullException.ThrowIfNull(harvesting);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _configLoader = configLoader;
        _resourceMonitor = resourceMonitor;
        _featureGate = featureGate;
        _configurationStore = configurationStore;
        _canonicals = canonicals;
        _featurePersistence = featurePersistence;
        _works = works;
        _descriptionIntelligence = descriptionIntelligence;
        _personReconciliation = personReconciliation;
        _claims = claims;
        _scoring = scoring;
        _canonicalArrays = canonicalArrays;
        _searchIndex = searchIndex;
        _providers = providers.ToList();
        _identityService = identityService;
        _harvesting = harvesting;
        _runner = runner;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay — let ingestion and hydration pipelines settle.
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _runner.RunAsync(ScheduledTaskKeys.DescriptionInsights, ProcessBatchAsync, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DESCRIPTION-INTEL-BATCH] Batch run failed");
            }

            var cron = _settings.Scheduling.DescriptionIntelligenceCron;
            var delay = CronScheduler.UntilNext(cron, TimeSpan.FromMinutes(15));

            _logger.LogInformation("[DESCRIPTION-INTEL-BATCH] Next run in {Delay}", delay);
            try
            {
                await _configurationStore.WaitForDelayOrChangeAsync(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<TaskRunResult> ProcessBatchAsync(CancellationToken ct)
    {
        if (!_featureGate.CanExecute(AiFeature.DescriptionIntelligence, MediaEngine.Domain.Enums.AiModelRole.TextScholar))
        {
            _logger.LogDebug("[DESCRIPTION-INTEL-BATCH] Feature disabled or selected model unavailable — skipping before scan");
            return TaskRunResult.Skipped("Description insights is off");
        }

        // Check if resources allow enrichment right now.
        var modelSize = _settings.Models.TextScholar.SizeMB;
        var recommendation = _resourceMonitor.CanLoadModel(modelSize);
        if (!recommendation.CanLoad)
        {
            _logger.LogInformation("[DESCRIPTION-INTEL-BATCH] Deferring: {Reason}", recommendation.Reason);
            return TaskRunResult.Skipped("Waiting: the machine is busy");
        }

        _logger.LogDebug(
            "[DESCRIPTION-INTEL-BATCH] Using selected {Profile} profile for enrichment",
            _settings.ResourceProfile);
        var modelId = _settings.Models.TextScholar.CatalogKey ?? _settings.ResourceProfile;
        var canonicalRepo = _canonicals;
        var workRepo = _works;
        var descIntel = _descriptionIntelligence;
        var personRecon = _personReconciliation;
        var claimRepo = _claims;
        var scoringEngine = _scoring;
        var configLoader = _configLoader;
        var arrayRepo = _canonicalArrays;
        var searchIndex = _searchIndex;
        var providers = _providers;
        var identityService = _identityService;
        var harvesting = _harvesting;

        var batchSize = Math.Clamp(_settings.EnrichmentBatchSize, 1, 50);

        // Find entities that have a description (or plot_summary) but no themes yet.
        var entityIds = await canonicalRepo.GetEntitiesNeedingEnrichmentAsync(
            hasField: "description",
            missingField: "themes",
            limit: batchSize,
            ct: ct).ConfigureAwait(false);

        if (entityIds.Count == 0)
        {
            _logger.LogDebug("[DESCRIPTION-INTEL-BATCH] No entities needing enrichment");
            return TaskRunResult.Succeeded(0, "Nothing needed enriching.");
        }

        _logger.LogInformation(
            "[DESCRIPTION-INTEL-BATCH] Processing {Count} entities needing description enrichment",
            entityIds.Count);

        int processed = 0;

        for (int i = 0; i < entityIds.Count; i++)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            var entityId = entityIds[i];
            string? inputFingerprint = null;

            try
            {
                // Determine media category from canonicals for vocabulary selection.
                var canonicals = await canonicalRepo.GetByEntityAsync(entityId, ct)
                    .ConfigureAwait(false);

                var mediaCategory = canonicals
                    .FirstOrDefault(c => string.Equals(c.Key, "media_type", StringComparison.OrdinalIgnoreCase))
                    ?.Value ?? "books";
                var sourceDescription = canonicals
                    .FirstOrDefault(c => string.Equals(c.Key, "description", StringComparison.OrdinalIgnoreCase))
                    ?.Value
                    ?? canonicals.FirstOrDefault(c =>
                        string.Equals(c.Key, "plot_summary", StringComparison.OrdinalIgnoreCase))?.Value
                    ?? string.Empty;
                inputFingerprint = AiFeatureFingerprint.Compute(sourceDescription, mediaCategory);
                var featureState = await _featurePersistence.GetAiFeatureStateAsync(
                    entityId,
                    FeatureKey,
                    ct).ConfigureAwait(false);
                if (featureState?.IsCurrent(inputFingerprint) == true
                    || featureState?.CanAttempt(DateTimeOffset.UtcNow) == false)
                {
                    continue;
                }

                var diResult = await descIntel.AnalyzeAsync(entityId, mediaCategory, ct)
                    .ConfigureAwait(false);

                if (diResult is not null)
                {
                    {
                        var persistenceResult = await _featurePersistence.ReplaceAiFeatureAsync(
                            new AiFeatureWriteRequest(
                                entityId,
                                FeatureKey,
                                new Dictionary<string, IReadOnlyList<string>>
                                {
                                    ["themes"] = diResult.Themes,
                                    ["mood"] = diResult.Mood,
                                    ["content_warnings"] = diResult.ContentWarnings,
                                },
                                new Dictionary<string, string?>
                                {
                                    ["tldr"] = diResult.Tldr,
                                    ["setting"] = diResult.Setting,
                                    ["time_period"] = diResult.TimePeriod,
                                    ["audience"] = diResult.Audience,
                                    ["pace"] = diResult.Pace,
                                },
                                WellKnownProviders.AiProvider,
                                ClaimConfidence.AiDescription,
                                PublishThreshold: 0.65,
                                ReviewThreshold: 0.75,
                                modelId,
                                PromptVersion,
                                inputFingerprint),
                            ct).ConfigureAwait(false);
                        if (persistenceResult.PublishedFields.Count > 0)
                        {
                            processed++;
                        }

                        _logger.LogDebug(
                            "[DESCRIPTION-INTEL-BATCH] Enriched entity {EntityId} — {Themes} themes, {Mood} mood",
                            entityId, diResult.Themes.Count, diResult.Mood.Count);
                    }

                    // ── AI person signal fallback ──────────────────────────────
                    // For each person extracted from the description, check if a
                    // companion QID claim already exists. If not, run standalone
                    // person reconciliation at confidence 0.75 (lowest tier).
                    if (personRecon is not null && diResult.People.Count > 0)
                    {
                        try
                        {
                            // Get the work title for notable-work matching.
                            var titleCanonical = canonicals
                                .FirstOrDefault(c => string.Equals(c.Key, "title", StringComparison.OrdinalIgnoreCase));
                            var workTitleForSearch = titleCanonical?.Value;

                            // Get existing QID claims to avoid redundant searches.
                            var existingClaims = await claimRepo.GetByEntityAsync(entityId, ct)
                                .ConfigureAwait(false);
                            var existingQidKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var claim in existingClaims)
                            {
                                if (claim.ClaimKey.EndsWith("_qid", StringComparison.OrdinalIgnoreCase)
                                    && !string.IsNullOrWhiteSpace(claim.ClaimValue))
                                {
                                    existingQidKeys.Add(claim.ClaimKey);
                                }
                            }

                            var reconAdapter = providers
                                .OfType<ReconciliationAdapter>()
                                .FirstOrDefault();
                            var wikidataProviderId = reconAdapter?.ProviderId
                                ?? WellKnownProviders.Wikidata;

                            var aiPersonClaims = new List<ProviderClaim>();

                            foreach (var person in diResult.People)
                            {
                                if (string.IsNullOrWhiteSpace(person.Name) || person.Confidence < 0.50)
                                {
                                    continue;
                                }

                                // Normalize the AI role to match PersonReference roles.
                                var normalizedRole = NormalizeAiRole(person.Role);
                                if (normalizedRole is null)
                                {
                                    continue;
                                }

                                // Map role to QID claim key.
                                var qidKey = normalizedRole switch
                                {
                                    "Author" => "author_qid",
                                    "Narrator" => "narrator_qid",
                                    "Director" => "director_qid",
                                    "Screenwriter" => "screenwriter_qid",
                                    "Composer" => "composer_qid",
                                    "Actor" => "cast_member_qid",
                                    _ => null,
                                };

                                if (qidKey is null)
                                {
                                    continue;
                                }

                                // Skip if a QID already exists for this role (from structured properties
                                // or Phase 3 standalone search — higher confidence tiers).
                                if (existingQidKeys.Contains(qidKey))
                                {
                                    _logger.LogDebug(
                                        "[DESCRIPTION-INTEL-BATCH] AI person '{Name}' ({Role}) skipped — {QidKey} already resolved for entity {EntityId}",
                                        person.Name, person.Role, qidKey, entityId);
                                    continue;
                                }

                                try
                                {
                                    var searchResult = await personRecon.SearchPersonAsync(
                                        person.Name, normalizedRole, workTitleForSearch, ct)
                                        .ConfigureAwait(false);

                                    if (searchResult is not null)
                                    {
                                        aiPersonClaims.Add(new ProviderClaim(
                                            qidKey,
                                            $"{searchResult.WikidataQid}::{searchResult.Name}",
                                            0.75));

                                        // Also deposit the name claim if not already present.
                                        var nameKey = qidKey.Replace("_qid", "");
                                        var hasNameClaim = existingClaims.Any(c =>
                                            string.Equals(c.ClaimKey, nameKey, StringComparison.OrdinalIgnoreCase));
                                        if (!hasNameClaim)
                                        {
                                            aiPersonClaims.Add(new ProviderClaim(nameKey, searchResult.Name, 0.75));
                                        }

                                        _logger.LogInformation(
                                            "[DESCRIPTION-INTEL-BATCH] AI person fallback resolved: '{Name}' ({Role}) → {QID} '{WikiName}' (score={Score:F2}) for entity {EntityId}",
                                            person.Name, normalizedRole, searchResult.WikidataQid,
                                            searchResult.Name, searchResult.Score, entityId);
                                    }
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException)
                                {
                                    _logger.LogWarning(ex,
                                        "[DESCRIPTION-INTEL-BATCH] AI person search failed for '{Name}' ({Role}); continuing",
                                        person.Name, person.Role);
                                }
                            }

                            // Persist AI-resolved person QID claims and create Person records.
                            if (aiPersonClaims.Count > 0)
                            {
                                // Phase 3c: lineage-aware persist so cast_member_qid /
                                // director_qid for TV episodes mirror onto the show Work.
                                WorkLineage? lineage = null;
                                try { lineage = await workRepo.GetLineageByAssetAsync(entityId, ct).ConfigureAwait(false); }
                                catch (Exception ex)
                                {
                                    _logger.LogDebug(ex,
                                        "[DESCRIPTION-INTEL-BATCH] Phase 3c lineage lookup failed for entity {EntityId} — parent mirror skipped",
                                        entityId);
                                }

                                await ScoringHelper.PersistAndScoreWithLineageAsync(
                                    entityId, aiPersonClaims, wikidataProviderId, lineage,
                                    claimRepo, canonicalRepo, scoringEngine, configLoader,
                                    providers, ct, arrayRepo, _logger, searchIndex,
                                    decisionSourceProviderId: WellKnownProviders.AiProvider).ConfigureAwait(false);

                                // Create Person records for the newly resolved QIDs.
                                var personRefs = aiPersonClaims
                                    .Where(c => c.Key.EndsWith("_qid", StringComparison.OrdinalIgnoreCase))
                                    .Select(c =>
                                    {
                                        var parts = c.Value.Split("::", 2);
                                        var qid = parts[0];
                                        var name = parts.Length > 1 ? parts[1] : qid;
                                        var role = c.Key.Replace("_qid", "") switch
                                        {
                                            "author" => "Author",
                                            "narrator" => "Narrator",
                                            "director" => "Director",
                                            "screenwriter" => "Screenwriter",
                                            "composer" => "Composer",
                                            "cast_member" => "Actor",
                                            _ => "Author",
                                        };
                                        return new PersonReference(role, name, qid);
                                    })
                                    .ToList();

                                if (personRefs.Count > 0)
                                {
                                    var personRequests = await identityService.EnrichAsync(
                                        entityId, personRefs, ct).ConfigureAwait(false);

                                    foreach (var personReq in personRequests)
                                    {
                                        try
                                        {
                                            await harvesting.ProcessSynchronousAsync(personReq, ct)
                                                .ConfigureAwait(false);
                                        }
                                        catch (Exception ex) when (ex is not OperationCanceledException)
                                        {
                                            _logger.LogWarning(ex,
                                                "[DESCRIPTION-INTEL-BATCH] Person enrichment failed for AI-resolved person {Id}; continuing",
                                                personReq.EntityId);
                                        }
                                    }
                                }

                                _logger.LogInformation(
                                    "[DESCRIPTION-INTEL-BATCH] AI person fallback: {ClaimCount} claims, {PersonCount} persons for entity {EntityId}",
                                    aiPersonClaims.Count, personRefs.Count, entityId);
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogWarning(ex,
                                "[DESCRIPTION-INTEL-BATCH] AI person fallback failed for entity {EntityId}; continuing",
                                entityId);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (AiRequestDeferredException)
            {
                // The engine room found the machine busy (first-run setup, someone using the app, or low resources).
                return TaskRunResult.Skipped("Waiting: the machine is busy");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogInformation(
                    "[DESCRIPTION-INTEL-BATCH] Yielding entity {EntityId} to interactive traffic",
                    entityId);
                return TaskRunResult.Skipped("Paused to make way for something you were doing");
            }
            catch (Exception ex)
            {
                if (inputFingerprint is not null)
                {
                    try
                    {
                        var failure = await _featurePersistence.RecordAiFeatureFailureAsync(
                            new AiFeatureFailureRequest(
                                entityId,
                                FeatureKey,
                                WellKnownProviders.AiProvider,
                                modelId,
                                PromptVersion,
                                inputFingerprint,
                                ex.Message,
                                Category: BackgroundJobOutcomeClassifier.Classify(ex, ct)),
                            ct).ConfigureAwait(false);
                        if (failure.Status == AiFeatureStatus.Poisoned)
                        {
                            _logger.LogError(
                                "[DESCRIPTION-INTEL-BATCH] Quarantined poison entity {Id} after {Attempts} attempts",
                                entityId,
                                failure.Attempts);
                        }
                    }
                    catch (Exception persistenceEx) when (persistenceEx is not OperationCanceledException)
                    {
                        _logger.LogError(
                            persistenceEx,
                            "[DESCRIPTION-INTEL-BATCH] Could not persist failure state for entity {Id}",
                            entityId);
                    }
                }
                _logger.LogWarning(ex,
                    "[DESCRIPTION-INTEL-BATCH] Failed to process entity {Id} — skipping",
                    entityId);
            }

            // Yield resources between entities to avoid saturating the LLM.
            if (i < entityIds.Count - 1)
            {
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
        }

        _logger.LogInformation(
            "[DESCRIPTION-INTEL-BATCH] Processed {Processed}/{Total} entities",
            processed, entityIds.Count);
        return TaskRunResult.Succeeded(processed);
    }

    /// <summary>
    /// Maps AI-extracted role strings (which may be lowercase, abbreviated, or variant)
    /// to the canonical PersonReference role names.
    /// Returns null for unrecognized roles.
    /// </summary>
    private static string? NormalizeAiRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return null;
        }

        return role.Trim().ToLowerInvariant() switch
        {
            "author" or "writer" or "novelist" => "Author",
            "narrator" or "reader" or "voice" => "Narrator",
            "director" or "filmmaker" => "Director",
            "screenwriter" or "screenplay" or "writer (screenplay)" => "Screenwriter",
            "composer" or "music" or "score" => "Composer",
            "actor" or "actress" or "cast" or "cast member" or "star" => "Actor",
            _ => null,
        };
    }
}
