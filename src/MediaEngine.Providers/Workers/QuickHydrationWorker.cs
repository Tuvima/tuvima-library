using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Workers;

/// <summary>
/// Quick Hydration worker (after Stage 2 QID resolution).
/// Leases <see cref="IdentityJobState.QidResolved"/> jobs and runs the Quick
/// enrichment pass via <see cref="IEnrichmentService"/>.
///
/// This is a plain service — the Api layer wraps it in a <c>BackgroundService</c>.
/// </summary>
public sealed class QuickHydrationWorker
{
    private readonly IIdentityJobRepository _jobRepo;
    private readonly IEnrichmentService _enrichment;
    private readonly CollectionFinalizationService _collectionFinalization;
    private readonly ILogger<QuickHydrationWorker> _logger;
    private readonly PostPipelineService _postPipeline;
    private readonly ICanonicalValueRepository _canonicalRepo;
    private readonly ICollectionRepository _collectionRepo;
    private readonly IUniverseEnrichmentScheduler _universeEnrichment;
    private readonly IConfigurationLoader _configLoader;
    private readonly IPipelineExecutionSnapshotProvider? _configurationSnapshots;

    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Lease batch size. Sourced from
    /// <c>config/core.json → pipeline.lease_sizes.hydration</c> at construction time.
    /// Hydration is per-job (no cross-job batching benefit), so the limit is set
    /// lower than the retail/wikidata stages to keep individual cycles responsive.
    /// </summary>
    public QuickHydrationWorker(
        IIdentityJobRepository jobRepo,
        IEnrichmentService enrichment,
        CollectionFinalizationService collectionFinalization,
        PostPipelineService postPipeline,
        ICanonicalValueRepository canonicalRepo,
        ICollectionRepository collectionRepo,
        IUniverseEnrichmentScheduler universeEnrichment,
        IConfigurationLoader configLoader,
        ILogger<QuickHydrationWorker> logger,
        IPipelineExecutionSnapshotProvider? configurationSnapshots = null)
    {
        _jobRepo = jobRepo;
        _enrichment = enrichment;
        _collectionFinalization = collectionFinalization;
        _postPipeline = postPipeline;
        _canonicalRepo = canonicalRepo;
        _collectionRepo = collectionRepo;
        _universeEnrichment = universeEnrichment;
        _configLoader = configLoader;
        _configurationSnapshots = configurationSnapshots;
        _logger = logger;
    }

    /// <summary>
    /// Polls for <see cref="IdentityJobState.QidResolved"/> jobs and runs Quick hydration.
    /// Returns the number of jobs processed.
    /// </summary>
    public async Task<int> PollAsync(CancellationToken ct)
    {
        var executionSnapshot = GetExecutionSnapshot();
        var jobs = await _jobRepo.LeaseNextAsync(
            "QuickHydrationWorker",
            [IdentityJobState.QidResolved],
            GetBatchSize(executionSnapshot),
            LeaseDuration,
            ct: ct);

        await Parallel.ForEachAsync(
            jobs,
            new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = Math.Max(
                    1,
                    executionSnapshot.Hydration.MaxConcurrentWikidataJobs),
            },
            async (job, jobCt) =>
                await ProcessLeasedJobAsync(job, executionSnapshot, jobCt).ConfigureAwait(false))
            .ConfigureAwait(false);

