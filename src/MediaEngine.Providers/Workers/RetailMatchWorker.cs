using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Intelligence.Contracts;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Workers;

/// <summary>
/// Stage 1: Retail Identification.
/// Leases <see cref="IdentityJobState.Queued"/> jobs, runs retail providers
/// per the configured strategy, scores candidates, and persists evidence.
///
/// Music and TV jobs are processed at album/show level rather than per-track/episode:
/// one API call fetches the full album (Apple) or season episode list (TMDB), then
/// each sibling job in the batch receives its per-item claims without additional calls.
///
/// This is a plain service — the Api layer wraps it in a <c>BackgroundService</c>
/// for polling lifecycle management.
/// </summary>
public sealed partial class RetailMatchWorker
{
    private readonly IIdentityJobRepository _jobRepo;
    private readonly IRetailCandidateRepository _candidateRepo;
    private readonly StageOutcomeFactory _outcomeFactory;
    private readonly TimelineRecorder _timeline;
    private readonly Services.BatchProgressService _batchProgress;
    private readonly IEnumerable<IExternalMetadataProvider> _providers;
    private readonly IRetailMatchScoringService _retailScoring;
    private readonly IMetadataClaimRepository _claimRepo;
    private readonly ICanonicalValueRepository _canonicalRepo;
    private readonly ICanonicalValueArrayRepository? _arrayRepo;
    private readonly IScoringEngine _scoringEngine;
    private readonly IConfigurationLoader _configLoader;
    private readonly IPipelineExecutionSnapshotProvider? _configurationSnapshots;
    private readonly IBridgeIdRepository _bridgeIdRepo;
    private readonly IWorkRepository _workRepo;
    private readonly ITvEpisodeCreditRepository? _episodeCredits;
    private readonly WorkClaimRouter _claimRouter;
    private readonly IHttpClientFactory _httpFactory;
    private readonly PostPipelineService _postPipeline;
    private readonly IEnrichmentConcurrencyLimiter _concurrency;
    private readonly IEntityAssetRepository? _entityAssetRepo;
    private readonly IImageCacheRepository? _imageCache;
    private readonly AssetPathService? _assetPaths;
    private readonly IAssetExportService? _assetExportService;
    private readonly AppleRetailClient _appleClient;
    private readonly TmdbRetailClient _tmdbClient;
    private readonly RetailCandidateScorer _candidateScorer;
    private readonly CoverArtWorker? _coverArtWorker;
    private readonly MusicBrainzReleaseClient? _musicBrainzReleaseClient;
    private readonly PersonEnrichmentWorker? _personEnrichment;
    private readonly IMediaOperationRepository? _operations;
    private readonly ImageDownloadCoordinator _imageDownloadCoordinator;
    private readonly ILogger<RetailMatchWorker> _logger;

    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Cross-file batching window. Sourced from
    /// <c>config/core.json → pipeline.lease_sizes.retail</c> at construction time.
    /// Larger values mean a single drop of N files (e.g. a TV season, an album)
    /// processes in one lease cycle instead of being chopped into multiple leases
    /// — which is what enables one Apple album call to cover all its tracks.
    /// </summary>

    public RetailMatchWorker(
        IIdentityJobRepository jobRepo,
        IRetailCandidateRepository candidateRepo,
        StageOutcomeFactory outcomeFactory,
        TimelineRecorder timeline,
        Services.BatchProgressService batchProgress,
        IEnumerable<IExternalMetadataProvider> providers,
        IRetailMatchScoringService retailScoring,
        IMetadataClaimRepository claimRepo,
        ICanonicalValueRepository canonicalRepo,
        IScoringEngine scoringEngine,
        IConfigurationLoader configLoader,
        IBridgeIdRepository bridgeIdRepo,
        IWorkRepository workRepo,
        WorkClaimRouter claimRouter,
        IHttpClientFactory httpFactory,
        PostPipelineService postPipeline,
        ILogger<RetailMatchWorker> logger,
        IEnrichmentConcurrencyLimiter? concurrencyLimiter = null,
        IEntityAssetRepository? entityAssetRepo = null,
        IImageCacheRepository? imageCache = null,
        AssetPathService? assetPaths = null,
        IAssetExportService? assetExportService = null,
        ICanonicalValueArrayRepository? arrayRepo = null,
        AppleRetailClient? appleClient = null,
        TmdbRetailClient? tmdbClient = null,
        RetailCandidateScorer? candidateScorer = null,
        CoverArtWorker? coverArtWorker = null,
        IPipelineExecutionSnapshotProvider? configurationSnapshots = null,
        MusicBrainzReleaseClient? musicBrainzReleaseClient = null,
        PersonEnrichmentWorker? personEnrichment = null,
        ImageDownloadCoordinator? imageDownloadCoordinator = null,
        ITvEpisodeCreditRepository? episodeCredits = null,
        IMediaOperationRepository? operations = null)
    {
        _jobRepo = jobRepo;
        _candidateRepo = candidateRepo;
        _outcomeFactory = outcomeFactory;
        _timeline = timeline;
        _batchProgress = batchProgress;
        _providers = providers;
        _retailScoring = retailScoring;
        _claimRepo = claimRepo;
        _canonicalRepo = canonicalRepo;
        _arrayRepo = arrayRepo;
        _scoringEngine = scoringEngine;
        _configLoader = configLoader;
        _configurationSnapshots = configurationSnapshots;
        _bridgeIdRepo = bridgeIdRepo;
        _workRepo = workRepo;
        _episodeCredits = episodeCredits;
        _claimRouter = claimRouter;
        _httpFactory = httpFactory;
        _postPipeline = postPipeline;
        _concurrency = concurrencyLimiter ?? NoopEnrichmentConcurrencyLimiter.Instance;
        _entityAssetRepo = entityAssetRepo;
        _imageCache = imageCache;
        _assetPaths = assetPaths;
        _assetExportService = assetExportService;
        _appleClient = appleClient ?? new AppleRetailClient(
            _httpFactory,
            new RetailRequestBuilder(),
            new ProviderRateLimiterCoordinator(),
            NullLogger<AppleRetailClient>.Instance);
        _tmdbClient = tmdbClient ?? new TmdbRetailClient(
            _httpFactory,
            new RetailRequestBuilder(),
            new ProviderRateLimiterCoordinator(),
            NullLogger<TmdbRetailClient>.Instance);
        _candidateScorer = candidateScorer ?? new RetailCandidateScorer();
        _coverArtWorker = coverArtWorker;
        _musicBrainzReleaseClient = musicBrainzReleaseClient;
        _personEnrichment = personEnrichment;
        _operations = operations;
        _imageDownloadCoordinator = imageDownloadCoordinator ?? ImageDownloadCoordinator.Shared;
        _logger = logger;

        // Lease size is read once at construction. A restart applies any
        // config change — same lifetime as every other CoreConfiguration value.
    }

