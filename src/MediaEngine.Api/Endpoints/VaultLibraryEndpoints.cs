using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Vault;
using MediaEngine.Contracts.Settings;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Vault-protected libraries on the Libraries pages (Library pages D10, D11, WP9): making one beside the ordinary library of a content type,
/// adding a folder as a Vault folder, listing them for a person who may see the Vault, the per-library "Look up details online" switch and
/// the "Move to Vault" destination check. Creating a library or a folder needs only a signed-in server administrator, with no PIN; listing and
/// changing a Vault library also need the administrator's Vault mode to be on in this device session. Anyone else, including every
/// non-administrator and every Kids profile, gets exactly the answer an unknown address gets ("not found") before a body is read, so nothing
/// tells them these actions exist. A Vault library is stored in the Vault's own tables and is never an account grant or a library grant.
/// No answer or log line names a folder, a path or a title.
/// </summary>
internal static class VaultLibraryEndpoints
{
    internal static void MapVaultLibraries(this RouteGroupBuilder group)
    {
        var vault = group.MapGroup("/vault-libraries").WithTags("Vault");

        vault.MapGet("/", async (HttpContext http, IRequestAuthorityResolver authorities, VaultAccessResolver access, VaultSiblingLibraryService siblings) =>
        {
            if (await UnlockedAdministratorAsync(http, authorities, access) is null)
            {
                return ApiErrors.NotFound("Not found.");
            }

            return Results.Ok(siblings.ToContract(await siblings.ListAsync(http.RequestAborted)));
        }).WithName("ListVaultSiblingLibraries").Produces<IReadOnlyList<MediaEngine.Contracts.Settings.LibraryFolderDto>>()
          .RequireAuthorization(AuthPolicies.Authenticated);

        vault.MapPost("/", async (HttpContext http, IRequestAuthorityResolver authorities, VaultAccessResolver access, VaultSiblingLibraryService siblings) =>
        {
            if (await AdministratorAsync(http, authorities) is not { } adminProfileId)
            {
                return ApiErrors.NotFound("Not found.");
            }

            if (await ReadBodyAsync<CreateVaultLibraryFromKindRequest>(http) is not { } request)
            {
                return ApiErrors.BadRequest(GenericRefusal);
            }

            var unlocked = await UnlockedAdministratorAsync(http, authorities, access) is not null;
            return Answered(await siblings.CreateAsync(adminProfileId, request.KindKey, request.Name, request.FolderPath, http.RequestAborted, request.Managed), unlocked);
        }).WithName("CreateVaultSiblingLibrary").Produces<VaultLibraryActionResultDto>()
          .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status409Conflict)
          .RequireAuthorization(AuthPolicies.Authenticated);

