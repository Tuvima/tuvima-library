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
    private async Task FinaliseJobAsync(
        JobContext ctx,
        ReconciliationAdapter reconAdapter,
        List<WikidataBridgeCandidate> allCandidates,
        CancellationToken ct)
    {
        var job = ctx.Job;

        // Phase 3c: fetch lineage once for this job. Used by both
        // ScoringHelper (parent-scope claim mirroring into the parent Work's
        // canonical_values) and RouteToWorksAsync (writing the resolved QID
        // and bridge IDs to works.external_identifiers). One DB round-trip
        // per job, reused throughout finalisation.
        WorkLineage? lineage = null;
        try { lineage = await _workRepo.GetLineageByAssetAsync(job.EntityId, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex,
                "Phase 3c: lineage lookup failed for asset {EntityId} — parent-scope mirror and Work routing skipped",
                job.EntityId);
        }

        if (ctx.ResolvedQid is null)
        {
            await TryResolveSiblingVariantQidAsync(ctx, lineage, ct).ConfigureAwait(false);
        }

        if (ctx.ResolvedQid is not null && ctx.MediaType == MediaType.Music
            && !MusicAlbumIdentityEvidence.Corroborates(ctx.ArtistHint, ctx.BridgeDict,
                ctx.AdditionalClaims.Concat(ctx.PreFetchedClaims ?? [])))
        {
            _logger.LogWarning("Discarding uncorroborated album QID {Qid} for {Album} by {Artist}; retaining retail identity.",
                ctx.ResolvedQid, ctx.AlbumHint, ctx.ArtistHint);
            ctx.ResolvedQid = null;
            ctx.AdditionalClaims.Clear();
            ctx.CollectedBridgeIds = null;
        }

        if (ctx.ResolvedQid is not null)
        {
            // Build candidate record.
            var (scoreTotal, isExact) = ctx.MatchedBy switch
            {
                "music_album" => (0.95, true),
                "bridge_id" => (1.0, true),
                "comic_series_rollup" => (0.90, true),
                "sibling_variant" => (0.93, true),
                _ => (0.75, false)
            };

            allCandidates.Add(new WikidataBridgeCandidate
            {
                JobId = job.Id,
                Qid = ctx.ResolvedQid,
                Label = ctx.AlbumHint ?? ctx.TitleHint ?? ctx.ResolvedQid,
                MatchedBy = ctx.MatchedBy ?? "unknown",
                BridgeIdType = ctx.PrimaryBridgeIdType,
                IsExactMatch = isExact,
                ScoreTotal = scoreTotal,
                Outcome = "AutoAccepted",
            });

            // Persist claims accumulated during group resolution.
            if (ctx.AdditionalClaims.Count > 0)
            {
                // Phase 3c: lineage-aware persist mirrors parent-scope display
                // claims (album, year, cover) onto the parent Work.
                await ScoringHelper.PersistAndScoreWithLineageAsync(
                    job.EntityId, ctx.AdditionalClaims, reconAdapter.ProviderId, lineage,
                    _claimRepo, _canonicalRepo, _scoringEngine, _configLoader, _providers, ct,
                    arrayRepo: _arrayRepo, logger: _logger);

                // Phase 3b: route any container-level structural data (the album
                // QID, child entity manifests) onto the parent Work.
                await RouteToWorksAsync(lineage, job.EntityId, ctx.MediaType, ctx.ResolvedQid,
                    ctx.AdditionalClaims, ct);
            }

            // Persist collected bridge IDs (non-music bridge resolution only).
            // ReconciliationAdapter.BuildClaimsForResolvedQidAsync now emits the dictionary
            // keyed by bridge claim key (e.g. "isbn_13", "tmdb_id"), not raw P-code.
            if (ctx.CollectedBridgeIds is { Count: > 0 })
            {
                var collectedEntries = ctx.CollectedBridgeIds
                    .Select(kvp => new BridgeIdEntry
                    {
                        EntityId = ResolveBridgeIdEntityId(lineage, job.EntityId, kvp.Key),
                        IdType = kvp.Key,
                        IdValue = kvp.Value,
                        ProviderId = reconAdapter.ProviderId.ToString(),
                        WikidataProperty = _bridgeIdHelper.GetPCode(kvp.Key),
                    }).ToList();

                await _bridgeIdRepo.UpsertBatchAsync(collectedEntries, ct);
            }

            // Record timeline event.
            var timelineMethod = ctx.PrimaryBridgeIdType ?? ctx.MatchedBy ?? "bridge_id";
            await _timeline.RecordBridgeResolvedAsync(job.EntityId, ctx.ResolvedQid, timelineMethod, job.IngestionRunId, ct);

            _logger.LogInformation(
                "Wikidata: '{Title}' identified as {Qid} via {Method} [entity {EntityId}]",
                ctx.TitleHint ?? ctx.AlbumHint ?? "(unknown)", ctx.ResolvedQid, ctx.MatchedBy, job.EntityId);

            await _jobRepo.SetResolvedQidAsync(job.Id, ctx.ResolvedQid, ct);
            await _jobRepo.UpdateStateAsync(job.Id, IdentityJobState.QidResolved, ct: ct);

            // Skip post-resolve property fetch for music — the resolved QID is the
            // ALBUM, not the track. Fetching its properties would overwrite the
            // track's title/duration/artist with album-level values.
            if (ctx.MediaType == MediaType.Music)
            {
                if (ctx.MatchedBy == "music_album")
                {
                    await UpdateBridgeOperationStageAsync(ctx.Operation, MediaOperationStage.ProviderLookup, 75, "Fetching Wikidata album properties.", ct, new
                    {
                        qid = ctx.ResolvedQid,
                        media_type = ctx.MediaType.ToString(),
                    }).ConfigureAwait(false);

                    IReadOnlyList<ProviderClaim> albumClaims;
                    if (ctx.PreFetchedClaims is not null)
                    {
                        albumClaims = ctx.PreFetchedClaims;
                    }
                    else
                    {
                        try
                        {
                            albumClaims = await reconAdapter.FetchAsync(
                                new ProviderLookupRequest
                                {
                                    EntityId = job.EntityId,
                                    EntityType = EntityType.MediaAsset,
                                    MediaType = ctx.MediaType,
                                    Title = ctx.TitleHint,
                                    Year = ctx.YearHint,
                                    PreResolvedQid = ctx.ResolvedQid,
                                    FileLanguage = ctx.LanguageHint,
                                }, ct);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogWarning(ex,
                                "Music album property fetch failed for QID {Qid} (entity {EntityId})",
                                ctx.ResolvedQid, job.EntityId);
                            albumClaims = [];
                        }
                    }

                    if (albumClaims.Count > 0)
                    {
                        var parentScopedAlbumClaims = albumClaims
                            .Where(c => ClaimScopeCatalog.IsParentScoped(c.Key, MediaType.Music)
                                || BridgeIdKeys.All.Contains(c.Key))
                            .ToList();

                        if (parentScopedAlbumClaims.Count > 0)
                        {
                            await ScoringHelper.PersistAndScoreWithLineageAsync(
                                job.EntityId, parentScopedAlbumClaims, reconAdapter.ProviderId, lineage,
                                _claimRepo, _canonicalRepo, _scoringEngine, _configLoader, _providers, ct,
                                arrayRepo: _arrayRepo, logger: _logger);

                            await RouteToWorksAsync(lineage, job.EntityId, ctx.MediaType, ctx.ResolvedQid,
                                parentScopedAlbumClaims, ct);
                        }
                    }
                }

                await _postPipeline.EvaluateAndOrganizeAsync(
                    job.EntityId, job.Id, ctx.ResolvedQid, job.IngestionRunId, ct);
                await _coverArt.DownloadAndPersistAsync(job.EntityId, ctx.ResolvedQid, ct)
                    .ConfigureAwait(false);
                await MarkBridgeSucceededAsync(ctx.Operation, job, ctx.ResolvedQid, ct).ConfigureAwait(false);
                return;
            }

            // Fetch full properties now that we have a QID.
            // Phase 6 QID dedup (E1): if a pre-fetched claims set was computed
            // for this QID group, use it directly — no HTTP call needed.
            // Otherwise fall back to FetchAsync (covers the single-job case and
            // any group whose representative FetchAsync failed).
            await UpdateBridgeOperationStageAsync(ctx.Operation, MediaOperationStage.ProviderLookup, 75, "Fetching full Wikidata properties.", ct, new
            {
                qid = ctx.ResolvedQid,
                media_type = ctx.MediaType.ToString(),
            }).ConfigureAwait(false);

            IReadOnlyList<ProviderClaim> fullClaims;
            var structuredFetchCompleted = false;
            if (ctx.PreFetchedClaims is not null)
            {
                fullClaims = ctx.PreFetchedClaims;
                structuredFetchCompleted = true;
            }
            else
            {
                try
                {
                    fullClaims = await reconAdapter.FetchAsync(
                        new ProviderLookupRequest
                        {
                            EntityId = job.EntityId,
                            EntityType = EntityType.MediaAsset,
                            MediaType = ctx.MediaType,
                            Title = ctx.TitleHint,
                            Year = ctx.YearHint,
                            PreResolvedQid = ctx.ResolvedQid,
                            FileLanguage = ctx.LanguageHint,
                        }, ct);
                    structuredFetchCompleted = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex,
                        "Full property fetch failed for QID {Qid} (entity {EntityId})",
                        ctx.ResolvedQid, job.EntityId);
                    fullClaims = [];
                }
            }

            if (fullClaims.Count > 0)
            {
                await UpdateBridgeOperationStageAsync(ctx.Operation, MediaOperationStage.WritingArtifact, 85, "Persisting Wikidata claims.", ct, new
                {
                    qid = ctx.ResolvedQid,
                    claim_count = fullClaims.Count,
                }).ConfigureAwait(false);

                // Phase 3c: lineage-aware persist mirrors parent-scope
                // display claims (show_name, year, description, cover,
                // genre, cast) onto the parent Work — the show or series.
                await ScoringHelper.PersistAndScoreWithLineageAsync(
                    job.EntityId, fullClaims, reconAdapter.ProviderId, lineage,
                    _claimRepo, _canonicalRepo, _scoringEngine, _configLoader, _providers, ct,
                    arrayRepo: _arrayRepo, logger: _logger);

                // Phase 3b: route the QID and container fields onto the
                // correct Work, then upsert any catalog children.
                await RouteToWorksAsync(lineage, job.EntityId, ctx.MediaType, ctx.ResolvedQid,
                    fullClaims, ct);

                // Quick hydration runs the people pass after QID resolution. Keeping
                // it out of the bridge batch lets later media commit their QIDs
                // without waiting on person/image enrichment for earlier items.
            }

            if (structuredFetchCompleted)
            {
                await MarkStructuredDiscoveryObservedAsync(job.EntityId, lineage, ctx.MediaType, fullClaims, ct)
                    .ConfigureAwait(false);
            }

            await TryHydrateSeriesManifestAsync(job, ctx, lineage, ctx.ResolvedQid, fullClaims, ct);

            if (_collectionFinalization is not null)
            {
                await _collectionFinalization.FinalizeAsync(
                    job.EntityId,
                    CollectionFinalizationReason.QidResolved,
                    job.IngestionRunId,
                    ct).ConfigureAwait(false);
            }

            await _postPipeline.EvaluateAndOrganizeAsync(
                job.EntityId, job.Id, ctx.ResolvedQid, job.IngestionRunId, ct);
            await _coverArt.DownloadAndPersistAsync(job.EntityId, ctx.ResolvedQid, ct)
                .ConfigureAwait(false);
            await TryMergeReadWorkIdentitiesAsync(ctx.MediaType, ct).ConfigureAwait(false);
            await MarkBridgeSucceededAsync(ctx.Operation, job, ctx.ResolvedQid, ct).ConfigureAwait(false);
        }
        else
        {
            // No QID found at all.
            await _jobRepo.UpdateStateAsync(job.Id, IdentityJobState.QidNoMatch, ct: ct);
            await _timeline.RecordBridgeNoMatchAsync(
                job.EntityId, job.IngestionRunId, ct);

            var reviewCreated = ShouldCreateReviewForBridgeNoMatch(ctx);
            if (reviewCreated)
            {
                await _outcomeFactory.CreateWikidataBridgeFailedAsync(
                    job.EntityId,
                    "Wikidata bridge resolution did not find a confirmed identity after the item had already been marked for review.",
                    job.IngestionRunId,
                    null,
                    ct).ConfigureAwait(false);
            }

            await TryOrganizeRetainedRetailIdentityAsync(job, ct);
            await MarkBridgeNoResultAsync(ctx.Operation, job, "No Wikidata candidate matched the retail bridge IDs or title hints.", ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Wikidata: no match for '{Title}' ({MediaType}) — {BridgeCount} bridge ID(s) tried; retaining retail identity; review_created={ReviewCreated} [entity {EntityId}]",
                ctx.TitleHint ?? "(unknown)", ctx.MediaType, ctx.BridgeIds.Count, reviewCreated, job.EntityId);
        }
    }

    private static bool ShouldCreateReviewForBridgeNoMatch(JobContext ctx) =>
        string.Equals(
            ctx.Job.State,
            IdentityJobState.RetailMatchedNeedsReview.ToString(),
            StringComparison.OrdinalIgnoreCase);

    private async Task TryResolveComicSeriesRollupsAsync(
        IEnumerable<JobContext> contexts,
        ReconciliationAdapter reconAdapter,
        CancellationToken ct)
    {
        foreach (var ctx in contexts)
        {
            await TryResolveComicSeriesRollupAsync(ctx, reconAdapter, ct).ConfigureAwait(false);
        }
    }

    private async Task TryResolveSiblingVariantQidsAsync(
        IEnumerable<JobContext> contexts,
        IReadOnlyDictionary<Guid, WorkLineage?> lineagesByEntity,
        CancellationToken ct)
    {
        foreach (var ctx in contexts)
        {
            lineagesByEntity.TryGetValue(ctx.Job.EntityId, out var lineage);
            await TryResolveSiblingVariantQidAsync(ctx, lineage, ct).ConfigureAwait(false);
        }
    }

    private async Task TryResolveSiblingVariantQidAsync(
        JobContext ctx,
        WorkLineage? lineage,
        CancellationToken ct)
    {
        if (!ShouldAttemptSiblingVariantQid(ctx))
        {
            return;
        }

        var candidateMediaTypes = GetSiblingVariantCandidateMediaTypes(ctx.MediaType);
        if (candidateMediaTypes.Count == 0)
        {
            return;
        }

        var creator = ctx.AuthorHint ?? ctx.ArtistHint;
        try
        {
            var match = await _workRepo.FindConfirmedSiblingQidAsync(
                ctx.MediaType,
                candidateMediaTypes,
                ctx.TitleHint!,
                creator,
                lineage?.TargetForSelfScope,
                ct).ConfigureAwait(false);

            if (match is null || string.IsNullOrWhiteSpace(match.WikidataQid))
            {
                return;
            }

            ctx.ResolvedQid = match.WikidataQid;
            ctx.MatchedBy = "sibling_variant";
            ctx.PrimaryBridgeIdType = null;
            AddDistinctAdditionalClaim(ctx, BridgeIdKeys.WikidataQid, match.WikidataQid, 0.93);
            AddDistinctAdditionalClaim(ctx, MetadataFieldConstants.QidResolutionMethod, "sibling_variant", 0.95);

            _logger.LogInformation(
                "Wikidata: resolved {MediaType} '{Title}' to QID {Qid} from owned {SiblingMediaType} sibling '{SiblingTitle}' [entity {EntityId}]",
                ctx.MediaType,
                ctx.TitleHint,
                match.WikidataQid,
                match.MediaType,
                match.Title,
                ctx.Job.EntityId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(
                ex,
                "Wikidata: sibling variant QID lookup failed for entity {EntityId} ({Title})",
                ctx.Job.EntityId,
                ctx.TitleHint);
        }
    }

    private static bool ShouldAttemptSiblingVariantQid(JobContext ctx)
    {
        return string.IsNullOrWhiteSpace(ctx.ResolvedQid)
            && !string.IsNullOrWhiteSpace(ctx.TitleHint)
            && string.Equals(ctx.Job.State, IdentityJobState.RetailMatched.ToString(), StringComparison.OrdinalIgnoreCase)
            && ctx.MediaType is MediaType.Books or MediaType.Audiobooks;
    }

    private static IReadOnlyList<MediaType> GetSiblingVariantCandidateMediaTypes(MediaType mediaType) =>
        mediaType switch
        {
            MediaType.Audiobooks => [MediaType.Books],
            MediaType.Books => [MediaType.Audiobooks],
            _ => [],
        };

    private async Task TryResolveComicSeriesRollupAsync(
        JobContext ctx,
        ReconciliationAdapter reconAdapter,
        CancellationToken ct)
    {
        if (!ShouldAttemptComicSeriesRollup(ctx))
        {
            return;
        }

        try
        {
            var result = await reconAdapter.ResolveAsync(
                new WikidataResolveRequest
                {
                    CorrelationKey = $"{ctx.Job.Id}:comic-series-rollup",
                    MediaType = MediaType.Comics,
                    Strategy = ResolveStrategy.Auto,
                    BridgeIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    WikidataProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    IsEditionAware = false,
                    AllowConstrainedTextFallback = true,
                    Title = ctx.SeriesHint,
                    Author = ctx.AuthorHint,
                    Year = null,
                    FileLanguage = ctx.LanguageHint,
                    SeriesTitle = ctx.SeriesHint,
                    IssueNumber = null,
                },
                ct).ConfigureAwait(false);

            if (!result.Found)
            {
                return;
            }

            var qid = result.WorkQid ?? result.Qid;
            if (string.IsNullOrWhiteSpace(qid))
            {
                return;
            }

            ctx.ResolvedQid = qid;
            ctx.MatchedBy = "comic_series_rollup";
            ctx.PrimaryBridgeIdType =
                ctx.BridgeIds.FirstOrDefault(entry =>
                    string.Equals(entry.IdType, BridgeIdKeys.ComicVineVolumeId, StringComparison.OrdinalIgnoreCase))?.IdType
                ?? ctx.BridgeIds.FirstOrDefault(entry =>
                    string.Equals(entry.IdType, BridgeIdKeys.ComicVineId, StringComparison.OrdinalIgnoreCase))?.IdType;
            ctx.AdditionalClaims.AddRange(result.Claims);
            AddDistinctAdditionalClaim(ctx, BridgeIdKeys.WikidataQid, qid, 0.9);
            AddDistinctAdditionalClaim(ctx, MetadataFieldConstants.WikidataQidScope, "series", 0.95);
            AddDistinctAdditionalClaim(ctx, MetadataFieldConstants.QidResolutionMethod, "comic_series_rollup", 0.95);
            ctx.CollectedBridgeIds = result.CollectedBridgeIds;

            _logger.LogInformation(
                "Wikidata: rolled comic issue '{Title}' up to series/run QID {Qid} using trusted ComicVine run context [entity {EntityId}]",
                ctx.TitleHint ?? ctx.SeriesHint ?? "(unknown)",
                qid,
                ctx.Job.EntityId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(
                ex,
                "Wikidata: comic series rollup failed for entity {EntityId} ({Series})",
                ctx.Job.EntityId,
                ctx.SeriesHint);
        }
    }

    private static bool ShouldAttemptComicSeriesRollup(JobContext ctx)
    {
        if (ctx.MediaType != MediaType.Comics
            || !string.IsNullOrWhiteSpace(ctx.ResolvedQid)
            || string.IsNullOrWhiteSpace(ctx.SeriesHint)
            || !string.Equals(ctx.Job.State, IdentityJobState.RetailMatched.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ctx.BridgeIds.Any(entry =>
            string.Equals(entry.IdType, BridgeIdKeys.ComicVineId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.IdType, BridgeIdKeys.ComicVineVolumeId, StringComparison.OrdinalIgnoreCase));
    }

    private static void MarkComicTextResolvedQidAsSeriesScope(JobContext ctx)
    {
        if (ctx.MediaType != MediaType.Comics
            || !string.Equals(ctx.MatchedBy, "retail_text", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(ctx.ResolvedQid)
            || string.IsNullOrWhiteSpace(ctx.SeriesHint)
            || !ctx.BridgeIds.Any(entry =>
                string.Equals(entry.IdType, BridgeIdKeys.ComicVineId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.IdType, BridgeIdKeys.ComicVineVolumeId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        ctx.MatchedBy = "comic_series_rollup";
        ctx.PrimaryBridgeIdType ??=
            ctx.BridgeIds.FirstOrDefault(entry =>
                string.Equals(entry.IdType, BridgeIdKeys.ComicVineVolumeId, StringComparison.OrdinalIgnoreCase))?.IdType
            ?? ctx.BridgeIds.FirstOrDefault(entry =>
                string.Equals(entry.IdType, BridgeIdKeys.ComicVineId, StringComparison.OrdinalIgnoreCase))?.IdType;

        ctx.AdditionalClaims.RemoveAll(claim =>
            string.Equals(claim.Key, MetadataFieldConstants.QidResolutionMethod, StringComparison.OrdinalIgnoreCase));
        AddDistinctAdditionalClaim(ctx, BridgeIdKeys.WikidataQid, ctx.ResolvedQid, 0.95);
        AddDistinctAdditionalClaim(ctx, MetadataFieldConstants.WikidataQidScope, "series", 0.95);
        AddDistinctAdditionalClaim(ctx, MetadataFieldConstants.QidResolutionMethod, "comic_series_rollup", 1.0);
    }

    private static void AddDistinctAdditionalClaim(
        JobContext ctx,
        string key,
        string value,
        double confidence)
    {
        if (ctx.AdditionalClaims.Any(claim =>
            string.Equals(claim.Key, key, StringComparison.OrdinalIgnoreCase)
            && string.Equals(claim.Value, value, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        ctx.AdditionalClaims.Add(new ProviderClaim(key, value, confidence));
    }

    private async Task TryMergeReadWorkIdentitiesAsync(MediaType mediaType, CancellationToken ct)
    {
        if (_workIdentityReconciliation is null
            || mediaType is not (MediaType.Books or MediaType.Audiobooks))
        {
            return;
        }

        try
        {
            var merged = await _workIdentityReconciliation.MergeDuplicateReadWorksByQidAsync(ct)
                .ConfigureAwait(false);
            if (merged > 0)
            {
                _logger.LogInformation(
                    "Wikidata: merged {Count} duplicate read work(s) after QID finalization.",
                    merged);
            }

            var aligned = await _workIdentityReconciliation.AlignAudiobookAuthorsWithBooksByQidAsync(ct)
                .ConfigureAwait(false);
            if (aligned > 0)
            {
                _logger.LogInformation(
                    "Wikidata: aligned author identities for {Count} audiobook work(s) after QID finalization.",
                    aligned);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Read work identity merge failed after Wikidata finalization; ingestion will continue.");
        }
    }

    private async Task TryHydrateSeriesManifestAsync(
        IdentityJob job,
        JobContext ctx,
        WorkLineage? lineage,
        string? resolvedQid,
        IReadOnlyList<ProviderClaim> fullClaims,
        CancellationToken ct)
    {
        if (_seriesManifestHydration is null || string.IsNullOrWhiteSpace(resolvedQid))
        {
            return;
        }

        try
        {
            await _seriesManifestHydration.HydrateAsync(new SeriesManifestHydrationContext(
                AssetId: job.EntityId,
                WorkId: lineage?.TargetForSelfScope,
                ResolvedWorkQid: resolvedQid,
                MediaType: ctx.MediaType,
                Title: ctx.TitleHint ?? ctx.AlbumHint,
                SeriesHint: ctx.SeriesHint,
                IngestionRunId: job.IngestionRunId,
                Lineage: lineage,
                FullClaims: fullClaims), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Series manifest hydration failed for job {JobId}; ingestion will continue",
                job.Id);
        }
    }

    // -------------------------------------------------------------------------
    // Public helpers used by the synchronous pipeline
    // -------------------------------------------------------------------------

    /// <summary>
    /// Processes a single identity job synchronously — used by
    /// <see cref="SynchronousIdentityPipelineService"/> when a single asset needs
    /// Stage 2 resolution without waiting for the next background poll.
    ///
    /// Internally creates a single-item batch and runs all six phases, so the
    /// semantics are identical to the batch path in <see cref="PollAsync"/>.
    /// </summary>
}
