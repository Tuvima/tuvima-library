using MediaEngine.Ingestion.Models;

namespace MediaEngine.Ingestion.Contracts;

/// <summary>
/// Core coordinator for the file monitoring and staging pipeline.
/// Spec: Phase 7 – Interfaces § IIngestionEngine.
///
/// Wires together <see cref="IFileWatcher"/>, <see cref="DebounceQueue"/>,
/// and <see cref="IBackgroundWorker"/> into a managed lifecycle.
/// </summary>
public interface IIngestionEngine
{
    /// <summary>
    /// Queue a single direct-intake file while preserving its stable source,
    /// destination-library, and actor context through the debounce pipeline.
    /// </summary>
    Task EnqueueIntakeAsync(IntakeFileRequest request, CancellationToken ct = default);

    /// <summary>Re-read local tags and technical metadata for one existing asset without rematching or moving it.</summary>
    Task<FileMetadataRereadResult> RereadAssetMetadataAsync(Guid assetId, CancellationToken ct = default);

    /// <summary>Starts the watcher and begins consuming the debounce queue.</summary>
    void Start();

    /// <summary>Drains in-flight operations and stops all background activity.</summary>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>
    /// Simulates the ingestion of all files under <paramref name="rootPath"/>
    /// without executing any file system mutations.
    /// Spec: "MUST provide a simulation mode that reports intended file system
    ///        changes without executing them."
    /// </summary>
    Task<IReadOnlyList<PendingOperation>> DryRunAsync(
        string rootPath, CancellationToken ct = default);

    /// <summary>
    /// Scans a directory for existing files and feeds synthetic "Created" events
    /// into the debounce queue so they are processed through the normal pipeline.
    /// Called after a watch-folder hot-swap to pick up files that were already present.
    /// Duplicates are harmless — the hash-based duplicate check short-circuits them.
    /// </summary>
    Task ScanDirectory(string directory, bool includeSubdirectories = true, CancellationToken ct = default);

    /// <summary>
    /// Scans multiple directories as one logical ingestion run. All accepted files
    /// share a single ingestion batch ID so the Dashboard shows one user-facing
    /// library update rather than one row per source folder.
    /// </summary>
    Task ScanDirectories(IReadOnlyList<IngestionScanTarget> targets, CancellationToken ct = default);

    /// <summary>
    /// Makes sure the import folder (as configured now) is being watched, looks at what is already in it, and tries again
    /// to file anything that was waiting for a managed folder or a review. Does nothing when no import folder is set.
    /// </summary>
    Task RefreshImportFolderAsync(CancellationToken ct = default);

    /// <summary>
    /// Stops the FileSystemWatcher and clears the FSW event buffer without
    /// touching the debounce queue or its consumer loop.
    /// Used by the dev wipe operation so that seed files written immediately
    /// after the wipe are not detected as spurious FSW events — they are
    /// instead enqueued deterministically via <see cref="ScanDirectory"/>.
    /// </summary>
    Task PauseWatcherAsync(CancellationToken ct = default);

    /// <summary>
    /// Restarts the FileSystemWatcher after a <see cref="PauseWatcherAsync"/> call.
    /// Clears the per-path dedup tracking so that previously-seen file paths
    /// (wiped and re-seeded) can be enqueued again.
    /// </summary>
    Task ResumeWatcherAsync(CancellationToken ct = default);
}

/// <summary>One directory included in a grouped ingestion scan.</summary>
public sealed record IngestionScanTarget(string Path, bool IncludeSubdirectories = true);

public sealed record FileMetadataRereadResult(
    Guid AssetId,
    string Status,
    bool Refreshed,
    bool ContentHashChanged,
    string Message);

/// <summary>
/// A file system operation that the ingestion engine intends to perform,
/// returned by <see cref="IIngestionEngine.DryRunAsync"/>.
/// </summary>
public sealed class PendingOperation
{
    public required string SourcePath { get; init; }
    public required string DestinationPath { get; init; }
    public required string OperationKind { get; init; } // "Move", "Rename", "WriteTag", etc.
    public string? Reason { get; init; }
}
