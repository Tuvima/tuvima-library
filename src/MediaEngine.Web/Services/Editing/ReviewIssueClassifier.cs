using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Web.Services.Editing;

public enum ReviewIssueBucket
{
    All,
    QuickFix,
    ManualReview,
    HighPriority,
}

public sealed record ReviewIssuePresentation(
    ReviewIssueBucket Bucket,
    string Label,
    string Explanation,
    bool HighPriority,
    string? Category = null);

public static class ReviewIssueClassifier
{
    /// <summary>The label, bucket and explanation for a review trigger, plus its shared <see cref="ReviewCategory"/> key.</summary>
    public static ReviewIssuePresentation Classify(string? trigger) =>
        Describe(trigger) with { Category = ReviewCategory.For(trigger) };

    private static ReviewIssuePresentation Describe(string? trigger)
    {
        var rootCause = ReviewRootCauseExtensions.FromTrigger(trigger);
        return trigger switch
        {
            "MissingQid" or "WikidataBridgeFailed" => new(ReviewIssueBucket.QuickFix, "No canonical identity found", "The retail match is retained, but no Wikidata identity could be confirmed.", false),
            "WritebackFailed" => new(ReviewIssueBucket.HighPriority, "File write-back failed", "Metadata could not be written to the physical file after retrying.", true),
            "MediaInspectionFailed" => new(ReviewIssueBucket.ManualReview, "File couldn't be read", "This file couldn't be read. It may be damaged or incomplete.", false),
            "StagedUnidentifiable" => new(ReviewIssueBucket.HighPriority, "Item could not be identified", "The file is blocked in staging because there is not enough reliable identity evidence.", true),
            "LanguageMismatch" => new(ReviewIssueBucket.ManualReview, "Language needs confirmation", "The file language conflicts with the configured library language.", false),
            "AmbiguousMediaType" or "RootWatchFolder" => new(ReviewIssueBucket.ManualReview, "Media type is uncertain", "The file could belong to more than one media lane and needs a person to choose.", false),
            "AmbiguousSeriesMembership" => new(ReviewIssueBucket.ManualReview, "More than one Series claims this", "Wikidata lists this in the main sequence of two Series and nothing says which one it belongs to. It stays off both until you choose.", false),
            "MovieMatchedAsTv" => new(ReviewIssueBucket.ManualReview, "Found as a TV title", "TMDB lists this film as a TV series. Nothing changes until you choose.", false),
            "FromMerge" => new(ReviewIssueBucket.ManualReview, "From merge", "This file came from a folder merge and could not be named yet. It waits in the From merge folder until a person confirms what it is.", false),
            "NameCollision" => new(ReviewIssueBucket.ManualReview, "Two different files want the same name", "Neither file was overwritten. The second one kept a numbered name so you can decide which is which.", false),
            "RetailMatchAmbiguous" => new(ReviewIssueBucket.ManualReview, "Match needs confirmation", "More than one provider record may match the detected item.", false),
            "MultipleQidMatches" => new(ReviewIssueBucket.ManualReview, "Canonical identity needs confirmation", "More than one Wikidata identity may match the confirmed retail record.", false),
            "RetailMatchFailed" => new(ReviewIssueBucket.ManualReview, "No reliable match", "No configured provider returned a sufficiently reliable match.", false),
            "PlaceholderTitle" => new(ReviewIssueBucket.ManualReview, "Missing identifying information", "A usable title or identifier is required before matching can continue.", false),
            "BatchEditFailed" => new(ReviewIssueBucket.ManualReview, "A batch edit didn't apply", "A batch edit changed the other items but couldn't change this one. Nothing was changed on it.", false),
            "MetadataConflict" => new(ReviewIssueBucket.ManualReview, "Metadata sources disagree", "Two or more sources supplied similarly credible but conflicting values.", false),
            _ when rootCause == ReviewRootCause.EnrichmentIncomplete => new(ReviewIssueBucket.QuickFix, "Enrichment incomplete", "The item is identified but one enrichment step needs confirmation.", false),
            _ => new(ReviewIssueBucket.ManualReview, "Manual review needed", "The ingestion pipeline could not safely make this decision automatically.", false),
        };
    }
}
