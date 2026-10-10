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
    bool HighPriority);

public static class ReviewIssueClassifier
{
    public const string CategoryAll = "all";
    public const string CategoryQuickFix = "quick";
    public const string CategoryManualReview = "manual";
    public const string CategoryNoProviderMatch = "no-provider-match";
    public const string CategoryIncompleteMetadata = "incomplete-metadata";
    public const string CategoryDuplicates = "duplicates";

    public static ReviewIssuePresentation Classify(string? trigger)
    {
        var rootCause = ReviewRootCauseExtensions.FromTrigger(trigger);
        return trigger switch
        {
            "ArtworkUnconfirmed" => new(ReviewIssueBucket.QuickFix, "Confirm artwork", "The artwork came from a less precise search and needs confirmation.", false),
            "MissingQid" or "WikidataBridgeFailed" => new(ReviewIssueBucket.QuickFix, "No canonical identity found", "The retail match is retained, but no Wikidata identity could be confirmed.", false),
            "WritebackFailed" => new(ReviewIssueBucket.HighPriority, "File write-back failed", "Metadata could not be written to the physical file after retrying.", true),
            "StagedUnidentifiable" => new(ReviewIssueBucket.HighPriority, "Item could not be identified", "The file is blocked in staging because there is not enough reliable identity evidence.", true),
            "LanguageMismatch" => new(ReviewIssueBucket.ManualReview, "Language needs confirmation", "The file language conflicts with the configured library language.", false),
            "AmbiguousMediaType" or "RootWatchFolder" => new(ReviewIssueBucket.ManualReview, "Media type is uncertain", "The file could belong to more than one media lane and needs a person to choose.", false),
            "MovieMatchedAsTv" => new(ReviewIssueBucket.ManualReview, "Found as a TV title", "TMDB lists this film as a TV series. Nothing changes until you choose.", false),
            "RetailMatchAmbiguous" => new(ReviewIssueBucket.ManualReview, "Match needs confirmation", "More than one provider record may match the detected item.", false),
            "MultipleQidMatches" => new(ReviewIssueBucket.ManualReview, "Canonical identity needs confirmation", "More than one Wikidata identity may match the confirmed retail record.", false),
            "RetailMatchFailed" => new(ReviewIssueBucket.ManualReview, "No reliable match", "No configured provider returned a sufficiently reliable match.", false),
            "PlaceholderTitle" => new(ReviewIssueBucket.ManualReview, "Missing identifying information", "A usable title or identifier is required before matching can continue.", false),
            "MetadataConflict" => new(ReviewIssueBucket.ManualReview, "Metadata sources disagree", "Two or more sources supplied similarly credible but conflicting values.", false),
            _ when rootCause == ReviewRootCause.EnrichmentIncomplete => new(ReviewIssueBucket.QuickFix, "Enrichment incomplete", "The item is identified but one enrichment step needs confirmation.", false),
            _ => new(ReviewIssueBucket.ManualReview, "Manual review needed", "The ingestion pipeline could not safely make this decision automatically.", false),
        };
    }

    public static string CategoryFor(string? trigger) => trigger switch
    {
        "RetailMatchFailed" => CategoryNoProviderMatch,
        "PlaceholderTitle" or "StagedUnidentifiable" => CategoryIncompleteMetadata,
        _ when trigger?.Contains("Duplicate", StringComparison.OrdinalIgnoreCase) == true => CategoryDuplicates,
        _ => Classify(trigger).Bucket == ReviewIssueBucket.QuickFix ? CategoryQuickFix : CategoryManualReview,
    };

    public static bool MatchesCategory(string? trigger, string category) => category switch
    {
        CategoryAll => true,
        CategoryQuickFix => Classify(trigger).Bucket == ReviewIssueBucket.QuickFix,
        CategoryManualReview => Classify(trigger).Bucket is ReviewIssueBucket.ManualReview or ReviewIssueBucket.HighPriority,
        CategoryNoProviderMatch => CategoryFor(trigger) == CategoryNoProviderMatch,
        CategoryIncompleteMetadata => CategoryFor(trigger) == CategoryIncompleteMetadata,
        CategoryDuplicates => CategoryFor(trigger) == CategoryDuplicates,
        _ => true,
    };
}
