using System.Collections.Concurrent;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Intelligence.Contracts;
using MediaEngine.Intelligence.Models;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Background metadata harvesting service.
///
/// Accepts <see cref="HarvestRequest"/> items from the ingestion pipeline and
/// dispatches them to registered <see cref="IExternalMetadataProvider"/> adapters
/// without blocking the ingestion thread.
///
/// Architecture:
/// - A bounded channel with wait-based backpressure decouples producers
///   (ingestion) from consumers (adapters) without silently dropping requests.
/// - Three owned consumer loops limit simultaneous in-flight adapter calls.
/// - First provider to return claims wins; remaining providers for that request
///   are skipped.
/// - After persisting new claims, the entity is re-scored and canonical values
///   are upserted.  A <c>"MetadataHarvested"</c> SignalR event is published.
/// - For <see cref="EntityType.Person"/> entities, Wikidata claims trigger a
///   call to <see cref="IPersonRepository.UpdateEnrichmentAsync"/> and a
///   <c>"PersonEnriched"</c> SignalR event.
///
/// Spec: Phase 9 – Non-Blocking Harvesting.
/// </summary>
public sealed class MetadataHarvestingService : BackgroundService, IMetadataHarvestingService
{
    // ── Channel ───────────────────────────────────────────────────────────────

    private readonly MetadataHarvestQueue _queue;
    private Task? _processingTask;

    // ── Concurrency ───────────────────────────────────────────────────────────

    /// <summary>
    /// Per-QID lock preventing concurrent merge attempts for the same Wikidata
    /// identifier. The merge is idempotent but the lock eliminates redundant
    /// ReassignAllLinksAsync / DeleteAsync / PersonMerged log noise.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _qidMergeLocks = new(StringComparer.OrdinalIgnoreCase);

    // ── Dependencies ──────────────────────────────────────────────────────────

    private readonly IReadOnlyList<IExternalMetadataProvider> _providers;
    private readonly IMetadataClaimRepository _claimRepo;
    private readonly ICanonicalValueRepository _canonicalRepo;
    private readonly IPersonRepository _personRepo;
    private readonly IFictionalEntityRepository _fictionalEntityRepo;
    private readonly IRelationshipPopulationService _relPopService;
    private readonly IScoringEngine _scoringEngine;
    private readonly IEventPublisher _eventPublisher;
    private readonly IConfigurationLoader _configLoader;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IImageCacheRepository _imageCache;
    private readonly ImageDownloadCoordinator _imageDownloadCoordinator;
    private readonly ISystemActivityRepository _activityRepo;
    private readonly IQidLabelRepository _qidLabelRepo;
    private readonly IEntityTimelineRepository? _timelineRepo;
    private readonly IEntityAssetRepository? _entityAssetRepo;
    private readonly AssetPathService _assetPathService;
    private readonly ILogger<MetadataHarvestingService> _logger;

    // ── Constructor ───────────────────────────────────────────────────────────

    public MetadataHarvestingService(
        IEnumerable<IExternalMetadataProvider> providers,
        IMetadataClaimRepository claimRepo,
        ICanonicalValueRepository canonicalRepo,
        IPersonRepository personRepo,
        IFictionalEntityRepository fictionalEntityRepo,
        IRelationshipPopulationService relPopService,
        MetadataHarvestQueue queue,
        IScoringEngine scoringEngine,
        IEventPublisher eventPublisher,
        IConfigurationLoader configLoader,
        IHttpClientFactory httpFactory,
        IImageCacheRepository imageCache,
        ISystemActivityRepository activityRepo,
        IQidLabelRepository qidLabelRepo,
        AssetPathService assetPathService,
        ILogger<MetadataHarvestingService> logger,
        IEntityTimelineRepository? timelineRepo = null,
        ImageDownloadCoordinator? imageDownloadCoordinator = null,
        IEntityAssetRepository? entityAssetRepo = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(claimRepo);
        ArgumentNullException.ThrowIfNull(canonicalRepo);
        ArgumentNullException.ThrowIfNull(personRepo);
        ArgumentNullException.ThrowIfNull(fictionalEntityRepo);
        ArgumentNullException.ThrowIfNull(relPopService);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(scoringEngine);
        ArgumentNullException.ThrowIfNull(eventPublisher);
        ArgumentNullException.ThrowIfNull(configLoader);
        ArgumentNullException.ThrowIfNull(httpFactory);
        ArgumentNullException.ThrowIfNull(imageCache);
        ArgumentNullException.ThrowIfNull(activityRepo);
        ArgumentNullException.ThrowIfNull(qidLabelRepo);
        ArgumentNullException.ThrowIfNull(assetPathService);
        ArgumentNullException.ThrowIfNull(logger);

        _providers = providers.ToList();
        _claimRepo = claimRepo;
        _canonicalRepo = canonicalRepo;
        _personRepo = personRepo;
        _fictionalEntityRepo = fictionalEntityRepo;
        _relPopService = relPopService;
        _queue = queue;
        _scoringEngine = scoringEngine;
        _eventPublisher = eventPublisher;
        _configLoader = configLoader;
        _httpFactory = httpFactory;
        _imageCache = imageCache;
        _imageDownloadCoordinator = imageDownloadCoordinator ?? ImageDownloadCoordinator.Shared;
        _activityRepo = activityRepo;
        _qidLabelRepo = qidLabelRepo;
        _assetPathService = assetPathService;
        _timelineRepo = timelineRepo;
        _entityAssetRepo = entityAssetRepo;
        _logger = logger;

    }

    // ── IMetadataHarvestingService ────────────────────────────────────────────

    /// <inheritdoc/>
    public ValueTask EnqueueAsync(HarvestRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _queue.EnqueueAsync(request, ct);
    }

    /// <inheritdoc/>
    public int PendingCount => _queue.PendingCount;

    /// <inheritdoc/>
    public Task ProcessSynchronousAsync(HarvestRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ProcessOneAsync(request, ct);
    }

    // ── Background loop ───────────────────────────────────────────────────────

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _processingTask = _queue.ExecuteAsync(
            consumerCount: 3,
            ProcessOneAsync,
            (request, ex) => _logger.LogWarning(
                ex,
                "Unhandled error processing harvest request for entity {Id}",
                request.EntityId),
            stoppingToken);
        return _processingTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.TryComplete();

