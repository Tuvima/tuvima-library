using System.Security.Claims;
using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Contracts;
using Microsoft.AspNetCore.Mvc;

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

        var hero = app.MapGroup("/api/v1/display/hero-settings")
            .WithTags("Display");

        hero.MapGet("", async (
            ClaimsPrincipal user,
            [FromServices] IProfileHeroSettingsRepository repository,
            [FromServices] IHeroPluginSlides pluginSlides,
            CancellationToken ct) =>
        {
            if (ProfileId(user) is not { } profileId)
            {
                return Unauthenticated();
            }

            var settings = await repository.GetAsync(profileId, ct);
            var plugins = (await pluginSlides.PluginsAsync(profileId, ct))
                .Select(plugin => new DisplayHeroPluginDto(plugin.PluginId, plugin.Name, plugin.Enabled)).ToList();
            return Results.Ok(new DisplayHeroSettingsDto(
                settings.Enabled, settings.SlideCount, settings.AllowWatch, settings.AllowRead, settings.AllowListen, settings.ContinuedOnly)
            { Plugins = plugins });
        })
            .WithName("GetDisplayHeroSettings")
            .WithSummary("Returns how the active profile wants the Home hero to behave.")
            .Produces<DisplayHeroSettingsDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireClientScope(ClientApiScopes.LibraryRead);

        hero.MapPut("", async (
            DisplayHeroSettingsDto request,
            ClaimsPrincipal user,
            [FromServices] IProfileHeroSettingsRepository repository,
            CancellationToken ct) =>
        {
            if (ProfileId(user) is not { } profileId)
            {
                return Unauthenticated();
            }

            if (request is null || request.SlideCount is < 1 or > ProfileHeroSettings.MaxSlides)
            {
                return ApiErrors.BadRequest($"The hero shows between 1 and {ProfileHeroSettings.MaxSlides} slides.");
            }

            try
            {
                await repository.SaveAsync(profileId, new ProfileHeroSettings(
                    request.Enabled, request.SlideCount, request.AllowWatch, request.AllowRead, request.AllowListen, request.ContinuedOnly), ct);
            }
            catch (KeyNotFoundException)
            {
                return ApiErrors.NotFound("The profile was not found.");
            }

            return Results.NoContent();
        })
            .WithName("SetDisplayHeroSettings")
            .WithSummary("Remembers how the active profile wants the Home hero to behave.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireClientScope(ClientApiScopes.ProgressWrite);

        hero.MapPut("/plugins/{pluginId}", async (
            string pluginId,
            DisplayHeroPluginSwitchRequest request,
            ClaimsPrincipal user,
            [FromServices] IProfileHeroSettingsRepository repository,
            [FromServices] IHeroPluginSlides pluginSlides,
            CancellationToken ct) =>
        {
            if (ProfileId(user) is not { } profileId)
            {
                return Unauthenticated();
            }

            // Only a plugin that really offers slides can be switched, so the table never fills with made-up names.
            var id = pluginId.Trim();
            if (request is null || !(await pluginSlides.PluginsAsync(profileId, ct)).Any(plugin => string.Equals(plugin.PluginId, id, StringComparison.OrdinalIgnoreCase)))
            {
                return ApiErrors.NotFound("That plugin does not offer Home slides.");
            }

            try
            {
                await repository.SetPluginSlidesAsync(profileId, id, request.Enabled, ct);
            }
            catch (KeyNotFoundException)
            {
                return ApiErrors.NotFound("The profile was not found.");
            }

            return Results.NoContent();
        })
            .WithName("SetDisplayHeroPluginSlides")
            .WithSummary("Switches one plugin's Home slides on or off for the active profile.")
            .Produces(StatusCodes.Status204NoContent)
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