        return jobs.Count;
    }

    private async Task ProcessLeasedJobAsync(
        IdentityJob job,
        PipelineExecutionSnapshot executionSnapshot,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(
            Math.Max(1, executionSnapshot.Hydration.QuickHydrationTimeoutSeconds)));
        try
        {
            await ProcessJobAsync(job, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            Exception failure = timeoutCts.IsCancellationRequested
                ? new TimeoutException(
                    $"Quick hydration exceeded the configured {executionSnapshot.Hydration.QuickHydrationTimeoutSeconds}-second timeout.",
                    ex)
                : ex;
            if (timeoutCts.IsCancellationRequested)
            {
                _logger.LogWarning(failure, "QuickHydrationWorker timed out for job {JobId}", job.Id);
            }
            else
            {
                _logger.LogWarning(
                    failure,
                    "QuickHydrationWorker operation was canceled by a downstream dependency for job {JobId}",
                    job.Id);
            }
            await IdentityJobRetryPolicy.ScheduleRetryOrDeadLetterAsync(
                _jobRepo,
                job,
                IdentityJobState.QidResolved,
                failure,
                executionSnapshot.Hydration,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "QuickHydrationWorker failed for job {JobId}", job.Id);
            await IdentityJobRetryPolicy.ScheduleRetryOrDeadLetterAsync(
                _jobRepo,
                job,
                IdentityJobState.QidResolved,
                ex,
                executionSnapshot.Hydration,
                ct).ConfigureAwait(false);
        }
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

    private static int GetBatchSize(PipelineExecutionSnapshot executionSnapshot) =>
        Math.Max(1, executionSnapshot.Core.Pipeline.LeaseSizes.Hydration);

    internal async Task ProcessJobAsync(IdentityJob job, CancellationToken ct)
    {
        await _jobRepo.UpdateStateAsync(job.Id, IdentityJobState.Hydrating, ct: ct);

        if (string.IsNullOrEmpty(job.ResolvedQid))
        {
            _logger.LogWarning("QuickHydrationWorker: job {JobId} has no resolved QID; skipping", job.Id);
            await _jobRepo.UpdateStateAsync(job.Id, IdentityJobState.Failed, "No resolved QID", ct);
            return;
        }

        // Pre-fetch title for narrative logging (best-effort — enrichment will improve it)
        var canonicals = await _canonicalRepo.GetByEntityAsync(job.EntityId, ct);
        var titleForLog = canonicals
            .FirstOrDefault(c => string.Equals(c.Key, MetadataFieldConstants.Title, StringComparison.OrdinalIgnoreCase))?.Value
            ?? canonicals
            .FirstOrDefault(c => string.Equals(c.Key, MetadataFieldConstants.ShowName, StringComparison.OrdinalIgnoreCase))?.Value
            ?? "(unknown)";
        var authorForLog = canonicals
            .FirstOrDefault(c => string.Equals(c.Key, MetadataFieldConstants.Author, StringComparison.OrdinalIgnoreCase))?.Value
            ?? canonicals
            .FirstOrDefault(c => string.Equals(c.Key, MetadataFieldConstants.Artist, StringComparison.OrdinalIgnoreCase))?.Value;

        _logger.LogInformation(
            "Hydration: starting for '{Title}'{AuthorPart} — {Qid} (entity {EntityId})",
            titleForLog,
            string.IsNullOrWhiteSpace(authorForLog) ? string.Empty : $" by {authorForLog}",
            job.ResolvedQid,
            job.EntityId);

        await _enrichment.RunQuickPassAsync(job.EntityId, job.ResolvedQid, ct);

        // Finalize browse grouping after enrichment populates canonical values,
        // before PostPipeline gates organization and Stage 3 batching.
        await _collectionFinalization.FinalizeAsync(
            job.EntityId,
            CollectionFinalizationReason.QuickHydration,
            ct).ConfigureAwait(false);

        await _postPipeline.EvaluateAndOrganizeAsync(
            job.EntityId, job.Id, job.ResolvedQid, ct);

        var refreshedCanonicals = await _canonicalRepo.GetByEntityAsync(job.EntityId, ct).ConfigureAwait(false);
        var batchKey = await BuildUniverseBatchKeyAsync(job.EntityId, job.ResolvedQid, job.MediaType, refreshedCanonicals, ct)
            .ConfigureAwait(false);

        await _jobRepo.UpdateStateAsync(job.Id, IdentityJobState.UniverseEnriching, ct: ct);
        await _universeEnrichment.QueueInlineAsync(
            new UniverseEnrichmentRequest(
                job.Id,
                job.EntityId,
                job.ResolvedQid,
                job.MediaType,
                batchKey,
                titleForLog),
            ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Hydration ready for Stage 3: '{Title}'{AuthorPart} — {Qid} ({MediaType}) [entity {EntityId}]",
            titleForLog,
            string.IsNullOrWhiteSpace(authorForLog) ? string.Empty : $" by {authorForLog}",
            job.ResolvedQid,
            job.MediaType,
            job.EntityId);
    }

    private async Task<string> BuildUniverseBatchKeyAsync(
        Guid entityId,
        string workQid,
        string mediaType,
        IReadOnlyList<CanonicalValue> canonicals,
        CancellationToken ct)
    {
        var lookup = canonicals.ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
        var workId = await _collectionRepo.GetWorkIdByMediaAssetAsync(entityId, ct).ConfigureAwait(false);
        var collectionId = workId.HasValue
            ? await _collectionRepo.GetCollectionIdByWorkIdAsync(workId.Value, ct).ConfigureAwait(false)
            : null;

        return mediaType switch
        {
            nameof(MediaType.Movies) => $"movie:work:{workQid}",
            nameof(MediaType.TV) when collectionId.HasValue => $"tv:collection:{collectionId.Value}",
            nameof(MediaType.TV) when lookup.TryGetValue("series_qid", out var tvSeriesQid) && !string.IsNullOrWhiteSpace(tvSeriesQid)
                => $"tv:series:{tvSeriesQid}",
            nameof(MediaType.TV) => $"tv:work:{workQid}",
            nameof(MediaType.Music) when collectionId.HasValue => $"music:collection:{collectionId.Value}",
            nameof(MediaType.Music) when lookup.TryGetValue("musicbrainz_release_group_id", out var releaseGroupId) && !string.IsNullOrWhiteSpace(releaseGroupId)
                => $"music:release-group:{releaseGroupId}",
            nameof(MediaType.Music) => $"music:work:{workQid}",
            nameof(MediaType.Books) when collectionId.HasValue => $"book:collection:{collectionId.Value}",
            nameof(MediaType.Books) when lookup.TryGetValue("series_qid", out var bookSeriesQid) && !string.IsNullOrWhiteSpace(bookSeriesQid)
                => $"book:series:{bookSeriesQid}",
            nameof(MediaType.Books) => $"book:work:{workQid}",
            nameof(MediaType.Audiobooks) when collectionId.HasValue => $"audiobook:collection:{collectionId.Value}",
            nameof(MediaType.Audiobooks) when lookup.TryGetValue("series_qid", out var audioSeriesQid) && !string.IsNullOrWhiteSpace(audioSeriesQid)
                => $"audiobook:series:{audioSeriesQid}",
            nameof(MediaType.Audiobooks) => $"audiobook:work:{workQid}",
            nameof(MediaType.Comics) when collectionId.HasValue => $"comic:collection:{collectionId.Value}",
            nameof(MediaType.Comics) when lookup.TryGetValue("series_qid", out var comicSeriesQid) && !string.IsNullOrWhiteSpace(comicSeriesQid)
                => $"comic:series:{comicSeriesQid}",
            nameof(MediaType.Comics) => $"comic:work:{workQid}",
            _ => $"work:{workQid}",
        };
    }
}
