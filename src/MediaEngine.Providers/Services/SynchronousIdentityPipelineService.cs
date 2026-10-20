using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Providers.Workers;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Replacement for <c>HydrationPipelineService</c> that implements
/// <see cref="IHydrationPipelineService"/> using the durable identity pipeline
/// workers instead of the monolith.
///
/// <see cref="EnqueueAsync"/> creates an <c>identity_jobs</c> row for async processing.
/// <see cref="RunSynchronousAsync"/> runs the three pipeline workers inline and
/// returns a <see cref="HydrationResult"/>.
/// </summary>
public sealed class SynchronousIdentityPipelineService : IHydrationPipelineService
{
    private readonly IIdentityJobRepository _jobRepo;
    private readonly ICanonicalValueRepository _canonicalRepo;
    private readonly RetailMatchWorker _retailWorker;
    private readonly WikidataBridgeWorker _bridgeWorker;
    private readonly QuickHydrationWorker _hydrationWorker;
    private readonly IEnrichmentConcurrencyLimiter _concurrency;
    private readonly ILogger<SynchronousIdentityPipelineService> _logger;

    public SynchronousIdentityPipelineService(
        IIdentityJobRepository jobRepo,
        ICanonicalValueRepository canonicalRepo,
        RetailMatchWorker retailWorker,
        WikidataBridgeWorker bridgeWorker,
        QuickHydrationWorker hydrationWorker,
        ILogger<SynchronousIdentityPipelineService> logger,
        IEnrichmentConcurrencyLimiter? concurrencyLimiter = null)
    {
        _jobRepo = jobRepo;
        _canonicalRepo = canonicalRepo;
        _retailWorker = retailWorker;
        _bridgeWorker = bridgeWorker;
        _hydrationWorker = hydrationWorker;
        _concurrency = concurrencyLimiter ?? NoopEnrichmentConcurrencyLimiter.Instance;
        _logger = logger;
    }

    /// <summary>
    /// Creates an <c>identity_jobs</c> row for asynchronous processing by the
    /// hosted service workers.
    /// </summary>
    public async ValueTask<Guid> EnqueueAsync(HarvestRequest request, CancellationToken ct = default)
    {
        var job = new IdentityJob
        {
            EntityId = request.EntityId,
            EntityType = request.EntityType.ToString(),
            MediaType = request.MediaType.ToString(),
            Pass = request.Pass.ToString(),
        };

        if (!string.IsNullOrWhiteSpace(request.PreResolvedQid) && request.IsUserResolution)
        {
            job.State = nameof(IdentityJobState.QidResolved);
            job.ResolvedQid = request.PreResolvedQid;
        }
        else if (request.SkipRetailStage)
        {
            job.State = nameof(IdentityJobState.RetailMatched);
        }

        await PersistInitialJobAsync(job, request.IsUserResolution, ct);

        if (!string.IsNullOrWhiteSpace(job.ResolvedQid))
        {
            await _jobRepo.SetResolvedQidAsync(job.Id, job.ResolvedQid, ct);

            if (request.IsUserResolution && _bridgeWorker is not null)
            {
                await _concurrency.RunAsync(
                    EnrichmentWorkKind.Wikidata,
                    token => _bridgeWorker.FetchAndPersistPropertiesAsync(
                        request.EntityId,
                        job.ResolvedQid,
                        job.MediaType,
                        token),
                    ct);
            }
        }

        _logger.LogInformation(
            "Enqueued identity job {JobId} for entity {EntityId} ({MediaType}) at state {State}",
            job.Id, job.EntityId, job.MediaType, job.State);

        return job.Id;
    }

