using MediaEngine.Domain.Models;

namespace MediaEngine.Domain.Contracts;

/// <summary>Read-only facts the Engine needs to preview deleting titles from the library.</summary>
public interface ILibraryItemDeletionRepository
{
    /// <summary>
    /// Loads the requested Works that exist, each with every media file under its editions. A Work with no files is still
    /// returned. Ids that are not Works are simply absent from the answer.
    /// </summary>
    Task<IReadOnlyList<LibraryItemDeletionWork>> GetWorksAsync(
        IReadOnlyList<Guid> workIds,
        CancellationToken ct = default);

    /// <summary>
    /// Hand-picked collections that hold any requested title, how many personal profiles have state on the titles, and
    /// whether processing is still running for them. Automatic (Smart) collections and Series or Universe links are never included.
    /// </summary>
    Task<LibraryItemDeletionRelatedFacts> GetRelatedFactsAsync(
        IReadOnlyList<Guid> workIds,
        CancellationToken ct = default);

    /// <summary>
    /// Of the given file paths, returns those that are also the file of a media asset belonging to a Work outside the request
    /// (compared without regard to case). Such a file must never be deleted on behalf of the request.
    /// </summary>
    Task<IReadOnlySet<string>> FindPathsUsedOutsideRequestAsync(
        IReadOnlyList<Guid> workIds,
        IReadOnlyCollection<string> paths,
        CancellationToken ct = default);
}