        vault.MapPost("/folders", async (HttpContext http, IRequestAuthorityResolver authorities, VaultAccessResolver access, VaultSiblingLibraryService siblings) =>
        {
            if (await AdministratorAsync(http, authorities) is not { } adminProfileId)
            {
                return ApiErrors.NotFound("Not found.");
            }

            if (await ReadBodyAsync<AddVaultFolderRequest>(http) is not { } request)
            {
                return ApiErrors.BadRequest(GenericRefusal);
            }

            var unlocked = await UnlockedAdministratorAsync(http, authorities, access) is not null;
            return Answered(await siblings.AddFolderAsync(adminProfileId, request.KindKey, request.FolderPath, http.RequestAborted, request.Managed), unlocked);
        }).WithName("AddVaultSiblingFolder").Produces<VaultLibraryActionResultDto>()
          .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status409Conflict)
          .RequireAuthorization(AuthPolicies.Authenticated);

        vault.MapGet("/destination", async (HttpContext http, IRequestAuthorityResolver authorities, VaultAccessResolver access, VaultSiblingLibraryService siblings, [FromQuery(Name = "kind_key")] string? kindKey) =>
        {
            // The answer says whether a Vault library exists, so it is for the unlocked administrator only; a locked one gets the same answer as anyone else.
            if (await UnlockedAdministratorAsync(http, authorities, access) is null)
            {
                return ApiErrors.NotFound("Not found.");
            }

            return string.IsNullOrWhiteSpace(kindKey)
                ? ApiErrors.BadRequest(VaultSiblingLibraryService.KindMissingMessage)
                : Results.Ok(await siblings.DestinationAsync(kindKey.Trim(), http.RequestAborted));
        }).WithName("GetVaultMoveDestination").Produces<VaultMoveDestinationDto>()
          .ProducesProblem(StatusCodes.Status400BadRequest).RequireAuthorization(AuthPolicies.Authenticated);

        vault.MapPut("/{libraryId:guid}/online-lookup", async (Guid libraryId, HttpContext http, IRequestAuthorityResolver authorities, VaultAccessResolver access, VaultSiblingLibraryService siblings) =>
        {
            if (await UnlockedAdministratorAsync(http, authorities, access) is null)
            {
                return ApiErrors.NotFound("Not found.");
            }

            if (await ReadBodyAsync<SetVaultOnlineLookupRequest>(http) is not { } request)
            {
                return ApiErrors.BadRequest("The request could not be read.");
            }

            return Answer(await siblings.SetOnlineLookupAsync(libraryId.ToString("D"), request.Enabled, http.RequestAborted), siblings);
        }).WithName("SetVaultLibraryOnlineLookup").Produces<MediaEngine.Contracts.Settings.LibraryFolderDto>()
          .ProducesProblem(StatusCodes.Status400BadRequest).RequireAuthorization(AuthPolicies.Authenticated);
    }

    /// <summary>The signed-in server administrator's profile id, or <see langword="null"/> for everyone else (a person, an app or a service).</summary>
    internal static async Task<Guid?> AdministratorAsync(HttpContext http, IRequestAuthorityResolver authorities)
    {
        var authority = await authorities.ResolveAsync(http, http.RequestAborted);
        return authority is { PrincipalKind: MediaEngine.Domain.Authorization.PrincipalKind.Human, IsAuthenticated: true, IsEffectiveAdministrator: true }
            && authority.ActiveProfileId is { } profileId && profileId != Guid.Empty
            ? profileId
            : null;
    }

    internal static async Task<Guid?> UnlockedAdministratorAsync(HttpContext http, IRequestAuthorityResolver authorities, VaultAccessResolver access)
    {
        if (await AdministratorAsync(http, authorities) is not { } profileId)
        {
            return null;
        }

        var authority = await authorities.ResolveAsync(http, http.RequestAborted);
        return await access.IsUnlockedAsync(authority, http.RequestAborted) ? profileId : null;
    }

    // The body is read here, after the caller is known, so a malformed body cannot tell a caller who is not an administrator that the action exists.
    private static async Task<T?> ReadBodyAsync<T>(HttpContext http) where T : class
    {
        try
        {
            return await http.Request.ReadFromJsonAsync<T>(http.RequestAborted);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The one refusal a caller who may not see the Vault ever gets, whatever went wrong, so a refusal never tells whether a Vault library exists.</summary>
    internal const string GenericRefusal = "That can't be done.";

    // Making a library or adding a folder works for any administrator, but what it answers does not: nothing but "worked" unless the caller may see the Vault
    // right now, and one constant refusal for every failure when they may not.
    private static IResult Answered(VaultSiblingOutcome outcome, bool unlocked)
    {
        if (outcome.Status == VaultSiblingStatus.Ok)
        {
            return Results.Ok(new VaultLibraryActionResultDto(true, unlocked ? outcome.Library?.Id : null));
        }

        return unlocked ? Refusal(outcome) : ApiErrors.BadRequest(GenericRefusal);
    }

    private static IResult Refusal(VaultSiblingOutcome outcome) => outcome.Status switch
    {
        VaultSiblingStatus.Invalid => ApiErrors.BadRequest(outcome.Message ?? GenericRefusal),
        VaultSiblingStatus.Conflict => ApiErrors.Conflict(outcome.Message ?? GenericRefusal),
        _ => ApiErrors.NotFound("Not found."),
    };

    private static IResult Answer(VaultSiblingOutcome outcome, VaultSiblingLibraryService siblings) => outcome.Status switch
    {
        VaultSiblingStatus.Ok when outcome.Library is { } library => Results.Ok(siblings.ToContract([library]).Single()),
        VaultSiblingStatus.Invalid => ApiErrors.BadRequest(outcome.Message ?? "That can't be done."),
        VaultSiblingStatus.Conflict => ApiErrors.Conflict(outcome.Message ?? "That can't be done."),
        _ => ApiErrors.NotFound("Not found."),
    };
}
