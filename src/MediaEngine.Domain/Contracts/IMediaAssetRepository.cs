using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Defines the persistence contract for <see cref="MediaAsset"/> records.
/// Implementations live in <c>MediaEngine.Storage</c>.
///
/// This interface is in the Domain layer so the ingestion engine can depend on it
/// without referencing the storage implementation directly.
///
/// Spec: Phase 4 – Hash Dominance invariant (content_hash UNIQUE);
///       Phase 7 – Asset Integrity; Conflict and Orphan handling.
/// </summary>
public interface IMediaAssetRepository
{
    /// <summary>
    /// Returns the asset whose <c>content_hash</c> matches <paramref name="contentHash"/>,
    /// or <see langword="null"/> if no such asset exists.
    ///
    /// Primary duplicate-detection call: invoke this before <see cref="InsertAsync"/>
    /// to honour the Hash Dominance invariant.
    /// </summary>
    Task<MediaAsset?> FindByHashAsync(string contentHash, CancellationToken ct = default);

    /// <summary>
    /// Returns the asset with <paramref name="id"/>, or <see langword="null"/> if not found.
    /// </summary>
    Task<MediaAsset?> FindByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Inserts <paramref name="asset"/> into <c>media_assets</c>.
    ///
    /// Uses <c>INSERT OR IGNORE</c> on the <c>content_hash</c> unique constraint,
    /// so a concurrent duplicate will not throw — it is silently skipped.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when a new row was written;
    /// <see langword="false"/> when the hash already existed (duplicate file).
    /// </returns>
    Task<bool> InsertAsync(MediaAsset asset, CancellationToken ct = default);

    /// <summary>
    /// Returns the asset whose <c>file_path_root</c> matches <paramref name="pathRoot"/>,
    /// or <see langword="null"/> if no such asset exists.
    ///
    /// Used by the deletion handler to locate the asset record when the file
    /// has already been removed from disk (content hash is unavailable).
    /// Spec: Phase B – Deleted File Cleanup (B-04).
    /// </summary>
    Task<MediaAsset?> FindByPathRootAsync(string pathRoot, CancellationToken ct = default);

    /// <summary>
    /// Updates the <c>status</c> column for the asset identified by <paramref name="id"/>.
    /// Used to transition assets through the
    /// Normal → Conflicted / Normal → Orphaned lifecycle states.
    /// </summary>
    Task UpdateStatusAsync(Guid id, AssetStatus status, CancellationToken ct = default);

    /// <summary>
    /// Updates the <c>file_path_root</c> column for the asset identified by <paramref name="id"/>.
    /// Called after a file is re-organized (moved from the Watch Folder to the Library)
    /// so subsequent lookups reflect the new location.
    /// </summary>
    Task UpdateFilePathAsync(Guid id, string newPath, CancellationToken ct = default);

    /// <summary>
    /// Stamps the first time an asset became visible to normal library browse
    /// surfaces. Returns <see langword="true"/> only when the stamp was newly
    /// written, allowing callers to publish one-time presentation events.
    /// </summary>
    Task<bool> MarkPresentedAsync(Guid id, DateTimeOffset presentedAt, CancellationToken ct = default);

    /// <summary>
    /// Updates the stored content hash for an existing asset when the same file
    /// path is re-detected after writeback or retagging changed the file bytes.
    /// Returns <see langword="false"/> if the new hash already belongs to a
    /// different asset.
    /// </summary>
    Task<bool> UpdateContentHashAsync(Guid id, string contentHash, CancellationToken ct = default);

    /// <summary>
    /// Permanently deletes the asset record identified by <paramref name="id"/>.
    /// Used during orphan cleanup when the file no longer exists on disk.
    /// </summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Returns all assets with the given <paramref name="status"/>.
    /// Used by the reconciliation service to scan for Normal-status assets
    /// and verify their files still exist on disk.
    /// </summary>
    Task<IReadOnlyList<MediaAsset>> ListByStatusAsync(AssetStatus status, CancellationToken ct = default);

    /// <summary>
    /// Returns the first asset linked to a Work via its Edition chain.
    /// Joins editions → media_assets and returns the first match with status 'Normal'.
    /// Used by the EPUB reader to resolve a Work ID into a playable asset.
    /// </summary>
    Task<MediaAsset?> FindFirstByWorkIdAsync(Guid workId, CancellationToken ct = default, Guid? profileId = null);

