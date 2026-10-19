using MediaEngine.Ingestion.Models;

namespace MediaEngine.Ingestion.Contracts;

/// <summary>
/// Scans a Library Root directory for media files and rebuilds the database
/// from embedded file metadata and batch reconciliation — the "Great Inhale v2".
///
/// <para>
/// Files already known to the database (matched by content hash) have their
/// paths updated if they have moved on disk. Files not yet in the database are
/// flagged for a follow-up ingestion pass to run processors, scoring, and
/// metadata hydration.
/// </para>
///
/// <para>
/// Person and universe data are database-owned. The scanner does not import
/// retired filesystem sidecars for those entities.
/// </para>
/// </summary>
public interface ILibraryScanner
{
    /// <summary>
    /// Scans <paramref name="libraryRoot"/> for media files and returns a
    /// summary of the scan results.
    /// </summary>
    /// <param name="libraryRoot">Absolute path to the Library Root directory.</param>
    /// <param name="ct">Cancellation token; checked between files.</param>
    Task<LibraryScanResult> ScanAsync(
        string libraryRoot,
        CancellationToken ct = default);

    /// <summary>
    /// Retired compatibility hook. Person recovery from filesystem sidecars is disabled;
    /// people are sourced from the database and canonical headshot assets.
    /// </summary>
    /// <param name="libraryRoot">Absolute path to the Library Root directory.</param>
    /// <param name="ct">Cancellation token; checked between files.</param>
    /// <returns>Number of person records upserted.</returns>
    Task<int> ScanPeopleAsync(
        string libraryRoot,
        CancellationToken ct = default);
}
