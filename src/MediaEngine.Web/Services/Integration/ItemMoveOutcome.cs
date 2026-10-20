using MediaEngine.Contracts.Items;

namespace MediaEngine.Web.Services.Integration;

/// <summary>An Engine answer about moving a title: the answer, a refusal in the Engine's plain words, a changed plan, or no answer.</summary>
public sealed record ItemMoveOutcome<T>(
    T? Value,
    ItemMoveOutcomeStatus Status,
    string? Message = null,
    ItemMovePreview? FreshPreview = null)
{
    public bool IsOk => Status == ItemMoveOutcomeStatus.Ok && Value is not null;

    public static ItemMoveOutcome<T> Ok(T value) => new(value, ItemMoveOutcomeStatus.Ok);

    public static ItemMoveOutcome<T> Failed(ItemMoveOutcomeStatus status, string? message = null) => new(default, status, message);

    public static ItemMoveOutcome<T> Conflict(ItemMovePreview? fresh, string? message) =>
        new(default, ItemMoveOutcomeStatus.Conflict, message, fresh);
}

public enum ItemMoveOutcomeStatus
{
    Ok,

    /// <summary>The title cannot be moved, or the place does not take it (the Engine says why).</summary>
    Refused,

    /// <summary>This person or device may not edit.</summary>
    Forbidden,

    /// <summary>The title is no longer in the library.</summary>
    NotFound,

    /// <summary>The plan changed since the preview, so nothing moved; <see cref="ItemMoveOutcome{T}.FreshPreview"/> holds the updated one.</summary>
    Conflict,

    /// <summary>The Engine could not be reached or failed.</summary>
    Unavailable,
}