        if (_processingTask is not null)
        {
            await _processingTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessOneAsync(HarvestRequest request, CancellationToken ct)
    {
        var allProviderConfigs = _configLoader.LoadAllProviders();
        var scoring = _configLoader.LoadScoring();

        // Build per-provider endpoint map (avoids key collisions across providers).
        var providerEndpoints = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pc in allProviderConfigs)
        {
            providerEndpoints[pc.Name] = new Dictionary<string, string>(pc.Endpoints, StringComparer.OrdinalIgnoreCase);
        }

        var enabledProviders = ProviderExecutionFilter.EnabledProviders(_providers, allProviderConfigs);

        // Build provider weight maps from executable provider configs.
        var (providerWeights, providerFieldWeights) =
            ScoringHelper.BuildWeightMaps(allProviderConfigs, enabledProviders);

        var sparqlBaseUrl = ResolveSparqlBaseUrl(providerEndpoints);

        // For Person entities: skip only when a concurrent worker already filled the visible profile.
        if (request.EntityType == EntityType.Person && request.EntityId != Guid.Empty)
        {
            var alreadyEnriched = await _personRepo.FindByIdAsync(request.EntityId, ct).ConfigureAwait(false);
            if (alreadyEnriched?.EnrichedAt is not null
                && !alreadyEnriched.IsGroup
                && HasCompletePersonProfile(alreadyEnriched))
            {
                _logger.LogDebug(
                    "Person {Id} already enriched at {EnrichedAt} — skipping duplicate harvest",
                    request.EntityId, alreadyEnriched.EnrichedAt);
                return;
            }
        }

        foreach (var provider in enabledProviders)
        {
            if (request.MediaType == MediaType.TV
                && string.Equals(provider.Name, "tmdb", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!provider.CanHandle(request.MediaType) || !provider.CanHandle(request.EntityType))
            {
                continue;
            }

            var baseUrl = ResolveBaseUrl(provider, providerEndpoints);
            var lookupRequest = BuildLookupRequest(request, provider, baseUrl, sparqlBaseUrl);

            IReadOnlyList<ProviderClaim> providerClaims;
            try
            {
                providerClaims = await provider.FetchAsync(lookupRequest, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Provider {Provider} threw unexpectedly for entity {Id}; skipping",
                    provider.Name, request.EntityId);
                continue;
            }

            // Graph evidence carries statement context that the flattened provider
            // claim stream cannot represent. Scalar event chronology (P585/P580/
            // P582/P4895) is not entity-valued, so include it in the normal claim
            // and scoring path before canonical persistence rather than losing it
            // behind the relationship-only Stage 3 boundary.
            var entitySubType = request.Hints.GetValueOrDefault("entity_sub_type") ?? request.EntityType.ToString();
            var entityQid = request.Hints.GetValueOrDefault(BridgeIdKeys.WikidataQid);
            var graphEvidence = request.EntityType is EntityType.Character or EntityType.Location or EntityType.Organization or EntityType.Event or EntityType.Object
                && provider is IFictionalEntityGraphEvidenceProvider graphProvider
                && !string.IsNullOrWhiteSpace(entityQid)
                ? await graphProvider.FetchFictionalEntityGraphEvidenceAsync(entityQid, entitySubType, ct).ConfigureAwait(false)
                : null;

            if (graphEvidence?.ScalarStatements.Count > 0)
            {
                providerClaims = providerClaims
                    .Concat(graphEvidence.ScalarStatements
                        .Where(statement => !string.IsNullOrWhiteSpace(statement.ClaimKey) && !string.IsNullOrWhiteSpace(statement.Value))
                        .Select(statement => new ProviderClaim(statement.ClaimKey, statement.Value, statement.Confidence)))
                    .GroupBy(claim => $"{claim.Key}\u001F{claim.Value}", StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.OrderByDescending(claim => claim.Confidence).First())
                    .ToList();
            }

            if (providerClaims.Count == 0)
            {
                continue;
            }

            // Wrap provider claims as domain MetadataClaim rows.
            var domainClaims = providerClaims
                .Select(pc => new MetadataClaim
                {
                    Id = Guid.NewGuid(),
                    EntityId = request.EntityId,
                    ProviderId = provider.ProviderId,
                    ClaimKey = pc.Key,
                    ClaimValue = pc.Value,
                    Confidence = pc.Confidence,
                    ClaimedAt = DateTimeOffset.UtcNow,
                    IsUserLocked = false,
                })
                .ToList();

            // Persist claims (append-only).
            await _claimRepo.InsertBatchAsync(domainClaims, ct).ConfigureAwait(false);

            // Load ALL claims for this entity and re-score.
            var allClaims = await _claimRepo.GetByEntityAsync(request.EntityId, ct).ConfigureAwait(false);
            var scoringConfig = new ScoringConfiguration
            {
                AutoLinkThreshold = scoring.AutoLinkThreshold,
                ConflictThreshold = scoring.ConflictThreshold,
                ConflictEpsilon = scoring.ConflictEpsilon,
                StaleClaimDecayDays = scoring.StaleClaimDecayDays,
                StaleClaimDecayFactor = scoring.StaleClaimDecayFactor,
            };
            var scoringContext = new ScoringContext
            {
                EntityId = request.EntityId,
                Claims = allClaims,
                ProviderWeights = providerWeights,
                ProviderFieldWeights = providerFieldWeights,
                Configuration = scoringConfig,
                DetectedMediaType = request.MediaType,
            };

            var scored = await _scoringEngine.ScoreEntityAsync(scoringContext, ct).ConfigureAwait(false);

            // Upsert canonical values (current best answers).
            // Phase B: also persist the IsConflicted flag from the scoring engine.
            var canonicals = scored.FieldScores
                .Where(f => !string.IsNullOrEmpty(f.WinningValue))
                .Select(f => new CanonicalValue
                {
                    EntityId = request.EntityId,
                    Key = f.Key,
                    Value = f.WinningValue!,
                    LastScoredAt = scored.ScoredAt,
                    IsConflicted = f.IsConflicted,
                    WinningProviderId = f.WinningProviderId,
                })
                .ToList();
            await _canonicalRepo.UpsertBatchAsync(canonicals, ct).ConfigureAwait(false);

            // Special handling: Wikidata claims for a Person entity.
            if (request.EntityType == EntityType.Person)
            {
                await HandlePersonEnrichmentAsync(request, providerClaims, provider, ct)
                    .ConfigureAwait(false);
            }

            // Special handling: Wikidata claims for fictional entity types.
            if (request.EntityType is EntityType.Character or EntityType.Location or EntityType.Organization or EntityType.Event or EntityType.Object)
            {
                await HandleFictionalEntityEnrichmentAsync(request, canonicals, provider, graphEvidence, ct)
                    .ConfigureAwait(false);
            }

            // Publish MetadataHarvested event.
            var updatedFields = domainClaims.Select(c => c.ClaimKey).Distinct().ToList();
            await _eventPublisher.PublishAsync(
                SignalREvents.MetadataHarvested,
                new MetadataHarvestedEvent(request.EntityId, provider.Name, updatedFields),
                ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Harvested {Count} claims from {Provider} for entity {Id}",
                domainClaims.Count, provider.Name, request.EntityId);

            // First provider to succeed wins; skip remaining providers.
            break;
        }
    }

    /// <summary>
    /// After a fictional entity (Character/Location/Organization/Event/Object) is enriched from
    /// Wikidata SPARQL, updates the entity's <c>EnrichedAt</c> timestamp, populates
    /// relationship edges from <c>_qid</c> claims, and triggers debounced
    /// <c>universe.xml</c> writing.
    /// </summary>
    private async Task HandleFictionalEntityEnrichmentAsync(
        HarvestRequest request,
        IReadOnlyList<CanonicalValue> canonicals,
        IExternalMetadataProvider provider,
        FictionalEntityGraphEvidence? graphEvidence,
        CancellationToken ct)
    {
        // Only Wikidata produces fictional entity enrichment claims.
        if (!string.Equals(provider.Name, "wikidata", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(provider.Name, "wikidata_reconciliation", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            // Build canonical values dictionary for relationship population.
            var canonicalDict = canonicals
                .ToDictionary(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase);

            if (canonicalDict.Count == 0)
            {
                _logger.LogWarning(
                    "Fictional entity enrichment returned no canonical values for entity {EntityId}; leaving it retryable",
                    request.EntityId);
                return;
            }

            // The generic provider-claim stream deliberately remains flat for scoring.
            // When supported, retrieve its statement-level evidence separately so P10663,
            // P580/P582/P585, P4895, and P7528 remain attached to their asserted fact.
            var entitySubType = request.Hints.GetValueOrDefault("entity_sub_type") ?? request.EntityType.ToString();
            var entityQid = request.Hints.GetValueOrDefault(BridgeIdKeys.WikidataQid);

            // P1080 is authoritative when present. In its absence, retain the owned-work
            // narrative root instead of erasing existing universe organization.
            var universeQid = graphEvidence?.NarrativeUniverseQid
                ?? request.Hints.GetValueOrDefault("universe_qid");
            var universeLabel = graphEvidence?.NarrativeUniverseLabel;
            if (!string.IsNullOrWhiteSpace(graphEvidence?.NarrativeUniverseQid))
            {
                await _fictionalEntityRepo.UpdateUniverseAsync(
                    request.EntityId,
                    graphEvidence.NarrativeUniverseQid,
                    graphEvidence.NarrativeUniverseLabel,
                    ct).ConfigureAwait(false);
            }

            // Resolve depth parameters from hint and config.
            var currentDepth = 0;
            if (request.Hints.TryGetValue("enrichment_depth", out var depthStr) &&
                int.TryParse(depthStr, out var parsedDepth))
            {
                currentDepth = parsedDepth;
            }

            var maxDepth = 1;
            try
            {
                maxDepth = _configLoader.LoadHydration().FictionalEntityEnrichmentDepth;
            }
            catch
            {
                // Fall back to default if config is unavailable.
            }

            // Populate relationship edges from _qid claims.
            await _relPopService.PopulateAsync(
                entityQid ?? string.Empty,
                canonicalDict,
                universeQid ?? string.Empty,
                universeLabel,
                contextWorkQid: null,
                temporalQualifiers: null,
                statementEvidence: graphEvidence?.Statements,
                currentDepth: currentDepth,
                maxDepth: maxDepth,
                ct: ct).ConfigureAwait(false);

            await _fictionalEntityRepo.UpdateEnrichmentAsync(
                request.EntityId,
                canonicalDict.GetValueOrDefault("name"),
                canonicalDict.GetValueOrDefault(MetadataFieldConstants.Description)
                    ?? canonicalDict.GetValueOrDefault(MetadataFieldConstants.ShortDescription),
                canonicalDict.GetValueOrDefault("image_url"),
                DateTimeOffset.UtcNow,
                ct).ConfigureAwait(false);

            await PersistFictionalEntityArtworkAsync(
                request.EntityId,
                canonicalDict.GetValueOrDefault("image_url"),
                ct).ConfigureAwait(false);

            // Determine action type for activity log.
            var actionType = entitySubType switch
            {
                "Location" => SystemActionType.LocationEnriched,
                "Organization" => SystemActionType.OrganizationEnriched,
                _ => SystemActionType.CharacterEnriched,
            };

            // Log activity.
            var label = request.Hints.GetValueOrDefault("label") ?? request.EntityId.ToString();

            // Cache fictional entity QID → label for offline resolution.
            if (!string.IsNullOrWhiteSpace(entityQid) && !string.IsNullOrWhiteSpace(label))
            {
                try
                {
                    await _qidLabelRepo.UpsertAsync(entityQid, label, null, entitySubType, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Failed to cache fictional entity QID label for {Qid}", entityQid);
                }
            }

            await _activityRepo.LogAsync(new SystemActivityEntry
            {
                ActionType = actionType,
                EntityId = request.EntityId,
                EntityType = entitySubType,
                Detail = $"Fictional entity \"{label}\" enriched from Wikidata",
                IngestionRunId = request.IngestionRunId,
            }, ct).ConfigureAwait(false);

            // Publish SignalR event.
            await _eventPublisher.PublishAsync(
                SignalREvents.FictionalEntityEnriched,
                new FictionalEntityEnrichedEvent(request.EntityId, label, entitySubType, universeQid),
                ct).ConfigureAwait(false);

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Fictional entity enrichment post-processing failed for entity {Id}",
                request.EntityId);
        }
    }

    private static bool HasCompletePersonProfile(Person person)
        => !string.IsNullOrWhiteSpace(person.Biography)
           && (!string.IsNullOrWhiteSpace(person.HeadshotUrl)
               || !string.IsNullOrWhiteSpace(person.LocalHeadshotPath));

    private async Task PersistFictionalEntityArtworkAsync(
        Guid entityId,
        string? imageUrl,
        CancellationToken ct)
    {
        if (_entityAssetRepo is null || string.IsNullOrWhiteSpace(imageUrl))
        {
            return;
        }

        try
        {
            var existing = (await _entityAssetRepo.GetByEntityAsync(
                    entityId.ToString("D"),
                    AssetType.CharacterPortrait.ToString(),
                    ct).ConfigureAwait(false))
                .FirstOrDefault(asset => string.Equals(asset.ImageUrl, imageUrl, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && !string.IsNullOrWhiteSpace(existing.LocalImagePath)
                                     && File.Exists(existing.LocalImagePath))
            {
                await _entityAssetRepo.SetPreferredAsync(existing.Id, ct).ConfigureAwait(false);
                return;
            }

            await using var downloadLease = await _imageDownloadCoordinator.AcquireAsync(imageUrl, ct).ConfigureAwait(false);
            byte[] bytes;
            var cachedPath = await _imageCache.FindBySourceUrlAsync(imageUrl, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(cachedPath) && File.Exists(cachedPath))
            {
                bytes = await File.ReadAllBytesAsync(cachedPath, ct).ConfigureAwait(false);
            }
            else
            {
                using var client = _httpFactory.CreateClient("headshot_download");
                using var response = await client.GetAsync(imageUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                bytes = await BoundedHttpContent.ReadImageAsync(response.Content, ct).ConfigureAwait(false);
            }

            if (bytes.Length == 0)
            {
                return;
            }

            var asset = existing ?? new EntityAsset
            {
                Id = Guid.NewGuid(),
                EntityId = entityId.ToString("D"),
                EntityType = "FictionalEntity",
                AssetTypeValue = AssetType.CharacterPortrait.ToString(),
                ImageUrl = imageUrl,
                SourceProvider = "wikidata_reconciliation",
                AssetClassValue = "Artwork",
                StorageLocationValue = "Central",
                OwnerScope = "FictionalEntity",
                IsPreferred = true,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            asset.LocalImagePath ??= _assetPathService.GetCentralAssetPath(
                "FictionalEntity",
                entityId,
                AssetType.CharacterPortrait.ToString(),
                asset.Id,
                MediaMimeTypes.InferImageExtension(imageUrl) ?? ".jpg");
            AssetPathService.EnsureDirectory(asset.LocalImagePath);
            await BoundedHttpContent.WriteFileAtomicallyAsync(asset.LocalImagePath, bytes, ct).ConfigureAwait(false);
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
            await _imageCache.InsertAsync(hash, asset.LocalImagePath, imageUrl, ct).ConfigureAwait(false);
            ArtworkVariantHelper.StampMetadataAndRenditions(asset, _assetPathService);
            await _entityAssetRepo.UpsertAsync(asset, ct).ConfigureAwait(false);
            await _entityAssetRepo.SetPreferredAsync(asset.Id, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Optional Wikidata artwork could not be persisted for fictional entity {EntityId}",
                entityId);
        }
    }

    private async Task HandlePersonEnrichmentAsync(
        HarvestRequest request,
        IReadOnlyList<ProviderClaim> claims,
        IExternalMetadataProvider provider,
        CancellationToken ct)
    {
        var personSw = System.Diagnostics.Stopwatch.StartNew();

        var qid = claims.FirstOrDefault(c => c.Key == BridgeIdKeys.WikidataQid)?.Value;
        var headshotUrl = claims.FirstOrDefault(c => c.Key == "headshot_url")?.Value;
        var biography = claims.FirstOrDefault(c => c.Key == "biography")?.Value;
        var shortDescription = claims.FirstOrDefault(c => c.Key == MetadataFieldConstants.ShortDescription)?.Value;
        var name = claims.FirstOrDefault(c => c.Key == "name")?.Value;

        if (qid is null && headshotUrl is null && biography is null && shortDescription is null && name is null)
        {
            return;
        }

        name = await ResolvePersonDisplayNameAsync(
            qid,
            name,
            request.Hints.GetValueOrDefault("name"),
            ct).ConfigureAwait(false);

        // ── Pseudonym detection from current claims ───────────────────────────
        // Compute isPseudonym HERE — before the dedup block — so the merge guard
        // can use the in-memory value even before UpdateBiographicalFieldsAsync
        // has persisted it to the DB (which happens further down).
        //
        // A person is a pseudonym when:
        //   1. P31 (instance_of) identifies a pen name or collective pseudonym, OR
        //   2. P1773 (attributed_to) claims exist — meaning real people stand behind
        //      this name, OR
        //   3. P527 (has_parts) claims exist with QID references — collective pen
        //      names like "James S. A. Corey" use P527 to list constituent authors
        //      (Daniel Abraham + Ty Franck). The Data Extension API may not return
        //      P31 for some entities, so P527 presence is a reliable fallback signal.
        // P527 is interpreted as pseudonym evidence only after ruling out a
        // musical group; bands use the same property for their actual members.
        // Check the _qid claim (stable Wikidata IDs) first; fall back to the
        // label claim for the "pen name" string in case the adapter returned
        // labels-only.
        var isGroup = IsMusicalGroup(claims);
        var isPseudonym = IsPseudonym(claims, isGroup);

        // ── Group detection from current claims ───────────────────────────────
        // ── QID-based deduplication ───────────────────────────────────────────
        // If another person record already owns this Wikidata QID, skip creating
        // a duplicate folder.  Both DB records will end up pointing at the same
        // filesystem folder (same "Name (QID)" path) via TryRenameExistingPersonFolder;
        // we just need to make sure the first-arriving enrichment wins the write.
        //
        // EXCEPTION: pseudonym pairs must NOT be merged. A pseudonym (e.g. "Richard
        // Bachman") and its real person (e.g. "Stephen King") carry different QIDs
        // but may share links. If either person is a pseudonym or they are linked
        // via person_aliases, we skip the merge and let both records coexist.
        //
        // A per-QID semaphore ensures only one thread executes the FindByQidAsync +
        // merge block at a time.  The merge is idempotent but the lock eliminates
        // redundant ReassignAllLinksAsync / DeleteAsync / PersonMerged log noise when
        // two enrichment tasks race to the same QID (e.g. James S.A. Corey Q6142591).
        bool isQidDuplicate = false;
        if (!string.IsNullOrWhiteSpace(qid))
        {
            var qidLock = _qidMergeLocks.GetOrAdd(qid, _ => new SemaphoreSlim(1, 1));
            await qidLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var canonicalPerson = await _personRepo.FindByQidAsync(qid, ct).ConfigureAwait(false);
                if (canonicalPerson is not null && canonicalPerson.Id != request.EntityId)
                {
                    // Check pseudonym relationship — never merge pseudonym ↔ real person.
                    // isPseudonym is the value computed from the current claims batch (above);
                    // this catches the case where the DB flag hasn't been persisted yet
                    // because UpdateBiographicalFieldsAsync hasn't run yet at this point.
                    var currentPerson = await _personRepo.FindByIdAsync(request.EntityId, ct).ConfigureAwait(false);
                    bool isPseudonymPair = isPseudonym                          // in-memory, from current claims
                                       || canonicalPerson.IsPseudonym           // DB flag on the canonical record
                                       || currentPerson?.IsPseudonym == true;   // DB flag on current record

                    // Also use IsPseudonymOrAliasAsync for a comprehensive DB-level check
                    // on both persons — catches alias links created in prior enrichment runs.
                    if (!isPseudonymPair)
                    {
                        isPseudonymPair = await _personRepo.IsPseudonymOrAliasAsync(request.EntityId, ct).ConfigureAwait(false)
                                       || await _personRepo.IsPseudonymOrAliasAsync(canonicalPerson.Id, ct).ConfigureAwait(false);
                    }

                    if (isPseudonymPair)
                    {
                        _logger.LogInformation(
                            "Person {Id} shares QID {Qid} with {CanonicalId} but is a pseudonym pair — skipping merge",
                            request.EntityId, qid, canonicalPerson.Id);
                    }
                    else
                    {
                        isQidDuplicate = true;
                        _logger.LogInformation(
                            "Person {Id} shares QID {Qid} with canonical person {CanonicalId} — merging",
                            request.EntityId, qid, canonicalPerson.Id);

                        // Merge: reassign all links from duplicate → canonical, then delete duplicate.
                        await _personRepo.ReassignAllLinksAsync(request.EntityId, canonicalPerson.Id, ct)
                            .ConfigureAwait(false);

                        // Delete the duplicate person folder from disk before deleting the DB record.
                        var duplicatePerson = await _personRepo.FindByIdAsync(request.EntityId, ct)
                            .ConfigureAwait(false);
                        if (duplicatePerson is not null)
                        {
                            DeletePersonFolder(duplicatePerson);
                        }

                        await _personRepo.DeleteAsync(request.EntityId, ct).ConfigureAwait(false);

                        await _activityRepo.LogAsync(new SystemActivityEntry
                        {
                            ActionType = SystemActionType.PersonMerged,
                            EntityId = canonicalPerson.Id,
                            EntityType = "Person",
                            Detail = $"Merged duplicate person \"{duplicatePerson?.Name ?? "?"}\" into canonical \"{canonicalPerson.Name}\" ({qid})",
                            IngestionRunId = request.IngestionRunId,
                        }, ct).ConfigureAwait(false);

                        await _eventPublisher.PublishAsync(
                            SignalREvents.PersonEnriched,
                            new PersonEnrichedEvent(canonicalPerson.Id, canonicalPerson.Name, canonicalPerson.HeadshotUrl, qid),
                            ct).ConfigureAwait(false);

                        return; // Merge complete — skip remaining enrichment for the deleted duplicate.
                    }
                }
            }
            finally
            {
                qidLock.Release();
            }
        }

        await _personRepo.UpdateEnrichmentAsync(request.EntityId, qid, headshotUrl, biography, name, ct)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(shortDescription))
        {
            await _canonicalRepo.UpsertBatchAsync(
                [
                    new CanonicalValue
                    {
                        EntityId = request.EntityId,
                        Key = MetadataFieldConstants.ShortDescription,
                        Value = shortDescription,
                        LastScoredAt = DateTimeOffset.UtcNow,
                    },
                ],
                ct).ConfigureAwait(false);
        }

        // Persist biographical fields from Wikidata claims.
        var dateOfBirth = claims.FirstOrDefault(c => c.Key == "date_of_birth")?.Value;
        var dateOfDeath = claims.FirstOrDefault(c => c.Key == "date_of_death")?.Value;
        var placeOfBirth = claims.FirstOrDefault(c => c.Key == "place_of_birth")?.Value;
        var placeOfDeath = claims.FirstOrDefault(c => c.Key == "place_of_death")?.Value;
        var nationality = claims.FirstOrDefault(c => c.Key == "nationality")?.Value
            ?? claims.FirstOrDefault(c => c.Key == "country_of_citizenship")?.Value;
        // isPseudonym was computed from claims before the dedup block above.

        if (dateOfBirth is not null || dateOfDeath is not null || placeOfBirth is not null ||
            placeOfDeath is not null || nationality is not null || isPseudonym || isGroup)
        {
            await _personRepo.UpdateBiographicalFieldsAsync(
                request.EntityId, dateOfBirth, dateOfDeath,
                placeOfBirth, placeOfDeath, nationality, isPseudonym, isGroup, ct)
                .ConfigureAwait(false);
        }

        // Social media and contact fields from Wikidata.
        var occupation = NormalizeMultiValue(claims.FirstOrDefault(c => c.Key == "occupation")?.Value);
        var instagram = claims.FirstOrDefault(c => c.Key == "instagram")?.Value;
        var twitter = claims.FirstOrDefault(c => c.Key == "twitter")?.Value;
        var tiktok = claims.FirstOrDefault(c => c.Key == "tiktok")?.Value;
        var mastodon = claims.FirstOrDefault(c => c.Key == "mastodon")?.Value;
        var website = claims.FirstOrDefault(c => c.Key == "website")?.Value;

        if (occupation is not null || instagram is not null || twitter is not null ||
            tiktok is not null || mastodon is not null || website is not null)
        {
            await _personRepo.UpdateSocialFieldsAsync(
                request.EntityId, occupation, instagram, twitter,
                tiktok, mastodon, website, ct)
                .ConfigureAwait(false);
        }

        // Pseudonym resolution: link pen names to real people (and vice versa).
        await ResolvePseudonymsAsync(request.EntityId, claims, isPseudonym, ct)
            .ConfigureAwait(false);

        // Musical groups own member relationships rather than author aliases.
        // Thin member identities are queued for full person hydration.
        await ResolveGroupMembersAsync(request.EntityId, claims, isGroup, ct)
            .ConfigureAwait(false);

        // Individual members frequently carry a P463 member-of edge even when
        // the group-side P527 list is incomplete. Link that reverse evidence to
        // any musical groups already present in the library.
        await ResolveMemberOfGroupsAsync(request.EntityId, claims, isGroup, ct)
            .ConfigureAwait(false);

        // Wikipedia description is now fetched by ReconciliationAdapter.FetchPersonAsync
        // (folded in as part of Task 1 cleanup). No separate call needed here.

        // Look up the person for event payload and people storage.
        var person = await _personRepo.FindByIdAsync(request.EntityId, ct)
            .ConfigureAwait(false);
        var resolvedPersonName = ResolveBestPersonName(
            person?.Name,
            name,
            request.Hints.GetValueOrDefault("name"),
            string.IsNullOrWhiteSpace(qid) ? null : $"Name pending ({qid})");
        var personName = resolvedPersonName ?? "Person";

        // Cache person QID → label for offline resolution.
        if (!string.IsNullOrWhiteSpace(qid) && !string.IsNullOrWhiteSpace(resolvedPersonName))
        {
            try
            {
                await _qidLabelRepo.UpsertAsync(qid, resolvedPersonName, shortDescription ?? biography, "Person", ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Failed to cache person QID label for {Qid}", qid);
            }
        }

        // Persist the headshot under the canonical .data/assets person asset folder.
        // Skip if this person shares a QID with a canonical record — avoids duplicate folders.
        if (!isQidDuplicate)
        {
            await PersistPersonStorageAsync(request.EntityId, person, headshotUrl, ct)
                .ConfigureAwait(false);
        }

        // Log PersonHydrated to the persistent activity ledger with headshot URL.
        try
        {
            var changesJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                name = personName,
                qid,
                headshot = headshotUrl,
            });

            await _activityRepo.LogAsync(new SystemActivityEntry
            {
                ActionType = SystemActionType.PersonHydrated,
                EntityId = request.EntityId,
                EntityType = "Person",
                CollectionName = personName,
                ChangesJson = changesJson,
                Detail = $"Person \"{personName}\" enriched from Wikidata",
                IngestionRunId = request.IngestionRunId,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to log PersonHydrated activity for {PersonId}", request.EntityId);
        }

        await _eventPublisher.PublishAsync(
            SignalREvents.PersonEnriched,
            new PersonEnrichedEvent(request.EntityId, personName, headshotUrl, qid),
            ct).ConfigureAwait(false);

        // Timeline: record person enrichment.
        if (_timelineRepo is not null)
        {
            try
            {
                await _timelineRepo.InsertEventAsync(new EntityEvent
                {
                    EntityId = request.EntityId,
                    EntityType = "Person",
                    EventType = "person_enriched",
                    Stage = null,
                    Trigger = "person_enrichment",
                    ResolvedQid = qid,
                    Detail = $"Person \"{personName}\" enriched from Wikidata",
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Failed to write person_enriched timeline event for {PersonId}",
                    request.EntityId);
            }
        }

        personSw.Stop();
        _logger.LogInformation(
            "[PERF] Person enrichment {PersonId} (\"{Name}\"): {ElapsedMs}ms (QID={Qid}, pseudonym={IsPseudonym})",
            request.EntityId, personName, personSw.ElapsedMilliseconds,
            qid ?? "(none)", isPseudonym);
    }

    /// <summary>
    /// Resolves pseudonym relationships after a Person is enriched from Wikidata.
    ///
    /// If the person is a pseudonym (P1773 = attributed_to), links to real people.
    /// If the person has pseudonyms (P742 = pseudonym), links to pen name records.
    /// </summary>
    private async Task ResolvePseudonymsAsync(
        Guid personId,
        IReadOnlyList<ProviderClaim> claims,
        bool isPseudonym,
        CancellationToken ct)
    {
        try
        {
            // P1773 (attributed_to) or P527 (has_parts): real people behind a pen name.
            // James S. A. Corey (Q6142591) uses P527 to list Daniel Abraham + Ty Franck.
            var attributedTo = claims
                .Where(c => c.Key == "attributed_to_qid" || c.Key == "has_parts_qid")
                .Select(c => c.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToList();

            if (isPseudonym && attributedTo.Count > 0)
            {
                foreach (var realQidList in attributedTo)
                {
                    var realQid = realQidList.Split("::")[0];
                    var realPerson = await _personRepo.FindByQidAsync(realQid, ct)
                        .ConfigureAwait(false);
                    if (realPerson is not null)
                    {
                        await _personRepo.LinkAliasAsync(personId, realPerson.Id, ct)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await CreateAndLinkStubPersonAsync(personId, realQid, false, ct).ConfigureAwait(false);
                    }
                }
            }

            // P742 (pseudonym): pen names used by this person.
            var pseudonymQids = claims
                .Where(c => c.Key == "pseudonym_qid")
                .Select(c => c.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToList();

            foreach (var penQidList in pseudonymQids)
            {
                var penQid = penQidList.Split("::")[0];
                var penPerson = await _personRepo.FindByQidAsync(penQid, ct)
                    .ConfigureAwait(false);
                if (penPerson is not null)
                {
                    await _personRepo.LinkAliasAsync(penPerson.Id, personId, ct)
                        .ConfigureAwait(false);
                }
                else
                {
                    await CreateAndLinkStubPersonAsync(personId, penQid, true, ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Pseudonym resolution failed for person {Id}; continuing", personId);
        }
    }

    internal static bool IsMusicalGroup(IReadOnlyList<ProviderClaim> claims)
        => claims.Any(claim =>
            claim.Key == "instance_of_qid"
            && (claim.Value.Contains("Q215380", StringComparison.OrdinalIgnoreCase)
                || claim.Value.Contains("Q5741069", StringComparison.OrdinalIgnoreCase)));

    internal static bool IsPseudonym(IReadOnlyList<ProviderClaim> claims, bool isGroup)
        => claims.Any(claim =>
                claim.Key == "instance_of_qid"
                && (claim.Value.Contains("Q127843", StringComparison.OrdinalIgnoreCase)
                    || claim.Value.Contains("Q15632617", StringComparison.OrdinalIgnoreCase)
                    || claim.Value.Contains("Q16017119", StringComparison.OrdinalIgnoreCase)))
            || claims.Any(claim =>
                claim.Key == "instance_of"
                && (claim.Value.Contains("pen name", StringComparison.OrdinalIgnoreCase)
                    || claim.Value.Contains("pseudonym", StringComparison.OrdinalIgnoreCase)))
            || claims.Any(claim =>
                claim.Key == "attributed_to_qid"
                && !string.IsNullOrWhiteSpace(claim.Value))
            || (!isGroup && claims.Any(claim =>
                claim.Key == "has_parts_qid"
                && !string.IsNullOrWhiteSpace(claim.Value)));

    internal sealed record PersonQidReference(string Qid, string? Label);

    internal static IReadOnlyList<PersonQidReference> GetGroupMemberReferences(
        IReadOnlyList<ProviderClaim> claims)
        => ParsePersonQidReferences(claims, "has_parts_qid");

    internal static IReadOnlyList<string> GetGroupMemberQids(IReadOnlyList<ProviderClaim> claims)
        => GetGroupMemberReferences(claims).Select(reference => reference.Qid).ToList();

    private async Task ResolveGroupMembersAsync(
        Guid groupId,
        IReadOnlyList<ProviderClaim> claims,
        bool isGroup,
        CancellationToken ct)
    {
        if (!isGroup)
        {
            return;
        }

        try
        {
            var hydrationRequests = new List<HarvestRequest>();
            foreach (var reference in GetGroupMemberReferences(claims))
            {
                try
                {
                    // A performer can belong to several groups whose profiles
                    // are enriched concurrently. Reuse the QID lock so those
                    // group jobs cannot race while creating the same person.
                    var qidLock = _qidMergeLocks.GetOrAdd(reference.Qid, _ => new SemaphoreSlim(1, 1));
                    await qidLock.WaitAsync(ct).ConfigureAwait(false);
                    Person member;
                    try
                    {
                        member = await _personRepo.FindByQidAsync(reference.Qid, ct).ConfigureAwait(false)
                            ?? await CreateGroupMemberStubAsync(reference, ct).ConfigureAwait(false);

                        if (IsPlaceholderPersonName(member.Name)
                            && !string.IsNullOrWhiteSpace(reference.Label))
                        {
                            await _personRepo.UpdateNameAsync(member.Id, reference.Label, ct).ConfigureAwait(false);
                            member.Name = reference.Label;
                        }

                        await _personRepo.LinkGroupMemberAsync(groupId, member.Id, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        qidLock.Release();
                    }

                    if (!HasCompletePersonProfile(member))
                    {
                        hydrationRequests.Add(BuildPersonHydrationRequest(member.Id, reference));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex,
                        "Group-member resolution failed for group {GroupId}, member {MemberQid}; continuing with remaining members",
                        groupId,
                        reference.Qid);
                }
            }

            // Persist the complete visible relationship graph before child work
            // can run on another consumer. This avoids a child write racing the
            // parent loop and leaving only the first band member linked.
            foreach (var hydrationRequest in hydrationRequests)
            {
                await EnqueueAsync(hydrationRequest, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Group-member resolution failed for person {Id}; continuing", groupId);
        }
    }

    private async Task<Person> CreateGroupMemberStubAsync(
        PersonQidReference reference,
        CancellationToken ct)
    {
        var memberName = ResolveBestPersonName(reference.Label)
            ?? await ResolvePersonDisplayNameAsync(reference.Qid, null, null, ct)
            .ConfigureAwait(false)
            ?? $"Name pending ({reference.Qid})";
        var member = new Person
        {
            Id = Guid.NewGuid(),
            Name = memberName,
            Roles = ["Artist", "Performer"],
            WikidataQid = reference.Qid,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await _personRepo.CreateAsync(member, ct).ConfigureAwait(false);
        return member;
    }

    private static HarvestRequest BuildPersonHydrationRequest(
        Guid personId,
        PersonQidReference reference)
        => new()
        {
            EntityId = personId,
            EntityType = EntityType.Person,
            MediaType = MediaType.Unknown,
            PreResolvedQid = reference.Qid,
            Hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = reference.Label ?? string.Empty,
                ["role"] = "Performer",
                [BridgeIdKeys.WikidataQid] = reference.Qid,
            },
        };

    private async Task ResolveMemberOfGroupsAsync(
        Guid memberId,
        IReadOnlyList<ProviderClaim> claims,
        bool isGroup,
        CancellationToken ct)
    {
        if (isGroup)
        {
            return;
        }

        var groupReferences = ParsePersonQidReferences(claims, "member_of_qid");
        if (groupReferences.Count == 0)
        {
            return;
        }

        var knownGroups = await _personRepo.FindByQidsAsync(
            groupReferences.Select(reference => reference.Qid), ct).ConfigureAwait(false);

        foreach (var group in knownGroups.Where(person => person.IsGroup))
        {
            await _personRepo.LinkGroupMemberAsync(group.Id, memberId, ct).ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<PersonQidReference> ParsePersonQidReferences(
        IReadOnlyList<ProviderClaim> claims,
        string claimKey)
        => claims
            .Where(claim => string.Equals(claim.Key, claimKey, StringComparison.OrdinalIgnoreCase))
            .Select(claim =>
            {
                var parts = claim.Value.Split("::", 2, StringSplitOptions.TrimEntries);
                var qid = parts[0];
                var label = parts.Length == 2 ? ResolveBestPersonName(parts[1]) : null;
                return new PersonQidReference(qid, label);
            })
            .Where(reference => !string.IsNullOrWhiteSpace(reference.Qid))
            .GroupBy(reference => reference.Qid, StringComparer.OrdinalIgnoreCase)
            .Select(group => new PersonQidReference(
                group.Key,
                group.Select(reference => reference.Label).FirstOrDefault(label => !string.IsNullOrWhiteSpace(label))))
            .ToList();

    private async Task CreateAndLinkStubPersonAsync(
        Guid existingPersonId,
        string missingQid,
        bool isMissingPersonPseudonym,
        CancellationToken ct)
    {
        // Fix 3: dedup — reuse an existing person row if the QID is already in the DB
        // instead of creating a second "Unknown Person (Qxxx)" stub.
        var existing = await _personRepo.FindByQidAsync(missingQid, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            _logger.LogDebug(
                "CreateAndLinkStubPersonAsync: reusing existing person {Id} for QID {Qid}",
                existing.Id, missingQid);

            if (isMissingPersonPseudonym)
            {
                await _personRepo.LinkAliasAsync(existing.Id, existingPersonId, ct).ConfigureAwait(false);
            }
            else
            {
                await _personRepo.LinkAliasAsync(existingPersonId, existing.Id, ct).ConfigureAwait(false);
            }

            return;
        }

        // Best-effort: use the locally cached Wikidata label if we have one
        // (populated by prior enrichment runs via _qidLabelRepo.UpsertAsync).
        // If the cache misses, fall back to a placeholder name; the next
        // enrichment pass will overwrite it once Wikidata is queried.
        var stubName = await ResolvePersonDisplayNameAsync(missingQid, null, null, ct)
            .ConfigureAwait(false)
            ?? $"Name pending ({missingQid})";

        var stubId = Guid.NewGuid();
        var stub = new Person
        {
            Id = stubId,
            Name = stubName,
            Roles = ["Author"], // Default role
            WikidataQid = missingQid,
            CreatedAt = DateTimeOffset.UtcNow,
            IsPseudonym = isMissingPersonPseudonym
        };

        await _personRepo.CreateAsync(stub, ct).ConfigureAwait(false);

        if (isMissingPersonPseudonym)
        {
            // The missing person is a pen name for the existing real person
            await _personRepo.LinkAliasAsync(stubId, existingPersonId, ct).ConfigureAwait(false);
        }
        else
        {
            // The missing person is a real person behind the existing pen name
            await _personRepo.LinkAliasAsync(existingPersonId, stubId, ct).ConfigureAwait(false);
        }

        // Enqueue the stub for hydration
        await EnqueueAsync(new HarvestRequest
        {
            EntityId = stubId,
            EntityType = EntityType.Person,
            MediaType = MediaType.Unknown,
            PreResolvedQid = missingQid
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads headshot from Wikimedia Commons and saves it to
    /// <c>{LibraryRoot}/.data/assets/people/{personId}/headshot.*</c>. The directory is created only when a
    /// headshot URL is available — no empty directories are created during entity
    /// creation without an image.
    /// </summary>
    private async Task PersistPersonStorageAsync(
        Guid personId,
        Person? person,
        string? headshotUrl,
        CancellationToken ct)
    {
        // Skip entirely if there is no headshot URL — no directory is created.
        if (string.IsNullOrEmpty(headshotUrl))
        {
            var noUrlName = person?.Name ?? personId.ToString();
            _logger.LogDebug("Person {Name}: no headshot URL available from Wikidata", noUrlName);
            return;
        }

        // Both Name and QID are required to resolve a stable image path.
        if (person is null)
        {
            return;
        }

        _logger.LogDebug("Person {Name}: headshot URL found at {Url}", person.Name, headshotUrl);

        try
        {
            var headshotPath = _assetPathService.GetPersonHeadshotPath(personId, MediaMimeTypes.InferImageExtension(headshotUrl) ?? ".jpg");

            await using var downloadLease = await _imageDownloadCoordinator
                .AcquireAsync(headshotUrl, ct)
                .ConfigureAwait(false);

            // Download headshot if URL is available and file doesn't exist.
            if (File.Exists(headshotPath))
            {
                _logger.LogDebug("Person {Name}: headshot already exists at {Path} — skipping download", person.Name, headshotPath);
                await _personRepo.UpdateLocalHeadshotPathAsync(personId, headshotPath, ct)
                    .ConfigureAwait(false);
                return;
            }

            try
            {
                byte[] bytes;
                var sourceCachedPath = await _imageCache.FindBySourceUrlAsync(headshotUrl, ct)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(sourceCachedPath) && File.Exists(sourceCachedPath))
                {
                    bytes = await File.ReadAllBytesAsync(sourceCachedPath, ct).ConfigureAwait(false);
                }
                else
                {
                    using var client = _httpFactory.CreateClient("headshot_download");
                    using var response = await client.GetAsync(headshotUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                        .ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    bytes = await BoundedHttpContent.ReadImageAsync(response.Content, ct)
                        .ConfigureAwait(false);
                }

                if (bytes.Length > 0)
                {
                    var hash = Convert.ToHexStringLower(
                        System.Security.Cryptography.SHA256.HashData(bytes));
                    var cached = await _imageCache.FindByHashAsync(hash, ct)
                        .ConfigureAwait(false);

                    // Create the directory only when we have bytes to write.
                    AssetPathService.EnsureDirectory(headshotPath);

                    if (cached is not null && File.Exists(cached))
                    {
                        File.Copy(cached, headshotPath, overwrite: false);
                    }
                    else
                    {
                        await BoundedHttpContent.WriteFileAtomicallyAsync(headshotPath, bytes, ct)
                            .ConfigureAwait(false);
                    }

                    await _imageCache.InsertAsync(hash, headshotPath, headshotUrl, ct)
                        .ConfigureAwait(false);

                    await _personRepo.UpdateLocalHeadshotPathAsync(personId, headshotPath, ct)
                        .ConfigureAwait(false);

                    _logger.LogInformation("Person {Name}: headshot downloaded to {Path}", person.Name, headshotPath);
                }
                else
                {
                    _logger.LogWarning("Person {Name}: headshot download failed — empty response body from {Url}", person.Name, headshotUrl);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Person {Name}: headshot download failed — {Reason}",
                    person.Name, ex.Message);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Person storage persistence failed for person {Id}; continuing",
                personId);
        }
    }

    /// <summary>Returns the stored display string for optional multi-value presentation.</summary>
    private static string? NormalizeMultiValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? value : value.Trim();
    }

    private async Task<string?> ResolvePersonDisplayNameAsync(
        string? qid,
        string? claimName,
        string? hintName,
        CancellationToken ct)
    {
        var resolved = ResolveBestPersonName(claimName, hintName);
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            return resolved;
        }

        if (string.IsNullOrWhiteSpace(qid))
        {
            return null;
        }

        try
        {
            var cachedLabel = await _qidLabelRepo.GetLabelAsync(qid, ct).ConfigureAwait(false);
            return ResolveBestPersonName(cachedLabel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "QID label cache lookup failed for {Qid}; using pending person name", qid);
            return null;
        }
    }

    private static string? ResolveBestPersonName(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var trimmed = candidate.Trim();
            if (!IsPlaceholderPersonName(trimmed))
            {
                return trimmed;
            }
        }

        return null;
    }

    internal static bool IsPlaceholderPersonName(string value)
    {
        if (value.StartsWith("Name pending (", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.StartsWith("Unknown Person (", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return value.Length > 1
            && value[0] is 'Q'
            && value.Skip(1).All(char.IsDigit);
    }

    /// <summary>
    /// Deletes the canonical person asset directory from disk during QID-based deduplication.
    /// </summary>
    private void DeletePersonFolder(Person person)
    {
        try
        {
            var personDir = _assetPathService.GetPersonRoot(person.Id);
            if (Directory.Exists(personDir))
            {
                Directory.Delete(personDir, recursive: true);
                _logger.LogInformation("Deleted duplicate person asset dir: {Dir}", personDir);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to delete person folder for {PersonId}", person.Id);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ResolveBaseUrl(
        IExternalMetadataProvider provider,
        Dictionary<string, Dictionary<string, string>> providerEndpoints)
    {
        if (providerEndpoints.TryGetValue(provider.Name, out var endpoints))
        {
            if (endpoints.TryGetValue(provider.Name, out var url))
            {
                return url.TrimEnd('/');
            }

            if (endpoints.TryGetValue("api", out var apiUrl))
            {
                return apiUrl.TrimEnd('/');
            }

            if (endpoints.Count > 0)
            {
                return endpoints.Values.First().TrimEnd('/');
            }
        }
        return string.Empty;
    }

    private static string? ResolveSparqlBaseUrl(Dictionary<string, Dictionary<string, string>> providerEndpoints)
        => providerEndpoints.Values
            .SelectMany(e => e)
            .FirstOrDefault(kv => kv.Key.Equals("wikidata_sparql", StringComparison.OrdinalIgnoreCase))
            .Value;

    private ProviderLookupRequest BuildLookupRequest(
        HarvestRequest request,
        IExternalMetadataProvider provider,
        string baseUrl,
        string? sparqlBaseUrl = null)
    {
        var h = request.Hints;
        var core = _configLoader.LoadCore();
        var lang = string.IsNullOrWhiteSpace(core.Language.Metadata) ? "en" : core.Language.Metadata;
        var country = string.IsNullOrWhiteSpace(core.Country) ? "us" : core.Country.ToUpperInvariant();
        return new ProviderLookupRequest
        {
            EntityId = request.EntityId,
            EntityType = request.EntityType,
            MediaType = request.MediaType,
            Title = h.GetValueOrDefault("title"),
            Author = h.GetValueOrDefault("author"),
            Year = h.GetValueOrDefault("year"),
            ShowName = h.GetValueOrDefault(MetadataFieldConstants.ShowName)
                ?? h.GetValueOrDefault(MetadataFieldConstants.Series),
            Series = h.GetValueOrDefault(MetadataFieldConstants.Series),
            SeasonNumber = h.GetValueOrDefault(MetadataFieldConstants.SeasonNumber),
            EpisodeNumber = h.GetValueOrDefault(MetadataFieldConstants.EpisodeNumber),
            Narrator = h.GetValueOrDefault("narrator"),
            Asin = h.GetValueOrDefault(BridgeIdKeys.Asin),
            Isbn = NormalizeIsbnHint(h.GetValueOrDefault(BridgeIdKeys.Isbn)),
            AppleBooksId = h.GetValueOrDefault(BridgeIdKeys.AppleBooksId),
            AudibleId = h.GetValueOrDefault(BridgeIdKeys.AudibleId),
            TmdbId = h.GetValueOrDefault(BridgeIdKeys.TmdbId),
            ImdbId = h.GetValueOrDefault(BridgeIdKeys.ImdbId),
            PersonName = h.GetValueOrDefault("name"),
            PersonRole = h.GetValueOrDefault("role"),
            PreResolvedQid = request.PreResolvedQid ?? h.GetValueOrDefault(BridgeIdKeys.WikidataQid),
            BaseUrl = baseUrl,
            SparqlBaseUrl = sparqlBaseUrl,
            Language = lang,
            Country = country,
            HydrationPass = request.Pass,
            Hints = h,
        };
    }

    /// <summary>
    /// Safety-net normalization for ISBN hints — strips URI prefixes and non-digit characters.
    /// </summary>
    private static string? NormalizeIsbnHint(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var digits = new string(raw.Where(char.IsDigit).ToArray());
        return digits.Length is 10 or 13 ? digits : raw?.Trim();
    }

    // ── IAsyncDisposable ──────────────────────────────────────────────────────

    public override void Dispose()
    {
        _queue.TryComplete();
        base.Dispose();
    }
}
