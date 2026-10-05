using System.Text.Json;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;

namespace MediaEngine.Providers.Services;

public sealed record RetailDecision(
    string Outcome,
    double FinalScore,
    string ThresholdPath,
    IReadOnlyList<string> RejectionReasons,
    double TextEvidence,
    bool CreatorPresentOnBothSides,
    bool CreatorContradiction,
    bool AutoAcceptBlocked,
    string MatchContext);

public sealed class RetailCandidateScorer
{
    public RetailDecision EvaluateDecision(
        IReadOnlyDictionary<string, string> fileHints,
        string? candidateTitle,
        string? candidateCreator,
        string? candidateYear,
        FieldMatchScores retailScore,
        double finalScore,
        double retailAcceptThreshold,
        double retailAmbiguousThreshold,
        string matchContext,
        string? fileCreatorOverride = null,
        IReadOnlyList<string>? autoAcceptCapReasons = null,
        MediaType mediaType = MediaType.Unknown,
        CandidateExtendedMetadata? extendedMetadata = null)
    {
        const double weakCreatorThreshold = 0.55;
        const double weakTextThreshold = 0.60;
        const double weakTitleThreshold = 0.50;

        _ = candidateTitle;

        var fileCreator = fileCreatorOverride ?? RetailHints.GetCreatorHint(fileHints);
        var fileYear = RetailHints.GetYearHint(fileHints);
        candidateYear = RetailHints.NormalizeYear(candidateYear);

        var creatorPresentOnBothSides = !string.IsNullOrWhiteSpace(fileCreator)
            && !string.IsNullOrWhiteSpace(candidateCreator);
        var yearPresentOnBothSides = !string.IsNullOrWhiteSpace(fileYear)
            && !string.IsNullOrWhiteSpace(candidateYear);
        var creatorDirectMatch = creatorPresentOnBothSides
            && RetailTextSimilarity.AreEquivalentNames(fileCreator, candidateCreator);
        var creatorContradiction = mediaType is not (MediaType.Movies or MediaType.TV or MediaType.Comics) && creatorPresentOnBothSides
            && !creatorDirectMatch
            && retailScore.AuthorScore < weakCreatorThreshold;
        var textEvidence = ComputeTextEvidence(
            retailScore,
            creatorPresentOnBothSides,
            yearPresentOnBothSides);
        var weakTextEvidence = retailScore.TitleScore < weakTitleThreshold
            || textEvidence < weakTextThreshold;

        var scoreWithoutCover = Math.Max(0.0, finalScore - retailScore.CoverArtScore);
        var coverWouldRescueWeakText = retailScore.CoverArtScore > 0.0
            && weakTextEvidence
            && finalScore >= retailAmbiguousThreshold
            && scoreWithoutCover < retailAmbiguousThreshold;

        var rejectionReasons = new List<string>();

        string outcome;
        string thresholdPath;
        if (finalScore >= retailAcceptThreshold)
        {
            outcome = "AutoAccepted";
            thresholdPath = "accept_threshold";
        }
        else if (finalScore >= retailAmbiguousThreshold)
        {
            outcome = "Ambiguous";
            thresholdPath = "ambiguous_threshold";
        }
        else
        {
            outcome = "Rejected";
            thresholdPath = "below_ambiguous_threshold";
        }

        var autoAcceptBlocked = false;
        foreach (var block in retailScore.AutoAcceptBlockReasons)
        {
            rejectionReasons.Add(block);
            if (outcome == "AutoAccepted") { outcome = "Ambiguous"; thresholdPath = "matrix_evidence_review"; autoAcceptBlocked = true; }
        }


        if (creatorContradiction)
        {
            rejectionReasons.Add("creator_similarity_weak");
            if (outcome == "AutoAccepted")
            {
                outcome = "Ambiguous";
                thresholdPath = "accept_capped_to_review";
                autoAcceptBlocked = true;
            }
        }

        if (autoAcceptCapReasons is { Count: > 0 })
        {
            foreach (var reason in autoAcceptCapReasons.Where(r => !string.IsNullOrWhiteSpace(r)))
            {
                if (!rejectionReasons.Contains(reason, StringComparer.Ordinal))
                {
                    rejectionReasons.Add(reason);
                }
            }

            if (outcome == "AutoAccepted")
            {
                outcome = "Ambiguous";
                thresholdPath = "accept_capped_to_review";
                autoAcceptBlocked = true;
            }
        }

        var qualityRejectionReasons = RetailCandidateQualityGuard.GetRejectionReasons(
            mediaType,
            fileHints,
            candidateTitle,
            extendedMetadata);
        if (qualityRejectionReasons.Count > 0)
        {
            foreach (var reason in qualityRejectionReasons)
            {
                if (!rejectionReasons.Contains(reason, StringComparer.Ordinal))
                {
                    rejectionReasons.Add(reason);
                }
            }

            outcome = "Rejected";
            thresholdPath = "candidate_quality_rejected";
            autoAcceptBlocked = true;
        }

        if (coverWouldRescueWeakText)
        {
            if (!rejectionReasons.Contains("cover_cannot_rescue_weak_text", StringComparer.Ordinal))
            {
                rejectionReasons.Add("cover_cannot_rescue_weak_text");
            }

            outcome = "Rejected";
            thresholdPath = "cover_rescue_rejected";
            autoAcceptBlocked = true;
        }

        return new RetailDecision(
            Outcome: outcome,
            FinalScore: Math.Round(finalScore, 4),
            ThresholdPath: thresholdPath,
            RejectionReasons: rejectionReasons,
            TextEvidence: textEvidence,
            CreatorPresentOnBothSides: creatorPresentOnBothSides,
            CreatorContradiction: creatorContradiction,
            AutoAcceptBlocked: autoAcceptBlocked,
            MatchContext: matchContext);
    }

