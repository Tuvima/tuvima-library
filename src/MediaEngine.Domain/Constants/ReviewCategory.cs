namespace MediaEngine.Domain.Constants;

/// <summary>
/// The one set of Needs Review categories. Every <see cref="ReviewTrigger"/> belongs to exactly one
/// category, so Live Ingestion's attention counts and the Needs Review list group items the same way.
/// The keys are on the wire as <c>IngestionReviewReasonDto.Key</c>; keep the strings.
/// </summary>
public static class ReviewCategory
{
    /// <summary>Items could not be matched to a known catalogue record.</summary>
    public const string Unmatched = "unmatched";

    /// <summary>Items matched below the confidence threshold.</summary>
    public const string LowConfidence = "low_confidence";

    /// <summary>Items need canonical identity or Wikidata confirmation.</summary>
    public const string MissingWikidata = "missing_wikidata";

    /// <summary>File or folder names do not provide enough context.</summary>
    public const string Naming = "naming";

    /// <summary>A provider or write-back step could not complete.</summary>
    public const string ProviderFailures = "provider_failures";

    /// <summary>Conflicting metadata requires a human decision.</summary>
    public const string MetadataConflicts = "metadata_conflicts";

    /// <summary>A category key with the label and explanation shown to the user.</summary>
    public sealed record Info(string Key, string Label, string Explanation);

    /// <summary>Every category, in display order.</summary>
    public static IReadOnlyList<Info> All { get; } =
    [
        new(Unmatched, "Unmatched", "Items could not be matched to a known catalogue record."),
        new(LowConfidence, "Low Confidence", "Items matched below the confidence threshold."),
        new(MissingWikidata, "Missing Wikidata Identity", "Items need canonical identity or Wikidata confirmation."),
        new(Naming, "Naming Issues", "File or folder names do not provide enough context."),
        new(ProviderFailures, "Provider Failures", "A provider or writeback step could not complete."),
        new(MetadataConflicts, "Metadata Conflicts", "Conflicting metadata requires a human decision."),
    ];

    /// <summary>
    /// The category key for a <see cref="ReviewTrigger"/> value, or <c>null</c> for an unknown or missing trigger
    /// (callers decide what to do with those).
    /// </summary>
    public static string? For(string? trigger) => trigger switch
    {
        ReviewTrigger.RetailMatchFailed or ReviewTrigger.StagedUnidentifiable or ReviewTrigger.MovieMatchedAsTv
            => Unmatched,
        ReviewTrigger.LowConfidence or ReviewTrigger.RetailMatchAmbiguous
            => LowConfidence,
        ReviewTrigger.MissingQid or ReviewTrigger.WikidataBridgeFailed or ReviewTrigger.MultipleQidMatches
            => MissingWikidata,
        ReviewTrigger.RootWatchFolder or ReviewTrigger.PlaceholderTitle or ReviewTrigger.AmbiguousMediaType
            or ReviewTrigger.NameCollision or ReviewTrigger.FromMerge
            => Naming,
        ReviewTrigger.WritebackFailed or ReviewTrigger.MediaInspectionFailed
            => ProviderFailures,
        ReviewTrigger.MetadataConflict or ReviewTrigger.UserFixMatch or ReviewTrigger.LanguageMismatch
            => MetadataConflicts,
        _ => null,
    };
}
