namespace MediaEngine.Domain.Contracts;

/// <summary>
/// The explicit "no outside contact" check every background worker and sweep makes before it asks a provider about a file
/// (Library setup spec D38). A library whose kind is plain (Other videos, Other audio, Other reading), or whose metadata policy is
/// local-only or manual, is never looked up, so a worker does not rely only on "no identity job was ever created for it".
/// A question the guard cannot answer is answered "blocked": a library whose settings cannot be read is never contacted about.
/// </summary>
public interface IExternalLookupGuard
{
    /// <summary>True when nothing may be asked of any outside provider about files in this library. No library (a file outside every library) is not blocked.</summary>
    bool BlocksLibrary(string? libraryId);

    /// <summary>
    /// The ids of every library that blocks outside lookup, read fresh, for a sweep that selects its own files and must leave these
    /// out of the selection (so they cannot crowd the batch). Throws when the library settings cannot be read, which stops the sweep.
    /// </summary>
    IReadOnlyCollection<string> GetBlockedLibraryIds();

    /// <summary>True when the media asset, or the work (through its first file), sits in a library that blocks outside lookup. An id that is neither is not blocked.</summary>
    Task<bool> BlocksEntityAsync(Guid entityId, CancellationToken ct = default);
}
