using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Services;

namespace MediaEngine.Api.Endpoints;

public static partial class MetadataEndpoints
{
    private const int SeriesPlacementCandidateLimit = 20;
    private const int SeriesPlacementCandidateFetch = 50;

    /// <summary>
    /// The editor's Matching Series block: which Series a Work is on, which Series list it, and a person's locked choice.
    /// The route id may be the Work, one of its Editions or one of its files (the editor launches from any of them).
    /// </summary>
    private static void MapSeriesPlacementEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/{entityId:guid}/series-placement", async (
            Guid entityId,
            string? q,
            HttpContext http,
            CatalogueResourceAuthorizationService resources,
            ISeriesPlacementRepository placements,
            CancellationToken ct) =>
        {
            var dto = await BuildSeriesPlacementAsync(
                placements, entityId, q, shelfId => CanSeeSeriesShelfAsync(http, resources, shelfId, ct), ct).ConfigureAwait(false);
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
            HttpContext http,
            CatalogueResourceAuthorizationService resources,
            ISeriesPlacementRepository placements,
            SeriesPlacementChoiceService choices,
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
                // A Series the caller may not see is as good as missing.
                if (request.CollectionId is { } shelfId
                    && !await CanSeeSeriesShelfAsync(http, resources, shelfId, ct).ConfigureAwait(false))
                {
                    return ApiErrors.NotFound("Series not found.");
                }

                var result = await choices.ChooseAsync(workId.Value, request.CollectionId, ct).ConfigureAwait(false);
                switch (result)
                {
                    case SeriesPlacementChoiceResult.WorkNotFound:
                        return ApiErrors.NotFound($"Work for {entityId} not found.");
                    case SeriesPlacementChoiceResult.ShelfNotFound:
                        return ApiErrors.NotFound("Series not found.");
                    case SeriesPlacementChoiceResult.NotASeriesShelfOfThisMediaType:
                        return ApiErrors.BadRequest("collection_id must be a Series of the same media type as this item.");
                }
            }
            else if (await choices.ReleaseAsync(workId.Value, ct).ConfigureAwait(false) == SeriesPlacementReleaseResult.PlacementFailed)
            {
                return ApiErrors.Problem(
                    StatusCodes.Status500InternalServerError,
                    "Series placement failed",
                    "The choice was released but the Library could not place this item again. It will be placed on the next scan.");
            }

            var dto = await BuildSeriesPlacementAsync(
                placements, entityId, q: null, shelfId => CanSeeSeriesShelfAsync(http, resources, shelfId, ct), ct).ConfigureAwait(false);
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

    /// <summary>A Series shelf is visible when the caller may see it the way the collection reads decide (the same resource scope).</summary>
    private static async Task<bool> CanSeeSeriesShelfAsync(
        HttpContext http,
        CatalogueResourceAuthorizationService resources,
        Guid shelfId,
        CancellationToken ct)
        => await resources.EvaluateEntityAsync(http, "Collection", shelfId, ApplicationPermissionIds.LibraryRead, ct).ConfigureAwait(false)
            == CatalogueResourceAccess.Allowed;

    private static async Task<SeriesPlacementDto?> BuildSeriesPlacementAsync(
        ISeriesPlacementRepository placements,
        Guid entityId,
        string? q,
        Func<Guid, Task<bool>> canSeeShelf,
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

        // Ask for more than will be shown: shelves the caller may not see are dropped, and the rest are cut to the limit.
        var view = await placements.GetViewAsync(workId.Value, q, SeriesPlacementCandidateFetch, ct).ConfigureAwait(false);
        if (view is null)
        {
            return null;
        }

        var current = view.Current is { } now && await canSeeShelf(now.CollectionId).ConfigureAwait(false) ? now : null;
        var listed = await VisibleAsync(view.Listed, canSeeShelf, int.MaxValue).ConfigureAwait(false);
        var candidates = await VisibleAsync(view.Candidates, canSeeShelf, SeriesPlacementCandidateLimit).ConfigureAwait(false);
        return new SeriesPlacementDto
        {
            WorkId = view.WorkId,
            MediaType = view.MediaType,
            SupportsSeries = true,
            Current = current is null ? null : ToContract(current),
            IsPersonChoice = view.IsPersonChoice,
            Listed = listed.Select(ToContract).ToList(),
            Candidates = candidates.Select(ToContract).ToList(),
        };
    }

    private static async Task<List<SeriesPlacementShelf>> VisibleAsync(
        IReadOnlyList<SeriesPlacementShelf> shelves,
        Func<Guid, Task<bool>> canSeeShelf,
        int limit)
    {
        var visible = new List<SeriesPlacementShelf>();
        foreach (var shelf in shelves)
        {
            if (visible.Count >= limit)
            {
                break;
            }

            if (await canSeeShelf(shelf.CollectionId).ConfigureAwait(false))
            {
                visible.Add(shelf);
            }
        }

        return visible;
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
