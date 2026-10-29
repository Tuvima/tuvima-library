using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Api.Services.Security;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Profiles;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;
using MediaEngine.Identity.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var access = app.MapGroup("/access").WithTags("Access");
        MapSelfService(access);
        MapAdministratorUnlock(access);
        MapManagedAccounts(access);
        MapManagedUsers(access);
        MapManagedProfiles(access);
        MapManagedDevices(access);
        MapManagedSessions(access);
        return app;
    }

    /// <summary>
    /// Paired phones and TVs for Settings &gt; Security &gt; Devices: a server administrator sees every device, a household administrator
    /// their household's, anyone else only their own account's. Revoking ends the device's tokens and tells a connected app to sign out.
    /// </summary>
    private static void MapManagedDevices(RouteGroupBuilder access)
    {
        var devices = access.MapGroup("/devices").RequireHumanSelfService();
        devices.MapGet("/", async (HttpContext http, IRequestAuthorityResolver resolver,
            ISelfServiceAuthorizationService decisions, [FromServices] ManagedClientDeviceService service,
            CancellationToken ct) =>
        {
            var authority = await RequireSelfAsync(http, resolver, decisions, ct);
            return Results.Ok(await service.ListAsync(authority, ct));
        })
        .WithName("ListManagedClientDevices")
        .Produces<IReadOnlyList<ManagedClientDeviceDto>>();

        devices.MapDelete("/{deviceId:guid}", async (Guid deviceId, HttpContext http,
            IRequestAuthorityResolver resolver, ISelfServiceAuthorizationService decisions,
            [FromServices] ManagedClientDeviceService service, CancellationToken ct) =>
        {
            var authority = await RequireSelfAsync(http, resolver, decisions, ct);
            return await service.RevokeAsync(authority, deviceId, ct) switch
            {
                ManagedDeviceRevokeOutcome.Revoked => Results.NoContent(),
                ManagedDeviceRevokeOutcome.Forbidden => ApiErrors.Forbidden("That device belongs to another account."),
                _ => ApiErrors.NotFound("Device not found or already revoked."),
            };
        })
        .WithName("RevokeManagedClientDevice")
        .Produces(StatusCodes.Status204NoContent);

        // Which person a phone backs its photos up to: a server administrator, or the household administrator of
        // the phone's household, chooses or clears it without a PIN.
        devices.MapPut("/{deviceId:guid}/backup-profile", async (Guid deviceId, SetManagedDeviceBackupProfileRequest request,
            HttpContext http, IRequestAuthorityResolver resolver,
            [FromServices] PhoneBackupProfileService backup, [FromServices] RecentSignInGuard recentSignIn,
            CancellationToken ct) => await ExecuteAsync(async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            var actor = await resolver.ResolveAsync(http, ct);
            return await backup.SetByAdministratorAsync(actor, deviceId, request.ProfileId, ct) switch
            {
                BackupProfileOutcome.Changed => Results.NoContent(),
                BackupProfileOutcome.ProfileNotFound => ApiErrors.NotFound("That person isn't in the phone's household."),
                _ => ApiErrors.NotFound("Device not found or already revoked."),
            };
        }))
        .WithName("SetManagedClientDeviceBackupProfile")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// The Devices page's signed-in sessions: a server administrator sees every account's, a household administrator
    /// their household's. Signing one out ends that device's sign-in; your own session is ended with Sign out.
    /// </summary>
    private static void MapManagedSessions(RouteGroupBuilder access)
    {
        var sessions = access.MapGroup("/sessions");
        sessions.MapGet("/", async (HttpContext http, IRequestAuthorityResolver resolver,
            [FromServices] ManagedSessionService service, CancellationToken ct) => await ExecuteAsync(async () =>
                Results.Ok(await service.ListAsync(await resolver.ResolveAsync(http, ct), ct))))
            .RequireEffectiveAdministratorOrHouseholdAdministrator()
            .WithName("ListManagedAccountSessions")
            .Produces<IReadOnlyList<ManagedAccountSessionsResponse>>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        sessions.MapDelete("/{sessionId:guid}", async (Guid sessionId, HttpContext http,
            IRequestAuthorityResolver resolver, [FromServices] ManagedSessionService service,
            [FromServices] RecentSignInGuard recentSignIn, CancellationToken ct) => await ExecuteAsync(async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            return await service.RevokeAsync(await resolver.ResolveAsync(http, ct), sessionId, ct) switch
            {
                ManagedSessionRevokeOutcome.Revoked => Results.NoContent(),
                ManagedSessionRevokeOutcome.OwnSession => ApiErrors.BadRequest(ManagedSessionService.OwnSessionMessage),
                ManagedSessionRevokeOutcome.Forbidden => ApiErrors.Forbidden("That sign-in belongs to someone you do not look after."),
                _ => ApiErrors.NotFound("Session not found or already signed out."),
            };
        }))
            .RequireEffectiveAdministratorOrHouseholdAdministrator()
            .WithName("RevokeManagedAccountSession")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static void MapSelfService(RouteGroupBuilder access)
    {
        var self = access.MapGroup("/self-service").RequireHumanSelfService();
        self.MapGet("/", async (string? originalClientIngress, bool originalClientIsHttps,
            HttpContext http, IRequestAuthorityResolver resolver,
            ISelfServiceAuthorizationService decisions, IAccountRepository accounts,
            IProfileRepository profiles, IProfileAccessRepository profileAccess, IIdentityRepository identities,
            IAccountExternalLoginService externalLogins,
            Microsoft.AspNetCore.Identity.UserManager<Account> users,
            AuthenticationProviderConfigurationService providerConfiguration,
            IConfigurationLoader configuration,
            CancellationToken ct) =>
        {
            var authority = await RequireSelfAsync(http, resolver, decisions, ct);
            var account = await accounts.GetByIdAsync(authority.AccountId!.Value, ct)
                ?? throw new UnauthorizedAccessException();
            var grants = await HouseholdProfileProjection.MapAsync(account.Id, accounts, profiles, profileAccess, ct);
            // A user signs in as the profile it opens.
            var defaultId = account.ProfileId;

            var policy = providerConfiguration.LoadWithSecrets();
            var network = configuration.LoadNetwork();
            var hasPassword = await identities.GetAccountCredentialAsync(
                account.Id, AccountCredentialKind.Password, ct).ConfigureAwait(false) is not null;
            var passkeys = await users.GetPasskeysAsync(account).ConfigureAwait(false);
            var linkedLogins = await externalLogins.GetByAccountAsync(account.Id, ct).ConfigureAwait(false);

            var passkeyReady = policy.PasskeySignInEnabled
                && AuthenticationEndpoints.IsCanonicalOriginReady(network);
            var availableProviders = AuthenticationEndpoints.AvailableExternalProviders(
                policy, network, originalClientIngress, originalClientIsHttps);

            var methods = AttachedAuthenticationMethods(
                hasPassword, passkeys.Count > 0, linkedLogins.Count > 0);

            var capabilities = new AccountSecurityCapabilitiesResponse(
                hasPassword,
                passkeys.Count > 0,
                linkedLogins.Count > 0,
                hasPassword && AuthenticationEndpoints.AllowsClient(
                    configuration.LoadNetwork(), originalClientIngress, originalClientIsHttps,
                    policy.PasswordSignInEnabled),
                AuthenticationEndpoints.IsPasskeyAvailable(
                    policy, configuration.LoadNetwork(), originalClientIngress, originalClientIsHttps),
                passkeyReady,
                availableProviders.Count > 0,
                availableProviders,
                await identities.GetAccountTwoStepAsync(account.Id, ct) is { IsEnabled: true });
            return Results.Ok(new AccountSelfServiceResponse(account.Id, account.Email,
                authority.ActiveProfileId.GetValueOrDefault(), defaultId, grants, methods, capabilities));
        }).Produces<AccountSelfServiceResponse>();

        self.MapGet("/external-logins", async (HttpContext http, IRequestAuthorityResolver resolver,
            ISelfServiceAuthorizationService decisions, IAccountExternalLoginService externalLogins,
            CancellationToken ct) =>
        {
            var authority = await RequireSelfAsync(http, resolver, decisions, ct);
            var values = await externalLogins.GetByAccountAsync(authority.AccountId!.Value, ct);
            return Results.Ok(values.Select(ProfileContractMapper.ToResponse).ToList());
        }).Produces<IReadOnlyList<AccountExternalLoginDto>>();

        self.MapDelete("/external-logins/{loginId:guid}", async (Guid loginId, HttpContext http,
            IRequestAuthorityResolver resolver, ISelfServiceAuthorizationService decisions,
            IAccountSignInMethodRepository signInMethods, IAuthorizationAuditWriter audit,
            AuthenticationPolicyMutationGate mutationGate,
            AuthenticationProviderConfigurationService providerConfiguration,
            IConfigurationLoader configuration,
            IAccountRepository accounts, IIdentityRepository identities,
            IAccountExternalLoginService externalLogins,
            Microsoft.AspNetCore.Identity.UserManager<Account> users,
            [FromServices] RecentSignInGuard recentSignIn,
            TimeProvider clock, CancellationToken ct) =>
        {
            var authority = await RequireSelfAsync(http, resolver, decisions, ct);
            if (await recentSignIn.RefuseIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            var accountId = authority.AccountId!.Value;
            using var mutation = await mutationGate.EnterAsync(ct).ConfigureAwait(false);
            var linked = await externalLogins.GetByAccountAsync(accountId, ct).ConfigureAwait(false);
            if (!linked.Any(login => login.Id == loginId))
            {
                return ApiErrors.NotFound("External login not found.");
            }

            if (!await AuthenticationEndpoints.HasUsableAccountSignInAsync(
                    providerConfiguration.LoadWithSecrets(), configuration.LoadNetwork(), accountId, accounts, identities,
                    externalLogins, users, excludedExternalLoginId: loginId, ct: ct).ConfigureAwait(false))
            {
                return ApiErrors.Conflict("Add another enabled sign-in method before removing this external login.");
            }

            var result = await signInMethods.RemoveExternalLoginAsync(accountId, loginId, ct);
            if (result == SignInMethodRemovalResult.NotFound)
            {
                return ApiErrors.NotFound("External login not found.");
            }

            if (result == SignInMethodRemovalResult.LastSignInMethod)
            {
                return ApiErrors.Conflict("Add another sign-in method before removing this external login.");
            }

            await WriteAuditAsync(audit, clock, authority, "account.external_login_unlinked", loginId, ct);
            return Results.NoContent();
        }).Produces(StatusCodes.Status204NoContent);

        // One-time reminders ("Not now") belong to the signed-in account; the account is never read from the request.
        self.MapGet("/notices", async (HttpContext http, IRequestAuthorityResolver resolver,
            ISelfServiceAuthorizationService decisions, [FromServices] AccountNoticeService notices,
            CancellationToken ct) =>
        {
            var authority = await RequireSelfAsync(http, resolver, decisions, ct);
            return Results.Ok(await notices.ListAsync(authority.AccountId!.Value, ct));
        })
        .WithName("ListAccountNotices")
        .Produces<AccountNoticesResponse>();

        self.MapPost("/notices/{key}/dismiss", async (string key, HttpContext http, IRequestAuthorityResolver resolver,
            ISelfServiceAuthorizationService decisions, [FromServices] AccountNoticeService notices,
            CancellationToken ct) =>
        {
            var authority = await RequireSelfAsync(http, resolver, decisions, ct);
            return await notices.DismissAsync(authority.AccountId!.Value, key, ct)
                ? Results.NoContent()
                : ApiErrors.BadRequest("That reminder is not one the Library knows.");
        })
        .WithName("DismissAccountNotice")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest);
    }

    internal static IReadOnlyList<string> AttachedAuthenticationMethods(
        bool hasPassword,
        bool hasPasskeys,
        bool hasExternalLogins)
    {
        var methods = new List<string>(3);
        if (hasPassword)
        {
            methods.Add("password");
        }
        if (hasPasskeys)
        {
            methods.Add("passkey");
        }
        if (hasExternalLogins)
        {
            methods.Add("external");
        }
        return methods;
    }

    private static void MapAdministratorUnlock(RouteGroupBuilder access)
    {
        access.MapGet("/admin-unlock", async (HttpContext http, IRequestAuthorityResolver resolver,
            IGrantAdminUnlockService unlocks, CancellationToken ct) =>
        {
            var authority = await resolver.ResolveAsync(http, ct);
            return Results.Ok(ToUnlock(await unlocks.GetStateAsync(authority, ct)));
        }).RequireEffectiveAdministratorOrHouseholdAdministrator(false).Produces<GrantAdminUnlockResponse>();

        access.MapPost("/admin-unlock", async (GrantAdminUnlockRequest request, HttpContext http,
            IRequestAuthorityResolver resolver, IGrantAdminUnlockService unlocks, CancellationToken ct) =>
        {
            try
            {
                var authority = await resolver.ResolveAsync(http, ct);
                return Results.Ok(ToUnlock(await unlocks.UnlockAsync(authority, request.Pin, ct)));
            }
            catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
            catch (InvalidOperationException ex) { return ApiErrors.Conflict(ex.Message); }
        }).RequireEffectiveAdministratorOrHouseholdAdministrator(false).RequireRateLimiting("authentication")
          .Produces<GrantAdminUnlockResponse>();

        access.MapDelete("/admin-unlock", async (HttpContext http, IRequestAuthorityResolver resolver,
            IGrantAdminUnlockService unlocks, CancellationToken ct) =>
        {
            var authority = await resolver.ResolveAsync(http, ct);
            await unlocks.LockAsync(authority, ct);
            return Results.NoContent();
        }).RequireEffectiveAdministratorOrHouseholdAdministrator(false).WithName("ExitAdministratorSurface")
          .Produces(StatusCodes.Status204NoContent);
    }

    /// <summary>
    /// Actions on a user (sign-in) that the Users and Sign-in list calls by user id: a starting password, and turning two-step codes off.
    /// Making, finding, changing the email of and deleting users are under <c>/access/users</c>; roles and access are the profile's.
    /// </summary>
    private static void MapManagedAccounts(RouteGroupBuilder access)
    {
        var group = access.MapGroup("/accounts");
        group.MapPost("/{accountId:guid}/temporary-password", async (Guid accountId, SetTemporaryPasswordRequest request,
            HttpContext http, IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            IAccountRepository accounts, IIdentityRepository identities, IProfileRepository profiles,
            [FromServices] IHouseholdRepository households,
            [FromServices] RecentSignInGuard recentSignIn,
            CancellationToken ct) => await ExecuteAsync(async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            await mutations.SetTemporaryPasswordAsync(await resolver.ResolveAsync(http, ct), accountId,
                request.TemporaryPassword, request.MustChangePassword, ct);
            var account = await accounts.GetByIdAsync(accountId, ct)
                ?? throw new KeyNotFoundException("Account not found.");
            return Results.Ok(new UserTemporaryPasswordResponse(
                await MapUser(account, identities, profiles, households, ct), account.TemporaryPasswordExpiresAt));
        })).RequireSecuredAccount()
           .RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("SetAccountTemporaryPassword")
           .Produces<UserTemporaryPasswordResponse>();

        // Someone lost their phone and their recovery codes: an administrator turns their two-step codes off so they can
        // sign in with their password and set them up again. It is audited and needs a recent sign-in.
        group.MapPost("/{accountId:guid}/two-step/reset", async (Guid accountId,
            HttpContext http, IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            IAccountRepository accounts, IIdentityRepository identities, IProfileRepository profiles,
            [FromServices] IHouseholdRepository households,
            [FromServices] RecentSignInGuard recentSignIn,
            CancellationToken ct) => await ExecuteAsync(async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            await mutations.ResetTwoStepAsync(await resolver.ResolveAsync(http, ct), accountId, ct);
            var account = await accounts.GetByIdAsync(accountId, ct)
                ?? throw new KeyNotFoundException("Account not found.");
            return Results.Ok(await MapUser(account, identities, profiles, households, ct));
        })).RequireSecuredAccount()
           .RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("ResetAccountTwoStep")
           .Produces<UserSummaryResponse>();
    }

    /// <summary>
    /// Users (sign-ins) under users, profiles and households: only a server administrator (or an application trusted with
    /// identity.users.write) lists, makes, changes the email of and deletes them (D4). Access and roles are the profile's, not here.
    /// </summary>
    private static void MapManagedUsers(RouteGroupBuilder access)
    {
        var group = access.MapGroup("/users");
        group.MapGet("/", async (HttpContext http, IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            IIdentityRepository identities, IProfileRepository profiles,
            [FromServices] IHouseholdRepository households, CancellationToken ct) => await ExecuteAsync(async () =>
        {
            // The route gate is repeated inside the service, which lists nothing for anyone else.
            var values = new List<UserSummaryResponse>();
            foreach (var user in await mutations.ListUsersAsync(await resolver.ResolveAsync(http, ct), ct))
            {
                values.Add(await MapUser(user, identities, profiles, households, ct));
            }

            return Results.Ok(values);
        })).RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersRead)
          .WithName("ListUsers")
          .Produces<IReadOnlyList<UserSummaryResponse>>();

        group.MapPost("/", async (CreateUserRequest request, HttpContext http, IRequestAuthorityResolver resolver,
            IAccountAccessMutationService mutations, IIdentityRepository identities, IProfileRepository profiles,
            [FromServices] IHouseholdRepository households, IConfigurationLoader configuration,
            [FromServices] RecentSignInGuard recentSignIn, CancellationToken ct) => await ExecuteAsync(async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            var method = request.Method?.Trim().ToLowerInvariant() switch
            {
                "invitation" => NewUserSignInMethod.Invitation,
                "temporary_password" => NewUserSignInMethod.TemporaryPassword,
                _ => throw new ArgumentException("method must be 'invitation' or 'temporary_password'."),
            };
            var created = await mutations.CreateUserAsync(await resolver.ResolveAsync(http, ct),
                new CreateUserCommand(request.Email, request.ProfileId,
                    request.NewProfile is null ? null : new NewUserProfileCommand(
                        request.NewProfile.Name, null, request.NewProfile.HouseholdId,
                        request.NewProfile.NewHouseholdName, request.NewProfile.IsKids),
                    method, request.TemporaryPassword), ct);
            var invitation = created.Invitation is null
                ? null
                : new AccountInvitationResponse(created.Invitation.AccountId, created.Invitation.Code,
                    created.Invitation.ExpiresAt,
                    PublicAddress.IsValid(configuration.LoadNetwork().Remote.PublicHostname)
                        ? configuration.LoadNetwork().Remote.PublicHostname!.Trim().TrimEnd('/')
                        : null);
            return Results.Created($"/access/users/{created.User.Id:D}",
                new CreateUserResponse(await MapUser(created.User, identities, profiles, households, ct), invitation,
                    created.User.TemporaryPasswordExpiresAt));
        })).RequireSecuredAccount()
           .RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("CreateUser")
           .Produces<CreateUserResponse>(StatusCodes.Status201Created);

        // The email a person signs in with. Their profile, roles, access and everything in it stay exactly as they were (D2).
        group.MapPut("/{userId:guid}/email", async (Guid userId, ChangeAccountEmailRequest request, HttpContext http,
            IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations, IIdentityRepository identities,
            IProfileRepository profiles, [FromServices] IHouseholdRepository households,
            [FromServices] RecentSignInGuard recentSignIn, CancellationToken ct) => await ExecuteAsync(async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            var changed = await mutations.ChangeEmailAsync(await resolver.ResolveAsync(http, ct), userId, request.Email, ct);
            return Results.Ok(await MapUser(changed, identities, profiles, households, ct));
        })).RequireSecuredAccount()
           .RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("ChangeUserEmail")
           .Produces<UserSummaryResponse>();

        // What deleting the user does, so the confirmation can say it. Changes nothing.
        group.MapGet("/{userId:guid}/deletion-preview", async (Guid userId, HttpContext http,
            IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            CancellationToken ct) => await ExecuteAsync(async () =>
        {
            var preview = await mutations.PreviewDeleteUserAsync(await resolver.ResolveAsync(http, ct), userId, ct);
            return Results.Ok(new UserDeletionPreviewResponse(
                preview.Plan.UserId, preview.Plan.Email, preview.Plan.ProfileName, preview.Plan.RemovesHousehold,
                preview.Plan.RemovesHousehold ? preview.Plan.HouseholdName : null,
                preview.Plan.HouseholdProfiles.Select(profile => new AccountDeletionProfileDto(profile.Id, profile.DisplayName)).ToList(),
                preview.RefusedReason, preview.HouseholdHasPersonalPhotos, preview.HouseholdHasSharedPhotos));
        })).RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("PreviewUserDeletion")
           .Produces<UserDeletionPreviewResponse>();

        // Deletes the sign-in only (D5); the profile and everything in it stay. The last sign-in that can look after a household that is
        // not the default one takes the household and every profile in it (D6), with every personal and shared photo and video (D18;
        // photos are deleted, never moved, and there is no choice to make). ?profiles=<id>,<id> is the list shown by the preview (empty for a
        // sign-in-only delete): if what would go differs, the Engine refuses with 409 and deletes nothing.
        // A delete that would remove a household and sends no list is refused the same way.
        group.MapDelete("/{userId:guid}", async (Guid userId, [FromQuery] string? profiles,
            HttpContext http, IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            [FromServices] RecentSignInGuard recentSignIn, CancellationToken ct) => await ExecuteAsync(async () =>
        {
            // The recent sign-in check comes first, so a stale session learns nothing from how the request is read.
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            var confirmed = ParseConfirmedProfiles(profiles);
            await mutations.DeleteUserAsync(await resolver.ResolveAsync(http, ct), userId, confirmed, ct);
            return Results.NoContent();
        })).RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("DeleteUser")
           .Produces(StatusCodes.Status204NoContent);
    }

    private static async Task<UserSummaryResponse> MapUser(Account user, IIdentityRepository identities,
        IProfileRepository profiles, [FromServices] IHouseholdRepository households, CancellationToken ct)
    {
        var profile = user.ProfileId is { } profileId ? await profiles.GetByIdAsync(profileId, ct) : null;
        var household = profile?.HouseholdId is { } householdId ? await households.GetByIdAsync(householdId, ct) : null;
        return new UserSummaryResponse(user.Id, user.Email, profile?.Id, profile?.DisplayName, profile?.HouseholdId, household?.Name,
            await identities.GetAccountCredentialAsync(user.Id, AccountCredentialKind.Password, ct) is not null,
            await identities.GetAccountTwoStepAsync(user.Id, ct) is { IsEnabled: true },
            user.IsEnabled, user.IsThisComputerOnly);
    }

    private static void MapManagedProfiles(RouteGroupBuilder access)
    {
        access.MapGet("/libraries", async (HttpContext http, IRequestAuthorityResolver resolver,
            IProfileAccessRepository profileAccess, IConfigurationLoader configuration, [Microsoft.AspNetCore.Mvc.FromServices] IVaultLibraryRepository vaultLibraries,
            [Microsoft.AspNetCore.Mvc.FromServices] IHouseholdLibraryAccessRepository householdLibraries, CancellationToken ct) =>
        {
            // The Vault is in no library list, not even the one an administrator picks the libraries to hide from (Videos spec D45).
            var vaultIds = await vaultLibraries.GetIdsAsync(ct);
            // A household administrator sees every library, with the ones hidden from their own profile marked: they can't be changed.
            var household = await HouseholdOnlyScopeAsync(http, resolver, ct);
            IReadOnlySet<Guid>? hiddenFromActor = null;
            // The libraries hidden for the household administrator's household can't be ticked for anyone in it (library access D2).
            IReadOnlySet<Guid>? hiddenForHousehold = null;
            if (household is { } ownHousehold)
            {
                var actor = await resolver.ResolveAsync(http, ct);
                hiddenFromActor = actor.ActiveProfileId is { } actingProfile
                    ? await profileAccess.GetHiddenLibrariesAsync(actingProfile, ct)
                    : new HashSet<Guid>();
                hiddenForHousehold = await householdLibraries.GetHiddenLibrariesAsync(ownHousehold, ct);
            }

            var values = configuration.LoadLibraries().Libraries
                .Where(library => library.Kind == LibraryKinds.Catalogued && !library.IsVault && !vaultIds.Contains(library.Id) &&
                    Guid.TryParse(library.Id, out _))
                .Select(library => new AccessLibraryOptionDto(Guid.Parse(library.Id),
                    library.Name, library.Category, library.Area,
                    hiddenFromActor?.Contains(Guid.Parse(library.Id)) == true,
                    hiddenForHousehold?.Contains(Guid.Parse(library.Id)) == true))
                .ToList();
            return Results.Ok(values);
        }).RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersRead)
          .Produces<IReadOnlyList<AccessLibraryOptionDto>>();

        // Who would stop seeing these files if they moved into a library (simpler access D13). Administrators only: it names other people.
        access.MapPost("/visibility-after-move", async (VisibilityAfterMoveRequest request,
            [FromServices] IVisibilityAfterMoveReadService service, CancellationToken ct) =>
        {
            var result = await service.GetAsync(request, ct);
            return result.People is { } people
                ? Results.Ok(new VisibilityAfterMoveResponse(people))
                : ApiErrors.BadRequest(result.Refusal ?? "That can't be checked.");
        }).RequireEffectiveAdministrator()
          .WithName("GetVisibilityAfterMove")
          .WithSummary("Which people (profiles) can see the given files (a folder or up to 5,000 file ids) now but would not once they are in the target library. Counts files; never lists administrators.")
          .Produces<VisibilityAfterMoveResponse>()
          .ProducesProblem(StatusCodes.Status400BadRequest);

        access.MapHouseholdLibraryAccess();
        access.MapHouseholdKidSettings();

        var profiles = access.MapGroup("/profiles");
        profiles.MapGet("/", async (HttpContext http, IRequestAuthorityResolver resolver,
            IProfileRepository repository, IAccountRepository accounts, IProfileAccessRepository profileAccess,
            [FromServices] IHouseholdLibraryAccessRepository householdLibraries, CancellationToken ct) =>
        {
            var household = await HouseholdOnlyScopeAsync(http, resolver, ct);
            var usersByProfile = (await accounts.GetAllAsync(ct)).ToDictionary(user => user.ProfileId);
            var values = new List<ManagedProfileResponse>();
            foreach (var profile in (await repository.GetAllAsync(ct))
                .Where(profile => household is null || profile.HouseholdId == household))
            {
                values.Add(await MapProfileDetailAsync(profile, usersByProfile.GetValueOrDefault(profile.Id), profileAccess, householdLibraries, ct));
            }

            return Results.Ok(values);
        })
            .RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersRead)
            .Produces<IReadOnlyList<ManagedProfileResponse>>();

        // Households: a server administrator lists, adds, renames and removes them; a household administrator only
        // lists their own. Removing is refused (409) while any person or sign-in belongs to the household.
        access.MapGet("/households", async (HttpContext http, IRequestAuthorityResolver resolver,
            [FromServices] HouseholdManagementService service, [FromServices] IHouseholdLibraryAccessRepository householdLibraries,
            CancellationToken ct) => await ExecuteAsync(async () =>
        {
            var listed = new List<HouseholdResponse>();
            foreach (var summary in await service.ListAsync(await resolver.ResolveAsync(http, ct), ct))
            {
                var hidden = await householdLibraries.GetHiddenLibrariesAsync(summary.Household.Id, ct);
                listed.Add(MapHousehold(summary) with { HiddenLibraryIds = hidden.Order().ToList() });
            }

            return Results.Ok(listed);
        }))
            .RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersRead)
            .WithName("ListHouseholds").Produces<IReadOnlyList<HouseholdResponse>>();

        access.MapPost("/households", async (CreateHouseholdRequest request, HttpContext http,
            IRequestAuthorityResolver resolver, [FromServices] HouseholdManagementService service,
            [FromServices] RecentSignInGuard recentSignIn, CancellationToken ct) => await ExecuteAsync(async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            var created = await service.CreateAsync(await resolver.ResolveAsync(http, ct), request.Name, ct);
            return Results.Created($"/access/households/{created.Household.Id:D}", MapHousehold(created));
        })).RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("CreateHousehold").Produces<HouseholdResponse>(StatusCodes.Status201Created);

        access.MapPut("/households/{householdId:guid}", async (Guid householdId, RenameHouseholdRequest request,
            HttpContext http, IRequestAuthorityResolver resolver, [FromServices] HouseholdManagementService service,
            [FromServices] RecentSignInGuard recentSignIn, CancellationToken ct) => await ExecuteAsync(async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            return Results.Ok(MapHousehold(
                await service.RenameAsync(await resolver.ResolveAsync(http, ct), householdId, request.Name, ct)));
        })).RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("RenameHousehold").Produces<HouseholdResponse>();

        access.MapDelete("/households/{householdId:guid}", async (Guid householdId, HttpContext http,
            IRequestAuthorityResolver resolver, [FromServices] HouseholdManagementService service,
            [FromServices] RecentSignInGuard recentSignIn, CancellationToken ct) => await ExecuteAsync(async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            await service.DeleteAsync(await resolver.ResolveAsync(http, ct), householdId, ct);
            return Results.NoContent();
        })).RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("DeleteHousehold").Produces(StatusCodes.Status204NoContent);

        access.MapPost("/households/{householdId:guid}/people", async (Guid householdId,
            AddHouseholdPersonRequest request, HttpContext http, IRequestAuthorityResolver resolver,
            IAccountAccessMutationService mutations, [FromServices] RecentSignInGuard recentSignIn,
            CancellationToken ct) => await ExecuteAsync(async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            var profile = await mutations.AddHouseholdPersonAsync(await resolver.ResolveAsync(http, ct),
                new AddHouseholdPersonCommand(householdId, request.DisplayName, request.AvatarColor,
                    request.IsChild, request.Pin, request.AvatarIcon), ct);
            return Results.Created($"/access/profiles/{profile.Id:D}", MapProfile(profile));
        })).RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("AddHouseholdPerson").Produces<ManagedProfileResponse>(StatusCodes.Status201Created);

        // The order people are listed in, shared by the whole household. Same authority as adding a person.
        access.MapPut("/households/{householdId:guid}/people/order", async (Guid householdId,
            ReorderHouseholdProfilesRequest request, HttpContext http, IRequestAuthorityResolver resolver,
            IAccountAccessMutationService mutations, CancellationToken ct) => await ExecuteAsync(async () =>
        {
            await mutations.ReorderHouseholdProfilesAsync(await resolver.ResolveAsync(http, ct), householdId,
                request.ProfileIds ?? [], ct);
            return Results.NoContent();
        })).RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("ReorderHouseholdPeople").Produces(StatusCodes.Status204NoContent);

        // What a profile may use. A household administrator hands out only what their own profile holds, only inside their household.
        profiles.MapPut("/{profileId:guid}/access", async (Guid profileId, ReplaceProfileAccessRequest request,
            HttpContext http, IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            [FromServices] RecentSignInGuard recentSignIn, CancellationToken ct) => await ExecuteAsync(async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            if (request.FeatureIds is null || request.HiddenLibraryIds is null)
            {
                throw new ArgumentException("Send both feature_ids and hidden_library_ids (either can be an empty list).");
            }

            await mutations.ReplaceProfileAccessAsync(await resolver.ResolveAsync(http, ct), profileId,
                request.FeatureIds.Select(id => new AccountFeatureId(id)).ToHashSet(),
                request.HiddenLibraryIds.ToHashSet(), request.ExpectedAccessVersion, ct);
            return Results.NoContent();
        })).RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("ReplaceProfileAccess")
           .Produces(StatusCodes.Status204NoContent);

        // Household administrator and server administrator roles of a profile (D15, D17).
        profiles.MapPut("/{profileId:guid}/roles", async (Guid profileId, SetProfileRolesRequest request,
            HttpContext http, IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            [FromServices] RecentSignInGuard recentSignIn, CancellationToken ct) => await ExecuteAsync(async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            await mutations.SetProfileRolesAsync(await resolver.ResolveAsync(http, ct), profileId,
                request.IsHouseholdAdmin, request.IsServerAdmin, ct);
            return Results.NoContent();
        })).RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("SetProfileRoles")
           .Produces(StatusCodes.Status204NoContent);

        profiles.MapPut("/{profileId:guid}", async (Guid profileId,
            UpdateManagedProfileRequest request, HttpContext http, IRequestAuthorityResolver resolver,
            IAccountAccessMutationService mutations,
            [FromServices] RecentSignInGuard recentSignIn,
            CancellationToken ct) => await ExecuteAsync(async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            var profile = await mutations.UpdateProfileAsync(await resolver.ResolveAsync(http, ct),
                profileId, new UpdateManagedProfileCommand(request.DisplayName, request.AvatarColor, request.AvatarIcon,
                    request.IsKids), ct);
            return Results.Ok(MapProfile(profile));
        })).RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .Produces<ManagedProfileResponse>();

        // "Ask for my PIN on admin pages" belongs to the profile. A person reads and changes it on their own profile, a server
        // administrator on any; a household administrator never on another administrator's profile.
        profiles.MapGet("/{profileId:guid}/admin-protection", async (Guid profileId, HttpContext http,
            IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            CancellationToken ct) => await ExecuteAsync(async () =>
        {
            var protection = await mutations.GetProfileAdminProtectionAsync(await resolver.ResolveAsync(http, ct), profileId, ct);
            return Results.Ok(MapAdminProtection(protection));
        })).RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersRead)
           .WithName("GetProfileAdminProtection")
           .Produces<ProfileAdminProtectionResponse>();

        profiles.MapPut("/{profileId:guid}/admin-protection", async (Guid profileId,
            SetProfileAdminProtectionRequest request, HttpContext http, IRequestAuthorityResolver resolver,
            IAccountAccessMutationService mutations,
            [FromServices] RecentSignInGuard recentSignIn,
            CancellationToken ct) => await ExecuteAsync(async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            if (!Enum.TryParse<AdminUnlockMode>(request.UnlockMode, true, out var mode) || !Enum.IsDefined(mode))
            {
                return ApiErrors.BadRequest("Unknown unlock mode.");
            }

            await mutations.SetProfileAdminProtectionAsync(await resolver.ResolveAsync(http, ct), profileId,
                new ProfileAdminProtectionCommand(request.Enabled, mode, request.UnlockMinutes), ct);
            return Results.NoContent();
        })).RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("SetProfileAdminProtection")
           .Produces(StatusCodes.Status204NoContent);

        // ?photos=move (the default) keeps the person's personal photos in the household's Shared Library; ?photos=delete removes them.
        profiles.MapDelete("/{profileId:guid}", async (Guid profileId, [FromQuery] string? photos, HttpContext http,
            IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            [FromServices] RecentSignInGuard recentSignIn, CancellationToken ct) => await ExecuteAsync(async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            var disposition = photos?.Trim().ToLowerInvariant() switch
            {
                null or "" or "move" => ProfilePhotoDisposition.MoveToShared,
                "delete" => ProfilePhotoDisposition.Delete,
                _ => throw new ArgumentException("photos must be 'move' or 'delete'."),
            };
            await mutations.DeleteProfileAsync(await resolver.ResolveAsync(http, ct), profileId, disposition, ct);
            return Results.NoContent();
        })).RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("DeleteManagedProfile")
           .Produces(StatusCodes.Status204NoContent);

        // A photo for someone else's profile: the existing /profiles/{id}/avatar only lets people change their own.
        profiles.MapPost("/{profileId:guid}/avatar", async (Guid profileId, HttpContext http,
            IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations, [FromServices] IProfileService profileService,
            [FromServices] TuvimaDataPaths dataPaths, [FromServices] RecentSignInGuard recentSignIn,
            CancellationToken ct) => await ExecuteAsync(async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            await mutations.RequireCanChangeProfileAsync(await resolver.ResolveAsync(http, ct), profileId, ct);
            return await ProfileEndpoints.UploadProfileAvatarAsync(profileId, http.Request, profileService, dataPaths, ct);
        })).RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("UploadManagedProfileAvatar")
           .DisableAntiforgery()
           .Produces<ProfileResponseDto>();

        profiles.MapDelete("/{profileId:guid}/avatar", async (Guid profileId, HttpContext http,
            IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations, [FromServices] IProfileService profileService,
            [FromServices] RecentSignInGuard recentSignIn, CancellationToken ct) => await ExecuteAsync(async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            await mutations.RequireCanChangeProfileAsync(await resolver.ResolveAsync(http, ct), profileId, ct);
            return await ProfileEndpoints.RemoveProfileAvatarAsync(profileId, profileService, ct);
        })).RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("RemoveManagedProfileAvatar")
           .Produces<ProfileResponseDto>();
    }

    /// <summary>
    /// The household a household administrator is limited to, or <see langword="null"/> for a server administrator or a
    /// trusted application, who see every household.
    /// </summary>
    private static async Task<Guid?> HouseholdOnlyScopeAsync(HttpContext http, IRequestAuthorityResolver resolver, CancellationToken ct)
    {
        var authority = await resolver.ResolveAsync(http, ct);
        return authority.PrincipalKind == PrincipalKind.Human && !authority.IsEffectiveAdministrator &&
            authority.IsEffectiveHouseholdAdministrator
            ? authority.ActiveProfileHouseholdId
            : null;
    }

    private static HouseholdResponse MapHousehold(HouseholdSummary summary) =>
        new(summary.Household.Id, summary.Household.Name, summary.ProfileCount, summary.AccountCount,
            summary.Household.CreatedAt);

    /// <summary>The comma-separated profile ids a delete was confirmed for; <see langword="null"/> when none were sent.</summary>
    private static IReadOnlyCollection<Guid>? ParseConfirmedProfiles(string? profiles)
    {
        if (profiles is null)
        {
            return null;
        }

        var ids = new List<Guid>();
        foreach (var part in profiles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Guid.TryParse(part, out var id))
            {
                throw new ArgumentException("profiles must be a comma-separated list of profile ids.");
            }

            ids.Add(id);
        }

        return ids.Distinct().ToArray();
    }

    internal static async Task<IResult> ExecuteAsync(Func<Task<IResult>> operation)
    {
        try { return await operation(); }
        catch (ArgumentException ex) { return ApiErrors.BadRequest(ex.Message); }
        catch (KeyNotFoundException ex) { return ApiErrors.NotFound(ex.Message); }
        catch (SecureAdministratorsFirstException ex) { return ApiErrors.Conflict(HouseholdRules.SecureAdministratorsFirstCode, ex.Message); }
        catch (InvalidOperationException ex) { return ApiErrors.Conflict(ex.Message); }
        catch (UnauthorizedAccessException ex) { return ApiErrors.Forbidden(ex.Message); }
    }

    private static GrantAdminUnlockResponse ToUnlock(GrantAdminUnlockState value) =>
        new(value.IsUnlocked, value.ExpiresAt, value.ProtectionVersion);

    private static async ValueTask<RequestAuthority> RequireSelfAsync(HttpContext http,
        IRequestAuthorityResolver resolver, ISelfServiceAuthorizationService decisions, CancellationToken ct)
    {
        var authority = await resolver.ResolveAsync(http, ct);
        if (authority.AccountId is not { } accountId ||
            !(await decisions.EvaluateAccountAsync(authority, accountId, ct)).IsAllowed)
        {
            throw new UnauthorizedAccessException();
        }

        return authority;
    }

    private static ProfileAdminProtectionResponse MapAdminProtection(ProfileAdminProtection protection) =>
        new(protection.IsEnabled, protection.UnlockMode, protection.UnlockMinutes, protection.ProtectionVersion);

    private static ManagedProfileResponse MapProfile(
        MediaEngine.Domain.Aggregates.Profile profile) =>
        new(profile.Id, profile.DisplayName, profile.AvatarColor, profile.AvatarImagePath,
            profile.CreatedAt, profile.HouseholdId,
            profile.Role == MediaEngine.Domain.Enums.ProfileRole.RestrictedProfile,
            profile.AvatarIcon,
            profile.IsServerAdministrator, profile.IsHouseholdAdministrator,
            AccessVersion: profile.AuthorizationVersion,
            SortPosition: profile.SortPosition);

    /// <summary>A profile with its roles, what it may use and the user it opens, for the lists that manage profiles.</summary>
    private static async Task<ManagedProfileResponse> MapProfileDetailAsync(
        MediaEngine.Domain.Aggregates.Profile profile, Account? user,
        IProfileAccessRepository profileAccess, IHouseholdLibraryAccessRepository householdLibraries, CancellationToken ct) =>
        MapProfile(profile) with
        {
            HouseholdHiddenLibraryIds = profile.HouseholdId is { } householdId
                ? (await householdLibraries.GetHiddenLibrariesAsync(householdId, ct)).Order().ToList()
                : [],
            FeatureIds = (await profileAccess.GetFeatureGrantsAsync(profile.Id, ct)).Select(feature => feature.Value).Order().ToList(),
            HiddenLibraryIds = (await profileAccess.GetHiddenLibrariesAsync(profile.Id, ct)).Order().ToList(),
            User = user is null ? null : new ManagedProfileUserDto(user.Id, user.Email),
        };

    private static ValueTask WriteAuditAsync(IAuthorizationAuditWriter audit, TimeProvider clock,
        RequestAuthority authority, string eventType, Guid loginId, CancellationToken ct) =>
        audit.WriteAsync(new AuthorizationAuditEvent(eventType, clock.GetUtcNow(), authority.AccountId,
            authority.ActiveProfileId, authority.ApplicationId, "external_login", loginId.ToString("D"),
            new Dictionary<string, string?> { ["changed"] = "true" }), ct);
}