    public string BuildScoreBreakdownJson(
        FieldMatchScores retailScore,
        RetailDecision decision,
        string matchContext,
        IReadOnlyDictionary<string, object?>? extraEvidence = null,
        double structuralBonus = 0.0)
    {
        var breakdown = new Dictionary<string, object?>
        {
            ["title"] = retailScore.TitleScore,
            ["field_scores"] = retailScore.FieldScores,
            ["author"] = retailScore.AuthorScore,
            ["year"] = retailScore.YearScore,
            ["format"] = retailScore.FormatScore,
            ["cross_field"] = retailScore.CrossFieldBoost,
            ["cover"] = retailScore.CoverArtScore,
            ["final_score"] = decision.FinalScore,
            ["text_evidence"] = decision.TextEvidence,
            ["threshold_path"] = decision.ThresholdPath,
            ["rejection_reasons"] = decision.RejectionReasons,
            ["match_context"] = matchContext,
            ["creator_present_on_both_sides"] = decision.CreatorPresentOnBothSides,
            ["creator_contradiction"] = decision.CreatorContradiction,
            ["auto_accept_blocked"] = decision.AutoAcceptBlocked,
        };

        if (structuralBonus != 0.0)
        {
            breakdown["structural_bonus"] = structuralBonus;
        }

        if (extraEvidence is not null)
        {
            foreach (var pair in extraEvidence)
            {
                breakdown[pair.Key] = pair.Value;
            }
        }

        return JsonSerializer.Serialize(breakdown);
    }

    public static int GetOutcomeRank(string outcome) => outcome switch
    {
        "AutoAccepted" => 2,
        "Ambiguous" => 1,
        _ => 0,
    };

    public static bool IsBetterCandidate(string candidateOutcome, double candidateScore, int candidateRank, string? currentOutcome, double currentScore, int currentRank)
    {
        if (currentOutcome is null)
        {
            return true;
        }

        var candidateOutcomeRank = GetOutcomeRank(candidateOutcome);
        var currentOutcomeRank = GetOutcomeRank(currentOutcome);
        if (candidateOutcomeRank != currentOutcomeRank)
        {
            return candidateOutcomeRank > currentOutcomeRank;
        }

        if (Math.Abs(candidateScore - currentScore) > 0.0001)
        {
            return candidateScore > currentScore;
        }

        return candidateRank < currentRank;
    }

    private static double ComputeTextEvidence(
        FieldMatchScores score,
        bool creatorPresentOnBothSides,
        bool yearPresentOnBothSides)
    {
        const double titleWeight = 0.60;
        const double creatorWeight = 0.25;
        const double yearWeight = 0.15;

        var weightedScore = score.TitleScore * titleWeight;
        var totalWeight = titleWeight;

        if (creatorPresentOnBothSides)
        {
            weightedScore += score.AuthorScore * creatorWeight;
            totalWeight += creatorWeight;
        }

        if (yearPresentOnBothSides)
        {
            weightedScore += score.YearScore * yearWeight;
            totalWeight += yearWeight;
        }

        return totalWeight <= 0
            ? 0.0
            : Math.Round(weightedScore / totalWeight, 4);
    }

}
