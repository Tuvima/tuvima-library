using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Security;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// The libraries hidden for a whole household (library access D2, LA1): <c>GET</c> and <c>PUT /access/households/{id}/libraries</c>. A server
/// administrator reads and replaces any household's list; a household administrator reads their own household's. A Vault library is refused.
/// </summary>
internal static class HouseholdLibraryAccessEndpoints
{
    internal static void MapHouseholdLibraryAccess(this RouteGroupBuilder access)
    {
        access.MapGet("/households/{householdId:guid}/libraries", async (Guid householdId, HttpContext http,
            IRequestAuthorityResolver resolver, [FromServices] HouseholdLibraryAccessService service, CancellationToken ct) =>
            await AccountEndpoints.ExecuteAsync(async () =>
            {
                var hidden = await service.GetAsync(await resolver.ResolveAsync(http, ct), householdId, ct).ConfigureAwait(false);
                return Results.Ok(Map(householdId, hidden));
            }))
            .RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersRead)
            .WithName("GetHouseholdLibraryAccess")
            .WithSummary("The libraries hidden for a household. Nothing is hidden by default; a Vault library is never listed.")
            .Produces<HouseholdLibraryAccessResponse>();

        access.MapPut("/households/{householdId:guid}/libraries", async (Guid householdId, ReplaceHouseholdLibrariesRequest request,
            HttpContext http, IRequestAuthorityResolver resolver, [FromServices] HouseholdLibraryAccessService service,
            [FromServices] RecentSignInGuard recentSignIn, CancellationToken ct) =>
            await AccountEndpoints.ExecuteAsync(async () =>
            {
                if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
                {
                    return stale;
                }

                if (request.HiddenLibraryIds is null)
                {
                    throw new ArgumentException("hidden_library_ids is required (it can be an empty list).");
                }

                var hidden = await service.ReplaceAsync(
                    await resolver.ResolveAsync(http, ct).ConfigureAwait(false), householdId, request.HiddenLibraryIds.ToHashSet(), ct)
                    .ConfigureAwait(false);
                return Results.Ok(Map(householdId, hidden));
            }))
            .RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
            .WithName("ReplaceHouseholdLibraryAccess")
            .WithSummary("Replaces the libraries hidden for a household (server administrator only). A Vault or unknown library is refused with 400.")
            .Produces<HouseholdLibraryAccessResponse>();
    }

    private static HouseholdLibraryAccessResponse Map(Guid householdId, IReadOnlySet<Guid> hidden) =>
        new(householdId, hidden.Order().ToList());
}
