using System.Collections.Concurrent;

namespace MediaEngine.Ingestion;

/// <summary>
/// Centralized concurrency guard that manages all lock dictionaries used by the
/// ingestion and enrichment pipelines. Documents and enforces the lock hierarchy
/// to prevent future deadlocks (Principle 5 — Gap Analysis Synthesis).
///
/// ──────────────────────────────────────────────────────────────────
/// Lock hierarchy (must acquire in this order to prevent deadlocks):
/// ──────────────────────────────────────────────────────────────────
///   1. Folder lock — broadest scope: serializes files in the same directory. Currently used only by
///      the Books work-resolution step (<see cref="AcquireFolderLockAsync"/>), which takes it briefly
///      while already holding the hash lock; nothing holds a folder lock while waiting for a hash lock.
///   2. Hash lock   — within folder lock: prevents duplicate-check races on the same content.
///   3. QID lock    — independent (hydration pipeline only): serializes person merge per Wikidata QID.
///   4. Person lock — independent (identity service only): serializes person find-or-create per name+role.
///   5. Artwork-owner lock — write-back stage only: serializes cover read-check-write-upsert per owner entity
///      (e.g. the tracks of one album or audiobook). Acquired after the hash lock and never held across other guards.
///
/// QID and Person locks are never held simultaneously with Folder or Hash locks
/// (they run in separate pipeline stages), so no ordering constraint exists between groups {1,2} and {3,4}.
/// Within each group the ordering above MUST be respected.
///
/// Periodic cleanup: <see cref="Cleanup"/> removes all semaphores that are not currently held.
/// Callers should invoke this periodically (e.g. after a batch completes) to prevent unbounded growth.
/// </summary>
public sealed class ConcurrencyGuard
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _folderLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hashLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _qidLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _personLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _artworkOwnerLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Acquires (or creates) the folder-level lock for the given folder key.</summary>
    public SemaphoreSlim GetFolderLock(string folderKey)
        => _folderLocks.GetOrAdd(folderKey, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Acquires the folder lock and returns a releaser. If <see cref="Cleanup"/> evicted the
    /// semaphore between lookup and acquisition, the acquisition is retried on the current instance
    /// so two callers can never hold "the same" folder lock through different semaphores.
    /// </summary>
    /// <remarks>
    /// The Books work-resolution step takes this lock for a short, leaf-level critical section
    /// (work/edition creation plus asset registration) while already holding the file's hash lock.
    /// That is safe because no code path holds a folder lock while waiting for a hash lock.
    /// </remarks>
    public async Task<IDisposable> AcquireFolderLockAsync(string folderKey, CancellationToken ct)
    {
        while (true)
        {
            var semaphore = _folderLocks.GetOrAdd(folderKey, _ => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            if (_folderLocks.TryGetValue(folderKey, out var current) && ReferenceEquals(current, semaphore))
            {
                return new SemaphoreReleaser(semaphore);
            }

            semaphore.Release();
        }
    }

    /// <summary>Acquires (or creates) the hash-level lock.</summary>
    public SemaphoreSlim GetHashLock(string hashHex)
        => _hashLocks.GetOrAdd(hashHex, _ => new SemaphoreSlim(1, 1));

    /// <summary>Releases and removes the hash lock entry after processing completes.</summary>
    public void ReleaseHashLock(string hashHex)
        => _hashLocks.TryRemove(hashHex, out _);

    /// <summary>Acquires (or creates) the QID-level lock for person merge operations.</summary>
    public SemaphoreSlim GetQidLock(string qid)
        => _qidLocks.GetOrAdd(qid, _ => new SemaphoreSlim(1, 1));

    /// <summary>Acquires (or creates) the person-level lock for find-or-create operations.</summary>
    public SemaphoreSlim GetPersonLock(string personKey)
        => _personLocks.GetOrAdd(personKey, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Returns (creating if needed) the lock that serializes artwork persistence for one owner entity.
    /// Prefer <see cref="AcquireArtworkOwnerLockAsync"/>, which also survives a concurrent <see cref="Cleanup"/>.
    /// </summary>
    public SemaphoreSlim GetArtworkOwnerLock(Guid ownerEntityId)
        => _artworkOwnerLocks.GetOrAdd(ownerEntityId.ToString("N"), _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Acquires the artwork-owner lock and returns a releaser. If <see cref="Cleanup"/> evicted the
    /// semaphore between lookup and acquisition, the acquisition is retried on the current instance
    /// so two callers can never hold "the same" owner lock through different semaphores.
    /// </summary>
    public async Task<IDisposable> AcquireArtworkOwnerLockAsync(Guid ownerEntityId, CancellationToken ct)
    {
        var key = ownerEntityId.ToString("N");
        while (true)
        {
            var semaphore = _artworkOwnerLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            if (_artworkOwnerLocks.TryGetValue(key, out var current) && ReferenceEquals(current, semaphore))
            {
                return new SemaphoreReleaser(semaphore);
            }

            semaphore.Release();
        }
    }

    private sealed class SemaphoreReleaser(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }

    /// <summary>
    /// Removes all semaphores that are not currently held (CurrentCount == 1).
    /// Safe to call periodically to prevent unbounded dictionary growth.
    /// Returns the number of entries removed.
    /// </summary>
    public int Cleanup()
    {
        int removed = 0;
        removed += CleanupDictionary(_folderLocks);
        removed += CleanupDictionary(_hashLocks);
        removed += CleanupDictionary(_qidLocks);
        removed += CleanupDictionary(_personLocks);
        removed += CleanupDictionary(_artworkOwnerLocks);
        return removed;
    }

    /// <summary>Current number of tracked locks across all categories (for diagnostics).</summary>
    public int TotalTrackedLocks =>
        _folderLocks.Count + _hashLocks.Count + _qidLocks.Count + _personLocks.Count + _artworkOwnerLocks.Count;

    private static int CleanupDictionary(ConcurrentDictionary<string, SemaphoreSlim> dict)
    {
        int removed = 0;
        foreach (var key in dict.Keys)
        {
            if (dict.TryGetValue(key, out var sem) && sem.CurrentCount == 1)
            {
                if (dict.TryRemove(key, out _))
                {
                    removed++;
                }
            }
        }
        return removed;
    }
}
