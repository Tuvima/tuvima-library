using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Security;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// What a household's Kid's profiles may see (library access D9, D10): <c>GET</c> and <c>PUT /access/households/{id}/kid-settings</c>. A
/// server administrator reads and replaces any household's; a household administrator their own household's.
/// </summary>
internal static class HouseholdKidSettingsEndpoints
{
    internal static void MapHouseholdKidSettings(this RouteGroupBuilder access)
    {
        access.MapGet("/households/{householdId:guid}/kid-settings", async (Guid householdId, HttpContext http,
            IRequestAuthorityResolver resolver, [FromServices] HouseholdKidSettingsService service, CancellationToken ct) =>
            await AccountEndpoints.ExecuteAsync(async () =>
            {
                var stored = await service.GetAsync(await resolver.ResolveAsync(http, ct).ConfigureAwait(false), householdId, ct).ConfigureAwait(false);
                return Results.Ok(Map(householdId, stored));
            }))
            .RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersRead)
            .WithName("GetHouseholdKidSettings")
            .WithSummary("What a household's Kid's profiles may see: a level each for Watch, Read and Listen, and whether unrated items are shown.")
            .Produces<HouseholdKidSettingsResponse>();

        access.MapPut("/households/{householdId:guid}/kid-settings", async (Guid householdId, UpdateHouseholdKidSettingsRequest request,
            HttpContext http, IRequestAuthorityResolver resolver, [FromServices] HouseholdKidSettingsService service,
            [FromServices] RecentSignInGuard recentSignIn, CancellationToken ct) =>
            await AccountEndpoints.ExecuteAsync(async () =>
            {
                if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
                {
                    return stale;
                }

                var stored = await service.ReplaceAsync(
                    await resolver.ResolveAsync(http, ct).ConfigureAwait(false), householdId,
                    new HouseholdKidSettings(request.WatchLevel, request.ReadLevel, request.ListenLevel, request.ShowUnrated), ct)
                    .ConfigureAwait(false);
                return Results.Ok(Map(householdId, stored));
            }))
            .RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
            .WithName("ReplaceHouseholdKidSettings")
            .WithSummary("Replaces a household's Kid settings (server administrator or that household's administrator). An unknown level is refused with 400.")
            .Produces<HouseholdKidSettingsResponse>();
    }

    private static HouseholdKidSettingsResponse Map(Guid householdId, HouseholdKidSettings settings) =>
        new(householdId, settings.WatchLevel, settings.ReadLevel, settings.ListenLevel, settings.ShowUnrated, ProfileContentLimits.Choices);
}
