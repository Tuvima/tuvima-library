using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Contracts;

/// <summary>One source folder of a library, as the activity read needs it: its id and its path.</summary>
public sealed record LibraryActivitySource(Guid SourceId, string Path);

/// <summary>The first check of one folder of a library (the nearest thing the Engine records to a scan of a new folder).</summary>
public sealed record LibraryActivityFolderCheck(
    string SourceId,
    string Path,
    string State,
    int Tried,
    int Unmatched,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt);

/// <summary>What one organising job (a rename, a merge or a change of what a folder is) did to the files of one folder.</summary>
public sealed record LibraryActivityOrganiseRun(
    Guid OperationId,
    Guid SourceId,
    int Moved,
    int Copied,
    int KindChanged,
    int Failed,
    DateTimeOffset LastAt);

/// <summary>
/// Reads, for one library, the events the Engine already records (Library pages D16): no new storage. A library's events are found by
/// the library id of the files they are about, by the library's source folders, or by the library id the job was started for, never by
/// a title search, so another library's events cannot appear. Short-lived connection per call.
/// </summary>
public interface ILibraryActivityReadRepository
{
    /// <summary>
    /// The newest activity rows about this library: titles added or sent to review (by the files' library), failed files (by their folder)
    /// and folders changed to or from this library. Newest first, at most <paramref name="take"/>.
    /// </summary>
    Task<IReadOnlyList<SystemActivityEntry>> GetEntriesAsync(
        Guid libraryId, IReadOnlyList<LibraryActivitySource> sources, int take, CancellationToken ct = default);

    /// <summary>The first checks of this library's folders, newest first, at most <paramref name="take"/>.</summary>
    Task<IReadOnlyList<LibraryActivityFolderCheck>> GetFolderChecksAsync(Guid libraryId, int take, CancellationToken ct = default);

    /// <summary>The organising jobs that ran on these folders, newest first, at most <paramref name="take"/>.</summary>
    Task<IReadOnlyList<LibraryActivityOrganiseRun>> GetOrganiseRunsAsync(
        IReadOnlyCollection<Guid> sourceIds, int take, CancellationToken ct = default);
}
