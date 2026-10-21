using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Vault;
using MediaEngine.Contracts.Vault;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Identity;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// NSFW mode and the Vault libraries (Videos spec D17 to D25, D36). The actions a person uses for their own NSFW mode answer
/// "not found" to anyone who cannot use it (a Kids or content-limited profile, an app, a signed-out caller), so nothing about the
/// Vault is drawn for them. The actions that create a Vault library or open NSFW access to a profile are for an administrator
/// signed in at the Dashboard, and ask for the administrator's own profile PIN again. No request or response here ever carries a
/// stored PIN, and a PIN is never logged.
/// </summary>
public static class VaultEndpoints
{
    public static IEndpointRouteBuilder MapVaultEndpoints(this IEndpointRouteBuilder app)
    {
        var nsfw = app.MapGroup("/api/v1/nsfw").WithTags("NSFW mode");

        nsfw.MapGet("/status", async (HttpContext http, IRequestAuthorityResolver resolver, VaultAdministrationService vault, CancellationToken ct) =>
        {
            if (await OwnSessionAsync(http, resolver, ct) is not { } own)
            {
                return ApiErrors.NotFound("Not found.");
            }

            var state = await vault.GetStateAsync(own.SessionId, own.ProfileId, ct);
            return state is null ? ApiErrors.NotFound("Not found.") : Results.Ok(ToDto(state));
        }).WithName("GetNsfwStatus").Produces<NsfwStatusDto>().RequireHumanSelfService();

        nsfw.MapPost("/unlock", async (NsfwUnlockRequest request, HttpContext http, IRequestAuthorityResolver resolver, VaultAdministrationService vault, CancellationToken ct) =>
        {
            if (await OwnSessionAsync(http, resolver, ct) is not { } own)
            {
                return ApiErrors.NotFound("Not found.");
            }

            try
            {
                return Results.Ok(ToDto(await vault.UnlockAsync(own.SessionId, own.ProfileId, request.Pin, ct)));
            }
            catch (Exception ex) when (ex is NsfwNotAvailableException or ProfilePinRequiredException or ProfilePinLockedException)
            {
                return PinFailure(ex);
            }
        }).WithName("UnlockNsfw").Produces<NsfwStatusDto>().ProducesProblem(StatusCodes.Status428PreconditionRequired)
          .ProducesProblem(StatusCodes.Status429TooManyRequests).RequireHumanSelfService();

        nsfw.MapPost("/lock", async (HttpContext http, IRequestAuthorityResolver resolver, VaultAdministrationService vault, CancellationToken ct) =>
        {
            if (await OwnSessionAsync(http, resolver, ct) is not { } own)
            {
                return ApiErrors.NotFound("Not found.");
            }

            await vault.LockAsync(own.SessionId, ct);
            return Results.NoContent();
        }).WithName("LockNsfw").Produces(StatusCodes.Status204NoContent).RequireHumanSelfService();

        nsfw.MapPost("/keepalive", async (HttpContext http, IRequestAuthorityResolver resolver, VaultAdministrationService vault, CancellationToken ct) =>
        {
            if (await OwnSessionAsync(http, resolver, ct) is not { } own)
            {
                return ApiErrors.NotFound("Not found.");
            }

            return await vault.KeepAliveAsync(own.SessionId, own.ProfileId, ct) ? Results.NoContent() : ApiErrors.NotFound("Not found.");
        }).WithName("KeepNsfwAlive").Produces(StatusCodes.Status204NoContent).RequireHumanSelfService();

        nsfw.MapPut("/pin", async (NsfwSetPinRequest request, HttpContext http, IRequestAuthorityResolver resolver, VaultAdministrationService vault, CancellationToken ct) =>
        {
            if (await OwnSessionAsync(http, resolver, ct) is not { } own)
            {
                return ApiErrors.NotFound("Not found.");
            }

            try
            {
                await vault.SetOwnPinAsync(own.ProfileId, request.NewPin, request.CurrentPin, ct);
                return Results.NoContent();
            }
            catch (ArgumentException ex)
            {
                return ApiErrors.BadRequest(ex.Message);
            }
            catch (Exception ex) when (ex is NsfwNotAvailableException or ProfilePinRequiredException or ProfilePinLockedException)
            {
                return PinFailure(ex);
            }
        }).WithName("SetNsfwPin").Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status428PreconditionRequired)
          .ProducesProblem(StatusCodes.Status429TooManyRequests).RequireHumanSelfService();

        var admin = app.MapGroup("/api/v1").WithTags("Vault");

