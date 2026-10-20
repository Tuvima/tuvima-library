using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Intelligence.Contracts;
using MediaEngine.Intelligence.Models;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Helpers;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Post-pipeline evaluation: re-scores the entity after hydration, auto-resolves
/// stale review items when confidence improves, checks for metadata conflicts,
/// and gates organization (promotion from staging to the organised library).
///
/// Extracted from <c>HydrationPipelineService</c> post-pipeline section.
/// </summary>
public sealed class PostPipelineService
{
    private readonly IMetadataClaimRepository _claimRepo;
    private readonly ICanonicalValueRepository _canonicalRepo;
    private readonly IScoringEngine _scoringEngine;
    private readonly IConfigurationLoader _configLoader;
    private readonly IEnumerable<IExternalMetadataProvider> _providers;
    private readonly IReviewQueueRepository _reviewRepo;
    private readonly IAutoOrganizeService _organizer;
    private readonly ICanonicalValueArrayRepository? _arrayRepo;
    private readonly ISearchIndexRepository? _searchIndex;
    private readonly ISystemActivityRepository? _activityRepo;
    private readonly ILogger<PostPipelineService> _logger;
    private readonly StageOutcomeFactory? _outcomeFactory;

    private static readonly string[] IdentitySupersededReviewTriggers =
    [
        ReviewTrigger.RetailMatchFailed,
        ReviewTrigger.RetailMatchAmbiguous,
        ReviewTrigger.WikidataBridgeFailed,
        ReviewTrigger.MissingQid,
        ReviewTrigger.MultipleQidMatches,
        ReviewTrigger.LowConfidence,
    ];

    private static readonly string[] RetainedRetailSupersededReviewTriggers =
    [
        ReviewTrigger.RetailMatchFailed,
        ReviewTrigger.RetailMatchAmbiguous,
        ReviewTrigger.WikidataBridgeFailed,
        ReviewTrigger.MissingQid,
        ReviewTrigger.LowConfidence,
    ];

    public PostPipelineService(
        IMetadataClaimRepository claimRepo,
        ICanonicalValueRepository canonicalRepo,
        IScoringEngine scoringEngine,
        IConfigurationLoader configLoader,
        IEnumerable<IExternalMetadataProvider> providers,
        IReviewQueueRepository reviewRepo,
        IAutoOrganizeService organizer,
        ILogger<PostPipelineService> logger,
        ICanonicalValueArrayRepository? arrayRepo = null,
        ISearchIndexRepository? searchIndex = null,
        ISystemActivityRepository? activityRepo = null,
        StageOutcomeFactory? outcomeFactory = null)
    {
        _claimRepo = claimRepo;
        _canonicalRepo = canonicalRepo;
        _scoringEngine = scoringEngine;
        _configLoader = configLoader;
        _providers = providers;
        _reviewRepo = reviewRepo;
        _organizer = organizer;
        _logger = logger;
        _arrayRepo = arrayRepo;
        _searchIndex = searchIndex;
        _activityRepo = activityRepo;
        _outcomeFactory = outcomeFactory;
    }

