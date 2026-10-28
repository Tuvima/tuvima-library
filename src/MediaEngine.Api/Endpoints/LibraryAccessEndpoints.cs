using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Which profiles may see a Selected-profiles library (Library pages D9, WP6). Administrators only. The library's access value itself is
/// saved through the library mutation, so the libraries file stays the one place that holds it; these routes read and replace only the
/// profiles chosen for it. A Vault library answers as a library that does not exist (Videos spec D45) and is never accepted as a grant.
/// </summary>
internal static class LibraryAccessEndpoints
{
    internal static void MapLibraryAccess(this RouteGroupBuilder group)
    {
        group.MapGet("/libraries/{libraryId:guid}/access", async (
            Guid libraryId,
            IConfigurationLoader configuration,
            [FromServices] IVaultLibraryRepository vaultLibraries,
            [FromServices] ILibraryProfileGrantRepository grants,
            [FromServices] IProfileRepository profiles,
            CancellationToken ct) =>
        {
            var library = await FindAsync(libraryId, configuration, vaultLibraries, ct).ConfigureAwait(false);
            if (library is null)
            {
                return ApiErrors.NotFound("Not found.");
            }

            return Results.Ok(await ReadAsync(libraryId, library.Access, grants, profiles, ct).ConfigureAwait(false));
        }).WithName("GetLibraryAccess").Produces<LibraryAccessGrantsDto>().RequireAdmin();

        group.MapPut("/libraries/{libraryId:guid}/access/profiles", async (
            Guid libraryId,
            SetLibraryProfileGrantsRequest request,
            HttpContext http,
            IConfigurationLoader configuration,
            [FromServices] IRequestAuthorityResolver authorities,
            [FromServices] IVaultLibraryRepository vaultLibraries,
            [FromServices] ILibraryProfileGrantRepository grants,
            [FromServices] IProfileRepository profiles,
            [FromServices] TimeProvider clock,
            CancellationToken ct) =>
        {
            var library = await FindAsync(libraryId, configuration, vaultLibraries, ct).ConfigureAwait(false);
            if (library is null)
            {
                return ApiErrors.NotFound("Not found.");
            }

            if (!string.Equals(library.Access, LibraryAccessLevels.Selected, StringComparison.OrdinalIgnoreCase))
            {
                return ApiErrors.Conflict("Choose Selected profiles for this library before choosing profiles.");
            }

            if (request.ProfileIds is null)
            {
                return ApiErrors.BadRequest("profile_ids is required.");
            }

            var actor = await authorities.ResolveAsync(http, ct).ConfigureAwait(false);
            try
            {
                await grants.ReplaceGrantedProfilesAsync(
                    libraryId, request.ProfileIds.ToHashSet(), clock.GetUtcNow(), actor.ActiveProfileId, ct).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                return ApiErrors.BadRequest(ex.Message);
            }

            return Results.Ok(await ReadAsync(libraryId, library.Access, grants, profiles, ct).ConfigureAwait(false));
        }).WithName("SetLibraryProfileGrants").Produces<LibraryAccessGrantsDto>().RequireAdmin();
    }

    // The library as configured, or null for an unknown id and for a Vault library (which is never named here).
    private static async Task<LibraryFolderConfig?> FindAsync(
        Guid libraryId, IConfigurationLoader configuration, IVaultLibraryRepository vaultLibraries, CancellationToken ct)
    {
        var text = libraryId.ToString("D");
        if ((await vaultLibraries.GetIdsAsync(ct).ConfigureAwait(false)).Contains(text))
        {
            return null;
        }

        var library = configuration.LoadLibraries().Libraries
            .FirstOrDefault(candidate => string.Equals(candidate.Id, text, StringComparison.OrdinalIgnoreCase));
        return library is null || string.Equals(library.Access, LibraryAccessLevels.Vault, StringComparison.OrdinalIgnoreCase) ? null : library;
    }

    private static async Task<LibraryAccessGrantsDto> ReadAsync(
        Guid libraryId, string access, ILibraryProfileGrantRepository grants, IProfileRepository profiles, CancellationToken ct)
    {
        var selected = string.Equals(access, LibraryAccessLevels.Selected, StringComparison.OrdinalIgnoreCase);
        var chosen = selected ? await grants.GetGrantedProfilesAsync(libraryId, ct).ConfigureAwait(false) : new HashSet<Guid>();
        var everyone = (await profiles.GetAllAsync(ct).ConfigureAwait(false))
            .Select(profile => new LibraryAccessProfileDto(
                profile.Id,
                profile.DisplayName,
                profile.AvatarColor,
                profile.AvatarIcon,
                string.IsNullOrWhiteSpace(profile.AvatarImagePath) ? null : $"/profiles/{profile.Id:D}/avatar",
                profile.Role == ProfileRole.RestrictedProfile,
                profile.IsServerAdministrator))
            .ToList();
        return new LibraryAccessGrantsDto(
            libraryId,
            selected ? LibraryAccessLevels.Selected : LibraryAccessLevels.Standard,
            chosen.Order().ToList(),
            everyone);
    }
}