        admin.MapPut("/profiles/{profileId:guid}/nsfw-access", async (
            Guid profileId, SetNsfwAccessRequest request, HttpContext http, IRequestAuthorityResolver resolver,
            VaultAdministrationService vault, CancellationToken ct) =>
        {
            if (await AdministratorProfileAsync(http, resolver, ct) is not { } adminProfileId)
            {
                return ApiErrors.NotFound("Not found.");
            }

            try
            {
                var mode = NsfwAccessRules.Parse(request.Mode);
                var access = await vault.SetAccessAsync(adminProfileId, profileId, mode, request.IdleLockMinutes, request.AdminPin, ct);
                return Results.Ok(new NsfwStatusDto
                {
                    Mode = NsfwAccessRules.ToStored(access.EffectiveMode),
                    Unlocked = false,
                    HasPin = access.HasPin,
                    IdleLockMinutes = access.EffectiveMode == NsfwAccessMode.Off
                        ? 0
                        : NsfwAccessRules.ResolveIdleLockMinutes(access.EffectiveMode, access.IdleLockMinutes),
                });
            }
            catch (KeyNotFoundException)
            {
                return ApiErrors.NotFound("Profile not found.");
            }
            catch (ArgumentException ex)
            {
                return ApiErrors.BadRequest(ex.Message);
            }
            catch (InvalidOperationException ex) when (ex is not VaultAdminPinNotSetException)
            {
                return ApiErrors.Conflict(ex.Message);
            }
            catch (Exception ex) when (ex is VaultAdminPinNotSetException or ProfilePinRequiredException or ProfilePinLockedException)
            {
                return PinFailure(ex);
            }
        }).WithName("SetProfileNsfwAccess").Produces<NsfwStatusDto>().ProducesProblem(StatusCodes.Status428PreconditionRequired)
          .ProducesProblem(StatusCodes.Status429TooManyRequests).RequireEffectiveAdministratorInPerson();

        admin.MapDelete("/profiles/{profileId:guid}/nsfw-pin", async (
            Guid profileId, HttpContext http, IRequestAuthorityResolver resolver, VaultAdministrationService vault, CancellationToken ct) =>
        {
            if (await AdministratorProfileAsync(http, resolver, ct) is null)
            {
                return ApiErrors.NotFound("Not found.");
            }

            await vault.ResetPinAsync(profileId, ct);
            return Results.NoContent();
        }).WithName("ResetProfileNsfwPin").Produces(StatusCodes.Status204NoContent).RequireEffectiveAdministratorInPerson();

        var libraries = app.MapGroup("/api/v1/vault/libraries").WithTags("Vault");

        libraries.MapGet("/", async (HttpContext http, IRequestAuthorityResolver resolver, VaultAdministrationService vault, VaultAccessResolver access, CancellationToken ct) =>
        {
            var authority = await resolver.ResolveAsync(http, ct);
            if (!authority.IsEffectiveAdministrator || !await access.IsUnlockedAsync(authority, ct))
            {
                return ApiErrors.NotFound("Not found.");
            }

            return Results.Ok((await vault.ListLibrariesAsync(ct)).Select(ToDto).ToList());
        }).WithName("ListVaultLibraries").Produces<IReadOnlyList<VaultLibraryDto>>().RequireEffectiveAdministratorInPerson();

        libraries.MapPost("/", async (CreateVaultLibraryRequest request, HttpContext http, IRequestAuthorityResolver resolver, VaultAdministrationService vault, CancellationToken ct) =>
        {
            if (await AdministratorProfileAsync(http, resolver, ct) is not { } adminProfileId)
            {
                return ApiErrors.NotFound("Not found.");
            }

            try
            {
                var created = await vault.CreateLibraryAsync(
                    adminProfileId, request.LookupSource, request.LookupServerId, request.AdultsConfirmed, request.AdminPin, ct);
                return Results.Ok(ToDto(created));
            }
            catch (ArgumentException ex)
            {
                return ApiErrors.BadRequest(ex.Message);
            }
            catch (Exception ex) when (ex is VaultAdminPinNotSetException or ProfilePinRequiredException or ProfilePinLockedException)
            {
                return PinFailure(ex);
            }
        }).WithName("CreateVaultLibrary").Produces<VaultLibraryDto>().ProducesProblem(StatusCodes.Status428PreconditionRequired)
          .ProducesProblem(StatusCodes.Status429TooManyRequests).RequireEffectiveAdministratorInPerson();

        libraries.MapPut("/{libraryId}", async (string libraryId, UpdateVaultLibraryRequest request, HttpContext http, IRequestAuthorityResolver resolver, VaultAdministrationService vault, VaultAccessResolver access, CancellationToken ct) =>
        {
            var authority = await resolver.ResolveAsync(http, ct);
            if (!authority.IsEffectiveAdministrator || authority.ActiveProfileId is not { } adminProfileId || !await access.IsUnlockedAsync(authority, ct))
            {
                return ApiErrors.NotFound("Not found.");
            }

            try
            {
                var updated = await vault.UpdateLibraryAsync(adminProfileId, libraryId, request.LookupSource, request.LookupServerId, request.AdminPin, ct);
                return updated is null ? ApiErrors.NotFound("Not found.") : Results.Ok(ToDto(updated));
            }
            catch (ArgumentException ex)
            {
                return ApiErrors.BadRequest(ex.Message);
            }
            catch (Exception ex) when (ex is VaultAdminPinNotSetException or ProfilePinRequiredException or ProfilePinLockedException)
            {
                return PinFailure(ex);
            }
        }).WithName("UpdateVaultLibrary").Produces<VaultLibraryDto>().ProducesProblem(StatusCodes.Status428PreconditionRequired)
          .ProducesProblem(StatusCodes.Status429TooManyRequests).RequireEffectiveAdministratorInPerson();

