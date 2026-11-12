using MediaEngine.Contracts.Editing;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// Checks and writes one kind of pending change (see <see cref="EditSessionChangeKinds"/>). A handler reuses the Engine's
/// existing write path for its kind; it never invents a second one.
/// </summary>
public interface IEditSessionChangeHandler
{
    /// <summary>The <see cref="EditSessionChangeKinds"/> value this handler writes.</summary>
    string Kind { get; }

    /// <summary>
    /// Checks the change without writing. Returns null when it can be applied, otherwise the plain-English reason it cannot
    /// (the target is gone, a value is not allowed, a name is taken, the folder is read-only).
    /// </summary>
    Task<string?> CheckAsync(EditSessionChange change, CancellationToken ct = default);

    /// <summary>
    /// Writes the change. Throws when the write fails. For a data change the result is how to put things back exactly as
    /// they were, so a later failure in the same unit can undo it; file moves return null because a move cannot be undone.
    /// </summary>
    Task<Func<CancellationToken, Task>?> ApplyAsync(EditSessionChange change, CancellationToken ct = default);
}

/// <summary>
/// A handler whose change also moves the item's file when the person was shown that move (universal editor v6, D24): the session passes
/// <paramref name="moveFile"/> as true when the same save holds a file-move change for the item that says it travels with this change
/// (<see cref="VideoSeriesMovePairs"/> checks that it names this very change and the choice it was shown with). Without one, for example a video dragged between sections in the order list,
/// the file stays where it is. The plain <see cref="IEditSessionChangeHandler"/> calls mean "do not move the file".
/// </summary>
public interface IFileFollowingEditSessionChangeHandler : IEditSessionChangeHandler
{
    Task<string?> CheckAsync(EditSessionChange change, bool moveFile, CancellationToken ct = default);

    Task<Func<CancellationToken, Task>?> ApplyAsync(EditSessionChange change, bool moveFile, CancellationToken ct = default);
}
