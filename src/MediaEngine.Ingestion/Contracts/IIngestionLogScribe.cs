using MediaEngine.Ingestion.Models;

namespace MediaEngine.Ingestion.Contracts;

/// <summary>
/// Best-effort writer for per-file ingestion lifecycle log entries and progress events.
/// </summary>
public interface IIngestionLogScribe
{
    /// <param name="quiet">True for a file in a Vault library: the log row is written but nothing is announced to open screens.</param>
    Task<Guid> InsertDetectedAsync(
        IngestionCandidate candidate,
        CancellationToken ct = default,
        bool quiet = false);

    Task UpdateStatusAsync(
        IngestionCandidate candidate,
        Guid logEntryId,
        string status,
        int progressPercent,
        bool isTerminal,
        CancellationToken ct = default,
        string? contentHash = null,
        string? mediaType = null,
        double? confidenceScore = null,
        string? detectedTitle = null,
        string? normalizedTitle = null,
        string? wikidataQid = null,
        Guid? mediaAssetId = null,
        string? errorDetail = null,
        string? title = null,
        bool quiet = false);

    Task RecordTerminalAsync(
        IngestionCandidate candidate,
        string status,
        string detail,
        CancellationToken ct = default,
        bool quiet = false);

    Task PublishProgressAsync(
        IngestionCandidate candidate,
        Guid logEntryId,
        string stage,
        int progressPercent,
        bool isTerminal,
        CancellationToken ct = default,
        Guid? mediaAssetId = null,
        string? title = null,
        string? mediaType = null,
        bool quiet = false);
}
