using MediaEngine.Contracts.Matching;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Api.Services.Review;

/// <summary>The outcome of applying one Needs Review choice: it worked, or why not (an HTTP-style status and a plain sentence).</summary>
public sealed record ReviewChoiceApplyResult(bool Succeeded, int Status = StatusCodes.Status200OK, string? Message = null)
{
    public static ReviewChoiceApplyResult Ok { get; } = new(true);

    public static ReviewChoiceApplyResult Failed(int status, string message) => new(false, status, message);
}

/// <summary>
/// The three writes a Needs Review choice can run. Each is the existing editor write (the retail match, the Series placement, the media
/// type change), so this seam only exists to keep the choose action testable.
/// </summary>
public interface IReviewChoiceApplier
{
    /// <summary>Applies a retail match the way the editor's Match does (identity, details, artwork, and the parent move where the match names one).</summary>
    Task<ReviewChoiceApplyResult> ApplyRetailMatchAsync(Guid entityId, ReplaceRetailMatchRequestDto request, CancellationToken ct);

    /// <summary>Places the item's Work on the chosen Series and locks the choice, as the editor's Series placement does.</summary>
    Task<ReviewChoiceApplyResult> PlaceOnSeriesAsync(Guid entityId, Guid collectionId, CancellationToken ct);

    /// <summary>Files the item under the chosen library type, as the reclassify action does.</summary>
    Task<ReviewChoiceApplyResult> ReclassifyAsync(Guid entityId, MediaType mediaType, CancellationToken ct);
}