    /// <summary>
    /// Re-scores the entity, evaluates confidence, auto-resolves stale reviews,
    /// checks for metadata conflicts, and gates organization.
    /// </summary>
    public async Task<bool> EvaluateAndOrganizeAsync(
        Guid entityId,
        Guid jobId,
        string? wikidataQid,
        CancellationToken ct,
        bool retainedRetailIdentity = false)
    {
        // 1. Reload all claims and re-score
        var allClaims = await _claimRepo.GetByEntityAsync(entityId, ct);
        var providerConfigs = _configLoader.LoadAllProviders();
        var scoring = _configLoader.LoadScoring();
        var hydration = _configLoader.LoadHydration();
        var detectedMediaType = await ScoringHelper.ResolveDetectedMediaTypeAsync(
            entityId,
            allClaims,
            _canonicalRepo,
            ct).ConfigureAwait(false);

        var (weights, fieldWeights) = ScoringHelper.BuildWeightMaps(providerConfigs, _providers);

        var scoringContext = new ScoringContext
        {
            EntityId = entityId,
            Claims = allClaims,
            ProviderWeights = weights,
            ProviderFieldWeights = fieldWeights,
            DetectedMediaType = detectedMediaType,
            Configuration = new Intelligence.Models.ScoringConfiguration
            {
                AutoLinkThreshold = scoring.AutoLinkThreshold,
                ConflictThreshold = scoring.ConflictThreshold,
                ConflictEpsilon = scoring.ConflictEpsilon,
                StaleClaimDecayDays = scoring.StaleClaimDecayDays,
                StaleClaimDecayFactor = scoring.StaleClaimDecayFactor,
            },
        };

        var scored = await _scoringEngine.ScoreEntityAsync(scoringContext, ct);

        // QID-presence boost: a confirmed Wikidata identity is strong evidence
        // of correct identification. Apply a modest boost (+0.10) to overall
        // confidence so enriched-but-sparse items (e.g. video stubs with only
        // title + year) clear the post-hydration organize threshold.
        double effectiveConfidence = scored.OverallConfidence;
        if (wikidataQid is not null && effectiveConfidence < 1.0)
        {
            effectiveConfidence = Math.Min(1.0, effectiveConfidence + 0.10);
            _logger.LogDebug(
                "QID-presence boost for entity {EntityId}: {Raw:F2} → {Boosted:F2} (QID {Qid})",
                entityId, scored.OverallConfidence, effectiveConfidence, wikidataQid);
        }

        _logger.LogInformation(
            "Post-pipeline confidence for entity {EntityId}: {Confidence:F2}",
            entityId, effectiveConfidence);

        if (!string.IsNullOrWhiteSpace(wikidataQid))
        {
            await ResolveIdentitySupersededReviewsAsync(entityId, wikidataQid, ct)
                .ConfigureAwait(false);
        }
        else if (retainedRetailIdentity)
        {
            await ResolveRetainedRetailSupersededReviewsAsync(entityId, ct)
                .ConfigureAwait(false);
        }

        // 2. Low confidence → review
        if (effectiveConfidence < hydration.AutoReviewConfidenceThreshold)
        {
            _logger.LogInformation(
                "Entity {EntityId} below confidence threshold ({Confidence:F2} < {Threshold:F2})",
                entityId, effectiveConfidence, hydration.AutoReviewConfidenceThreshold);

            var promoted = _outcomeFactory is not null
                ? await _outcomeFactory.PromoteProvisionalAsync(entityId, ct)
                    .ConfigureAwait(false)
                : await _reviewRepo.PromotePendingReadyByEntityAsync(entityId, ct)
                    .ConfigureAwait(false);
            var hasReadyPendingReview = promoted.Count > 0;
            if (!hasReadyPendingReview)
            {
                var pendingReviews = await _reviewRepo.GetByEntityAsync(entityId, ct);
                hasReadyPendingReview = pendingReviews.Any(review =>
                    string.Equals(review.Status, ReviewStatus.Pending, StringComparison.OrdinalIgnoreCase)
                    && review.ReviewReadyAt is not null);
            }

            if (!hasReadyPendingReview)
            {
                if (_outcomeFactory is not null)
                {
                    await _outcomeFactory.CreateLowConfidenceAsync(
                        entityId,
                        effectiveConfidence,
                        ct: ct).ConfigureAwait(false);
                }
                else
                {
                    await _reviewRepo.InsertAsync(new ReviewQueueEntry
                    {
                        Id = Guid.NewGuid(),
                        EntityId = entityId,
                        EntityType = "MediaAsset",
                        Trigger = ReviewTrigger.LowConfidence,
                        ConfidenceScore = effectiveConfidence,
                        Detail = $"Post-pipeline confidence {effectiveConfidence:P0} below auto-review threshold",
                        ReviewReadyAt = DateTimeOffset.UtcNow,
                        AutomationCompletedAt = DateTimeOffset.UtcNow,
                    }, ct);
                }
            }

            return false;
        }

        // 3. Auto-resolve stale review items
        await TryAutoResolveStaleReviewItemsAsync(entityId, effectiveConfidence, ct);
        await TryAutoResolveMetadataConflictsAsync(entityId, effectiveConfidence, ct);

        // 4. Organization gate
        // A resolved retail identity is enough to promote into the library.
        // When Stage 2 cannot resolve a Wikidata QID, the item should still
        // retain its retail match and organize in-place for periodic re-checks.
        double organizeThreshold = hydration.PostHydrationOrganizeThreshold;

        if (effectiveConfidence >= organizeThreshold)
        {
            _logger.LogInformation(
                "Entity {EntityId} meets organization threshold ({Confidence:F2} >= {Threshold:F2}), promoting",
                entityId, scored.OverallConfidence, organizeThreshold);

            await _organizer.TryAutoOrganizeAsync(entityId, ct);
            return true;
        }

        return false;
    }