        libraries.MapPost("/{libraryId}/remove", async (string libraryId, RemoveVaultLibraryRequest request, HttpContext http, IRequestAuthorityResolver resolver, VaultAdministrationService vault, VaultAccessResolver access, CancellationToken ct) =>
        {
            var authority = await resolver.ResolveAsync(http, ct);
            if (!authority.IsEffectiveAdministrator || authority.ActiveProfileId is not { } adminProfileId || !await access.IsUnlockedAsync(authority, ct))
            {
                return ApiErrors.NotFound("Not found.");
            }

            try
            {
                return await vault.RemoveLibraryAsync(adminProfileId, libraryId, request.AdminPin, ct)
                    ? Results.NoContent()
                    : ApiErrors.NotFound("Not found.");
            }
            catch (InvalidOperationException ex) when (ex is not VaultAdminPinNotSetException)
            {
                return ApiErrors.Conflict(ex.Message);
            }
            catch (Exception ex) when (ex is VaultAdminPinNotSetException or ProfilePinRequiredException or ProfilePinLockedException)
            {
                return PinFailure(ex);
            }
        }).WithName("RemoveVaultLibrary").Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status428PreconditionRequired)
          .ProducesProblem(StatusCodes.Status429TooManyRequests).RequireEffectiveAdministratorInPerson();

        // The Vault's own artwork (Videos spec D27). Anyone without an unlocked NSFW session gets exactly what a missing file gets.
        app.MapGet("/api/v1/vault/artwork/{artworkId:guid}", async (
            Guid artworkId, HttpContext http, IRequestAuthorityResolver resolver, VaultAccessResolver access,
            IVaultArtworkRepository artwork, CancellationToken ct) =>
        {
            var authority = await resolver.ResolveAsync(http, ct);
            if (!await access.IsUnlockedAsync(authority, ct)
                || await artwork.GetAsync(artworkId, ct) is not { } record
                || !File.Exists(record.FilePath))
            {
                return ApiErrors.NotFound("Artwork image not found.");
            }

            var extension = Path.GetExtension(record.FilePath);
            var contentType = extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png"
                : extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ? "image/webp"
                : "image/jpeg";
            return Results.File(record.FilePath, contentType, enableRangeProcessing: true);
        }).WithName("GetVaultArtwork").WithTags("Vault").Produces<byte[]>(StatusCodes.Status200OK, "image/jpeg")
          .ProducesProblem(StatusCodes.Status404NotFound).RequireAuthorization(AuthPolicies.Authenticated);

        return app;
    }

    private sealed record OwnSession(Guid SessionId, Guid ProfileId);

    /// <summary>The caller's own device session, or <see langword="null"/> for anything that is not a person on a Dashboard session.</summary>
    private static async Task<OwnSession?> OwnSessionAsync(HttpContext http, IRequestAuthorityResolver resolver, CancellationToken ct)
    {
        var authority = await resolver.ResolveAsync(http, ct);
        return authority is { PrincipalKind: PrincipalKind.Human, IsAuthenticated: true, SessionId: { } sessionId, ActiveProfileId: { } profileId }
            && sessionId != Guid.Empty && profileId != Guid.Empty
            ? new OwnSession(sessionId, profileId)
            : null;
    }

    private static async Task<Guid?> AdministratorProfileAsync(HttpContext http, IRequestAuthorityResolver resolver, CancellationToken ct)
    {
        var authority = await resolver.ResolveAsync(http, ct);
        return authority.IsEffectiveAdministrator && authority.ActiveProfileId is { } profileId && profileId != Guid.Empty ? profileId : null;
    }

    internal static IResult PinFailure(Exception exception) => exception switch
    {
        ProfilePinLockedException => Results.Problem(
            title: "Too many attempts",
            detail: "Too many attempts. Try again later.",
            statusCode: StatusCodes.Status429TooManyRequests),
        VaultAdminPinNotSetException notSet => Results.Problem(
            title: "Administrator PIN required",
            detail: notSet.Message,
            statusCode: StatusCodes.Status428PreconditionRequired),
        ProfilePinRequiredException => Results.Problem(
            title: "PIN required",
            detail: "Enter the PIN to continue.",
            statusCode: StatusCodes.Status428PreconditionRequired),
        _ => ApiErrors.NotFound("Not found."),
    };

    private static NsfwStatusDto ToDto(NsfwState state) => new()
    {
        Mode = NsfwAccessRules.ToStored(state.Mode),
        Unlocked = state.Unlocked,
        HasPin = state.HasPin,
        IdleLockMinutes = state.IdleLockMinutes,
    };

    private static VaultLibraryDto ToDto(VaultLibrary library) => new()
    {
        Id = library.LibraryId,
        LookupSource = library.EffectiveLookupSource,
        LookupServerId = library.LookupServerId,
        AdultsConfirmedAt = library.AdultsConfirmedAt,
    };
}
