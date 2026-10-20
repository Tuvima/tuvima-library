using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;

namespace MediaEngine.Api.Endpoints;

public static partial class MetadataEndpoints
{
    private const int SeriesPlacementCandidateLimit = 20;

    /// <summary>
    /// The editor's Matching Series block: which Series a Work is on, which Series list it, and a person's locked choice.
    /// The route id may be the Work, one of its Editions or one of its files (the editor launches from any of them).
    /// </summary>
    private static void MapSeriesPlacementEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/{entityId:guid}/series-placement", async (
            Guid entityId,
            string? q,
            ISeriesPlacementRepository placements,
            CancellationToken ct) =>
        {
            var dto = await BuildSeriesPlacementAsync(placements, entityId, q, ct).ConfigureAwait(false);
            return dto is null
                ? ApiErrors.NotFound($"Work for {entityId} not found.")
                : Results.Ok(dto);
        })
        .WithName("GetSeriesPlacement")
        .WithSummary("Return the Series a Work is on, the Series that list it, other Series of its media type, and whether a person chose the placement.")
        .Produces<SeriesPlacementDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead)
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead);

        group.MapPut("/{entityId:guid}/series-placement", async (
            Guid entityId,
            SeriesPlacementChoiceRequestDto request,
            ISeriesPlacementRepository placements,
            CollectionFinalizationService finalization,
            SeriesAmbiguityReviewer ambiguityReviewer,
            CancellationToken ct) =>
        {
            var workId = await placements.ResolveWorkIdAsync(entityId, ct).ConfigureAwait(false);
            if (workId is null)
            {
                return ApiErrors.NotFound($"Work for {entityId} not found.");
            }

            var mediaType = await placements.GetWorkMediaTypeAsync(workId.Value, ct).ConfigureAwait(false);
            if (!SeriesPlacementRule.UsesSeriesShelves(mediaType))
            {
                return ApiErrors.BadRequest("This kind of item is not placed on Series shelves.");
            }

            if (request.Locked)
            {
                var result = await placements.SetChoiceAsync(workId.Value, request.CollectionId, ct).ConfigureAwait(false);
                switch (result)
                {
                    case SeriesPlacementChoiceResult.WorkNotFound:
                        return ApiErrors.NotFound($"Work for {entityId} not found.");
                    case SeriesPlacementChoiceResult.ShelfNotFound:
                    case SeriesPlacementChoiceResult.NotASeriesShelfOfThisMediaType:
                        return ApiErrors.BadRequest("collection_id must be a Series of the same media type as this item.");
                }

                // The person has answered "which Series?", so any open question about this Work is closed.
                await ambiguityReviewer.ResolveAsync(workId.Value, ct).ConfigureAwait(false);
            }
            else
            {
                // Let the Library decide again: release the choice, then run placement for this Work from scratch.
                await placements.ReleaseChoiceAsync(workId.Value, ct).ConfigureAwait(false);
                var entities = await placements.GetReviewEntitiesAsync(workId.Value, ct).ConfigureAwait(false);
                if (entities.StableAssetId is { } assetId)
                {
                    var outcome = await finalization
                        .FinalizeAsync(assetId, CollectionFinalizationReason.PersonReleasedSeriesChoice, ct)
                        .ConfigureAwait(false);
                    if (outcome.Assignment.Outcome == CollectionAssignmentOutcome.Failed)
                    {
                        return ApiErrors.Problem(
                            StatusCodes.Status500InternalServerError,
                            "Series placement failed",
                            "The choice was released but the Library could not place this item again. It will be placed on the next scan.");
                    }
                }
            }

            var dto = await BuildSeriesPlacementAsync(placements, entityId, q: null, ct).ConfigureAwait(false);
            return dto is null
                ? ApiErrors.NotFound($"Work for {entityId} not found.")
                : Results.Ok(dto);
        })
        .WithName("SetSeriesPlacement")
        .WithSummary("Save a person's Series choice as a lock the Engine always respects, or release it so the Library decides again.")
        .Produces<SeriesPlacementDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite)
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite);
    }

    private static async Task<SeriesPlacementDto?> BuildSeriesPlacementAsync(
        ISeriesPlacementRepository placements,
        Guid entityId,
        string? q,
        CancellationToken ct)
    {
        var workId = await placements.ResolveWorkIdAsync(entityId, ct).ConfigureAwait(false);
        if (workId is null)
        {
            return null;
        }

        var mediaType = await placements.GetWorkMediaTypeAsync(workId.Value, ct).ConfigureAwait(false);
        if (mediaType is null)
        {
            return null;
        }

        if (!SeriesPlacementRule.UsesSeriesShelves(mediaType))
        {
            return new SeriesPlacementDto { WorkId = workId.Value, MediaType = mediaType, SupportsSeries = false };
        }

        var view = await placements.GetViewAsync(workId.Value, q, SeriesPlacementCandidateLimit, ct).ConfigureAwait(false);
        return view is null
            ? null
            : new SeriesPlacementDto
            {
                WorkId = view.WorkId,
                MediaType = view.MediaType,
                SupportsSeries = true,
                Current = view.Current is null ? null : ToContract(view.Current),
                IsPersonChoice = view.IsPersonChoice,
                Listed = view.Listed.Select(ToContract).ToList(),
                Candidates = view.Candidates.Select(ToContract).ToList(),
            };
    }

    private static SeriesPlacementShelfDto ToContract(SeriesPlacementShelf shelf) => new()
    {
        CollectionId = shelf.CollectionId,
        Label = shelf.Label,
        SeriesQid = shelf.SeriesQid,
        MediaType = shelf.MediaType,
        Scope = shelf.Scope,
        PositionLabel = shelf.PositionLabel,
    };
}
