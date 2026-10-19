namespace MediaEngine.Domain.Enums;

/// <summary>
/// Five internal root causes that compress 15+ review triggers.
/// The engine reasons about these states; the UI shows the specific
/// <see cref="Constants.ReviewTrigger"/> for user context.
/// </summary>
public enum ReviewRootCause
{
    /// <summary>
    /// Not enough data to identify the item.
    /// Maps from: RetailMatchFailed, StagedUnidentifiable, PlaceholderTitle, RootWatchFolder, FromMerge, MediaInspectionFailed.
    /// </summary>
    InsufficientEvidence,

    /// <summary>
    /// Evidence exists but is contradictory or ambiguous.
    /// Maps from: RetailMatchAmbiguous, MultipleQidMatches, AmbiguousMediaType, LowConfidence, MovieMatchedAsTv, NameCollision.
    /// </summary>
    ConflictingEvidence,

    /// <summary>
    /// Retail matched but Wikidata couldn't confirm identity.
    /// Maps from: WikidataBridgeFailed, MissingQid.
    /// </summary>
    NoCanonicalIdentity,

    /// <summary>
    /// Identity resolved but enrichment has gaps.
    /// Maps from: LanguageMismatch, WritebackFailed.
    /// </summary>
    EnrichmentIncomplete
}

/// <summary>
/// Extension methods for <see cref="ReviewRootCause"/>.
/// </summary>
public static class ReviewRootCauseExtensions
{
    /// <summary>Maps a <see cref="Constants.ReviewTrigger"/> string value to its root cause category.</summary>
    public static ReviewRootCause? FromTrigger(string? trigger) => trigger switch
    {
        "RetailMatchFailed" or "StagedUnidentifiable" or "PlaceholderTitle" or "RootWatchFolder" or "FromMerge" or "MediaInspectionFailed"
            => ReviewRootCause.InsufficientEvidence,
        "RetailMatchAmbiguous" or "MultipleQidMatches" or "AmbiguousMediaType" or "LowConfidence" or "MovieMatchedAsTv" or "NameCollision"
            => ReviewRootCause.ConflictingEvidence,
        "WikidataBridgeFailed" or "MissingQid"
            => ReviewRootCause.NoCanonicalIdentity,
        "LanguageMismatch" or "WritebackFailed"
            => ReviewRootCause.EnrichmentIncomplete,
        _ => null,
    };
}
