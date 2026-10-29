using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Library;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// What has happened in one library (Library pages D16, WP7). Administrators only, like the library's other pages. The answer is limited to
/// this library's own events, and a library the caller may not see (the library visibility rule), a Vault library and an unknown id all
/// answer as a library that does not exist (Videos spec D45), so the route never confirms that a Vault library exists and never returns a
/// title from one.
/// </summary>
internal static class LibraryActivityEndpoints
{
    internal static void MapLibraryActivity(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/library/libraries").WithTags("Libraries");

        group.MapGet("/{libraryId:guid}/activity", async (
            Guid libraryId,
            int? take,
            HttpContext http,
            IConfigurationLoader configuration,
            [FromServices] IRequestAuthorityResolver authorities,
            [FromServices] LibraryVisibilityRule visibility,
            [FromServices] IVaultLibraryRepository vaultLibraries,
            [FromServices] LibraryActivityReadService activity,
            CancellationToken ct) =>
        {
            var text = libraryId.ToString("D");
            var library = configuration.LoadLibraries().Libraries
                .FirstOrDefault(candidate => string.Equals(candidate.Id, text, StringComparison.OrdinalIgnoreCase));
            if (library is null
                || library.IsVault
                || (await vaultLibraries.GetIdsAsync(ct).ConfigureAwait(false)).Contains(text))
            {
                return ApiErrors.NotFound("Not found.");
            }

            // The same rule every other library read asks: hidden for this profile or its household is not found.
            var authority = await authorities.ResolveAsync(http, ct).ConfigureAwait(false);
            if (authority.ActiveProfileId is { } profileId
                && !await visibility.MaySeeAsync(profileId, authority.IsEffectiveAdministrator, libraryId, true, ct).ConfigureAwait(false))
            {
                return ApiErrors.NotFound("Not found.");
            }

            return Results.Ok(await activity.GetAsync(library, take, ct).ConfigureAwait(false));
        }).WithName("GetLibraryActivity").WithSummary("Returns what has happened in one library, newest first, in plain sentences.")
          .Produces<LibraryActivityDto>().RequireAdmin();
    }
}
