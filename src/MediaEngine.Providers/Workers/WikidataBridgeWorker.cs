using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Intelligence.Contracts;
using MediaEngine.Providers.Adapters;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging;
using Tuvima.Wikidata;

namespace MediaEngine.Providers.Workers;

/// <summary>
/// Stage 2: Wikidata Bridge Resolution.
/// Leases jobs in <see cref="IdentityJobState.RetailMatched"/> or
/// <see cref="IdentityJobState.RetailMatchedNeedsReview"/> state.
/// Never processes <see cref="IdentityJobState.RetailNoMatch"/> — the strict retail gate.
///
/// Uses bridge IDs from Stage 1 to find the canonical Wikidata entity (QID).
/// If bridge IDs do not resolve, the item keeps retail metadata and remains
/// eligible for review or later recheck.
///
/// This is a plain service — the Api layer wraps it in a <c>BackgroundService</c>.
/// </summary>
public sealed partial class WikidataBridgeWorker
{
    private readonly IIdentityJobRepository _jobRepo;
    private readonly IWikidataCandidateRepository _candidateRepo;
    private readonly StageOutcomeFactory _outcomeFactory;
    private readonly TimelineRecorder _timeline;
    private readonly BridgeIdHelper _bridgeIdHelper;
    private readonly IEnumerable<IExternalMetadataProvider> _providers;
    private readonly IBridgeIdRepository _bridgeIdRepo;
    private readonly IMetadataClaimRepository _claimRepo;
    private readonly ICanonicalValueRepository _canonicalRepo;
    private readonly ICanonicalValueArrayRepository? _arrayRepo;
    private readonly IScoringEngine _scoringEngine;
    private readonly IConfigurationLoader _configLoader;
    private readonly IPipelineExecutionSnapshotProvider? _configurationSnapshots;
    private readonly IWorkRepository _workRepo;
    private readonly WorkClaimRouter _claimRouter;
    private readonly CatalogUpsertService _catalogUpsert;
    private readonly PostPipelineService _postPipeline;
    private readonly PersonEnrichmentWorker? _personEnrichment;
    private readonly WikidataSeriesManifestHydrationService? _seriesManifestHydration;
    private readonly CoverArtWorker _coverArt;
    private readonly CollectionFinalizationService? _collectionFinalization;
    private readonly IWorkIdentityReconciliationService? _workIdentityReconciliation;
    private readonly IEnrichmentConcurrencyLimiter _concurrency;
    private readonly IMediaOperationTracker? _operationTracker;
    private readonly IEntityCapabilityStateRepository? _capabilityStates;
    private readonly IRetailMatchScoringService? _retailMatchScoring;
    private readonly IImageEnrichmentService? _imageEnrichment;
    private readonly ILogger<WikidataBridgeWorker> _logger;

    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Cross-job batching window. Sourced from
    /// <c>config/core.json → pipeline.lease_sizes.wikidata</c> at construction time.
    /// Larger values mean more jobs share a single Wikidata reconciliation call
    /// (one call per unique album/show, one call per unique bridge ID).
    /// </summary>