    private async Task ResolveRetainedRetailSupersededReviewsAsync(
        Guid entityId,
        CancellationToken ct)
    {
        var resolved = await _reviewRepo.ResolvePendingByEntityAndTriggersAsync(
            entityId,
            RetainedRetailSupersededReviewTriggers,
            "system:retained-retail-identity",
            ct).ConfigureAwait(false);

        if (resolved <= 0)
        {
            return;
        }

        _logger.LogInformation(
            "Resolved {Count} superseded review item(s) for entity {EntityId} after retained retail identity completed without Wikidata",
            resolved, entityId);

        if (_activityRepo is null)
        {
            return;
        }

        try
        {
            await _activityRepo.LogAsync(new SystemActivityEntry
            {
                ActionType = SystemActionType.ReviewItemResolved,
                EntityId = entityId,
                EntityType = "MediaAsset",
                Detail = "Review cleared by identity pipeline: retail identity was retained without a Wikidata link.",
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex,
                "Activity log failed for retained-retail review cleanup on entity {EntityId}",
                entityId);
        }
    }

    private async Task ResolveIdentitySupersededReviewsAsync(
        Guid entityId,
        string wikidataQid,
        CancellationToken ct)
    {
        var resolved = await _reviewRepo.ResolvePendingByEntityAndTriggersAsync(
            entityId,
            IdentitySupersededReviewTriggers,
            "system:identity-superseded",
            ct).ConfigureAwait(false);

        if (resolved <= 0)
        {
            return;
        }

        _logger.LogInformation(
            "Resolved {Count} superseded review item(s) for entity {EntityId} after accepted identity {Qid}",
            resolved, entityId, wikidataQid);

        if (_activityRepo is null)
        {
            return;
        }

        try
        {
            await _activityRepo.LogAsync(new SystemActivityEntry
            {
                ActionType = SystemActionType.ReviewItemResolved,
                EntityId = entityId,
                EntityType = "MediaAsset",
                Detail = $"Review cleared by identity pipeline: Wikidata ID {wikidataQid} was accepted.",
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex,
                "Activity log failed for superseded review cleanup on entity {EntityId}",
                entityId);
        }
    }

    /// <summary>
    /// Resolves review items that are no longer relevant because confidence has improved.
    /// Handles current match failure, bridge failure, and LowConfidence triggers.
    /// </summary>
    private async Task TryAutoResolveStaleReviewItemsAsync(
        Guid entityId, double confidence, CancellationToken ct)
    {
        var pendingReviews = await _reviewRepo.GetByEntityAsync(entityId, ct);

        var autoResolveTriggers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            nameof(ReviewTrigger.AmbiguousMediaType),
            nameof(ReviewTrigger.RootWatchFolder),
            nameof(ReviewTrigger.RetailMatchFailed),
            nameof(ReviewTrigger.RetailMatchAmbiguous),
            nameof(ReviewTrigger.LowConfidence),
            nameof(ReviewTrigger.WikidataBridgeFailed),
            nameof(ReviewTrigger.MissingQid),
            nameof(ReviewTrigger.WritebackFailed),
        };

        foreach (var review in pendingReviews)
        {
            if (review.Status != "Pending")
            {
                continue;
            }

            if (review.ReviewReadyAt is not null)
            {
                continue;
            }

            if (!autoResolveTriggers.Contains(review.Trigger))
            {
                continue;
            }

            _logger.LogInformation(
                "Auto-resolving {Trigger} review for entity {EntityId} (confidence: {Confidence:F2})",
                review.Trigger, entityId, confidence);

            await _reviewRepo.UpdateStatusAsync(
                review.Id,
                ReviewStatus.Resolved,
                "system:post-pipeline",
                ct);
        }
    }

    /// <summary>
    /// Resolves MetadataConflict reviews when no conflicts remain in the scored canonicals.
    /// </summary>
    private async Task TryAutoResolveMetadataConflictsAsync(
        Guid entityId, double confidence, CancellationToken ct)
    {
        var pendingReviews = await _reviewRepo.GetByEntityAsync(entityId, ct);

        foreach (var review in pendingReviews)
        {
            if (review.Status != "Pending")
            {
                continue;
            }

            if (review.ReviewReadyAt is not null)
            {
                continue;
            }

            if (!string.Equals(review.Trigger, nameof(ReviewTrigger.MetadataConflict),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Re-check: if confidence is high enough, the conflict is likely resolved
            if (confidence >= 0.80)
            {
                _logger.LogInformation(
                    "Auto-resolving MetadataConflict review for entity {EntityId} (confidence: {Confidence:F2})",
                    entityId, confidence);

                await _reviewRepo.UpdateStatusAsync(
                    review.Id,
                    ReviewStatus.Resolved,
                    "system:post-pipeline",
                    ct);
            }
        }
    }
}
