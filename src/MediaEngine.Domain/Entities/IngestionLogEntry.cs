namespace MediaEngine.Domain.Entities;

/// <summary>
/// Tracks a single file through the ingestion pipeline from detection to completion.
/// Provides a single-table answer to "what happened to my file?"
///
/// Status values: detected → hashing → processed → scored → staged → organized →
///                hydrating → complete | failed | needs_review
/// </summary>
public sealed class IngestionLogEntry
{
    /// <summary>Unique identifier for this log entry (matches the MediaAsset ID when available).</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Full absolute path of the ingested file.</summary>
    public required string FilePath { get; set; }

    /// <summary>Media asset ID once the file has been registered; null before asset creation.</summary>
    public Guid? MediaAssetId { get; set; }

    /// <summary>SHA-256 content hash; populated after hashing step.</summary>
    public string? ContentHash { get; set; }

    /// <summary>
    /// Current pipeline status. One of: detected, hashing, processed, scored,
    /// registered, queued_identity, hydrating, complete, failed, needs_review.
    /// </summary>
    public string Status { get; set; } = "detected";

    /// <summary>Resolved media type (e.g. "Books", "Movies"); populated after processing.</summary>
    public string? MediaType { get; set; }

    /// <summary>Overall confidence score from the scoring engine.</summary>
    public double? ConfidenceScore { get; set; }

    /// <summary>Title as detected from file metadata or filename.</summary>
    public string? DetectedTitle { get; set; }

    /// <summary>Title after normalization (quality tag stripping, dot replacement).</summary>
    public string? NormalizedTitle { get; set; }

    /// <summary>Wikidata QID resolved during hydration.</summary>
    public string? WikidataQid { get; set; }

    /// <summary>Error detail when status is "failed".</summary>
    public string? ErrorDetail { get; set; }

    /// <summary>When the file was first detected.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When this entry was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