    private PipelineExecutionSnapshot GetExecutionSnapshot() =>
        _configurationSnapshots?.Current
        ?? new PipelineExecutionSnapshot(
            0,
            DateTimeOffset.UtcNow,
            _configLoader.LoadCore(),
            _configLoader.LoadHydration(),
            _configLoader.LoadPipelines(),
            _configLoader.LoadAllProviders());

    private async Task EnrichPeopleWithoutMediaMatchAsync(Guid entityId, CancellationToken ct)
    {
        if (_operations is not null)
        {
            var identityJob = await _jobRepo.GetByEntityAsync(entityId, ct).ConfigureAwait(false);
            await _operations.EnsureAsync(new MediaOperation
            {
                OperationType = "enrichment.people",
                BatchId = identityJob?.IngestionRunId,
                OperationKind = "enrichment",
                EntityId = entityId,
                EntityKind = "media_asset",
                QueueName = "people",
                Status = MediaOperationStatus.Queued,
                IdempotencyKey = $"people:retail:{entityId}:v1",
            }, ct).ConfigureAwait(false);
            return;
        }

        if (_personEnrichment is null)
        {
            return;
        }

        try
        {
            await _concurrency.RunAsync(
                EnrichmentWorkKind.Wikidata,
                token => _personEnrichment.EnrichFromClaimsAsync(entityId, token),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Contributor enrichment was partial after media matching found no result for entity {EntityId}",
                entityId);
        }
    }

    /// <summary>
    /// Polls for <see cref="IdentityJobState.Queued"/> jobs and processes them.
    /// Called by the Api-layer hosted service on each poll tick.
    /// Returns the number of jobs processed.
    /// </summary>
    public async Task<int> PollAsync(CancellationToken ct)
    {
        var jobs = await _jobRepo.LeaseNextAsync(
            "RetailMatchWorker",
            [IdentityJobState.Queued],
            GetBatchSize(),
            LeaseDuration,
            ct: ct);

        // Separate Music and TV jobs for group processing; everything else is per-item.
        var musicJobs = new List<IdentityJob>();
        var tvJobs = new List<IdentityJob>();
        var otherJobs = new List<IdentityJob>();

        foreach (var job in jobs)
        {
            if (string.Equals(job.MediaType, "Music", StringComparison.OrdinalIgnoreCase))
            {
                musicJobs.Add(job);
            }
            else if (string.Equals(job.MediaType, "TV", StringComparison.OrdinalIgnoreCase))
            {
                tvJobs.Add(job);
            }
            else
            {
                otherJobs.Add(job);
            }
        }

        var work = new List<Task>();

        // Process non-Music/TV jobs independently so a slow provider or retry for
        // one file does not block unrelated Stage 1 work.
        work.AddRange(otherJobs.Select(job =>
            _concurrency.RunAsync(
                EnrichmentWorkKind.RetailProvider,
                token => ProcessJobWithRetryAsync(job, token),
                ct)));

        // Process Music jobs grouped by album (artist+album key).
        if (musicJobs.Count > 0)
        {
            if (ShouldUseAppleMusicAlbumBatch())
            {
                work.Add(ProcessMusicBatchAsync(musicJobs, ct));
            }
            else
            {
                _logger.LogInformation(
                    "Music: configured Stage 1 provider order does not start with apple_api; using ranked provider pipeline for {Count} track(s)",
                    musicJobs.Count);

                work.AddRange(musicJobs.Select(job =>
                    _concurrency.RunAsync(
                        EnrichmentWorkKind.RetailProvider,
                        token => ProcessJobWithRetryAsync(job, token),
                        ct)));
            }
        }

        // Process TV jobs grouped by show+season (show_name+season_number key).
        if (tvJobs.Count > 0)
        {
            work.Add(ProcessTvBatchAsync(tvJobs, ct));
        }

        await Task.WhenAll(work).ConfigureAwait(false);

        foreach (var runId in jobs
                     .Select(j => j.IngestionRunId)
                     .Where(id => id.HasValue)
                     .Select(id => id!.Value)
                     .Distinct())
        {
            await _batchProgress.EmitProgressAsync(runId, isFinal: false, ct).ConfigureAwait(false);
        }

        return jobs.Count;
    }

}
