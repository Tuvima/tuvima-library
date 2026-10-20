using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Persistence contract for the per-file ingestion lifecycle log.
/// Tracks each file from detection through completion, providing
/// a single-table answer to "what happened to my file?"
/// </summary>
public interface IIngestionLogRepository
{
    /// <summary>
    /// Creates a new ingestion log entry when a file is first detected.
    /// </summary>
    Task InsertAsync(IngestionLogEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Updates the status and optional fields of an existing log entry.
    /// Only non-null fields are overwritten.
    /// </summary>
    Task UpdateStatusAsync(
        Guid id,
        string status,
        string? contentHash = null,
        string? mediaType = null,
        double? confidenceScore = null,
        string? detectedTitle = null,
        string? normalizedTitle = null,
        string? wikidataQid = null,
        Guid? mediaAssetId = null,
        string? errorDetail = null,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the most recent log entries, ordered newest-first.
    /// </summary>
    Task<IReadOnlyList<IngestionLogEntry>> GetRecentAsync(
        int limit = 50,
        CancellationToken ct = default);

    /// <summary>
    /// Finds a log entry by its unique ID.
    /// </summary>
    Task<IngestionLogEntry?> FindByIdAsync(Guid id, CancellationToken ct = default);
}
