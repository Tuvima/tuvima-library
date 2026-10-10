using MediaEngine.Contracts.Review;
using MediaEngine.Domain.Constants;

namespace MediaEngine.Api.Services.Review;

public enum ReviewMoveToTvOutcome
{
    Moved,
    ReviewItemNotFound,
    NotPending,
    WrongTrigger,
    SuggestionMissing,
    AssetMissing,
    NoTvLibrary,
    CannotReassign,
}

/// <summary>Result of <see cref="IReviewMoveToTvService.MoveAsync"/>; <see cref="Response"/> is set only when moved.</summary>
public sealed record ReviewMoveToTvResult(
    ReviewMoveToTvOutcome Outcome,
    string? Message = null,
    ReviewMoveToTvResponse? Response = null);

/// <summary>
/// Applies the "Move to TV" decision on a <see cref="ReviewTrigger.MovieMatchedAsTv"/> review item.
/// </summary>
public interface IReviewMoveToTvService
{
    Task<ReviewMoveToTvResult> MoveAsync(Guid reviewItemId, string resolvedBy, CancellationToken ct = default);
}
