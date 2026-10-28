using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// The data-store record of Library's Import and Rejected areas (Photo and video filing spec D1, D9, D15): one row per file, which is
/// also the filing journal, and the duplicate groups found by the cleanup job.
/// </summary>
public interface IViewIntakeRepository
{
    /// <summary>Adds a row. A file already tracked at the same path (open states) is returned instead of added twice.</summary>
    Task<ViewIntakeFile> AddAsync(ViewIntakeFile file, CancellationToken ct = default);

    Task<ViewIntakeFile?> GetByPathAsync(string currentPath, CancellationToken ct = default);

    Task<ViewIntakeFile?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>The rows that are in one of <paramref name="states"/>, oldest first, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<ViewIntakeFile>> GetByStateAsync(IReadOnlyCollection<string> states, int limit, CancellationToken ct = default);

    /// <summary>The rows of one group.</summary>
    Task<IReadOnlyList<ViewIntakeFile>> GetGroupAsync(string groupKey, CancellationToken ct = default);

    /// <summary>The refused files, newest first, optionally for one kind.</summary>
    Task<IReadOnlyList<ViewIntakeFile>> GetRejectedAsync(string? contentKind, int limit, CancellationToken ct = default);

    /// <summary>Rejected rows whose expiry has passed.</summary>
    Task<IReadOnlyList<ViewIntakeFile>> GetExpiredAsync(DateTimeOffset now, int limit, CancellationToken ct = default);

    /// <summary>How many files of a kind are waiting in the Import area (all of them when <paramref name="contentKind"/> is null).</summary>
    Task<int> CountWaitingAsync(string? contentKind, CancellationToken ct = default);

    /// <summary>Records a state change and, when given, the new paths, hash and reason. Fields passed as null are kept.</summary>
    Task UpdateAsync(
        Guid id,
        string state,
        string? currentPath = null,
        string? targetPath = null,
        string? filedPath = null,
        string? contentHash = null,
        string? reasonCode = null,
        string? reasonText = null,
        DateTimeOffset? expiresAt = null,
        CancellationToken ct = default);

    /// <summary>Records the size a waiting file had when it was last looked at, and when. A file whose size keeps changing is still being copied.</summary>
    Task SetSizeAsync(Guid id, long byteSize, CancellationToken ct = default);

    /// <summary>Clears the target path of a row (a move that never started is put back to waiting).</summary>
    Task ClearTargetAsync(Guid id, CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Removes filed and duplicate rows older than <paramref name="olderThan"/>; they are only a short trail for the summary.</summary>
    Task<int> PruneFinishedAsync(DateTimeOffset olderThan, CancellationToken ct = default);

    // ── Duplicate groups ─────────────────────────────────────────────────────

    /// <summary>Records (or refreshes) a group the job found. A dismissed group stays dismissed unless it grew.</summary>
    Task UpsertDuplicateGroupAsync(Guid libraryId, string scopeKind, string contentHash, int copyCount, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Removes groups of the given scope that the latest scan did not see again (their copies were cleaned up).</summary>
    Task<int> RemoveUnseenDuplicateGroupsAsync(string scopeKind, DateTimeOffset seenSince, CancellationToken ct = default);

    Task<IReadOnlyList<ViewDuplicateGroup>> GetDuplicateGroupsAsync(string? scopeKind, Guid? libraryId, bool includeDismissed, CancellationToken ct = default);

    Task<ViewDuplicateGroup?> GetDuplicateGroupAsync(Guid id, CancellationToken ct = default);

    Task<bool> DismissDuplicateGroupAsync(Guid id, Guid dismissedBy, DateTimeOffset now, CancellationToken ct = default);
}
