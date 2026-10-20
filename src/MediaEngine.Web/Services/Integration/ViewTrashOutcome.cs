namespace MediaEngine.Web.Services.Integration;

/// <summary>Why the Engine did or did not answer a question about the signed-in person's View Trash.</summary>
public enum ViewTrashOutcomeStatus
{
    /// <summary>The Engine answered.</summary>
    Ok,

    /// <summary>The Engine refused: this is not the person's own space, the caller is a paired app or service, or View is not allowed.</summary>
    Forbidden,

    /// <summary>The Engine could not be reached or failed.</summary>
    Unavailable,
}

/// <summary>An answer about the signed-in person's Trash, or the reason there is none. <see cref="Message"/> is the Engine's plain sentence for a refusal.</summary>
public sealed record ViewTrashOutcome<T>(T? Value, ViewTrashOutcomeStatus Status, string? Message = null)
{
    public bool IsOk => Status == ViewTrashOutcomeStatus.Ok && Value is not null;

    public static ViewTrashOutcome<T> Ok(T value) => new(value, ViewTrashOutcomeStatus.Ok);

    public static ViewTrashOutcome<T> Failed(ViewTrashOutcomeStatus status, string? message = null) => new(default, status, message);
}
