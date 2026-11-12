using MediaEngine.Contracts.Review;

namespace MediaEngine.Api.Services.Review;

/// <summary>How picking a choice in Needs Review went.</summary>
public enum ReviewChooseOutcome
{
    /// <summary>The choice was applied and the review item is resolved.</summary>
    Chosen,

    /// <summary>No such review item (or it is not one a caller may touch).</summary>
    ReviewItemNotFound,

    /// <summary>The review item is no longer waiting, so there is nothing to choose.</summary>
    NotPending,

    /// <summary>The choice id is not one the list offers for this item.</summary>
    UnknownChoice,

    /// <summary>The choice was offered but applying it failed; nothing was resolved. <see cref="ReviewChooseResult.FailureStatus"/> and the message say why.</summary>
    ApplyFailed,
}

/// <summary>The result of <see cref="IReviewChooseService.ChooseAsync"/>.</summary>
public sealed record ReviewChooseResult(
    ReviewChooseOutcome Outcome,
    string? Message = null,
    int FailureStatus = StatusCodes.Status500InternalServerError,
    ReviewChooseResponse? Response = null);

/// <summary>
/// Picks one of the choices Needs Review offers on a row (Needs Review redesign D11): a retail match, a Series, or a library type. It
/// applies the pick through the very services the editor uses, so the result is exactly what picking it in the editor would give.
/// </summary>
public interface IReviewChooseService
{
    Task<ReviewChooseResult> ChooseAsync(Guid reviewItemId, string choiceId, string resolvedBy, CancellationToken ct = default);
}
