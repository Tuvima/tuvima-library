namespace MediaEngine.Web.Services.Integration;

/// <summary>Why a batch edit call has no answer.</summary>
public enum LibraryBatchEditOutcomeStatus
{
    /// <summary>The Engine answered.</summary>
    Ok,

    /// <summary>The Engine refused: the signed-in person may not batch edit.</summary>
    Forbidden,

    /// <summary>The Engine did not accept the request (too many items, an unknown field or a wrong operation).</summary>
    Invalid,

    /// <summary>The Engine could not be reached or failed.</summary>
    Unavailable,
}

/// <summary>An answer about a batch edit, or the reason there is none. <see cref="Message"/> is the Engine's plain sentence for a refusal.</summary>
public sealed record LibraryBatchEditOutcome<T>(T? Value, LibraryBatchEditOutcomeStatus Status, string? Message = null)
{
    public bool IsOk => Status == LibraryBatchEditOutcomeStatus.Ok && Value is not null;

    public static LibraryBatchEditOutcome<T> Ok(T value) => new(value, LibraryBatchEditOutcomeStatus.Ok);

    public static LibraryBatchEditOutcome<T> Failed(LibraryBatchEditOutcomeStatus status, string? message = null) => new(default, status, message);
}
