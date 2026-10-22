namespace MediaEngine.Domain.Contracts;

/// <summary>
/// The waiting mark of a library move (batched write-back D11). While a file is being renamed and moved, the folder watcher, the
/// rescan and the reconciliation leave it alone: it is never marked missing and its new place is never ingested as a new file.
/// </summary>
public interface IFileMoveInProgress
{
    /// <summary>The full paths (old and new place) of every file with a move in progress, compared without regard to case.</summary>
    Task<IReadOnlySet<string>> GetPathsAsync(CancellationToken ct = default);

    /// <summary>True when the media asset has a move in progress.</summary>
    Task<bool> HasMoveForAssetAsync(Guid assetId, CancellationToken ct = default);
}
