using MediaEngine.Domain.Models;

namespace MediaEngine.Domain.Contracts;

/// <summary>The facts the Engine needs to preview deleting titles from the library, and the one write that removes their records.</summary>
public interface ILibraryItemDeletionRepository
{
    /// <summary>
    /// Loads the requested Works that exist, each with every media file under its editions. A Work with no files is still
    /// returned. Ids that are not Works are simply absent from the answer.
    /// </summary>
    Task<IReadOnlyList<LibraryItemDeletionWork>> GetWorksAsync(
        IReadOnlyList<Guid> workIds,
        CancellationToken ct = default,
        bool vaultItems = false);

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

    /// <summary>
    /// Stops processing for the titles: every identification or enrichment job that is queued or running for a title or one of its
    /// files is marked failed, so no worker picks it up. A job a worker is running right now cannot be interrupted, so this waits
    /// (up to about 30 seconds) for it to let go, then carries on and logs a warning naming the jobs that did not. The jobs themselves
    /// are removed by <see cref="DeleteWorksAsync"/>.
    /// </summary>
    Task CancelProcessingAsync(
        IReadOnlyList<Guid> workIds,
        CancellationToken ct = default);

    /// <summary>
    /// In one transaction, removes the titles (Works), their editions and media assets and every record tied to them: chosen values,
    /// metadata claims, jobs, Needs Review rows, artwork links and the artwork only they used, search entries, saved items and
    /// ratings, every profile's progress, the titles' own activity history, and hand-picked collections left empty. Personal activity
    /// history (<c>personal_activity</c>) is never touched. Returns what was removed.
    /// </summary>
    Task<LibraryItemDeletionDataResult> DeleteWorksAsync(
        IReadOnlyList<Guid> workIds,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves the id of a show, season, album (a parent Work) or a Series (a collection) to the titles the library owns inside it.
    /// Null when the id is neither, or when it is a Vault group (Vault titles are never reachable from here). Reads only.
    /// </summary>
    Task<LibraryItemDeletionGroup?> ResolveGroupAsync(Guid groupId, CancellationToken ct = default);
}