    public WikidataBridgeWorker(
        IIdentityJobRepository jobRepo,
        IWikidataCandidateRepository candidateRepo,
        StageOutcomeFactory outcomeFactory,
        TimelineRecorder timeline,
        BridgeIdHelper bridgeIdHelper,
        IEnumerable<IExternalMetadataProvider> providers,
        IBridgeIdRepository bridgeIdRepo,
        IMetadataClaimRepository claimRepo,
        ICanonicalValueRepository canonicalRepo,
        IScoringEngine scoringEngine,
        IConfigurationLoader configLoader,
        IWorkRepository workRepo,
        WorkClaimRouter claimRouter,
        CatalogUpsertService catalogUpsert,
        PostPipelineService postPipeline,
        CoverArtWorker coverArt,
        ILogger<WikidataBridgeWorker> logger,
        IEnrichmentConcurrencyLimiter? concurrencyLimiter = null,
        ICanonicalValueArrayRepository? arrayRepo = null,
        WikidataSeriesManifestHydrationService? seriesManifestHydration = null,
        PersonEnrichmentWorker? personEnrichment = null,
        IMediaOperationTracker? operationTracker = null,
        IEntityCapabilityStateRepository? capabilityStates = null,
        CollectionFinalizationService? collectionFinalization = null,
        IWorkIdentityReconciliationService? workIdentityReconciliation = null,
        IPipelineExecutionSnapshotProvider? configurationSnapshots = null,
        IRetailMatchScoringService? retailMatchScoring = null,
        IImageEnrichmentService? imageEnrichment = null)
    {
        _jobRepo = jobRepo;
        _candidateRepo = candidateRepo;
        _outcomeFactory = outcomeFactory;
        _timeline = timeline;
        _bridgeIdHelper = bridgeIdHelper;
        _providers = providers;
        _bridgeIdRepo = bridgeIdRepo;
        _claimRepo = claimRepo;
        _canonicalRepo = canonicalRepo;
        _arrayRepo = arrayRepo;
        _scoringEngine = scoringEngine;
        _configLoader = configLoader;
        _configurationSnapshots = configurationSnapshots;
        _workRepo = workRepo;
        _claimRouter = claimRouter;
        _catalogUpsert = catalogUpsert;
        _postPipeline = postPipeline;
        _personEnrichment = personEnrichment;
        _seriesManifestHydration = seriesManifestHydration;
        _coverArt = coverArt;
        _collectionFinalization = collectionFinalization;
        _workIdentityReconciliation = workIdentityReconciliation;
        _logger = logger;
        _concurrency = concurrencyLimiter ?? NoopEnrichmentConcurrencyLimiter.Instance;
        _operationTracker = operationTracker;
        _capabilityStates = capabilityStates;
        _retailMatchScoring = retailMatchScoring;
        _imageEnrichment = imageEnrichment;

        // Lease size is read once at construction. A restart applies any
        // config change — same lifetime as every other CoreConfiguration value.
    }

    /// <summary>
    /// Polls for <see cref="IdentityJobState.RetailMatched"/> and
    /// <see cref="IdentityJobState.RetailMatchedNeedsReview"/> jobs.
    /// Returns the number of jobs processed.
    ///
    /// PollAsync runs in six phases so that N jobs produce far fewer than N Wikidata calls:
    ///
    ///   Phase 1 — Lease: lease up to the configured batch size.
    ///   Phase 2 — Load context: batch-fetch bridge IDs and canonical values
    ///             for all jobs in two SQL queries (vs N×2 previously).
    ///   Phase 3 — Build job contexts: assemble per-job working DTOs and
    ///             compute the grouping key (bridge signature or title+author).
    ///   Phase 4 — Resolve QIDs: a single
    ///             <see cref="ReconciliationAdapter.ResolveBatchAsync"/> call.
    ///             The adapter internally groups by music album, primary bridge
    ///             ID, and text signature so N jobs produce far fewer than N
    ///             Wikidata calls.
    ///   Phase 5 — Distribute results: propagate each group's resolved QID and
    ///             claims to all sibling jobs in that group.
    ///   Phase 6 — Per-job finalisation: persist candidates, update job state,
    ///             and trigger full property fetch (FetchAsync with PreResolvedQid).
    ///             The adapter's response cache ensures jobs sharing a QID hit the
    ///             cache on the second and subsequent FetchAsync calls.
    /// </summary>
    public Task<int> PollAsync(CancellationToken ct) =>
        _concurrency.RunAsync(
            EnrichmentWorkKind.Wikidata,
            PollCoreAsync,
            ct);

    internal static bool ShouldResetBatchAfterFailure(Exception exception, CancellationToken ct) =>
        exception is not OperationCanceledException || !ct.IsCancellationRequested;

    private PipelineExecutionSnapshot GetExecutionSnapshot() =>
        _configurationSnapshots?.Current
        ?? new PipelineExecutionSnapshot(
            0,
            DateTimeOffset.UtcNow,
            _configLoader.LoadCore(),
            _configLoader.LoadHydration(),
            _configLoader.LoadPipelines(),
            _configLoader.LoadAllProviders());

    private int GetBatchSize() =>
        Math.Max(1, GetExecutionSnapshot().Core.Pipeline.LeaseSizes.Wikidata);

}
