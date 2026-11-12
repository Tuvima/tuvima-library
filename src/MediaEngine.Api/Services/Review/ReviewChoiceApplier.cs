using MediaEngine.Api.Services.Matching;
using MediaEngine.Api.Services.Metadata;
using MediaEngine.Contracts.Matching;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Services;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Services.Review;

/// <inheritdoc/>
public sealed class ReviewChoiceApplier(
    RetailMatchWriter retailMatch,
    SeriesPlacementChoiceService seriesChoices,
    ISeriesPlacementRepository placements,
    MediaTypeReclassifyService reclassifier) : IReviewChoiceApplier
{
    public async Task<ReviewChoiceApplyResult> ApplyRetailMatchAsync(Guid entityId, ReplaceRetailMatchRequestDto request, CancellationToken ct)
    {
        // The same write as saving a match in the editor: the match is replaced, and nothing is moved to another parent by one click.
        var result = await retailMatch.ReplaceWithoutMovingAsync(entityId, request, ct).ConfigureAwait(false);
        var status = (result as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status200OK;
        if (status is >= 200 and < 300)
        {
            return ReviewChoiceApplyResult.Ok;
        }

        // The editor's own refusal (for example "Television retail matches must use TheTVDB") is the reason shown.
        var message = (result as IValueHttpResult)?.Value is ProblemDetails problem
            ? problem.Detail ?? problem.Title
            : null;
        return ReviewChoiceApplyResult.Failed(status, message ?? "That match could not be applied.");
    }

    public async Task<ReviewChoiceApplyResult> PlaceOnSeriesAsync(Guid entityId, Guid collectionId, CancellationToken ct)
    {
        // The route id may be the Work, one of its Editions or one of its files; the placement service resolves the Work.
        var workId = await placements.ResolveWorkIdAsync(entityId, ct).ConfigureAwait(false);
        if (workId is null)
        {
            return ReviewChoiceApplyResult.Failed(StatusCodes.Status404NotFound, "The book for this item was not found.");
        }

        var mediaType = await placements.GetWorkMediaTypeAsync(workId.Value, ct).ConfigureAwait(false);
        if (!SeriesPlacementRule.UsesSeriesShelves(mediaType))
        {
            return ReviewChoiceApplyResult.Failed(StatusCodes.Status400BadRequest, "This kind of item is not placed on Series shelves.");
        }

        var result = await seriesChoices.ChooseAsync(workId.Value, collectionId, ct).ConfigureAwait(false);
        return result switch
        {
            SeriesPlacementChoiceResult.Applied => ReviewChoiceApplyResult.Ok,
            SeriesPlacementChoiceResult.WorkNotFound => ReviewChoiceApplyResult.Failed(StatusCodes.Status404NotFound, "The book for this item was not found."),
            SeriesPlacementChoiceResult.ShelfNotFound => ReviewChoiceApplyResult.Failed(StatusCodes.Status404NotFound, "That Series was not found."),
            _ => ReviewChoiceApplyResult.Failed(StatusCodes.Status400BadRequest, "That Series is not a Series of the same kind as this item."),
        };
    }

    public async Task<ReviewChoiceApplyResult> ReclassifyAsync(Guid entityId, MediaType mediaType, CancellationToken ct)
    {
        await reclassifier.ReclassifyAsync(entityId, mediaType, ct).ConfigureAwait(false);
        return ReviewChoiceApplyResult.Ok;
    }
}
