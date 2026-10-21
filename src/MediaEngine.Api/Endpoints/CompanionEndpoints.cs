using System.Security.Claims;
using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Companion actions: a second device (for example a phone) follows where one of the caller's own devices is playing,
/// or holds a position set by hand. The profile and the companion's own device id come from the caller's claims; only
/// the caller's own profile is ever visible.
/// </summary>
public static class CompanionEndpoints
{
    public static IEndpointRouteBuilder MapCompanionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/companion")
            .WithTags("Companion")
            .AddEndpointFilter(new PlayerResourceFailureFilter());

        group.MapGet("/devices", async (
            Guid? profileId,
            ClaimsPrincipal user,
            ICompanionSessionService companion,
            CancellationToken ct) =>
        {
            var identity = PlayerEndpoints.Bind(user, profileId, null, null);
            return Results.Ok(await companion.GetDevicesAsync(identity.ProfileId, ct));
        })
        .WithName("GetCompanionDevices")
        .WithSummary("List the caller's own devices that are playing or paused right now (heard from within 45 seconds).")
        .Produces<CompanionDeviceListDto>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.QueueRead);

        group.MapGet("/session", async (
            Guid? profileId,
            string? companionDeviceId,
            ClaimsPrincipal user,
            ICompanionSessionService companion,
            CancellationToken ct) =>
        {
            try
            {
                var identity = PlayerEndpoints.Bind(user, profileId, companionDeviceId, null);
                return Results.Ok(await companion.GetSessionAsync(identity.ProfileId, identity.DeviceId, ct));
            }
            catch (ArgumentException ex)
            {
                return ApiErrors.BadRequest(ex.Message);
            }
        })
        .WithName("GetCompanionSession")
        .WithSummary("Return this device's companion choice (following a device, a manual position, or none) with the followed device's latest state.")
        .Produces<CompanionSessionDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireClientScope(ClientApiScopes.QueueRead);

        group.MapPut("/session/follow", async (
            CompanionFollowRequestDto request,
            ClaimsPrincipal user,
            ICompanionSessionService companion,
            CancellationToken ct) =>
        {
            try
            {
                var identity = PlayerEndpoints.Bind(user, request.ProfileId, request.CompanionDeviceId, null);
                return Results.Ok(await companion.FollowAsync(
                    request with { ProfileId = identity.ProfileId, CompanionDeviceId = identity.DeviceId }, ct));
            }
            catch (ArgumentException ex)
            {
                return ApiErrors.BadRequest(ex.Message);
            }
        })
        .WithName("FollowCompanionDevice")
        .WithSummary("Follow one of the caller's own devices from this device.")
        .Produces<CompanionSessionDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.ProgressWrite);

        group.MapPut("/session/manual", async (
            CompanionManualPositionRequestDto request,
            ClaimsPrincipal user,
            ICompanionSessionService companion,
            CancellationToken ct) =>
        {
            try
            {
                var identity = PlayerEndpoints.Bind(user, request.ProfileId, request.CompanionDeviceId, null);
                return Results.Ok(await companion.SetManualAsync(
                    request with { ProfileId = identity.ProfileId, CompanionDeviceId = identity.DeviceId }, ct));
            }
            catch (ArgumentException ex)
            {
                return ApiErrors.BadRequest(ex.Message);
            }
        })
        .WithName("SetCompanionManualPosition")
        .WithSummary("Hold a position set by hand, such as an episode or chapter, instead of following a device.")
        .Produces<CompanionSessionDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.ProgressWrite);

        group.MapGet("/context", async (
            Guid workId,
            int? chapterIndex,
            double? positionSeconds,
            Guid? profileId,
            ClaimsPrincipal user,
            IProfileRepository profiles,
            CompanionContextReadService context,
            CancellationToken ct) =>
        {
            if (workId == Guid.Empty || chapterIndex is < 0 || positionSeconds is { } seconds && (!double.IsFinite(seconds) || seconds < 0))
            {
                return ApiErrors.BadRequest("A work id is required, a chapter must be zero or more and a position must be zero or more.");
            }

            // The profile comes from the caller's claims alone. A query that names a different profile is refused, and a caller
            // with no profile claim is judged at the default protection level, so no one can read another profile's spoiler level.
            var claimed = Guid.TryParse(user.FindFirstValue(TuvimaClaimTypes.ActiveProfileId), out var claimedId) ? claimedId : (Guid?)null;
            if (profileId is { } asked && claimed is { } own && asked != own)
            {
                return Results.Problem(
                    detail: "The companion only answers for the signed-in profile.",
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Forbidden.");
            }

            var profile = claimed is { } id ? await profiles.GetByIdAsync(id, ct) : null;
            var result = await context.GetAsync(workId, chapterIndex, positionSeconds, profile, ct);
            return result is null ? ApiErrors.NotFound($"Work '{workId}' not found.") : Results.Ok(result);
        })
        .WithName("GetCompanionContext")
        .WithSummary("Return what the companion shows for one work at one position: who is here up to that point, where else they appear, the previous episode and the Universe.")
        .Produces<CompanionContextDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.QueueRead)
        .RequireClientScope(ClientApiScopes.LibraryRead);

        group.MapDelete("/session", async (
            Guid? profileId,
            string? companionDeviceId,
            ClaimsPrincipal user,
            ICompanionSessionService companion,
            CancellationToken ct) =>
        {
            try
            {
                var identity = PlayerEndpoints.Bind(user, profileId, companionDeviceId, null);
                await companion.ClearAsync(identity.ProfileId, identity.DeviceId, ct);
                return Results.NoContent();
            }
            catch (ArgumentException ex)
            {
                return ApiErrors.BadRequest(ex.Message);
            }
        })
        .WithName("ClearCompanionSession")
        .WithSummary("Stop following and clear any manual position for this device.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireClientScope(ClientApiScopes.ProgressWrite);

        return app;
    }
}
