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

public sealed partial class WikidataBridgeWorker
{
    private async Task<int> PollCoreAsync(CancellationToken ct)
    {
        // ── Phase 1: Lease ────────────────────────────────────────────────────
        // Strict retail gate: only RetailMatched or RetailMatchedNeedsReview.
        // RetailNoMatch is NEVER included — enforced at the SQL level.

        var leased = await _jobRepo.LeaseNextAsync(
            "WikidataBridgeWorker",
            [IdentityJobState.RetailMatched, IdentityJobState.RetailMatchedNeedsReview],
            GetBatchSize(),
            LeaseDuration,
            ct: ct);

        if (leased.Count == 0)
        {
            return 0;
        }

        // A file in a library that never looks anything up is closed here, before Wikidata is asked (Library setup D38).
        var jobs = await _lookupGuard.SettleBlockedJobsAsync(_jobRepo, leased, _logger, ct).ConfigureAwait(false);
        if (jobs.Count == 0)
        {
            return leased.Count;
        }

        _logger.LogInformation("Wikidata: leased {JobCount} job(s) for bridge resolution", jobs.Count);

        var operationByJobId = new Dictionary<Guid, MediaOperation?>();
        foreach (var job in jobs)
        {
            var operation = await EnsureBridgeOperationAsync(job, MediaOperationStage.Queued, ct).ConfigureAwait(false);
            operationByJobId[job.Id] = operation;
            await MarkBridgeCapabilityQueuedAsync(job, operation, ct).ConfigureAwait(false);
        }

        var reconAdapter = _providers
            .OfType<ReconciliationAdapter>()
            .FirstOrDefault();

        if (reconAdapter is null)
        {
            _logger.LogWarning("No ReconciliationAdapter available — cannot resolve bridge IDs");
            foreach (var j in jobs)
            {
                await _jobRepo.UpdateStateAsync(j.Id, IdentityJobState.QidNoMatch,
                    "No reconciliation adapter configured", ct);
                await MarkBridgeBlockedAsync(operationByJobId.GetValueOrDefault(j.Id), j, "No reconciliation adapter configured", ct).ConfigureAwait(false);
                await TryOrganizeRetainedRetailIdentityAsync(j, ct);
            }

            return jobs.Count;
        }

        // Transition all jobs to BridgeSearching before any async work.
        foreach (var job in jobs)
        {
            try { await _jobRepo.UpdateStateAsync(job.Id, IdentityJobState.BridgeSearching, ct: ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not transition job {JobId} to BridgeSearching", job.Id);
            }
            await UpdateBridgeOperationStageAsync(operationByJobId.GetValueOrDefault(job.Id), MediaOperationStage.ProviderLookup, 10, FindOwnedBookHandoffQid(job) is null ? "Searching Wikidata bridge IDs." : "Using the Wikidata identity from the owned book.", ct).ConfigureAwait(false);
            await MarkBridgeCapabilityRunningAsync(job, operationByJobId.GetValueOrDefault(job.Id), ct).ConfigureAwait(false);
        }

        var contexts = new List<JobContext>(jobs.Count);

        try
        {
            // ── Phase 2: Load context (batch SQL) ─────────────────────────────────
            // Two queries replace N×2 individual reads.
            var lineagesByEntity = new Dictionary<Guid, WorkLineage?>();
            var contextEntityIds = new HashSet<Guid>(jobs.Select(j => j.EntityId));
            foreach (var job in jobs)
            {
                WorkLineage? lineage = null;
                if (string.Equals(job.EntityType, "MediaAsset", StringComparison.OrdinalIgnoreCase))
                {
                    try { lineage = await _workRepo.GetLineageByAssetAsync(job.EntityId, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogDebug(ex,
                            "Wikidata context: lineage lookup failed for asset {EntityId}; using asset-scoped bridge IDs only",
                            job.EntityId);
                    }
                }

                lineagesByEntity[job.EntityId] = lineage;
                if (lineage is not null)
                {
                    contextEntityIds.Add(lineage.TargetForSelfScope);
                    contextEntityIds.Add(lineage.TargetForParentScope);
                }
            }

            var entityIds = contextEntityIds.ToList();
            var allBridgeIds = await _bridgeIdRepo.GetByEntitiesAsync(entityIds, ct);
            var allCanonicals = await _canonicalRepo.GetByEntitiesAsync(entityIds, ct);

            // ── Phase 3: Build job contexts ───────────────────────────────────────
            foreach (var job in jobs)
            {
                if (!Enum.TryParse<MediaType>(job.MediaType, true, out var mediaType))
                {
                    mediaType = MediaType.Unknown;
                }

                var lineage = lineagesByEntity.GetValueOrDefault(job.EntityId);
                var bridgeIds = CollectScopedBridgeIdsForResolution(
                    job.EntityId,
                    mediaType,
                    lineage,
                    allBridgeIds);
                var canonicals = CollectScopedCanonicalsForResolution(
                    job.EntityId,
                    lineage,
                    allCanonicals);
                bridgeIds = MergeCanonicalBridgeIdsForResolution(
                    job.EntityId,
                    mediaType,
                    lineage,
                    bridgeIds,
                    canonicals);
                var resolutionScope = ResolveBridgeResolutionScope(mediaType);
                bridgeIds = OrderBridgeIdsForResolution(mediaType, resolutionScope, bridgeIds);

                var bridgeDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var wikidataProps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var bridge in bridgeIds)
                {
                    bridgeDict.TryAdd(bridge.IdType, bridge.IdValue);

                    var pCode = _bridgeIdHelper.GetPCode(bridge.IdType);
                    if (pCode is not null)
                    {
                        // Media-type aware: TMDB uses P4947 (movies) or P4983 (TV)
                        if (string.Equals(bridge.IdType, BridgeIdKeys.TmdbId, StringComparison.OrdinalIgnoreCase)
                            && mediaType == MediaType.TV)
                        {
                            pCode = "P4983";
                        }
                        wikidataProps.TryAdd(bridge.IdType, pCode);
                    }
                }

                var (
                    titleHint,
                    authorHint,
                    yearHint,
                    albumHint,
                    artistHint,
                    seriesHint,
                    languageHint,
                    seasonNumber,
                    episodeNumber,
                    issueNumber) = BuildLookupHints(
                        mediaType,
                        canonicals,
                        lineage?.TargetForParentScope);

                var context = new JobContext(
                    Job: job,
                    MediaType: mediaType,
                    BridgeIds: bridgeIds,
                    BridgeDict: bridgeDict,
                    WikidataProps: wikidataProps,
                    TitleHint: titleHint,
                    AuthorHint: authorHint,
                    YearHint: yearHint,
                    AlbumHint: albumHint,
                    ArtistHint: artistHint,
                    SeriesHint: seriesHint,
                    LanguageHint: languageHint,
                    SeasonNumber: seasonNumber,
                    EpisodeNumber: episodeNumber,
                    IssueNumber: issueNumber)
                {
                    Operation = operationByJobId.GetValueOrDefault(job.Id)
                };

                // An audiobook that took its Wikidata ID from an owned, confirmed book keeps it: no
                // bridge-ID or text lookup runs for it.
                var ownedBookQid = FindOwnedBookHandoffQid(job);
                if (ownedBookQid is not null)
                {
                    ApplyOwnedBookHandoff(context, ownedBookQid);
                }

                contexts.Add(context);
            }

            // ── Phase 4: Resolve QIDs via the unified facade ──────────────────────
            // ResolveBatchAsync internally groups by music album and bridge ID
            // signatures so N jobs produce far fewer than N Wikidata calls.

            {
                var bridgeCount = contexts.Count(ctx => ctx.MediaType != MediaType.Music && ctx.BridgeIds.Count > 0);
                var titleOnlyCount = contexts.Count(ctx => ctx.MediaType != MediaType.Music && ctx.BridgeIds.Count == 0 && !string.IsNullOrWhiteSpace(ctx.TitleHint));
                var musicCount = contexts.Count(ctx => ctx.MediaType == MediaType.Music);
                _logger.LogInformation(
                    "Wikidata: dispatching {TotalJobs} job(s) to ResolveBatchAsync - {MusicCount} music, {BridgeCount} with bridge IDs, {TitleOnlyCount} non-music title-only request(s) expected to be skipped",
                    contexts.Count, musicCount, bridgeCount, titleOnlyCount);
            }

            var resolveRequests = contexts
                .Where(ctx => ctx.ResolvedQid is null)
                .Select(ctx => BuildResolveRequest(ctx, ctx.Job.Id.ToString()))
                .ToList();

            var resolveResults = resolveRequests.Count == 0
                ? new Dictionary<string, WikidataResolveResult>()
                : await reconAdapter.ResolveBatchAsync(resolveRequests, ct);

            // ── Phase 5: Distribute results onto each job context ──────────────────
            foreach (var ctx in contexts)
            {
                if (!resolveResults.TryGetValue(ctx.Job.Id.ToString(), out var result) || !result.Found)
                {
                    continue;
                }

                await UpdateBridgeOperationStageAsync(ctx.Operation, MediaOperationStage.Analyzing, 60, "Wikidata bridge result received.", ct, new
                {
                    qid = result.WorkQid ?? result.Qid,
                    matched_by = result.MatchedBy.ToString(),
                    candidate_count = result.RankedBridgeCandidates.Count,
                    series_count = result.BridgeSeries.Count,
                    relationship_count = result.BridgeRelationships.Count,
                    diagnostics = result.BridgeDiagnostics
                }).ConfigureAwait(false);

                ctx.ResolvedQid = result.WorkQid ?? result.Qid;
                ctx.AdditionalClaims.AddRange(result.Claims);
                ctx.CollectedBridgeIds = result.CollectedBridgeIds;
                ctx.PrimaryBridgeIdType = result.PrimaryBridgeIdType;
                ctx.MatchedBy = result.MatchedBy switch
                {
                    ResolveStrategy.MusicAlbum => "music_album",
                    ResolveStrategy.BridgeId => "bridge_id",
                    ResolveStrategy.TextSearch => "retail_text",
                    _ => null,
                };

                // Persist the resolution method as a canonical value so the
                // The Dashboard can filter items by how their Wikidata match was made.
                if (ctx.MatchedBy is not null)
                {
                    var canonicalMethod = ResolveQidResolutionMethod(ctx.MediaType, ctx.MatchedBy);
                    ctx.AdditionalClaims.Add(new ProviderClaim(
                        MetadataFieldConstants.QidResolutionMethod, canonicalMethod, 1.0));
                }

                MarkComicTextResolvedQidAsSeriesScope(ctx);
                MarkContainerRollupQidScope(ctx);

                // Album rollups must emit a QID claim so lineage-aware scoring can
                // persist the resolved identity on the album Work.
                if (result.MatchedBy == ResolveStrategy.MusicAlbum
                    && !string.IsNullOrWhiteSpace(ctx.ResolvedQid)
                    && !ctx.AdditionalClaims.Any(c => string.Equals(
                        c.Key, BridgeIdKeys.WikidataQid, StringComparison.OrdinalIgnoreCase)))
                {
                    ctx.AdditionalClaims.Add(new ProviderClaim(
                        BridgeIdKeys.WikidataQid, ctx.ResolvedQid, 0.95));
                }
            }

            await TryResolveComicSeriesRollupsAsync(contexts, reconAdapter, ct).ConfigureAwait(false);
            await TryResolveSiblingVariantQidsAsync(contexts, lineagesByEntity, ct).ConfigureAwait(false);

            // ── Phase 5 summary ───────────────────────────────────────────────────
            {
                var resolvedCount = contexts.Count(ctx => ctx.ResolvedQid is not null);
                _logger.LogInformation(
                    "Wikidata: distributing results — {Resolved} of {Total} job(s) have a resolved QID",
                    resolvedCount, contexts.Count);
            }
        }
        catch (Exception ex) when (ShouldResetBatchAfterFailure(ex, ct))
        {
            _logger.LogError(ex,
                "Wikidata: batch resolution failed for {Count} job(s) — resetting for retry",
                jobs.Count);

            // Reset all jobs from BridgeSearching back to their pre-lease state
            // so the next poll cycle can retry them.
            foreach (var job in jobs)
            {
                try
                {
                    // job.State still holds the pre-BridgeSearching value (RetailMatched
                    // or RetailMatchedNeedsReview) because UpdateStateAsync only writes
                    // to the DB, not the in-memory IdentityJob object.
                    var resetState = Enum.TryParse<IdentityJobState>(job.State, true, out var s)
                        ? s
                        : IdentityJobState.RetailMatched;
                    await IdentityJobRetryPolicy.ScheduleRetryOrDeadLetterAsync(
                        _jobRepo,
                        job,
                        resetState,
                        ex,
                        GetExecutionSnapshot().Hydration,
                        ct);
                }
                catch (Exception resetEx)
                {
                    _logger.LogWarning(resetEx,
                        "Could not reset job {JobId} after batch failure", job.Id);
                }
            }

            return jobs.Count;
        }

        // ── Phase 6: Per-job finalisation ─────────────────────────────────────
        // E1 — QID dedup: group resolved non-music contexts by (QID, MediaType)
        // and call FetchAsync once per unique group. The fetched claims are stored
        // on all sibling contexts so the per-job finalisation path can apply them
        // without a second HTTP call (even if the adapter's response cache would
        // have served it from memory, this makes the dedup explicit and measurable).
        var resolvedContextsNeedingFetch = contexts
            .Where(ctx => ctx.ResolvedQid is not null
                && (ctx.MediaType != MediaType.Music || ctx.MatchedBy == "music_album"))
            .ToList();

        var qidGroups = resolvedContextsNeedingFetch
            .GroupBy(ctx => (ctx.ResolvedQid!, ctx.MediaType))
            .ToList();

        var dedupSavings = 0;
        foreach (var group in qidGroups)
        {
            var siblings = group.ToList();
            var representative = siblings[0];

            IReadOnlyList<ProviderClaim>? sharedClaims = null;
            try
            {
                sharedClaims = await reconAdapter.FetchAsync(
                    new ProviderLookupRequest
                    {
                        EntityId = representative.Job.EntityId,
                        EntityType = EntityType.MediaAsset,
                        MediaType = representative.MediaType,
                        Title = representative.TitleHint,
                        Year = representative.YearHint,
                        PreResolvedQid = representative.ResolvedQid,
                        FileLanguage = representative.LanguageHint,
                    }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Wikidata QID dedup: FetchAsync failed for QID {Qid} ({MediaType})",
                    representative.ResolvedQid, representative.MediaType);
            }

            // Fan out the pre-fetched claims to all siblings. The representative gets
            // them too, so FinaliseJobAsync skips its own FetchAsync call for all
            // members of the group.
            foreach (var sibling in siblings)
            {
                sibling.PreFetchedClaims = sharedClaims;
            }

            if (siblings.Count > 1)
            {
                dedupSavings += siblings.Count - 1;
            }
        }

        if (dedupSavings > 0)
        {
            _logger.LogInformation(
                "Wikidata: QID dedup saved {Savings} FetchAsync call(s) across {Groups} unique QID group(s)",
                dedupSavings, qidGroups.Count(g => g.Count() > 1));
        }

        var allCandidates = new List<WikidataBridgeCandidate>();

        foreach (var ctx in contexts)
        {
            try
            {
                await FinaliseJobAsync(ctx, reconAdapter, allCandidates, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "WikidataBridgeWorker finalisation failed for job {JobId}", ctx.Job.Id);
                await IdentityJobRetryPolicy.ScheduleRetryOrDeadLetterAsync(
                        _jobRepo,
                        ctx.Job,
                        IdentityJobState.RetailMatched,
                        ex,
                        GetExecutionSnapshot().Hydration,
                        ct)
                    .ConfigureAwait(false);
                await MarkBridgeFailedAsync(ctx.Operation, ctx.Job, ex, terminal: true, ct).ConfigureAwait(false);
            }
        }

        // Batch-insert all candidates in one call.
        if (allCandidates.Count > 0)
        {
            await _candidateRepo.InsertBatchAsync(allCandidates, ct);
        }

        return jobs.Count;
    }

    // -------------------------------------------------------------------------
    // Per-job finalisation (Phase 6)
    // -------------------------------------------------------------------------

}
