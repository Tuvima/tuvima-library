namespace MediaEngine.Web.Services.Integration;

/// <summary>Why the Engine did or did not answer an Activity request.</summary>
public enum PersonalActivityOutcomeStatus
{
    /// <summary>The Engine answered.</summary>
    Ok,

    /// <summary>The Engine would answer once the administrator PIN has been entered.</summary>
    UnlockRequired,

    /// <summary>The Engine refused: the signed-in person may not see or do this.</summary>
    Forbidden,

    /// <summary>The person or household asked for no longer exists.</summary>
    NotFound,

    /// <summary>The Engine did not accept the filters (for example a date range that ends before it starts).</summary>
    Invalid,

    /// <summary>The Engine could not be reached or failed.</summary>
    Unavailable,
}

/// <summary>An Activity answer, or the reason there is none. <see cref="Message"/> is the Engine's plain sentence for a refusal.</summary>
public sealed record PersonalActivityOutcome<T>(T? Value, PersonalActivityOutcomeStatus Status, string? Message = null)
{
    public bool IsOk => Status == PersonalActivityOutcomeStatus.Ok && Value is not null;

    public static PersonalActivityOutcome<T> Ok(T value) => new(value, PersonalActivityOutcomeStatus.Ok);

    public static PersonalActivityOutcome<T> Failed(PersonalActivityOutcomeStatus status, string? message = null) => new(default, status, message);
}
