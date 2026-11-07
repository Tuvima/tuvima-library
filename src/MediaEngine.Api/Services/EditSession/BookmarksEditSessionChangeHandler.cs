using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Editing;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// Saves a title's staged bookmark changes (universal editor v5 D37) with the rest of the session. It writes through
/// <see cref="EditorBookmarksService"/>, which checks that the person is an administrator and the title is not in the Vault, then applies every
/// operation in one transaction. The check plays the operations without writing, so Review and Save cannot disagree. If a later change in the
/// session fails, the undo puts the title's bookmarks back exactly as they were.
/// </summary>
public sealed class BookmarksEditSessionChangeHandler(EditorBookmarksService bookmarks) : IEditSessionChangeHandler
{
    public string Kind => EditSessionChangeKinds.Bookmarks;

    public Task<string?> CheckAsync(EditSessionChange change, CancellationToken ct = default) =>
        bookmarks.CheckAsync(change.EntityId, change.Bookmarks, ct);

    public async Task<Func<CancellationToken, Task>?> ApplyAsync(EditSessionChange change, CancellationToken ct = default)
    {
        if (change.Bookmarks is not { Ops.Count: > 0 } bookmarkChange)
        {
            throw new InvalidOperationException(EditorBookmarksService.NothingToChange);
        }

        return await bookmarks.ApplyAsync(change.EntityId, bookmarkChange, ct).ConfigureAwait(false);
    }
}
