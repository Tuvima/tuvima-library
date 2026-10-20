using MediaEngine.Contracts.Library;

namespace MediaEngine.Web.Services.Integration;

/// <summary>Why the Engine did or did not answer a request to move items into an album, season, series, audiobook or collection.</summary>
public enum LibraryMoveOutcomeStatus
{
    /// <summary>The Engine answered.</summary>
    Ok,

    /// <summary>The Engine refused: the signed-in person may not change these items (match and write permission are both needed).</summary>
    Forbidden,

    /// <summary>An item or the destination is no longer in the library.</summary>
    NotFound,

    /// <summary>The Engine did not accept the request (wrong kind, nothing chosen, an unmatched choice).</summary>
    Invalid,

    /// <summary>The preview expired or the editing session changed; the person reviews the move again.</summary>
    Expired,

    /// <summary>The Engine could not be reached or failed.</summary>
    Unavailable,

    /// <summary>Something changed since the preview, so nothing was moved. <see cref="LibraryMoveOutcome{T}.FreshPreview"/> holds the updated one.</summary>
    Conflict,
}

/// <summary>
/// An answer about moving items, or the reason there is none. <see cref="Message"/> is the Engine's plain sentence for a refusal.
/// <see cref="FreshPreview"/> is set only for <see cref="LibraryMoveOutcomeStatus.Conflict"/>.
/// </summary>
public sealed record LibraryMoveOutcome<T>(
    T? Value,
    LibraryMoveOutcomeStatus Status,
    string? Message = null,
    LibraryMovePreview? FreshPreview = null)
{
    public bool IsOk => Status == LibraryMoveOutcomeStatus.Ok && Value is not null;

    public static LibraryMoveOutcome<T> Ok(T value) => new(value, LibraryMoveOutcomeStatus.Ok);

    public static LibraryMoveOutcome<T> Failed(LibraryMoveOutcomeStatus status, string? message = null) => new(default, status, message);

    /// <summary>The Engine moved nothing because the preview is out of date; carries the updated preview.</summary>
    public static LibraryMoveOutcome<T> Conflict(LibraryMovePreview freshPreview, string? message = null) =>
        new(default, LibraryMoveOutcomeStatus.Conflict, message, freshPreview);
}
