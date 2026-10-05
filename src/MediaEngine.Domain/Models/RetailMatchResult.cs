using MediaEngine.Domain.Enums;

namespace MediaEngine.Domain.Models;

/// <summary>
/// A scored retail provider candidate, ranked against file metadata.
/// Produced by IRetailMatchScoringService during Stage 1 (Retail Identification).
/// </summary>
public sealed class RetailMatchResult
{
    /// <summary>Retail provider name (e.g. "apple_books", "tmdb").</summary>
    public string ProviderName { get; init; } = "";

    /// <summary>Provider-specific item identifier (e.g. Apple Books collectionId, TMDB movie ID).</summary>
    public string? ProviderItemId { get; init; }

    /// <summary>Resolved title from the retail result.</summary>
    public string? Title { get; init; }

    /// <summary>Author or creator name from the retail result.</summary>
    public string? Author { get; init; }

    /// <summary>Release year from the retail result.</summary>
    public string? Year { get; init; }

    /// <summary>Cover art URL from the retail result.</summary>
    public string? CoverUrl { get; init; }

    /// <summary>
    /// Edition-specific description from the retail provider.
    /// HTML-sanitized (basic formatting preserved: b, i, em, strong, p, br).
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Bridge identifiers extracted from the retail result.
    /// Keys are claim keys (e.g. "apple_books_id", "isbn", "tmdb_id").
    /// Values are the identifier strings.
    /// </summary>
    public Dictionary<string, string> BridgeIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-field match scores against file metadata.</summary>
    public FieldMatchScores Scores { get; init; } = new();

    /// <summary>Composite confidence score (0.0–1.0). Weighted average of field scores.</summary>
    public double Confidence { get; init; }

    /// <summary>Whether this result was auto-accepted (confidence ≥ threshold).</summary>
    public bool IsAutoAccepted { get; init; }
}

/// <summary>
/// Per-field match scores comparing a retail result against file metadata.
/// </summary>
public sealed record RetailFieldScore(string Key, string Label, double? Score, double Weight, bool Missing,
    string Role, double Contribution, string MissingPolicy, string Verdict, string? LocalValue, string? CandidateValue);

public sealed record CandidateSignals(MediaType MediaType, string Scope, string? Title, string? Creator,
    string? Year, CandidateExtendedMetadata Metadata);

public sealed class FieldMatchScores
{
    public IReadOnlyList<RetailFieldScore> FieldScores { get; init; } = [];
    public IReadOnlyList<string> AutoAcceptBlockReasons { get; init; } = [];

    /// <summary>Title similarity score (0.0–1.0).</summary>
    public double TitleScore { get; init; }

    /// <summary>Author/creator similarity score (0.0–1.0).</summary>
    public double AuthorScore { get; init; }

    /// <summary>Year match score (1.0 = exact, 0.8 = off by 1, 0.3 = off by 2+).</summary>
    public double YearScore { get; init; }

    /// <summary>Format/media type consistency score (1.0 = exact match).</summary>
    public double FormatScore { get; init; }

    /// <summary>Cross-field boost/penalty from secondary signals (narrator-in-description, etc.).</summary>
    public double CrossFieldBoost { get; init; }

    /// <summary>Cover art perceptual hash similarity boost (0.0, 0.05, or 0.10).</summary>
    public double CoverArtScore { get; init; }

    /// <summary>Weighted composite of all field scores including cross-field signals.</summary>
    public double CompositeScore { get; init; }
}

/// <summary>
/// Extended candidate metadata for cross-field scoring.
/// Passed alongside the basic title/author/year to enable richer signal matching.
/// </summary>
public sealed class CandidateExtendedMetadata
{
    public IReadOnlyDictionary<string, string> Signals { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public string? Scope { get; init; }
    public MediaType? Kind { get; init; }

    /// <summary>Description text from the retail result.</summary>
    public string? Description { get; init; }

    /// <summary>Publisher name from the retail result.</summary>
    public string? Publisher { get; init; }

    /// <summary>Page count from the retail result (books).</summary>
    public int? PageCount { get; init; }

    /// <summary>Duration in seconds from the retail result (audiobooks, music).</summary>
    public double? DurationSeconds { get; init; }

    /// <summary>Genres from the retail result.</summary>
    public IReadOnlyList<string>? Genres { get; init; }

    /// <summary>Language code from the retail result.</summary>
    public string? Language { get; init; }

    /// <summary>Series, show, volume, or other container title from the retail result.</summary>
    public string? Series { get; init; }

    /// <summary>Issue number from the retail result for comic issue matching.</summary>
    public string? IssueNumber { get; init; }

    /// <summary>
    /// Cover art perceptual hash similarity against the file's embedded cover art (0.0–1.0),
    /// or <c>null</c> if not computed (no embedded cover, no candidate cover URL, or hash unavailable).
    /// </summary>
    public double? CoverArtSimilarity { get; init; }
}
