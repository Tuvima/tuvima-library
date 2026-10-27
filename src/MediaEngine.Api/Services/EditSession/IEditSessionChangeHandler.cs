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
