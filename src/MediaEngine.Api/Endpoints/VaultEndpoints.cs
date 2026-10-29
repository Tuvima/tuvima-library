using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Libraries;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Api.Services.Security;
using MediaEngine.Api.Services.Vault;
using MediaEngine.Contracts.Vault;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Identity;
using MediaEngine.Providers.Services.Vault;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Vault mode and the Vault libraries (Videos spec D17 to D25, D36). The actions a person uses for their own Vault mode answer
/// "not found" to anyone who cannot use it (a Kids or content-limited profile, an app, a signed-out caller), so nothing about the
/// Vault is drawn for them. The actions that create a Vault library or open Vault access to a profile are for an administrator
/// signed in at the Dashboard, and ask for the administrator's own profile PIN again. No request or response here ever carries a
/// stored PIN, and a PIN is never logged.
/// </summary>
public static class VaultEndpoints
{
    public static IEndpointRouteBuilder MapVaultEndpoints(this IEndpointRouteBuilder app)
    {
        var nsfw = app.MapGroup("/api/v1/nsfw").WithTags("Vault mode");

        nsfw.MapGet("/status", async (HttpContext http, IRequestAuthorityResolver resolver, VaultAdministrationService vault, IUserPlaybackSettingsService settings, CancellationToken ct) =>
        {
            if (await OwnSessionAsync(http, resolver, ct) is not { } own)
            {
                return ApiErrors.NotFound("Not found.");
            }

            var state = await vault.GetStateAsync(own.SessionId, own.ProfileId, ct);
            return state is null
                ? ApiErrors.NotFound("Not found.")
                : Results.Ok(ToDto(state, await settings.GetShowVaultInNavigationAsync(own.ProfileId, ct)));
        }).WithName("GetNsfwStatus").Produces<NsfwStatusDto>().RequireHumanSelfService();

        // The person's own "Show Vault in the navigation bar" choice (Library pages D20). The same access check as the status
        // action decides who may have it: anyone who cannot use Vault mode gets "not found" and nothing is stored.
        nsfw.MapPut("/navigation", async (NsfwNavigationRequest request, HttpContext http, IRequestAuthorityResolver resolver, VaultAdministrationService vault, IUserPlaybackSettingsService settings, CancellationToken ct) =>
        {
            if (await OwnSessionAsync(http, resolver, ct) is not { } own)
            {
                return ApiErrors.NotFound("Not found.");
            }

            var state = await vault.GetStateAsync(own.SessionId, own.ProfileId, ct);
            if (state is null)
            {
                return ApiErrors.NotFound("Not found.");
            }

            await settings.SetShowVaultInNavigationAsync(own.ProfileId, request.ShowInNavigation, ct);
            return Results.Ok(ToDto(state, request.ShowInNavigation));
        }).WithName("SetNsfwNavigation").Produces<NsfwStatusDto>().RequireHumanSelfService();

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

        // The person's own "Turn Vault mode off after being idle for" choice (5, 15, 30, 60 minutes or Never).
        nsfw.MapPut("/idle", async (NsfwIdleRequest request, HttpContext http, IRequestAuthorityResolver resolver, VaultAdministrationService vault, CancellationToken ct) =>
        {
            if (await OwnSessionAsync(http, resolver, ct) is not { } own)
            {
                return ApiErrors.NotFound("Not found.");
            }

            try
            {
                return Results.Ok(ToDto(await vault.SetOwnIdleAsync(own.SessionId, own.ProfileId, request.IdleLockMinutes, ct)));
            }
            catch (ArgumentException ex)
            {
                return ApiErrors.BadRequest(ex.Message);
            }
            catch (NsfwNotAvailableException)
            {
                return ApiErrors.NotFound("Not found.");
            }
        }).WithName("SetNsfwIdle").Produces<NsfwStatusDto>().ProducesProblem(StatusCodes.Status400BadRequest)
          .RequireHumanSelfService();

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

        // The Vault sections a profile may open once its Vault mode is on (Vault sections D28, D39). The list names the sections, so it is for an
        // administrator whose own Vault mode is on; anyone else gets the answer an unknown address gets.
        admin.MapGet("/profiles/{profileId:guid}/vault-sections", async (
            Guid profileId, HttpContext http, IRequestAuthorityResolver resolver, VaultAccessResolver access,
            [FromServices] VaultSiblingLibraryService siblings, [FromServices] IVaultSectionGrantRepository grants, [FromServices] IProfileRepository profiles, CancellationToken ct) =>
        {
            var authority = await resolver.ResolveAsync(http, ct);
            if (!authority.IsEffectiveAdministrator || !await access.IsUnlockedAsync(authority, ct))
            {
                return ApiErrors.NotFound("Not found.");
            }

            return await SectionsOfAsync(profileId, siblings, grants, profiles, ct);
        }).WithName("GetProfileVaultSections").Produces<ProfileVaultSectionsDto>().RequireEffectiveAdministratorInPerson();

        admin.MapPut("/profiles/{profileId:guid}/vault-sections", async (
            Guid profileId, SetProfileVaultSectionsRequest request, HttpContext http, IRequestAuthorityResolver resolver, VaultAccessResolver access,
            [FromServices] VaultSiblingLibraryService siblings, [FromServices] IVaultSectionGrantRepository grants, [FromServices] IProfileRepository profiles,
            TimeProvider clock, CancellationToken ct) =>
        {
            var authority = await resolver.ResolveAsync(http, ct);
            if (!authority.IsEffectiveAdministrator || authority.ActiveProfileId is not { } adminProfileId || !await access.IsUnlockedAsync(authority, ct))
            {
                return ApiErrors.NotFound("Not found.");
            }

            try
            {
                await grants.ReplaceGrantedAsync(profileId, request.LibraryIds ?? [], clock.GetUtcNow(), adminProfileId, ct);
            }
            catch (KeyNotFoundException)
            {
                return ApiErrors.NotFound("Profile not found.");
            }
            catch (ArgumentException ex)
            {
                return ApiErrors.BadRequest(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return ApiErrors.Conflict(ex.Message);
            }

            return await SectionsOfAsync(profileId, siblings, grants, profiles, ct);
        }).WithName("SetProfileVaultSections").Produces<ProfileVaultSectionsDto>().ProducesProblem(StatusCodes.Status400BadRequest)
          .RequireEffectiveAdministratorInPerson();

        var libraries = app.MapGroup("/api/v1/vault/libraries").WithTags("Vault");

        // The library settings live inside the Vault area (Videos spec D45): a caller whose Vault mode is off gets "not found" before a
        // body or an id is read, so a malformed body cannot tell a locked caller that these routes exist.
        libraries.WithMetadata(VaultGatedRoute.Instance);

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

        // Puts a folder on the server into a Vault library. Like every action that opens the Vault up it is for an unlocked administrator
        // in person and asks for their own profile PIN again; anyone else gets "not found". A missing or unusable folder adds nothing.
        libraries.MapPost("/{libraryId}/folders", async (string libraryId, AttachVaultFolderRequest request, HttpContext http, IRequestAuthorityResolver resolver, VaultFolderAttachmentService attachment, VaultAccessResolver access, CancellationToken ct) =>
        {
            var authority = await resolver.ResolveAsync(http, ct);
            if (!authority.IsEffectiveAdministrator || authority.ActiveProfileId is not { } adminProfileId || adminProfileId == Guid.Empty || !await access.IsUnlockedAsync(authority, ct))
            {
                return ApiErrors.NotFound("Not found.");
            }

            try
            {
                var outcome = await attachment.AttachAsync(adminProfileId, libraryId, request.FolderPath, request.AdminPin, ct);
                return outcome.Status switch
                {
                    VaultFolderAttachmentStatus.Attached when outcome.Library is { } attached => Results.Ok(ToDto(attached)),
                    VaultFolderAttachmentStatus.FolderNotFound => ApiErrors.BadRequest(outcome.Message ?? VaultFolderAttachmentService.FolderNotFoundMessage),
                    VaultFolderAttachmentStatus.NotAllowed => ApiErrors.Conflict(outcome.Message ?? VaultFolderAttachmentService.FolderNotAllowedMessage),
                    _ => ApiErrors.NotFound("Not found."),
                };
            }
            catch (Exception ex) when (ex is VaultAdminPinNotSetException or ProfilePinRequiredException or ProfilePinLockedException)
            {
                return PinFailure(ex);
            }
        }).WithName("AttachVaultFolder").Produces<VaultLibraryDto>().ProducesProblem(StatusCodes.Status400BadRequest)
          .ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status428PreconditionRequired)
          .ProducesProblem(StatusCodes.Status429TooManyRequests).RequireEffectiveAdministratorInPerson();

        libraries.MapPost("/{libraryId}/remove", async (string libraryId, RemoveVaultLibraryRequest request, HttpContext http, IRequestAuthorityResolver resolver, VaultAdministrationService vault, VaultAccessResolver access, IConfigurationLoader configuration, RecycleBinService recycleBin, CancellationToken ct) =>
        {
            var authority = await resolver.ResolveAsync(http, ct);
            if (!authority.IsEffectiveAdministrator || authority.ActiveProfileId is not { } adminProfileId || !await access.IsUnlockedAsync(authority, ct))
            {
                return ApiErrors.NotFound("Not found.");
            }

            // Files deleted from this library wait in the bin, where they are recognised as Vault only through the library. They are
            // the person's to restore or empty first; removing the library never throws them away.
            if (recycleBin.List().Any(entry => string.Equals(entry.LibraryId, libraryId, StringComparison.OrdinalIgnoreCase)))
            {
                return ApiErrors.Conflict("Restore or empty this library's recycle bin entries before removing it.");
            }

            var sourcePaths = configuration.LoadLibraries().Libraries
                .Where(library => string.Equals(library.Id, libraryId, StringComparison.OrdinalIgnoreCase))
                .SelectMany(library => library.Sources)
                .Select(source => source.Path)
                .ToList();
            try
            {
                if (!await vault.RemoveLibraryAsync(adminProfileId, libraryId, request.AdminPin, ct, sourcePaths))
                {
                    return ApiErrors.NotFound("Not found.");
                }

                // The Vault mark is gone, so the settings entry goes with it. Left behind it would turn into an ordinary library
                // that keeps being watched and scanned.
                await VaultLibrarySettings.RemoveEntryAsync(configuration, libraryId, ct);
                return Results.NoContent();
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

        // The Vault's own artwork (Videos spec D27, D41). Anyone without an unlocked Vault mode session gets exactly what a missing file gets.
        // "w" asks for a web-sized copy (300, 400, 640, 800 or 1280 wide), made once and kept beside the original; any other width
        // gets the original. The stored original is never changed.
        app.MapGet("/api/v1/vault/artwork/{artworkId:guid}", async (
            Guid artworkId, [FromQuery] int? w, HttpContext http, IRequestAuthorityResolver resolver, VaultAccessResolver access,
            IVaultArtworkRepository artwork, CancellationToken ct) =>
        {
            // Private and uncacheable whether or not the image is found, so a cached answer never outlives the Vault mode session.
            CatalogueResourceAuthorizationService.ApplyVaultCacheControl(http.Response);
            var authority = await resolver.ResolveAsync(http, ct);
            if (!await access.MayOpenAnySectionAsync(authority, ct)
                || await artwork.GetAsync(artworkId, ct) is not { } record
                || !File.Exists(record.FilePath))
            {
                return ApiErrors.NotFound("Artwork image not found.");
            }

            var path = record.FilePath;
            if (w is { } width && VaultArtworkSizer.IsAllowedWidth(width))
            {
                var copy = await VaultArtworkSizer.GetOrCreateCopyAsync(record.FilePath, width, ct);
                // No copy (unreadable or already small enough): the original is served.
                path = copy ?? path;
            }

            var extension = Path.GetExtension(path);
            var contentType = extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png"
                : extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ? "image/webp"
                : "image/jpeg";
            return Results.File(path, contentType, enableRangeProcessing: true);
        }).WithName("GetVaultArtwork").WithTags("Vault").WithMetadata(VaultGatedRoute.Instance).Produces<byte[]>(StatusCodes.Status200OK, "image/jpeg")
          .ProducesProblem(StatusCodes.Status404NotFound).RequireAuthorization(AuthPolicies.Authenticated);

        // Browsing: recently added, scenes, folders, performers, studios, tags and search (WP14), behind one central gate.
        app.MapVaultBrowseEndpoints();
        app.MapVaultImageEndpoints();

        // Identify, how a video was matched, and the quiet Vault activity list (WP12).
        app.MapVaultIdentifyEndpoints();

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

    private static async Task<IResult> SectionsOfAsync(
        Guid profileId, VaultSiblingLibraryService siblings, IVaultSectionGrantRepository grants, IProfileRepository profiles, CancellationToken ct)
    {
        if (await profiles.GetByIdAsync(profileId, ct) is not { } person)
        {
            return ApiErrors.NotFound("Profile not found.");
        }

        var granted = await grants.GetGrantedAsync(profileId, ct);
        var sections = (await siblings.ListAsync(ct))
            .Select(library => new VaultSectionDto
            {
                LibraryId = library.Id,
                Name = library.Name,
                KindKey = library.KindKey ?? string.Empty,
                Granted = granted.Contains(library.Id),
            })
            .ToList();
        return Results.Ok(new ProfileVaultSectionsDto { OpensAll = person.Role == MediaEngine.Domain.Enums.ProfileRole.Administrator, Sections = sections });
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

    private static NsfwStatusDto ToDto(NsfwState state, bool showInNavigation = false) => new()
    {
        Mode = NsfwAccessRules.ToStored(state.Mode),
        Unlocked = state.Unlocked,
        HasPin = state.HasPin,
        IdleLockMinutes = state.IdleLockMinutes,
        ShowInNavigation = showInNavigation,
    };

    private static VaultLibraryDto ToDto(VaultLibrary library) => new()
    {
        Id = library.LibraryId,
        LookupSource = library.EffectiveLookupSource,
        LookupServerId = library.LookupServerId,
        AdultsConfirmedAt = library.AdultsConfirmedAt,
    };
}
