using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Jobs;
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

public sealed partial class RetailMatchWorker
{
    private sealed record ProviderAttempt(
        PipelineProviderEntry Entry,
        Dictionary<string, string> Hints,
        bool AllowAcceptedTransition,
        string HintSource);

    private static bool TransitionMatches(string configuredOutcome, bool fallbackIdentityAccepted) =>
        configuredOutcome.ToLowerInvariant() switch
        {
            "accepted" => true,
            "identity-fallback-accepted" => fallbackIdentityAccepted,
            _ => false
        };

    private static Dictionary<string, string> BuildTransitionHints(
        IReadOnlyDictionary<string, string> currentHints,
        IReadOnlyList<ProviderClaim> acceptedClaims,
        IReadOnlyCollection<string> configuredFields)
    {
        var merged = new Dictionary<string, string>(currentHints, StringComparer.OrdinalIgnoreCase);
        var allowed = configuredFields.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var claim in acceptedClaims)
        {
            if (!allowed.Contains(claim.Key) || string.IsNullOrWhiteSpace(claim.Value))
            {
                continue;
            }

            merged[claim.Key] = TextEncodingRepair.RepairMojibake(claim.Value.Trim());
        }

        return merged;
    }

    private async Task<Dictionary<string, string>> BuildFileHintsAsync(Guid entityId, CancellationToken ct)
    {
        var batched = await BuildFileHintsBatchAsync([entityId], ct).ConfigureAwait(false);
        return batched.GetValueOrDefault(entityId)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyDictionary<Guid, Dictionary<string, string>>> BuildFileHintsBatchAsync(
        IReadOnlyList<Guid> entityIds,
        CancellationToken ct)
    {
        var ids = entityIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, Dictionary<string, string>>();
        }

        var canonicalBatch = await _canonicalRepo.GetByEntitiesAsync(ids, ct).ConfigureAwait(false);
        var arrayBatch = _arrayRepo is null
            ? new Dictionary<Guid, IReadOnlyDictionary<string, IReadOnlyList<CanonicalArrayEntry>>>()
            : await _arrayRepo.GetAllByEntitiesAsync(ids, ct).ConfigureAwait(false);
        var claimBatch = await _claimRepo.GetByEntitiesAsync(ids, ct).ConfigureAwait(false);

        var result = new Dictionary<Guid, Dictionary<string, string>>();
        foreach (var entityId in ids)
        {
            ct.ThrowIfCancellationRequested();
            var hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            MergeFileHints(
                hints,
                canonicalBatch.GetValueOrDefault(entityId) ?? [],
                arrayBatch.GetValueOrDefault(entityId),
                claimBatch.GetValueOrDefault(entityId) ?? []);
            result[entityId] = hints;
        }

        return result;
    }

    private static void MergeFileHints(
        Dictionary<string, string> hints,
        IReadOnlyList<CanonicalValue> canonicals,
        IReadOnlyDictionary<string, IReadOnlyList<CanonicalArrayEntry>>? arrays,
        IReadOnlyList<MetadataClaim> claims)
    {
        foreach (var c in canonicals)
        {
            if (!string.IsNullOrWhiteSpace(c.Key) && !string.IsNullOrWhiteSpace(c.Value))
            {
                hints.TryAdd(c.Key, TextEncodingRepair.RepairMojibake(c.Value));
            }
        }

        if (arrays is not null)
        {
            foreach (var (key, entries) in arrays)
            {
                if (hints.ContainsKey(key))
                {
                    continue;
                }

                var values = entries
                    .OrderBy(entry => entry.Ordinal)
                    .Select(entry => TextEncodingRepair.RepairMojibake(entry.Value))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (values.Count > 0)
                {
                    hints.TryAdd(key, JoinHintValues(key, values));
                }
            }
        }

        foreach (var group in claims
            .Where(claim => !string.IsNullOrWhiteSpace(claim.ClaimKey)
                && !string.IsNullOrWhiteSpace(claim.ClaimValue))
            .GroupBy(claim => claim.ClaimKey, StringComparer.OrdinalIgnoreCase))
        {
            if (hints.ContainsKey(group.Key))
            {
                continue;
            }

            var values = group
                .OrderByDescending(claim => claim.IsUserLocked)
                .ThenByDescending(claim => claim.Confidence)
                .ThenByDescending(claim => claim.ClaimedAt)
                .Select(claim => TextEncodingRepair.RepairMojibake(claim.ClaimValue.Trim()))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (values.Count > 0)
            {
                hints.TryAdd(group.Key, JoinHintValues(group.Key, values));
            }
        }
    }

    private static string JoinHintValues(string key, IReadOnlyList<string> values)
    {
        if (values.Count == 1 || !IsMultiValueCreatorHint(key))
        {
            return values[0];
        }

        return string.Join(" and ", values);
    }

    private static bool IsMultiValueCreatorHint(string key) =>
        key.Equals(MetadataFieldConstants.Author, StringComparison.OrdinalIgnoreCase)
        || key.Equals(MetadataFieldConstants.Artist, StringComparison.OrdinalIgnoreCase)
        || key.Equals(MetadataFieldConstants.Composer, StringComparison.OrdinalIgnoreCase)
        || key.Equals(MetadataFieldConstants.Director, StringComparison.OrdinalIgnoreCase)
        || key.Equals(MetadataFieldConstants.Narrator, StringComparison.OrdinalIgnoreCase)
        || key.Equals(MetadataFieldConstants.Illustrator, StringComparison.OrdinalIgnoreCase)
        || key.Equals("writer", StringComparison.OrdinalIgnoreCase);

    internal async Task ProcessJobAsync(IdentityJob job, CancellationToken ct)
    {
        await _jobRepo.UpdateStateAsync(job.Id, IdentityJobState.RetailSearching, ct: ct);

        if (!Enum.TryParse<MediaType>(job.MediaType, true, out var mediaType))
        {
            mediaType = MediaType.Unknown;
        }

        // Look up the asset's work lineage once. Used by the router below to
        // write provider bridge IDs to the correct Work — track-level IDs
        // (apple_music_id, isrc) on the asset's own Work; album-level IDs
        // (apple_music_collection_id, musicbrainz_id) on the parent. Null
        // when the job targets a Work directly (manual flows) — in that case
        // we skip work-level routing entirely.
        WorkLineage? lineage = null;
        if (string.Equals(job.EntityType, "MediaAsset", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                lineage = await _workRepo.GetLineageByAssetAsync(job.EntityId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Lineage lookup failed for asset {EntityId} — work-level external_identifiers writes will be skipped",
                    job.EntityId);
            }
        }

        // Load pipeline configuration for this media type
        var executionConfig = GetExecutionSnapshot();
        var pipelineConfig = executionConfig.Pipelines;
        var pipeline = pipelineConfig.GetPipelineForMediaType(job.MediaType);
        var strategy = pipeline.Strategy;
        var hydrationConfig = executionConfig.Hydration;

        var retailAcceptThreshold = pipeline.Scoring.AutoAcceptThreshold
            ?? hydrationConfig.RetailAutoAcceptThreshold;
        var retailAmbiguousThreshold = pipeline.Scoring.AmbiguousThreshold
            ?? hydrationConfig.RetailAmbiguousThreshold;

        // Get ranked providers for this media type
        var providerConfigs = executionConfig.Providers;
        var rankedProviders = pipeline.Providers.Count > 0
            ? pipeline.Providers.OrderBy(p => p.Rank).Select(p => p.Name).ToList()
            : providerConfigs.Select(p => p.Name).ToList();
        var enabledProviders = ProviderExecutionFilter.EnabledProviderNames(
            rankedProviders,
            _providers,
            providerConfigs)
            .Where(name => mediaType != MediaType.TV
                || string.Equals(name, "tvdb", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (enabledProviders.Count == 0)
        {
            var message = $"No enabled retail provider is configured for media type '{job.MediaType}'.";
            await _jobRepo.ScheduleRetryForOutcomeAsync(
                job.Id,
                IdentityJobState.Queued,
                DateTimeOffset.UtcNow.AddMinutes(30),
                message,
                BackgroundJobOutcomeCategory.UnavailableCapability,
                ct).ConfigureAwait(false);

            _logger.LogWarning(
                "RetailMatchWorker: {Message} Entity {EntityId} will stay queued instead of becoming no-match.",
                message,
                job.EntityId);
            return;
        }

        // Build hints from existing canonicals plus claim fallbacks. Some local
        // processor evidence, especially authors, can be multi-valued and may
        // not have a scalar canonical yet.
        var hints = await BuildFileHintsAsync(job.EntityId, ct);
        var allCandidates = new List<RetailMatchCandidate>();
        var tvdbSeriesIdByCandidate = new Dictionary<Guid, string>();
        RetailMatchCandidate? bestCandidate = null;
        var bestScore = 0.0;
        var providerRank = 0;
        var providerFailures = 0;
        var sequentialBridgeIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var acceptedIdentity = false;
        var acceptedEnrichmentProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var acceptedProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var (configuredLanguage, configuredCountry, _) = GetConfiguredLocale();
        var fileLanguage = hints.GetValueOrDefault(MetadataFieldConstants.Language);
        var providerAttempts = enabledProviders
            .Select(providerName => new ProviderAttempt(
                pipeline.Providers.FirstOrDefault(entry =>
                    string.Equals(entry.Name, providerName, StringComparison.OrdinalIgnoreCase))
                    ?? new PipelineProviderEntry
                    {
                        Name = providerName,
                        Purpose = "identity"
                    },
                new Dictionary<string, string>(hints, StringComparer.OrdinalIgnoreCase),
                AllowAcceptedTransition: true,
                HintSource: "file-hints"))
            .ToList();
        var attemptBudget = Math.Max(1, pipeline.MaxProviderAttempts);

        // Iterate providers per strategy
        for (var attemptIndex = 0;
             attemptIndex < providerAttempts.Count && providerRank < attemptBudget;
             attemptIndex++)
        {
            var attempt = providerAttempts[attemptIndex];
            var pipelineEntry = attempt.Entry;
            var providerName = pipelineEntry.Name;
            var attemptHints = attempt.Hints;
            providerRank++;
            var isFallbackIdentityAttempt = pipelineEntry.UseAsIdentityFallback
                && !acceptedIdentity;
            if (pipelineEntry.RequiresIdentity
                && !acceptedIdentity
                && !isFallbackIdentityAttempt)
            {
                _logger.LogInformation(
                    "Provider {Provider} skipped for entity {EntityId} because its configured enrichment role requires an accepted identity",
                    providerName,
                    job.EntityId);
                continue;
            }

            var provider = _providers.FirstOrDefault(p =>
                string.Equals(p.Name, providerName, StringComparison.OrdinalIgnoreCase));

            if (provider is null)
            {
                continue;
            }

            try
            {
                var authorHint = attemptHints.GetValueOrDefault(MetadataFieldConstants.Author);
                var artistHint = attemptHints.GetValueOrDefault(MetadataFieldConstants.Artist);
                var composerHint = attemptHints.GetValueOrDefault(MetadataFieldConstants.Composer);
                if (mediaType == MediaType.Music)
                {
                    var creatorHint = GetMusicCreatorHint(attemptHints);
                    artistHint = creatorHint;
                    authorHint = StringHelpers.FirstNonBlank(authorHint, creatorHint);
                }

                // Build lookup request
                var lookupRequest = new ProviderLookupRequest
                {
                    EntityId = job.EntityId,
                    EntityType = EntityType.MediaAsset,
                    MediaType = mediaType,
                    Title = attemptHints.GetValueOrDefault(MetadataFieldConstants.Title),
                    Author = authorHint,
                    Year = attemptHints.GetValueOrDefault(MetadataFieldConstants.Year),
                    Narrator = attemptHints.GetValueOrDefault(MetadataFieldConstants.Narrator),
                    ShowName = attemptHints.GetValueOrDefault(MetadataFieldConstants.ShowName)
                        ?? attemptHints.GetValueOrDefault(MetadataFieldConstants.Series),
                    Album = attemptHints.GetValueOrDefault(MetadataFieldConstants.Album),
                    Artist = artistHint,
                    Composer = composerHint,
                    Director = attemptHints.GetValueOrDefault(MetadataFieldConstants.Director),
                    SeasonNumber = attemptHints.GetValueOrDefault(MetadataFieldConstants.SeasonNumber)
                        ?? attemptHints.GetValueOrDefault("season"),
                    EpisodeNumber = attemptHints.GetValueOrDefault(MetadataFieldConstants.EpisodeNumber)
                        ?? attemptHints.GetValueOrDefault("episode"),
                    TrackNumber = attemptHints.GetValueOrDefault(MetadataFieldConstants.TrackNumber),
                    Series = attemptHints.GetValueOrDefault(MetadataFieldConstants.Series),
                    Genre = attemptHints.GetValueOrDefault(MetadataFieldConstants.Genre),
                    Isbn = attemptHints.GetValueOrDefault(BridgeIdKeys.Isbn),
                    Asin = attemptHints.GetValueOrDefault(BridgeIdKeys.Asin),
                    Hints = attemptHints,
                    PriorProviderBridgeIds = strategy == ProviderStrategy.Sequential
                        ? sequentialBridgeIds : null,
                    Language = configuredLanguage,
                    FileLanguage = fileLanguage,
                    Country = configuredCountry,
                };

                var claims = await provider.FetchAsync(lookupRequest, ct);
                if (claims.Count == 0)
                {
                    continue;
                }

                // Extract candidate metadata from claims
                var candidateTitle = claims
                    .FirstOrDefault(c => string.Equals(c.Key, MetadataFieldConstants.Title,
                        StringComparison.OrdinalIgnoreCase))?.Value;
                var candidateAuthor = claims
                    .FirstOrDefault(c => string.Equals(c.Key, MetadataFieldConstants.Author,
                        StringComparison.OrdinalIgnoreCase))?.Value;
                var candidateYear = claims
                    .FirstOrDefault(c => string.Equals(c.Key, MetadataFieldConstants.Year,
                        StringComparison.OrdinalIgnoreCase))?.Value;

                var (structuralBonus, structuralEvidence) = ComputeSingleItemStructuralSignal(
                    mediaType, attemptHints, claims);
                structuralEvidence["provider_attempt"] = providerRank;
                structuralEvidence["provider_role"] = pipelineEntry.Purpose;
                structuralEvidence["hint_source"] = attempt.HintSource;
                var extendedMetadata = BuildCandidateExtendedMetadata(claims);

                // Score candidate
                var retailScore = _retailScoring.ScoreCandidate(
                    attemptHints, candidateTitle, candidateAuthor, candidateYear, mediaType,
                    extendedMetadata: extendedMetadata,
                    structuralBonus: structuralBonus);

                var decision = _candidateScorer.EvaluateDecision(
                    attemptHints,
                    candidateTitle,
                    candidateAuthor,
                    candidateYear,
                    retailScore,
                    retailScore.CompositeScore,
                    retailAcceptThreshold,
                    retailAmbiguousThreshold,
                    "single_item",
                    mediaType: mediaType,
                    extendedMetadata: extendedMetadata);

                // Extract bridge IDs from claims
                var bridgeIdsJson = BuildBridgeIdsJson(claims);

                // Build candidate record
                var candidate = new RetailMatchCandidate
                {
                    JobId = job.Id,
                    ProviderId = provider.ProviderId,
                    ProviderName = provider.Name,
                    ProviderItemId = claims
                        .FirstOrDefault(c => string.Equals(c.Key, "provider_item_id",
                            StringComparison.OrdinalIgnoreCase))?.Value,
                    Rank = providerRank,
                    Title = candidateTitle ?? "(unknown)",
                    Creator = candidateAuthor,
                    Year = candidateYear,
                    ScoreTotal = decision.FinalScore,
                    ScoreBreakdownJson = _candidateScorer.BuildScoreBreakdownJson(
                        retailScore,
                        decision,
                        "single_item",
                        structuralEvidence,
                        structuralBonus),
                    BridgeIdsJson = bridgeIdsJson,
                    Description = claims
                        .FirstOrDefault(c => string.Equals(c.Key, MetadataFieldConstants.Description,
                            StringComparison.OrdinalIgnoreCase))?.Value,
                    ImageUrl = claims
                        .FirstOrDefault(c => string.Equals(c.Key, MetadataFieldConstants.CoverUrl,
                            StringComparison.OrdinalIgnoreCase))?.Value,
                    Outcome = decision.Outcome,
                };

                allCandidates.Add(candidate);
                if (claims.FirstOrDefault(c => string.Equals(c.Key, BridgeIdKeys.TvdbId, StringComparison.OrdinalIgnoreCase))
                    is { Value: { Length: > 0 } tvdbSeriesId })
                {
                    tvdbSeriesIdByCandidate[candidate.Id] = tvdbSeriesId;
                }

                var fallbackIdentityAccepted = isFallbackIdentityAttempt
                    && decision.Outcome == "AutoAccepted";
                if ((IsIdentityPurpose(pipelineEntry.Purpose) || isFallbackIdentityAttempt)
                    && decision.Outcome == "AutoAccepted")
                {
                    acceptedIdentity = true;
                }
                if (decision.Outcome == "AutoAccepted")
                {
                    acceptedProviders.Add(provider.Name);
                }

                // Track best candidate
                if (IsBetterCandidate(candidate, bestCandidate))
                {
                    bestScore = candidate.ScoreTotal;
                    bestCandidate = candidate;
                }

                var shouldPersistProviderClaims = ShouldPersistProviderClaims(
                    decision,
                    pipelineEntry,
                    acceptedIdentity,
                    retailScore,
                    claims);

                // Persist claims if candidate is accepted/ambiguous, or if a configured
                // enrichment provider safely corroborates the accepted identity.
                if (shouldPersistProviderClaims)
                {
                    if (!isFallbackIdentityAttempt
                        && IsEnrichmentPurpose(pipelineEntry.Purpose))
                    {
                        acceptedEnrichmentProviders.Add(provider.Name);
                    }

                    // Phase 3c: pass lineage so parent-scope claims mirror
                    // onto the parent Work (book series → series Work,
                    // audiobook series → series Work, etc.).
                    await ScoringHelper.PersistAndScoreWithLineageAsync(
                        job.EntityId, claims, provider.ProviderId, lineage,
                        _claimRepo, _canonicalRepo, _scoringEngine, _configLoader, _providers, ct,
                        arrayRepo: _arrayRepo, logger: _logger);

                    // Extract bridge IDs for Stage 2
                    var bridgeEntries = claims
                        .Where(c => BridgeIdHelper.IsBridgeId(c.Key) && !string.IsNullOrWhiteSpace(c.Value))
                        .Select(c => new BridgeIdEntry
                        {
                            EntityId = ResolveBridgeIdEntityId(lineage, job.EntityId, c.Key),
                            IdType = c.Key,
                            IdValue = c.Value,
                            ProviderId = provider.ProviderId.ToString(),
                        }).ToList();

                    if (bridgeEntries.Count > 0)
                    {
                        await _bridgeIdRepo.UpsertBatchAsync(bridgeEntries, ct);
                    }

                    // Phase 3b: also write provider bridge IDs to the appropriate
                    // Work's external_identifiers JSON. Track-level IDs land on
                    // the asset's own Work; album/show/series-level IDs land on
                    // the parent. WriteExternalIdentifiersAsync is no-overwrite,
                    // so re-running this for sibling tracks of the same album
                    // is harmless.
                    if (lineage is not null && bridgeEntries.Count > 0)
                    {
                        var bridgeDict = bridgeEntries
                            .GroupBy(b => b.IdType, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g => g.Key, g => g.First().IdValue,
                                StringComparer.OrdinalIgnoreCase);

                        var (forParent, forSelf) = _claimRouter.SplitBridgeIds(lineage, bridgeDict);

                        if (forParent.Count > 0)
                        {
                            await _workRepo.WriteExternalIdentifiersAsync(
                                lineage.TargetForParentScope, forParent, ct);
                        }

                        if (forSelf.Count > 0)
                        {
                            await _workRepo.WriteExternalIdentifiersAsync(
                                lineage.TargetForSelfScope, forSelf, ct);
                        }
                    }

                    // Sequential: accumulate bridge IDs for next provider
                    if (strategy == ProviderStrategy.Sequential)
                    {
                        foreach (var c in claims.Where(c => BridgeIdHelper.IsBridgeId(c.Key)))
                        {
                            sequentialBridgeIds.TryAdd(c.Key, c.Value);
                        }
                    }
                }

                if (attempt.AllowAcceptedTransition
                    && fallbackIdentityAccepted
                    && pipelineEntry.AcceptedTransition is { } transition
                    && TransitionMatches(transition.When, fallbackIdentityAccepted))
                {
                    var targetEntry = pipeline.Providers.FirstOrDefault(entry =>
                        string.Equals(entry.Name, transition.Provider, StringComparison.OrdinalIgnoreCase));
                    var targetEnabled = targetEntry is not null
                        && enabledProviders.Contains(targetEntry.Name, StringComparer.OrdinalIgnoreCase);
                    if (targetEnabled)
                    {
                        var transitionHints = BuildTransitionHints(
                            attemptHints,
                            claims,
                            transition.HintFields);
                        var transitionAttempts = Math.Min(
                            transition.MaxAttempts,
                            Math.Max(0, attemptBudget - providerRank));
                        for (var transitionIndex = 0; transitionIndex < transitionAttempts; transitionIndex++)
                        {
                            providerAttempts.Insert(
                                attemptIndex + 1 + transitionIndex,
                                new ProviderAttempt(
                                    targetEntry!,
                                    new Dictionary<string, string>(transitionHints, StringComparer.OrdinalIgnoreCase),
                                    AllowAcceptedTransition: false,
                                    HintSource: $"accepted-candidate:{providerName}"));
                        }

                        _logger.LogInformation(
                            "Provider {Provider} accepted identity fallback for {EntityId}; scheduled {Count} configured reconciliation attempt(s) with {TargetProvider}",
                            providerName,
                            job.EntityId,
                            transitionAttempts,
                            transition.Provider);
                    }
                }

                // Waterfall: stop after first accepted candidate
                if (strategy == ProviderStrategy.Waterfall && decision.Outcome == "AutoAccepted")
                {
                    break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                providerFailures++;
                _logger.LogWarning(ex,
                    "Provider {Provider} failed for entity {EntityId}",
                    providerName, job.EntityId);
            }
        }

        if (mediaType == MediaType.Music && lineage is not null)
        {
            await PersistAcceptedMusicBrainzAlbumManifestAsync(
                    lineage,
                    sequentialBridgeIds,
                    ct)
                .ConfigureAwait(false);

            foreach (var entry in pipeline.Providers.Where(entry =>
                acceptedProviders.Contains(entry.Name)
                && entry.AcceptedActions.Contains("apple-album-manifest", StringComparer.OrdinalIgnoreCase)))
            {
                await PersistAcceptedAppleAlbumManifestAsync(
                        lineage,
                        hints,
                        sequentialBridgeIds,
                        entry.Name,
                        ct)
                    .ConfigureAwait(false);
            }
        }

        // Persist ALL candidates (winners and losers)
        if (allCandidates.Count > 0)
        {
            await _candidateRepo.InsertBatchAsync(allCandidates, ct);
        }

        bestCandidate = SelectIdentityCandidateWhenConfigured(allCandidates, bestCandidate, pipeline);
        bestScore = bestCandidate?.ScoreTotal ?? 0.0;

        // A file naming several episodes (S01E01E02) links to all of them only when the
        // whole run exists at the TV provider; otherwise it goes to Review.
        string? rangeReviewReason = null;
        if (mediaType == MediaType.TV && bestCandidate is { Outcome: "AutoAccepted" })
        {
            rangeReviewReason = await LinkEpisodeRangeAsync(
                    job, hints, tvdbSeriesIdByCandidate.GetValueOrDefault(bestCandidate.Id), lineage, ct)
                .ConfigureAwait(false);
            if (rangeReviewReason is not null)
            {
                _logger.LogInformation(
                    "TV multi-episode file for entity {EntityId} needs review: {Reason}",
                    job.EntityId, rangeReviewReason);
            }
        }

        // Determine final job state based on best candidate
        if (bestCandidate is not null && bestCandidate.Outcome == "AutoAccepted" && rangeReviewReason is null)
        {
            await _jobRepo.SetSelectedCandidateAsync(job.Id, bestCandidate.Id, ct);
            await _jobRepo.UpdateStateAsync(job.Id, IdentityJobState.RetailMatched, ct: ct);
            await PersistProviderProvenanceAsync(
                job.EntityId,
                bestCandidate.ProviderName,
                acceptedEnrichmentProviders,
                ct).ConfigureAwait(false);
            await _timeline.RecordRetailMatchedAsync(
                job.EntityId, bestCandidate.ProviderName,
                allCandidates.Count, job.IngestionRunId, ct);

            _logger.LogInformation(
                "Retail match found for entity {EntityId}: '{Title}' from {Provider} (score: {Score:F2})",
                job.EntityId, bestCandidate.Title, bestCandidate.ProviderName, bestScore);

            try
            {
                await _postPipeline.EvaluateAndOrganizeAsync(
                    job.EntityId, job.Id, wikidataQid: null, job.IngestionRunId, ct)
                    .ConfigureAwait(false);
                if (_coverArtWorker is not null)
                {
                    await _coverArtWorker.DownloadAndPersistAsync(job.EntityId, wikidataQid: null, ct)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception orgEx) when (orgEx is not OperationCanceledException)
            {
                _logger.LogWarning(orgEx,
                    "Post-retail organization failed for entity {EntityId} — pipeline continues",
                    job.EntityId);
            }
        }
        else if (bestCandidate is not null
            && (bestCandidate.Outcome == "Ambiguous" || rangeReviewReason is not null))
        {
            await _jobRepo.SetSelectedCandidateAsync(job.Id, bestCandidate.Id, ct);
            await _jobRepo.UpdateStateAsync(job.Id, IdentityJobState.RetailMatchedNeedsReview, ct: ct);
            await _outcomeFactory.CreateRetailAmbiguousAsync(
                job.EntityId, job.MediaType, bestScore, job.IngestionRunId, null, ct);
            await _timeline.RecordRetailMatchedAsync(
                job.EntityId, bestCandidate.ProviderName,
                allCandidates.Count, job.IngestionRunId, ct);

            _logger.LogInformation(
                "Retail match ambiguous for entity {EntityId}: '{Title}' (score: {Score:F2})",
                job.EntityId, bestCandidate.Title, bestScore);
        }
        else if (providerFailures > 0)
        {
            var message = $"Retail provider lookup failed for {providerFailures} provider(s); retrying before no-match classification.";
            await _jobRepo.ScheduleRetryForOutcomeAsync(
                job.Id,
                IdentityJobState.Queued,
                DateTimeOffset.UtcNow.AddMinutes(10),
                message,
                BackgroundJobOutcomeCategory.TransientDependencyFailure,
                ct).ConfigureAwait(false);

            _logger.LogWarning(
                "RetailMatchWorker: {Message} Entity {EntityId}; candidates evaluated: {CandidateCount}, best score: {Score:F2}",
                message,
                job.EntityId,
                allCandidates.Count,
                bestScore);
        }
        else
        {
            await _jobRepo.UpdateStateAsync(job.Id, IdentityJobState.RetailNoMatch, ct: ct);
            await EnrichPeopleWithoutMediaMatchAsync(job.EntityId, ct).ConfigureAwait(false);

            var titleHint = hints.GetValueOrDefault(MetadataFieldConstants.Title);

            // If the file has a placeholder title with no bridge IDs, route to a
            // dedicated PlaceholderTitle review trigger instead of the generic
            // RetailMatchFailed bucket — these items will never match retail.
            if (PlaceholderTitleDetector.IsPlaceholder(titleHint)
                && !PlaceholderTitleDetector.HasBridgeId(hints))
            {
                await _outcomeFactory.CreatePlaceholderTitleAsync(
                    job.EntityId, titleHint, job.IngestionRunId, null, ct);
            }
            else
            {
                await _outcomeFactory.CreateRetailFailedAsync(
                    job.EntityId, job.MediaType, job.IngestionRunId, null, ct);
            }

            await _timeline.RecordRetailNoMatchAsync(
                job.EntityId, titleHint ?? "(unknown)", job.IngestionRunId, ct);

            _logger.LogInformation(
                "No retail match for entity {EntityId} — {CandidateCount} candidates evaluated, best score: {Score:F2}",
                job.EntityId, allCandidates.Count, bestScore);
        }
    }

    /// <summary>
    /// Links a multi-episode file to every episode it covers. The file stays attached to its
    /// first episode; one coverage row per episode is written. Returns null when the file is
    /// a single episode or was linked, otherwise the reason it must go to Review.
    /// </summary>
    private async Task<string?> LinkEpisodeRangeAsync(
        IdentityJob job,
        IReadOnlyDictionary<string, string> hints,
        string? tvdbSeriesId,
        WorkLineage? lineage,
        CancellationToken ct)
    {
        var reviewReason = await LinkEpisodeRangeCoreAsync(job, hints, tvdbSeriesId, lineage, ct)
            .ConfigureAwait(false);
        if (reviewReason is not null || !hints.ContainsKey(MetadataFieldConstants.EpisodeEnd))
        {
            await ClearFilenameCoverageAsync(job, ct).ConfigureAwait(false);
        }

        return reviewReason;
    }

    /// <summary>
    /// Removes coverage left by an earlier run once the file no longer qualifies, so stale
    /// extra episodes do not linger. Coverage set by a person (manual) is never touched.
    /// </summary>
    private async Task ClearFilenameCoverageAsync(IdentityJob job, CancellationToken ct)
    {
        if (_coverageRepo is null
            || !string.Equals(job.EntityType, "MediaAsset", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var existing = await _coverageRepo.ListByAssetAsync(job.EntityId, ct).ConfigureAwait(false);
            if (existing.Count > 0
                && existing.All(row => row.Source == MediaAssetCoverage.SourceFilename))
            {
                await _coverageRepo.ReplaceForAssetAsync(job.EntityId, [], ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "TV: could not clear earlier episode coverage for file {EntityId}", job.EntityId);
        }
    }

    private async Task<string?> LinkEpisodeRangeCoreAsync(
        IdentityJob job,
        IReadOnlyDictionary<string, string> hints,
        string? tvdbSeriesId,
        WorkLineage? lineage,
        CancellationToken ct)
    {
        if (!hints.TryGetValue(MetadataFieldConstants.EpisodeEnd, out var episodeEnd)
            || string.IsNullOrWhiteSpace(episodeEnd))
        {
            return null;
        }

        var firstText = hints.GetValueOrDefault(MetadataFieldConstants.EpisodeNumber)
            ?? hints.GetValueOrDefault("episode");
        var seasonText = hints.GetValueOrDefault(MetadataFieldConstants.SeasonNumber)
            ?? hints.GetValueOrDefault("season");
        int? first = int.TryParse(firstText, out var firstValue) ? firstValue : null;

        if (tvdbSeriesId is null && lineage is not null)
        {
            // The winning candidate carried no tvdb_id; fall back to one already recorded for the show.
            try
            {
                var known = await _bridgeIdRepo.FindAsync(lineage.RootParentWorkId, BridgeIdKeys.TvdbId, ct)
                    .ConfigureAwait(false);
                tvdbSeriesId = known is { IdValue: { Length: > 0 } recorded } ? recorded : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "TV: could not look up a recorded TVDB series id (entity {EntityId})", job.EntityId);
            }
        }

        if (tvdbSeriesId is null)
        {
            _logger.LogInformation(
                "TV: multi-episode file {EntityId} has no TheTVDB series id, so its range cannot be verified (only TheTVDB is supported)",
                job.EntityId);
        }

        IReadOnlySet<int>? providerEpisodes = null;
        if (first.HasValue
            && int.TryParse(seasonText, out var season)
            && tvdbSeriesId is { Length: > 0 } seriesId
            && _rangeCatalogue is not null)
        {
            try
            {
                providerEpisodes = await _rangeCatalogue
                    .GetSeasonEpisodeNumbersAsync(seriesId, season, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "TV: could not read TheTVDB's season list (English, aired order) to verify a multi-episode file (entity {EntityId})",
                    job.EntityId);
            }

            if (providerEpisodes is null)
            {
                _logger.LogInformation(
                    "TV: TheTVDB has no aired-order episodes for series {SeriesId} season {Season}; file {EntityId} goes to review (DVD or absolute order is not checked)",
                    seriesId, season, job.EntityId);
            }
        }

        var decision = TvEpisodeRangeCoverage.Evaluate(first, episodeEnd, providerEpisodes);
        if (decision.Kind == EpisodeRangeKind.NotARange)
        {
            return null;
        }

        if (decision.Kind == EpisodeRangeKind.NeedsReview)
        {
            return decision.Reason;
        }

        if (_coverageRepo is null
            || lineage is null
            || lineage.ParentWorkId is not { } seasonWorkId
            || !string.Equals(job.EntityType, "MediaAsset", StringComparison.OrdinalIgnoreCase))
        {
            return "The file cannot be linked to its other episodes yet.";
        }

        try
        {
            var rows = new List<MediaAssetCoverage>();
            for (var index = 0; index < decision.Episodes.Count; index++)
            {
                var episode = decision.Episodes[index];
                var workId = await _workRepo.GetOrCreateChildAsync(MediaType.TV, seasonWorkId, episode, episode, ct)
                    .ConfigureAwait(false);
                if (index == 0 && workId != lineage.WorkId)
                {
                    return "The file is attached to a different episode than its filename names.";
                }

                rows.Add(new MediaAssetCoverage(
                    job.EntityId, workId, index + 1, null, null, MediaAssetCoverage.SourceFilename));
            }

            await _coverageRepo.ReplaceForAssetAsync(job.EntityId, rows, ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "TV: could not record the episodes covered by file {EntityId}", job.EntityId);
            return "The episodes covered by this file could not be recorded.";
        }
    }

    private static Guid ResolveBridgeIdEntityId(WorkLineage? lineage, Guid assetId, string key)
    {
        if (lineage is null)
        {
            return assetId;
        }

        return ClaimScopeCatalog.IsParentScoped(key, lineage.MediaType)
            ? lineage.TargetForParentScope
            : lineage.TargetForSelfScope;
    }

    private async Task PersistProviderProvenanceAsync(
        Guid entityId,
        string identityProvider,
        IReadOnlyCollection<string> enrichmentProviders,
        CancellationToken ct)
    {
        await _canonicalRepo.UpsertBatchAsync(
        [
            new CanonicalValue
            {
                EntityId = entityId,
                Key = MetadataFieldConstants.IdentityProvider,
                Value = identityProvider,
                LastScoredAt = DateTimeOffset.UtcNow,
            },
        ], ct).ConfigureAwait(false);

        if (_arrayRepo is null)
        {
            return;
        }

        var entries = enrichmentProviders
            .OrderBy(provider => provider, StringComparer.OrdinalIgnoreCase)
            .Select((provider, ordinal) => new CanonicalArrayEntry
            {
                Ordinal = ordinal,
                Value = provider,
            })
            .ToList();
        await _arrayRepo.SetValuesAsync(
            entityId,
            MetadataFieldConstants.EnrichmentProviders,
            entries,
            ct).ConfigureAwait(false);
    }

}
