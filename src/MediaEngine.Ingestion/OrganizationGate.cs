using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Ingestion.Services;
using MediaEngine.Intelligence.Models;

namespace MediaEngine.Ingestion;

/// <summary>
/// Centralized organization gate. Every path that promotes a file from
/// staging to the library MUST call <see cref="Evaluate"/> first.
/// </summary>
public sealed class OrganizationGate : IOrganizationGate
{
    private const double UnidentifiableThreshold = 0.40;

    private readonly double _highConfidenceThreshold;

    public OrganizationGate(ScoringConfiguration scoringConfig)
    {
        _highConfidenceThreshold = scoringConfig.AutoLinkThreshold;
    }

    public GateResult Evaluate(
        double overallConfidence,
        IReadOnlyDictionary<string, string> canonicalValues,
        bool hasUserLock,
        bool mediaTypeNeedsReview,
        string? filingCategory)
    {
        // Gate 1: Media type still under review.
        if (mediaTypeNeedsReview)
        {
            return new GateResult(false, "Media type disambiguation in progress",
                "low-confidence", ReviewTrigger.AmbiguousMediaType,
                "Media type could not be determined with sufficient confidence.");
        }

        bool highConfidence = overallConfidence >= _highConfidenceThreshold;
        bool passesConfidence = highConfidence || hasUserLock;

        // Gate 2: Placeholder title with no bridge ID.
        if (passesConfidence && !hasUserLock)
        {
            string? title = canonicalValues.GetValueOrDefault("title");
            if (MetadataGuards.IsPlaceholderTitle(title)
                && !MetadataGuards.HasBridgeId(canonicalValues))
            {
                return new GateResult(false,
                    $"Placeholder title \"{title ?? "(blank)"}\" with no bridge IDs",
                    "low-confidence", ReviewTrigger.PlaceholderTitle,
                    $"Title \"{title ?? "(blank)"}\" appears to be a placeholder with no ISBN, ASIN, or QID");
            }
        }

        // Gate 3: "Other" category block.
        if (passesConfidence
            && string.Equals(filingCategory, LibraryOrganizationPath.UnknownCategory, StringComparison.OrdinalIgnoreCase))
        {
            return new GateResult(false,
                "Resolved category is 'Other'",
                "other", ReviewTrigger.LowConfidence,
                $"File would be organized into 'Other' category. Manual review required.");
        }

        // Gate 4: Confidence-based staging tier.
        if (!passesConfidence)
        {
            if (overallConfidence < UnidentifiableThreshold)
            {
                return new GateResult(false,
                    $"Confidence {overallConfidence:P0} below unidentifiable threshold",
                    "unidentifiable", ReviewTrigger.StagedUnidentifiable,
                    $"Overall confidence {overallConfidence:P0} — file is unidentifiable. Staged for manual review.");
            }

            return new GateResult(false,
                $"Confidence {overallConfidence:P0} below organization threshold ({_highConfidenceThreshold:P0})",
                "low-confidence", ReviewTrigger.LowConfidence,
                $"Overall confidence {overallConfidence:P0} below organization threshold ({_highConfidenceThreshold:P0}). Staged for review.");
        }

        // All gates passed.
        return new GateResult(true, null, "pending");
    }
}
