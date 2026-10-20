namespace MediaEngine.Web.Services.Integration;

/// <summary>Why the Engine did or did not answer a request to delete titles from the library.</summary>
public enum LibraryItemDeletionOutcomeStatus
{
    /// <summary>The Engine answered.</summary>
    Ok,

    /// <summary>The Engine refused: the signed-in person is not an administrator, or their administrator PIN is locked.</summary>
    Forbidden,

    /// <summary>One or more of the titles are no longer in the library.</summary>
    NotFound,

    /// <summary>The Engine did not accept the request (nothing chosen, too many titles, or a group instead of a single title).</summary>
    Invalid,

    /// <summary>The Engine could not be reached or failed.</summary>
    Unavailable,

    /// <summary>The plan changed since the preview, so nothing was deleted. <see cref="LibraryItemDeletionOutcome{T}.FreshPreview"/> holds the updated one.</summary>
    Conflict,
}

/// <summary>
/// An answer about deleting titles, or the reason there is none. <see cref="Message"/> is the Engine's plain sentence for a refusal.
/// <see cref="FreshPreview"/> is set only for <see cref="LibraryItemDeletionOutcomeStatus.Conflict"/>.
/// </summary>
public sealed record LibraryItemDeletionOutcome<T>(
    T? Value,
    LibraryItemDeletionOutcomeStatus Status,
    string? Message = null,
    MediaEngine.Contracts.Items.LibraryItemDeletionPreview? FreshPreview = null)
{
    public bool IsOk => Status == LibraryItemDeletionOutcomeStatus.Ok && Value is not null;

    public static LibraryItemDeletionOutcome<T> Ok(T value) => new(value, LibraryItemDeletionOutcomeStatus.Ok);

    public static LibraryItemDeletionOutcome<T> Failed(LibraryItemDeletionOutcomeStatus status, string? message = null) => new(default, status, message);

    /// <summary>The Engine changed nothing because the plan is out of date; carries the updated preview.</summary>
    public static LibraryItemDeletionOutcome<T> Conflict(MediaEngine.Contracts.Items.LibraryItemDeletionPreview freshPreview, string? message = null) =>
        new(default, LibraryItemDeletionOutcomeStatus.Conflict, message, freshPreview);
}
