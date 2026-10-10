namespace MediaEngine.Domain.Constants;

/// <summary>
/// String constants for the <c>trigger</c> column in <c>review_queue</c>.
///
/// Identifies why a review item was created. Stored as TEXT in SQLite for
/// readability and extensibility — future phases can introduce new triggers
/// without a schema migration.
/// </summary>
public static class ReviewTrigger
{
    /// <summary>
    /// The pipeline's overall confidence for the entity fell below the
    /// <c>auto_review_confidence_threshold</c> after all stages completed.
    /// </summary>
    public const string LowConfidence = "LowConfidence";

    /// <summary>
    /// The Wikidata bridge lookup returned multiple QID candidates and could
    /// not disambiguate automatically. The user must select the correct QID.
    /// </summary>
    public const string MultipleQidMatches = "MultipleQidMatches";

    /// <summary>
    /// The user explicitly requested a "Fix Match" action from the Dashboard,
    /// signalling that the current metadata assignment is incorrect.
    /// </summary>
    public const string UserFixMatch = "UserFixMatch";

    /// <summary>
    /// The <see cref="MediaEngine.Intelligence.CollectionArbiter"/> scored the entity in
    /// the NeedsReview disposition band (between Conflict and AutoLink thresholds).
    /// </summary>
    public const string ArbiterNeedsReview = "ArbiterNeedsReview";

    /// <summary>
    /// The scoring engine detected a metadata conflict: two or more claims for
    /// the same field have confidence values within the conflict epsilon. The file
    /// is still organised with the best guess, but the user should verify the
    /// conflicting field.
    /// </summary>
    public const string MetadataConflict = "MetadataConflict";

    /// <summary>
    /// The media type could not be determined with sufficient confidence.
    /// The file's container format is shared across multiple media types
    /// (e.g. MP3 → Audiobook/Music, MP4 → Movie/TV Show) and
    /// heuristic analysis did not produce a clear winner. The user must
    /// manually select the correct media type.
    /// </summary>
    public const string AmbiguousMediaType = "AmbiguousMediaType";

    /// <summary>
    /// Stage 2 (Wikidata Bridge Resolution) did not find a QID for the work, but a retail
    /// provider (Stage 1 (Retail Identification)) matched by ISBN/ASIN.  The book is likely new or
    /// in early release.  A <c>NF{6-digit}</c> placeholder QID was assigned
    /// and should be replaced with a real QID when Wikidata catches up.
    /// </summary>
    public const string MissingQid = "MissingQid";

    /// <summary>
    /// File scored below 0.40 overall confidence with no user-locked claims.
    /// Moved to .staging/unidentifiable/ — deeply broken or unrecognizable.
    /// </summary>
    public const string StagedUnidentifiable = "StagedUnidentifiable";

    /// <summary>
    /// The file's title is a placeholder ("Unknown", "Untitled", blank) and
    /// no bridge identifier (ISBN, ASIN, QID) exists to prove its identity.
    /// The user must provide a real title or match it manually.
    /// </summary>
    public const string PlaceholderTitle = "PlaceholderTitle";

    /// <summary>
    /// Cover art was deposited by a retail provider using a text search (title + author)
    /// rather than a precise bridge identifier lookup (ISBN, Apple Books ID). The match
    /// may be incorrect — the user should confirm or replace the artwork.
    /// </summary>
    public const string ArtworkUnconfirmed = "ArtworkUnconfirmed";

    /// <summary>
    /// The file's embedded language metadata (dc:language in EPUB OPF, id3 lang tag)
    /// does not match the user's configured library language in
    /// <c>CoreConfiguration.Language</c>. The book may be in the wrong language
    /// or in a foreign edition. The user should confirm or reclassify the item.
    /// </summary>
    public const string LanguageMismatch = "LanguageMismatch";

    /// <summary>A user submitted a problem report flagging incorrect or missing metadata.</summary>
    public const string UserReport = "UserReport";

    /// <summary>
    /// Stage 1 (Retail Identification) returned no results from any configured provider.
    /// The file's metadata did not match any retail catalogue entry.
    /// </summary>
    public const string RetailMatchFailed = "RetailMatchFailed";

    /// <summary>
    /// Stage 1 (Retail Identification) returned results but the top candidate's
    /// confidence score fell between the ambiguous threshold (0.50) and the
    /// auto-accept threshold (0.85). The user should verify the match.
    /// </summary>
    public const string RetailMatchAmbiguous = "RetailMatchAmbiguous";

    /// <summary>
    /// Stage 2 (Wikidata Bridge Resolution) could not find a Wikidata entity
    /// matching the bridge IDs from Stage 1. The retail match is preserved but
    /// the item lacks universe linkage. A periodic re-check will retry.
    /// </summary>
    public const string WikidataBridgeFailed = "WikidataBridgeFailed";

    /// <summary>
    /// The file was dropped directly into the root of a watch folder rather than
    /// a typed media subfolder (e.g. Books/, Movies/).  An ambiguous file extension
    /// (MP3, MP4, MKV, AVI, FLAC, WAV, M4A, OGG, WEBM) cannot be reliably classified
    /// without the folder context; confidence is capped at 0.40 so the user must
    /// confirm the media type.
    /// </summary>
    public const string RootWatchFolder = "RootWatchFolder";

    /// <summary>
    /// The auto re-tag sweep tried to write metadata to a file and either
    /// the file was corrupt, the tagger threw an unrecoverable error, or
    /// retry attempts were exhausted. The user must decide whether to retry
    /// the write or skip the file permanently.
    /// </summary>
    public const string WritebackFailed = "WritebackFailed";

    /// <summary>
    /// A file in a Movies library had no movie match, but TMDB lists a matching title in its TV catalogue
    /// (for example a miniseries or web series). The suggestion is never applied automatically;
    /// the user chooses Move to TV, Search again, or Keep as unmatched film. <c>candidates_json</c>
    /// carries the TMDB TV suggestion.
    /// </summary>
    public const string MovieMatchedAsTv = "MovieMatchedAsTv";
}
