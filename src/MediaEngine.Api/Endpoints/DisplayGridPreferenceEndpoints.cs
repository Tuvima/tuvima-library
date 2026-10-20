using System.Security.Claims;
using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Remembers, per profile, the order a person chose on each "View all" grid so it follows them to every device. Every
/// signed-in profile (Kids included) may read and change its own choice; there is no profile id in the request, so a
/// call can only ever touch the profile on the caller's own claim.
/// </summary>
public static class DisplayGridPreferenceEndpoints
{
    // Kept in step with the values the data structure allows (profile_grid_preferences CHECK constraints).
    private static readonly string[] GridKeys =
    [
        DisplayGridValues.RememberWatch, DisplayGridValues.RememberRead, DisplayGridValues.RememberListen,
        DisplayGridValues.RememberMyList, DisplayGridValues.RememberLiked,
    ];

    private static readonly string[] Sorts =
    [
        DisplayGridValues.SortAdded, DisplayGridValues.SortTitle, DisplayGridValues.SortYear, DisplayGridValues.SortPlayed,
    ];

    public static IEndpointRouteBuilder MapDisplayGridPreferenceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/display/grid-preferences")
            .WithTags("Display");

        group.MapGet("", async (
            ClaimsPrincipal user,
            IProfileGridPreferenceRepository repository,
            CancellationToken ct) =>
        {
            if (ProfileId(user) is not { } profileId)
            {
                return Unauthenticated();
            }

            return Results.Ok(new DisplayGridPreferencesDto(await repository.GetSortsAsync(profileId, ct)));
        })
            .WithName("GetDisplayGridPreferences")
            .WithSummary("Returns the sort the active profile last chose on each View all grid.")
            .Produces<DisplayGridPreferencesDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireClientScope(ClientApiScopes.LibraryRead);

        group.MapPut("/{gridKey}", async (
            string gridKey,
            DisplayGridSortPreferenceRequest request,
            ClaimsPrincipal user,
            IProfileGridPreferenceRepository repository,
            CancellationToken ct) =>
        {
            if (ProfileId(user) is not { } profileId)
            {
                return Unauthenticated();
            }

            var key = gridKey.Trim().ToLowerInvariant();
            if (!GridKeys.Contains(key))
            {
                return ApiErrors.BadRequest($"Grids are: {string.Join(", ", GridKeys)}.");
            }

            var sort = request?.Sort?.Trim().ToLowerInvariant();
            if (sort is null || !Sorts.Contains(sort))
            {
                return ApiErrors.BadRequest($"Grids can be sorted by: {string.Join(", ", Sorts)}.");
            }

            try
            {
                await repository.SaveSortAsync(profileId, key, sort, ct);
            }
            catch (KeyNotFoundException)
            {
                return ApiErrors.NotFound("The profile was not found.");
            }

            return Results.NoContent();
        })
            .WithName("SetDisplayGridPreference")
            .WithSummary("Remembers the sort the active profile chose on one View all grid.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireClientScope(ClientApiScopes.ProgressWrite);

        return app;
    }

    private static Guid? ProfileId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(TuvimaClaimTypes.ActiveProfileId), out var value) && value != Guid.Empty ? value : null;

    private static IResult Unauthenticated() =>
        ApiErrors.Problem(401, "Authentication required.", "A trusted profile is required.");
}
