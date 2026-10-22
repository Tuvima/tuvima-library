using MediaEngine.Domain.Models;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// The files the Engine could not match (Library setup spec D34). One row per media asset; the row goes when a lookup matches the file
/// (the identity job leaving the no-match state removes it), or when the file is removed. Kept items never retry and never count.
/// </summary>
public interface IUnmatchedAssetRepository
{
    /// <summary>
    /// Marks the asset Unmatched, or refreshes the row it already has: the first-unmatched time and a kept mark are preserved, the reason and
    /// last-tried time are updated, and the next retry is set to <paramref name="nextRetryAt"/> (never for a kept item).
    /// </summary>
    Task MarkUnmatchedAsync(Guid assetId, string libraryId, string reason, DateTimeOffset now, DateTimeOffset nextRetryAt, CancellationToken ct = default);

    /// <summary>The Unmatched row of one asset, or null.</summary>
    Task<UnmatchedAsset?> GetAsync(Guid assetId, CancellationToken ct = default);

    /// <summary>The Unmatched rows of the given assets (kept ones included); an asset with no row is absent. Reads only the ids asked for.</summary>
    Task<IReadOnlyList<UnmatchedAsset>> ListByAssetsAsync(IReadOnlyCollection<Guid> assetIds, CancellationToken ct = default);

    /// <summary>
    /// Every Unmatched file that is not kept, for the given works (all of a work's files, in whichever library each now is). Files that are
    /// gone or orphaned are left out. Reads only the works asked for.
    /// </summary>
    Task<IReadOnlyList<UnmatchedWorkFile>> ListOpenByWorksAsync(IReadOnlyCollection<Guid> workIds, CancellationToken ct = default);

    /// <summary>Removes the row (the file matched). Returns true when a row was removed.</summary>
    Task<bool> ClearAsync(Guid assetId, CancellationToken ct = default);

    /// <summary>Keep as is: stops the item's retries and takes it out of the count and filter. Returns false when the asset is not Unmatched.</summary>
    Task<bool> KeepAsIsAsync(Guid assetId, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Keep all Unmatched as they are, for one library. Returns how many items were kept.</summary>
    Task<int> KeepAllAsIsAsync(string libraryId, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Look up again: clears the kept mark and makes the item due now. Returns false when the asset is not Unmatched.</summary>
    Task<bool> LookUpAgainAsync(Guid assetId, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Makes one item that is not kept due now (Retry now). Returns false when the asset is not Unmatched or is kept.</summary>
    Task<bool> MakeDueAsync(Guid assetId, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Makes every item that is not kept due now: one library's, or all libraries' when <paramref name="libraryId"/> is null. Returns how many.</summary>
    Task<int> MakeAllDueAsync(string? libraryId, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Items that are not kept and are due at or before <paramref name="now"/>, oldest due first; one library's when <paramref name="libraryId"/> is given.</summary>
    Task<IReadOnlyList<UnmatchedAsset>> ListDueAsync(DateTimeOffset now, int limit, string? libraryId = null, CancellationToken ct = default);

    /// <summary>Records that a retry ran: sets the last-tried time and the next retry (not for a kept item).</summary>
    Task RecordTriedAsync(Guid assetId, DateTimeOffset now, DateTimeOffset nextRetryAt, CancellationToken ct = default);

    /// <summary>Each library's Unmatched count, kept items excluded. A library with none is absent.</summary>
    Task<IReadOnlyDictionary<string, int>> CountByLibraryAsync(CancellationToken ct = default);

    /// <summary>One library's Unmatched files, newest first. Kept items are left out unless <paramref name="includeKept"/> is set.</summary>
    Task<UnmatchedAssetPage> ListByLibraryAsync(string libraryId, bool includeKept, int skip, int take, CancellationToken ct = default);
}
