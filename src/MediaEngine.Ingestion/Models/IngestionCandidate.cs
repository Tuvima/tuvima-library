using MediaEngine.Domain.Enums;

namespace MediaEngine.Ingestion.Models;

/// <summary>
/// A file that has passed through the debounce settle period and the
/// file-lock probe, and is now safe to hand off to the ingestion pipeline.
///
/// Created exclusively by <see cref="DebounceQueue"/> after it confirms
/// the file is no longer being written to.
///
/// After pipeline processing, <see cref="Metadata"/> and
/// <see cref="DetectedMediaType"/> are populated by <c>IngestionEngine</c>
/// and consumed by <c>FileOrganizer</c> and <c>IMetadataTagger</c>.
/// </summary>
public sealed class IngestionCandidate
{
    /// <summary>Full absolute path to the file ready for processing.</summary>
    public required string Path { get; init; }

    /// <summary>Previous path for <see cref="FileEventType.Renamed"/> candidates; otherwise <see langword="null"/>.</summary>
    public string? OldPath { get; init; }

    /// <summary>The type of change that originally triggered this candidate.</summary>
    public required FileEventType EventType { get; init; }

    /// <summary>When the originating <see cref="FileEvent"/> was first detected.</summary>
    public required DateTimeOffset DetectedAt { get; init; }

    /// <summary>When the debounce queue confirmed the file was accessible.</summary>
    public required DateTimeOffset ReadyAt { get; init; }

    /// <summary>
    /// <see langword="true"/> when the file-lock probe exhausted all retry attempts
    /// and the file could not be confirmed as accessible.
    /// The ingestion engine should log the failure and schedule a recovery scan.
    /// </summary>
    public bool IsFailed { get; init; }

    /// <summary>Human-readable reason for failure. <see langword="null"/> on success.</summary>
    public string? FailureReason { get; init; }

    // -------------------------------------------------------------------------
    // Pipeline-enriched fields (set by IngestionEngine after processing)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Flat KV metadata resolved from processor claims and the scoring engine.
    /// Keys are lowercase claim names (e.g. "title", "author", "year").
    /// <see langword="null"/> until the ingestion engine has run the processor.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; set; }

    /// <summary>
    /// Media type detected by magic-byte analysis.
    /// <see langword="null"/> until detection has run.
    /// </summary>
    public MediaType? DetectedMediaType { get; set; }

    /// <summary>
    /// Confidence prior derived from the Library Folder's category configuration.
    /// Category-specific folders (e.g. "Movies") contribute +0.10, multi-type
    /// folders (e.g. Books accepting Epub + Audiobook) contribute +0.05, and
    /// general catch-all folders contribute 0.00.
    /// Applied as an additive boost to the scoring engine's overall confidence.
    /// </summary>
    public double CategoryConfidencePrior { get; set; }

    /// <summary>Stable direct-intake routing and actor context.</summary>
    public IntakeContext? Intake { get; set; }

    // -------------------------------------------------------------------------
    // Factory helpers
    // -------------------------------------------------------------------------

    /// <summary>Creates a successfully probed candidate from a <see cref="FileEvent"/>.</summary>
    internal static IngestionCandidate FromEvent(FileEvent evt) => new()
    {
        Path = evt.Path,
        OldPath = evt.OldPath,
        EventType = evt.EventType,
        DetectedAt = evt.OccurredAt,
        ReadyAt = DateTimeOffset.UtcNow,
        Intake = evt.Intake,
    };

    /// <summary>Creates a failed candidate when the probe is exhausted.</summary>
    internal static IngestionCandidate Failed(FileEvent evt, string reason) => new()
    {
        Path = evt.Path,
        OldPath = evt.OldPath,
        EventType = evt.EventType,
        DetectedAt = evt.OccurredAt,
        ReadyAt = DateTimeOffset.UtcNow,
        IsFailed = true,
        FailureReason = reason,
        Intake = evt.Intake,
    };
}
