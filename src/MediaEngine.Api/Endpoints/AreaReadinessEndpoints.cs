using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Library;
using MediaEngine.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Why Home, Watch, Read or Listen may be empty (honest-waiting-messages WP1). Any signed-in profile may ask; an administrator gets the
/// facts, a member or Kids profile gets only that they are not shown (spec D6), so the route never reveals counts, folder names or
/// setup state to someone who cannot act on them.
/// </summary>
internal static class AreaReadinessEndpoints
{
    internal static void MapAreaReadiness(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/library/areas").WithTags("Library");

        group.MapGet("/{area}/readiness", async (
            string area,
            HttpContext http,
            [FromServices] IRequestAuthorityResolver authorities,
            [FromServices] AreaReadinessReadService readiness,
            CancellationToken ct) =>
        {
            area = area.Trim().ToLowerInvariant();
            if (!AreaReadinessReadService.IsKnownArea(area))
            {
                return ApiErrors.NotFound("Not found.");
            }

            var authority = await authorities.ResolveAsync(http, ct).ConfigureAwait(false);
            return authority.IsEffectiveAdministrator
                ? Results.Ok(await readiness.GetAdministratorAsync(area, ct).ConfigureAwait(false))
                : Results.Ok(AreaReadinessReadService.ForNonAdministrator(area, authority.ActiveProfileIsRestricted));
        }).WithName("GetAreaReadiness").WithSummary("Returns why an area has nothing to show: setup, libraries, folders and where its files stand.")
          .Produces<AreaReadinessDto>()
          .RequireClientScope(ApplicationPermissionIds.LibraryRead.Value);
    }
}