    /// <summary>
    /// Returns a set of all <c>file_path_root</c> values currently stored in
    /// <c>media_assets</c>.
    ///
    /// Used by the startup scan to filter out files that are already tracked,
    /// preventing spurious batch creation on restart.  The result is consumed
    /// once and discarded — callers should not cache it.
    /// </summary>
    Task<HashSet<string>> GetAllFilePathsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns assets whose <c>writeback_fields_hash</c> does not match the
    /// expected hash for their media type — these are the assets the auto
    /// re-tag sweep needs to process. Skips assets whose
    /// <c>writeback_next_retry_at</c> is in the future (still in retry cooldown)
    /// and assets in <c>writeback_status = 'failed'</c>.
    /// </summary>
    /// <param name="expectedHashesByMediaType">
    /// Per-media-type expected writeback hashes (e.g. "TV" → "abc123…").
    /// </param>
    /// <param name="batchSize">Maximum number of stale assets to return.</param>
    /// <param name="nowEpochSeconds">Current unix epoch — used to filter out cooldown rows.</param>
    Task<IReadOnlyList<StaleRetagAsset>> GetStaleForRetagAsync(
        IReadOnlyDictionary<string, string> expectedHashesByMediaType,
        int batchSize,
        long nowEpochSeconds,
        CancellationToken ct = default,
        Guid? afterAssetId = null);

    /// <summary>
    /// Stamps the writeback hash and clears retry state after a successful
    /// re-tag write. Sets <c>writeback_status='ok'</c> and resets attempts/error.
    /// </summary>
    Task UpdateWritebackHashAsync(Guid assetId, string newHash, CancellationToken ct = default);

    /// <summary>
    /// Records an adapter write attempt that has no complete metadata read-back.
    /// It is not an applied/verified hash. A changed expected hash makes the
    /// asset eligible for another sweep.
    /// </summary>
    Task MarkWritebackUnverifiedAsync(Guid assetId, string expectedHash, CancellationToken ct = default);

    /// <summary>Records a format request the adapter cannot perform, without creating a review item.</summary>
    Task MarkWritebackUnsupportedAsync(Guid assetId, string expectedHash, string reason, CancellationToken ct = default);

    /// <summary>
    /// Records a transient failure (e.g. file locked) and schedules the next
    /// retry attempt. Sets <c>writeback_status='retry'</c>, increments
    /// <c>writeback_attempts</c>, and stores the next retry epoch.
    /// </summary>
    Task ScheduleRetagRetryAsync(
        Guid assetId,
        long nextRetryAtEpochSeconds,
        string error,
        CancellationToken ct = default);

    /// <summary>
    /// Marks an asset as permanently failed for re-tag (corrupt file or
    /// retry attempts exhausted). Sets <c>writeback_status='failed'</c>.
    /// The Action Center will surface a <see cref="ReviewTrigger.WritebackFailed"/>
    /// review item for these rows.
    /// </summary>
    Task MarkRetagFailedAsync(Guid assetId, string error, CancellationToken ct = default);

    /// <summary>
    /// Stamps the owning library on an asset. Called by ingestion once
    /// <c>ILibraryFolderResolver</c> has mapped the file's source path to a
    /// logical library. Side-by-side-with-Plex plan §F.
    /// </summary>
    Task SetLibraryIdAsync(Guid id, string? libraryId, CancellationToken ct = default);

    /// <summary>
    /// Marks an asset as orphaned (file disappeared from disk). Sets
    /// <c>is_orphaned = 1</c> and stamps <c>orphaned_at</c> with the current
    /// UTC time. Side-by-side-with-Plex plan §L.
    /// </summary>
    Task MarkOrphanedAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Clears the orphan flag on an asset — used by the reconciler when the
    /// same content hash reappears within the grace window. Side-by-side-with-Plex
    /// plan §L.
    /// </summary>
    Task ClearOrphanedAsync(Guid id, CancellationToken ct = default);

    /// <summary>Returns every physical rendition owned by one Edition.</summary>
    Task<IReadOnlyList<MediaAsset>> ListByEditionAsync(Guid editionId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MediaAsset>>([]);

    /// <summary>
    /// Updates delivery metadata without changing the asset's Work or Edition.
    /// Implementations must co-locate an explicitly derived asset with its source
    /// Edition and reject cycles. Encoding properties alone never cause a move.
    /// </summary>
    Task<bool> UpdateRenditionAsync(MediaAsset asset, CancellationToken ct = default)
        => Task.FromResult(false);
}

/// <summary>
/// Projection returned by <see cref="IMediaAssetRepository.GetStaleForRetagAsync"/>.
/// Carries enough state for the worker to attempt a write and route failures.
/// </summary>
public sealed record StaleRetagAsset(
    Guid AssetId,
    string FilePathRoot,
    string MediaType,
    string? CurrentHash,
    int Attempts);
