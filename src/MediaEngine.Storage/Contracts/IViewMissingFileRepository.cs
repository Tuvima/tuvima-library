namespace MediaEngine.Storage.Contracts;

/// <summary>One place a file is indexed at (a row of <c>local_file_sources</c>) and whether it is marked missing.</summary>
public sealed record ViewIndexedPlace(
    Guid Id,
    Guid FileId,
    Guid LibraryId,
    Guid SourceId,
    string FilePath,
    long ByteSize,
    DateTimeOffset? MissingSince);

/// <summary>
/// Files that disappeared outside Library (Safer deleting in View). A place is marked missing, never deleted; an item reads as Missing
/// when none of its viewable files has a place left (D6), and is purged only after the retention setting (D8).
/// </summary>
public interface IViewMissingFileRepository
{
    /// <summary>Every place indexed under one folder.</summary>
    Task<IReadOnlyList<ViewIndexedPlace>> GetPlacesAsync(Guid sourceId, CancellationToken ct = default);

    /// <summary>
    /// Marks the given places missing (from <paramref name="now"/>, unless already marked) and clears the mark on the others, then works out
    /// again which items are Missing. Returns how many items changed state.
    /// </summary>
    Task<int> ApplyAsync(
        IReadOnlyCollection<Guid> markMissing, IReadOnlyCollection<Guid> clearMissing, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Records whether a folder could be reached at the last look (D4).</summary>
    Task SetFolderReachableAsync(Guid sourceId, bool reachable, CancellationToken ct = default);

    /// <summary>Items that went missing at or before <paramref name="cutoff"/> and whose folders are all reachable.</summary>
    Task<IReadOnlyList<Guid>> GetPurgeCandidatesAsync(DateTimeOffset cutoff, int limit, CancellationToken ct = default);

    /// <summary>
    /// Removes one Missing item and everything of it (notes, tags, Gallery spots, search entries) in one transaction. The item is checked
    /// again first; false means it came back or is no longer eligible and nothing was removed. No file on disk is touched.
    /// </summary>
    Task<bool> PurgeItemAsync(Guid itemId, DateTimeOffset cutoff, CancellationToken ct = default);
}
