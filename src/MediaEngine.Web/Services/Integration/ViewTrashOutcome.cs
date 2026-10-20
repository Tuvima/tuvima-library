namespace MediaEngine.Web.Services.Integration;

/// <summary>Why the Engine did or did not answer a question about the signed-in person's View Trash.</summary>
public enum ViewTrashOutcomeStatus
{
    /// <summary>The Engine answered.</summary>
    Ok,

    /// <summary>The Engine refused: this is not the person's own space, the caller is a paired app or service, or View is not allowed.</summary>
    Forbidden,

    /// <summary>The Engine could not be reached or failed. For Empty Trash this means the request never reached the Engine, so nothing was deleted.</summary>
    Unavailable,

    /// <summary>
    /// Empty Trash only: the request was sent but no answer came back (a timeout, a cancellation, a server error, or a connection that
    /// dropped after sending), so it is not known whether anything was deleted. Never offered a blind retry; the person looks at the
    /// Trash again, and reopening the dialog asks for a fresh summary.
    /// </summary>
    Unconfirmed,

    /// <summary>Empty Trash only: the summary the person confirmed is out of date, so nothing was deleted. <see cref="ViewTrashOutcome{T}.FreshPreview"/> holds the updated one.</summary>
    Conflict,
}

/// <summary>
/// An answer about the signed-in person's Trash, or the reason there is none. <see cref="Message"/> is the Engine's plain sentence for a
/// refusal. <see cref="FreshPreview"/> is set only for <see cref="ViewTrashOutcomeStatus.Conflict"/>.
/// </summary>
public sealed record ViewTrashOutcome<T>(
    T? Value,
    ViewTrashOutcomeStatus Status,
    string? Message = null,
    MediaEngine.Contracts.LocalAssets.ViewTrashEmptyPreviewDto? FreshPreview = null)
{
    public bool IsOk => Status == ViewTrashOutcomeStatus.Ok && Value is not null;

    public static ViewTrashOutcome<T> Ok(T value) => new(value, ViewTrashOutcomeStatus.Ok);

    public static ViewTrashOutcome<T> Failed(ViewTrashOutcomeStatus status, string? message = null) => new(default, status, message);

    /// <summary>The Engine changed nothing because the summary is out of date; carries the updated one.</summary>
    public static ViewTrashOutcome<T> Conflict(MediaEngine.Contracts.LocalAssets.ViewTrashEmptyPreviewDto freshPreview, string? message = null) =>
        new(default, ViewTrashOutcomeStatus.Conflict, message, freshPreview);
}