    /// <summary>
    /// Runs the full identity pipeline inline: Stage 1 (retail) → Stage 2 (bridge)
    /// → Quick hydration. Returns a <see cref="HydrationResult"/> for the caller.
    /// </summary>
    public async Task<HydrationResult> RunSynchronousAsync(
        HarvestRequest request, CancellationToken ct = default)
    {
        var job = new IdentityJob
        {
            EntityId = request.EntityId,
            EntityType = request.EntityType.ToString(),
            MediaType = request.MediaType.ToString(),
            Pass = request.Pass.ToString(),
        };

        // Path A: Pre-resolved QID (user selected a Wikidata entity directly)
        // Skip retail + bridge resolution, but FETCH full Wikidata properties.
        if (!string.IsNullOrWhiteSpace(request.PreResolvedQid) && request.IsUserResolution)
        {
            job.State = nameof(IdentityJobState.QidResolved);
            job.ResolvedQid = request.PreResolvedQid;
            await PersistInitialJobAsync(job, request.IsUserResolution, ct);
            await _jobRepo.SetResolvedQidAsync(job.Id, request.PreResolvedQid, ct);
            await _jobRepo.UpdateStateAsync(job.Id, IdentityJobState.QidResolved, ct: ct);

            // Fetch full Wikidata properties (genre, series, description, people, etc.)
            // This was previously skipped, leaving manual QID matches without enrichment data.
            await _concurrency.RunAsync(
                EnrichmentWorkKind.Wikidata,
                token => _bridgeWorker.FetchAndPersistPropertiesAsync(
                    request.EntityId, request.PreResolvedQid, job.MediaType, token),
                ct);
        }
        // Path B: Skip retail, run bridge (user confirmed a retail match, now auto-resolve Wikidata)
        else if (request.SkipRetailStage)
        {
            job.State = nameof(IdentityJobState.RetailMatched);
            await PersistInitialJobAsync(job, request.IsUserResolution, ct);
            await _jobRepo.UpdateStateAsync(job.Id, IdentityJobState.RetailMatched, ct: ct);

            // Stage 2: Wikidata bridge resolution using existing bridge IDs + text fallback
            await _concurrency.RunAsync(
                EnrichmentWorkKind.Wikidata,
                token => _bridgeWorker.ProcessJobAsync(job, token),
                ct);

            var updatedJob = await _jobRepo.GetByIdAsync(job.Id, ct);
            if (updatedJob is not null)
            {
                job = updatedJob;
            }
        }
        // Path C: Full pipeline (normal automated flow)
        else
        {
            await PersistInitialJobAsync(job, request.IsUserResolution, ct);

            // Stage 1: Retail identification
            await _concurrency.RunAsync(
                EnrichmentWorkKind.RetailProvider,
                token => _retailWorker.ProcessJobAsync(job, token),
                ct);

            // Reload job state after retail worker
            var updatedJob = await _jobRepo.GetByIdAsync(job.Id, ct);
            if (updatedJob is null)
            {
                _logger.LogWarning("Job {JobId} not found after retail stage", job.Id);
                return BuildResult(job);
            }
            job = updatedJob;

            // Only proceed to Stage 2 if retail matched (strict gate)
            if (job.State == nameof(IdentityJobState.RetailMatched) ||
                job.State == nameof(IdentityJobState.RetailMatchedNeedsReview))
            {
                await _concurrency.RunAsync(
                    EnrichmentWorkKind.Wikidata,
                    token => _bridgeWorker.ProcessJobAsync(job, token),
                    ct);

                updatedJob = await _jobRepo.GetByIdAsync(job.Id, ct);
                if (updatedJob is not null)
                {
                    job = updatedJob;
                }
            }
        }

        // Reload to get latest state
        var finalJob = await _jobRepo.GetByIdAsync(job.Id, ct);
        if (finalJob is not null)
        {
            job = finalJob;
        }

        // Stage 3: Quick hydration (if QID resolved)
        if (job.State == nameof(IdentityJobState.QidResolved))
        {
            await _hydrationWorker.ProcessJobAsync(job, ct);

            finalJob = await _jobRepo.GetByIdAsync(job.Id, ct);
            if (finalJob is not null)
            {
                job = finalJob;
            }
        }

        _logger.LogInformation(
            "Synchronous pipeline completed for entity {EntityId}: state={State}, QID={Qid}",
            job.EntityId, job.State, job.ResolvedQid);

        return BuildResult(job);
    }

    private async Task PersistInitialJobAsync(
        IdentityJob job,
        bool isUserResolution,
        CancellationToken ct)
    {
        if (isUserResolution)
        {
            job.Id = await _jobRepo.CreateOrResumeUserResolutionAsync(job, ct);
        }
        else
        {
            await _jobRepo.CreateAsync(job, ct);
        }
    }

    private HydrationResult BuildResult(IdentityJob job)
    {
        var needsReview = job.State is
            nameof(IdentityJobState.RetailNoMatch) or
            nameof(IdentityJobState.RetailMatchedNeedsReview) or
            nameof(IdentityJobState.QidNeedsReview) or
            nameof(IdentityJobState.QidNoMatch) or
            nameof(IdentityJobState.Failed);

        string? reviewReason = job.State switch
        {
            nameof(IdentityJobState.RetailNoMatch) => "Retail identification failed",
            nameof(IdentityJobState.QidNoMatch) => "Wikidata bridge resolution failed",
            nameof(IdentityJobState.Failed) => job.LastError,
            _ => null,
        };

        return new HydrationResult
        {
            WikidataQid = job.ResolvedQid,
            NeedsReview = needsReview,
            ReviewReason = reviewReason,
        };
    }
}
