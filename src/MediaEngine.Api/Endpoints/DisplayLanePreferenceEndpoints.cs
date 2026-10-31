using System.Security.Claims;
using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Collections;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// The Lanes settings (Collections and lists spec D10): an administrator orders the shared lanes at a place and switches
/// them off for everyone; every profile can hide a shared lane for itself and order the lanes of its own collections.
/// The place is home, watch, read or listen. A profile is never named in a request, so a call only ever touches the
/// profile on the caller's own claim.
/// </summary>
public static class DisplayLanePreferenceEndpoints
{
    public static IEndpointRouteBuilder MapDisplayLanePreferenceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/display/lanes")
            .WithTags("Display");

        group.MapGet("/{place}", async (
            string place,
            ClaimsPrincipal user,
            HttpContext httpContext,
            IProfileRepository profiles,
            CollectionLaneReadService lanes,
            IRequestAuthorityResolver authorities,
            CancellationToken ct) =>
        {
            if (ProfileId(user) is not { } profileId)
            {
                return Unauthenticated();
            }

            if (CollectionLanePolicy.LocationFor(place) is not { } location)
            {
                return UnknownPlace();
            }

            var profile = await profiles.GetByIdAsync(profileId, ct);
            var authority = await authorities.ResolveAsync(httpContext, ct);
            var canManage = authority.IsEffectiveAdministrator;
            var listings = await lanes.GetListingsAsync(profile, location, includeHiddenShared: canManage, ct);
            return Results.Ok(new DisplayLaneSettingsDto(
                place.Trim().ToLowerInvariant(),
                canManage,
                canManage ? listings.Shared.Select(ToDto).ToList() : [],
                listings.Mine.Select(ToDto).ToList(),
                listings.HiddenByMe.Select(ToDto).ToList()));
        })
            .WithName("GetDisplayLaneSettings")
            .WithSummary("Returns the lanes at one place: the shared lanes (for an administrator), the active profile's own and the ones it hid.")
            .Produces<DisplayLaneSettingsDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireClientScope(ClientApiScopes.LibraryRead);

        group.MapPut("/{place}/shared", async (
            string place,
            DisplaySharedLaneOrderRequest request,
            ClaimsPrincipal user,
            IProfileRepository profiles,
            CollectionLaneReadService lanes,
            [FromServices] ICollectionPlacementRepository placements,
            CancellationToken ct) =>
        {
            if (ProfileId(user) is not { } profileId)
            {
                return Unauthenticated();
            }

            if (CollectionLanePolicy.LocationFor(place) is not { } location)
            {
                return UnknownPlace();
            }

            if (request?.Lanes is null)
            {
                return ApiErrors.BadRequest("Send the lanes in the order they should appear.");
            }

            var profile = await profiles.GetByIdAsync(profileId, ct);
            var known = (await lanes.GetListingsAsync(profile, location, includeHiddenShared: true, ct))
                .Shared.Select(lane => lane.CollectionId).ToHashSet();
            if (request.Lanes.Any(lane => !known.Contains(lane.CollectionId)))
            {
                return ApiErrors.BadRequest("One of those lanes is not a shared lane at this place.");
            }

            await placements.SaveSharedOrderAsync(
                location,
                request.Lanes.DistinctBy(lane => lane.CollectionId).Select(lane => (lane.CollectionId, lane.Visible)).ToList(),
                ct);
            CollectionLanePolicy.MarkChanged();
            return Results.NoContent();
        })
            .WithName("SetDisplaySharedLanes")
            .WithSummary("Orders the shared lanes at one place and switches each on or off for everyone.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireAdministratorOrApplication(ApplicationPermissionIds.CollectionsWrite);

        group.MapPut("/{place}/mine", async (
            string place,
            DisplayOwnLaneOrderRequest request,
            ClaimsPrincipal user,
            IProfileRepository profiles,
            CollectionLaneReadService lanes,
            [FromServices] ICollectionPlacementRepository placements,
            CancellationToken ct) =>
        {
            if (ProfileId(user) is not { } profileId)
            {
                return Unauthenticated();
            }

            if (CollectionLanePolicy.LocationFor(place) is not { } location)
            {
                return UnknownPlace();
            }

            if (request?.CollectionIds is null)
            {
                return ApiErrors.BadRequest("Send your lanes in the order they should appear.");
            }

            var profile = await profiles.GetByIdAsync(profileId, ct);
            var own = (await lanes.GetListingsAsync(profile, location, includeHiddenShared: false, ct))
                .Mine.Select(lane => lane.CollectionId).ToHashSet();
            if (request.CollectionIds.Any(id => !own.Contains(id)))
            {
                return ApiErrors.BadRequest("One of those lanes is not one of your own at this place.");
            }

            await placements.SaveSharedOrderAsync(
                location,
                request.CollectionIds.Distinct().Select(id => (id, true)).ToList(),
                ct);
            CollectionLanePolicy.MarkChanged();
            return Results.NoContent();
        })
            .WithName("SetDisplayOwnLanes")
            .WithSummary("Orders the lanes of the active profile's own collections at one place.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireClientScope(ClientApiScopes.ProgressWrite);

        group.MapPut("/{place}/hidden/{collectionId:guid}", async (
            string place,
            Guid collectionId,
            DisplayLaneHideRequest request,
            ClaimsPrincipal user,
            [FromServices] ICollectionPlacementRepository placements,
            CancellationToken ct) =>
        {
            if (ProfileId(user) is not { } profileId)
            {
                return Unauthenticated();
            }

            if (CollectionLanePolicy.LocationFor(place) is not { } location)
            {
                return UnknownPlace();
            }

            if (request is null)
            {
                return ApiErrors.BadRequest("Say whether to hide the lane.");
            }

            // Only a lane that really is shown at this place can be hidden, so the table never fills with made-up ids.
            if (!(await placements.GetByLocationAsync(location, ct)).Any(placement => placement.CollectionId == collectionId))
            {
                return ApiErrors.NotFound("That lane is not shown at this place.");
            }

            await placements.SetProfileHiddenAsync(profileId, collectionId, location, request.Hidden, ct);
            CollectionLanePolicy.MarkChanged();
            return Results.NoContent();
        })
            .WithName("SetDisplayLaneHidden")
            .WithSummary("Hides one lane from the active profile, or shows it again.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireClientScope(ClientApiScopes.ProgressWrite);

        return app;
    }

    private static DisplayLaneDto ToDto(CollectionLaneListing lane) =>
        new(lane.CollectionId, lane.Name, lane.CollectionType, lane.IconName, lane.Visible);

    private static Guid? ProfileId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(TuvimaClaimTypes.ActiveProfileId), out var value) && value != Guid.Empty ? value : null;

    private static IResult Unauthenticated() =>
        ApiErrors.Problem(401, "Authentication required.", "A trusted profile is required.");

    private static IResult UnknownPlace() =>
        ApiErrors.BadRequest("Lanes are at: home, watch, read, listen.");
}
