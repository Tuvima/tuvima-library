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
            var grants = await MapGrants(account.Id, accounts, profiles, profileAccess, ct);
            var defaultId = grants.FirstOrDefault(grant => grant.IsDefault && grant.IsEnabled)?.ProfileId
                ?? authority.ActiveProfileId!.Value;

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

    private static void MapManagedAccounts(RouteGroupBuilder access)
    {
        var group = access.MapGroup("/accounts");
        group.MapGet("/", async (HttpContext http, IRequestAuthorityResolver resolver,
            IAccountRepository accounts, IIdentityRepository identities,
            IProfileRepository profiles, IProfileAccessRepository profileAccess, IConfigurationLoader configuration, CancellationToken ct) =>
        {
            var household = await HouseholdOnlyScopeAsync(http, resolver, ct);
            var values = new List<AccountAccessResponse>();
            foreach (var account in await accounts.GetAllAsync(ct))
            {
                if (household is { } own && account.HouseholdId != own)
                {
                    continue;
                }

                values.Add(await MapAccount(account, accounts, identities, profiles, profileAccess, configuration, ct));
            }

            return Results.Ok(values);
        }).RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersRead)
          .Produces<IReadOnlyList<AccountAccessResponse>>();

        group.MapGet("/{accountId:guid}", async (Guid accountId, HttpContext http, IRequestAuthorityResolver resolver,
            IAccountRepository accounts, IIdentityRepository identities, IProfileRepository profiles, IProfileAccessRepository profileAccess,
            IConfigurationLoader configuration, CancellationToken ct) =>
        {
            var household = await HouseholdOnlyScopeAsync(http, resolver, ct);
            var account = await accounts.GetByIdAsync(accountId, ct);
            if (household is { } own && account is not null && account.HouseholdId != own)
            {
                // Same answer as a missing account, so another household's sign-ins can't be discovered.
                account = null;
            }

            return account is null ? ApiErrors.NotFound("Account not found.")
                : Results.Ok(await MapAccount(account, accounts, identities, profiles, profileAccess, configuration, ct));
        }).RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersRead)
          .Produces<AccountAccessResponse>();

        group.MapPost("/", async (CreateManagedAccountRequest request, HttpContext http,
            IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            IAccountRepository accounts, IIdentityRepository identities, IProfileRepository profiles, IProfileAccessRepository profileAccess,
            IConfigurationLoader configuration,
            [FromServices] RecentSignInGuard recentSignIn,
            CancellationToken ct) => await ExecuteAsync(async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            var command = new CreateAccountAccessCommand(request.Email,
                request.IsAdministrator, request.ProfileId,
                request.NewProfile is null ? null : new NewAccountProfileCommand(
                    request.NewProfile.DisplayName, request.NewProfile.AvatarColor),
                FeaturesForNewAccount(request.FeatureIds, request.NewProfile is not null),
                request.LibraryIds.ToHashSet(),
                request.TemporaryPassword);
            var account = await mutations.CreateAsync(await resolver.ResolveAsync(http, ct), command, ct);
            return Results.Created($"/access/accounts/{account.Id:D}",
                await MapAccount(account, accounts, identities, profiles, profileAccess, configuration, ct));
        })).RequireSecuredAccount()
           .RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .Produces<AccountAccessResponse>(StatusCodes.Status201Created);

        group.MapPost("/{accountId:guid}/temporary-password", async (Guid accountId, SetTemporaryPasswordRequest request,
            HttpContext http, IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            IAccountRepository accounts, IIdentityRepository identities, IProfileRepository profiles, IProfileAccessRepository profileAccess,
            IConfigurationLoader configuration,
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
            return Results.Ok(await MapAccount(account, accounts, identities, profiles, profileAccess, configuration, ct));
        })).RequireSecuredAccount()
           .RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("SetAccountTemporaryPassword")
           .Produces<AccountAccessResponse>();

        // The email a person signs in with. Not self-service: a server administrator, or the household administrator of the
        // account's household, changes it, and it is audited.
        group.MapPut("/{accountId:guid}/email", async (Guid accountId, ChangeAccountEmailRequest request,
            HttpContext http, IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            IAccountRepository accounts, IIdentityRepository identities, IProfileRepository profiles, IProfileAccessRepository profileAccess,
            IConfigurationLoader configuration,
            [FromServices] RecentSignInGuard recentSignIn,
            CancellationToken ct) => await ExecuteAsync(async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            var account = await mutations.ChangeEmailAsync(await resolver.ResolveAsync(http, ct), accountId,
                request.Email, ct);
            return Results.Ok(await MapAccount(account, accounts, identities, profiles, profileAccess, configuration, ct));
        })).RequireSecuredAccount()
           .RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("ChangeAccountEmail")
           .Produces<AccountAccessResponse>();

        // Someone lost their phone and their recovery codes: an administrator turns their two-step codes off so they can
        // sign in with their password and set them up again. It is audited and needs a recent sign-in.
        group.MapPost("/{accountId:guid}/two-step/reset", async (Guid accountId,
            HttpContext http, IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            IAccountRepository accounts, IIdentityRepository identities, IProfileRepository profiles, IProfileAccessRepository profileAccess,
            IConfigurationLoader configuration,
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
            return Results.Ok(await MapAccount(account, accounts, identities, profiles, profileAccess, configuration, ct));
        })).RequireSecuredAccount()
           .RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("ResetAccountTwoStep")
           .Produces<AccountAccessResponse>();

        group.MapPut("/{accountId:guid}", async (Guid accountId, UpdateManagedAccountRequest request,
            HttpContext http, IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            IAccountRepository accounts, IIdentityRepository identities, IProfileRepository profiles, IProfileAccessRepository profileAccess,
            IConfigurationLoader configuration,
            [FromServices] RecentSignInGuard recentSignIn,
            CancellationToken ct) => await ExecuteAsync(async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            var value = await mutations.UpdateAsync(await resolver.ResolveAsync(http, ct), accountId,
                new UpdateAccountAccessCommand(request.Email,
                    request.IsEnabled, request.IsAdministrator), ct);
            return Results.Ok(await MapAccount(value, accounts, identities, profiles, profileAccess, configuration, ct));
        })).RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .Produces<AccountAccessResponse>();

        // The profiles Delete user would take with this user (only when it takes a household), so the confirmation can name them.
        // Changes nothing. The same answer as GET /access/users/{id}/deletion-preview, in the older shape.
        group.MapGet("/{accountId:guid}/deletion-preview", async (Guid accountId, HttpContext http,
            IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            CancellationToken ct) => await ExecuteAsync(async () =>
            Results.Ok(new AccountDeletionPreviewResponse(
                (await mutations.PreviewDeleteUserAsync(await resolver.ResolveAsync(http, ct), accountId, ct))
                .Plan.HouseholdProfiles.Select(profile => new AccountDeletionProfileDto(profile.Id, profile.DisplayName)).ToList()))))
            .RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
            .WithName("PreviewAccountDeletion")
            .Produces<AccountDeletionPreviewResponse>();

        // Deletes a user's sign-in (D5): only a server administrator (or a trusted application) may, and the profile and everything
        // in it stay. The last sign-in that can look after a household that is not the default one takes the household and its
        // profiles (D6). ?photos=move (the default, also for a missing or unknown value) keeps the removed profiles' personal
        // photos in the Shared Library; ?photos=delete removes them. ?profiles=<id>,<id> is the list the person
        // confirmed (from the deletion preview; present but empty means none): if the profiles that would go differ, the
        // Engine refuses with 409 "Something changed since you looked..." and deletes nothing. A delete that would remove a household and
        // sends no list is refused the same way.
        group.MapDelete("/{accountId:guid}", async (Guid accountId, [FromQuery] string? photos, [FromQuery] string? profiles,
            HttpContext http,
            IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            [FromServices] RecentSignInGuard recentSignIn,
            CancellationToken ct) => await ExecuteAsync(async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            var disposition = string.Equals(photos?.Trim(), "delete", StringComparison.OrdinalIgnoreCase)
                ? ProfilePhotoDisposition.Delete
                : ProfilePhotoDisposition.MoveToShared;
            await mutations.DeleteUserAsync(
                await resolver.ResolveAsync(http, ct), accountId, disposition, ParseConfirmedProfiles(profiles), ct);
            return Results.NoContent();
        })).RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("DeleteAccount")
           .Produces(StatusCodes.Status204NoContent);

        // Only a server administrator decides who looks after a household.
        group.MapPut("/{accountId:guid}/household-admin", async (Guid accountId, SetHouseholdAdminRequest request,
            HttpContext http, IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            IAccountRepository accounts, IIdentityRepository identities, IProfileRepository profiles, IProfileAccessRepository profileAccess,
            IConfigurationLoader configuration,
            [FromServices] RecentSignInGuard recentSignIn,
            CancellationToken ct) => await ExecuteAsync(async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            await mutations.SetHouseholdAdminAsync(await resolver.ResolveAsync(http, ct), accountId,
                request.HouseholdAdmin, ct);
            var account = await accounts.GetByIdAsync(accountId, ct)
                ?? throw new KeyNotFoundException("Account not found.");
            return Results.Ok(await MapAccount(account, accounts, identities, profiles, profileAccess, configuration, ct));
        })).RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("SetAccountHouseholdAdmin")
           .Produces<AccountAccessResponse>();

        group.MapDelete("/{accountId:guid}/own-sign-in", async (Guid accountId, HttpContext http,
            IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            [FromServices] RecentSignInGuard recentSignIn,
            CancellationToken ct) => await ExecuteAsync(async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            await mutations.RemoveOwnSignInAsync(await resolver.ResolveAsync(http, ct), accountId, ct);
            return Results.NoContent();
        })).RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("RemoveOwnSignIn").Produces(StatusCodes.Status204NoContent);

        group.MapPut("/{accountId:guid}/access", async (Guid accountId,
            ReplaceAccountAccessRequest request, HttpContext http, IRequestAuthorityResolver resolver,
            IAccountAccessMutationService mutations, [FromServices] RecentSignInGuard recentSignIn,
            CancellationToken ct) => await ExecuteAsync(async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            await mutations.ReplaceAccessAsync(await resolver.ResolveAsync(http, ct), accountId,
                request.FeatureIds.Select(id => new AccountFeatureId(id)).ToHashSet(),
                request.LibraryIds.ToHashSet(), ct);
            return Results.NoContent();
        })).RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("ReplaceAccountAccess").Produces(StatusCodes.Status204NoContent);

        group.MapPut("/{accountId:guid}/grants/{profileId:guid}", async (Guid accountId,
            Guid profileId, SetAccountProfileGrantAccessRequest request, HttpContext http,
            IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            [FromServices] RecentSignInGuard recentSignIn,
            CancellationToken ct) => await ExecuteAsync(async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            await mutations.UpsertGrantAsync(await resolver.ResolveAsync(http, ct), new AccountProfileGrant
            {
                AccountId = accountId,
                ProfileId = profileId,
                IsDefault = request.IsDefault,
                IsEnabled = true,
                AdminEnabled = request.AdminEnabled,
                AuthorizationVersion = 1,
            }, ct);
            return Results.NoContent();
        })).RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("SetAccountProfileGrant").Produces(StatusCodes.Status204NoContent);

        group.MapDelete("/{accountId:guid}/grants/{profileId:guid}", async (Guid accountId,
            Guid profileId, HttpContext http, IRequestAuthorityResolver resolver,
            IAccountAccessMutationService mutations, [FromServices] RecentSignInGuard recentSignIn,
            CancellationToken ct) => await ExecuteAsync(async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            await mutations.RevokeGrantAsync(await resolver.ResolveAsync(http, ct), accountId, profileId, ct);
            return Results.NoContent();
        })).RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("RevokeAccountProfileGrant").Produces(StatusCodes.Status204NoContent);

        group.MapPut("/{accountId:guid}/grants/{profileId:guid}/admin-protection", async (
            Guid accountId, Guid profileId, SetGrantAdminProtectionRequest request, HttpContext http,
            IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
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

            await mutations.SetAdminProtectionAsync(await resolver.ResolveAsync(http, ct), accountId,
                profileId, new GrantAdminProtectionCommand(
                    request.Enabled, mode, request.UnlockMinutes), ct);
            return Results.NoContent();
        })).RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("SetAccountProfileAdminProtection").Produces(StatusCodes.Status204NoContent);

        access.MapPost("/invitations", async (CreateAccountInvitationRequest request, HttpContext http,
            IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            IConfigurationLoader configuration,
            [FromServices] RecentSignInGuard recentSignIn,
            CancellationToken ct) => await ExecuteAsync(async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            var issued = await mutations.IssueInvitationAsync(await resolver.ResolveAsync(http, ct),
                new IssueAccountInvitationCommand(request.Email, request.ProfileIds,
                    request.DefaultProfileId, request.NewHouseholdPersonName), ct);
            return Results.Ok(new AccountInvitationResponse(
                issued.AccountId, issued.Code, issued.ExpiresAt,
                PublicAddress.IsValid(configuration.LoadNetwork().Remote.PublicHostname)
                    ? configuration.LoadNetwork().Remote.PublicHostname!.Trim().TrimEnd('/')
                    : null));
        })).RequireSecuredAccount()
           .RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .Produces<AccountInvitationResponse>();
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
                preview.RefusedReason, preview.HouseholdHasPersonalPhotos));
        })).RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("PreviewUserDeletion")
           .Produces<UserDeletionPreviewResponse>();

        // Deletes the sign-in only (D5); the profile and everything in it stay. The last sign-in that can look after a household that is
        // not the default one takes the household and every profile in it (D6). ?photos=move (the default) keeps the removed profiles'
        // photos in the Shared Library; ?photos=delete removes them. ?profiles=<id>,<id> is the list shown by the preview (empty for a
        // sign-in-only delete): if what would go differs, the Engine refuses with 409 and deletes nothing.
        // A delete that would remove a household and sends no list is refused the same way.
        group.MapDelete("/{userId:guid}", async (Guid userId, [FromQuery] string? photos, [FromQuery] string? profiles,
            HttpContext http, IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            [FromServices] RecentSignInGuard recentSignIn, CancellationToken ct) => await ExecuteAsync(async () =>
        {
            // The recent sign-in check comes first, so a stale session learns nothing from how the request is read.
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
            var confirmed = ParseConfirmedProfiles(profiles);
            await mutations.DeleteUserAsync(await resolver.ResolveAsync(http, ct), userId, disposition, confirmed, ct);
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
            IProfileAccessRepository profileAccess, IConfigurationLoader configuration, [Microsoft.AspNetCore.Mvc.FromServices] IVaultLibraryRepository vaultLibraries, CancellationToken ct) =>
        {
            // The Vault is in no library list, not even the one an administrator picks grants from (Videos spec D45).
            var vaultIds = await vaultLibraries.GetIdsAsync(ct);
            // A household administrator can only hand out what the household already has.
            var household = await HouseholdOnlyScopeAsync(http, resolver, ct);
            IReadOnlySet<Guid>? allowed = null;
            if (household is not null)
            {
                // A household administrator hands out what the profile they are acting as already holds.
                var actor = await resolver.ResolveAsync(http, ct);
                allowed = actor.ActiveProfileId is { } actingProfile
                    ? await profileAccess.GetLibraryGrantsAsync(actingProfile, ct)
                    : new HashSet<Guid>();
            }

            var values = configuration.LoadLibraries().Libraries
                .Where(library => library.Kind == LibraryKinds.Catalogued && !vaultIds.Contains(library.Id) &&
                    Guid.TryParse(library.Id, out var id) && (allowed is null || allowed.Contains(id)))
                .Select(library => new AccessLibraryOptionDto(Guid.Parse(library.Id),
                    library.Name, library.Category, library.Area))
                .ToList();
            return Results.Ok(values);
        }).RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersRead)
          .Produces<IReadOnlyList<AccessLibraryOptionDto>>();

        var profiles = access.MapGroup("/profiles");
        profiles.MapGet("/", async (HttpContext http, IRequestAuthorityResolver resolver,
            IProfileRepository repository, IAccountRepository accounts, IProfileAccessRepository profileAccess,
            CancellationToken ct) =>
        {
            var household = await HouseholdOnlyScopeAsync(http, resolver, ct);
            var ownSignIns = await OwnSignInsByProfileAsync(accounts, ct);
            var usersByProfile = (await accounts.GetAllAsync(ct))
                .Where(user => user.ProfileId is not null)
                .ToDictionary(user => user.ProfileId!.Value);
            var values = new List<ManagedProfileResponse>();
            foreach (var profile in (await repository.GetAllAsync(ct))
                .Where(profile => household is null || profile.HouseholdId == household))
            {
                values.Add(await MapProfileDetailAsync(profile, ownSignIns.GetValueOrDefault(profile.Id),
                    usersByProfile.GetValueOrDefault(profile.Id), profileAccess, ct));
            }

            return Results.Ok(values);
        })
            .RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersRead)
            .Produces<IReadOnlyList<ManagedProfileResponse>>();

        profiles.MapPost("/", async (CreateManagedProfileRequest request, HttpContext http,
            IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            CancellationToken ct) => await ExecuteAsync(async () =>
        {
            var profile = await mutations.CreateProfileAsync(await resolver.ResolveAsync(http, ct),
                new CreateManagedProfileCommand(request.AccountId, request.DisplayName,
                    request.AvatarColor, request.IsDefault), ct);
            return Results.Created($"/access/profiles/{profile.Id:D}", MapProfile(profile));
        })).RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .Produces<ManagedProfileResponse>(StatusCodes.Status201Created);

        // Households: a server administrator lists, adds, renames and removes them; a household administrator only
        // lists their own. Removing is refused (409) while any person or sign-in belongs to the household.
        access.MapGet("/households", async (HttpContext http, IRequestAuthorityResolver resolver,
            [FromServices] HouseholdManagementService service, CancellationToken ct) => await ExecuteAsync(async () =>
            Results.Ok((await service.ListAsync(await resolver.ResolveAsync(http, ct), ct)).Select(MapHousehold).ToList())))
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
                    request.IsChild, request.Pin, request.AvatarIcon, request.ContentLimit,
                    request.ContentLimitAllowUnrated), ct);
            return Results.Created($"/access/profiles/{profile.Id:D}", MapProfile(profile));
        })).RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("AddHouseholdPerson").Produces<ManagedProfileResponse>(StatusCodes.Status201Created);

        // What a profile may use. A household administrator hands out only what their own profile holds, only inside their household.
        profiles.MapPut("/{profileId:guid}/access", async (Guid profileId, ReplaceProfileAccessRequest request,
            HttpContext http, IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            [FromServices] RecentSignInGuard recentSignIn, CancellationToken ct) => await ExecuteAsync(async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            if (request.FeatureIds is null || request.LibraryIds is null)
            {
                throw new ArgumentException("Send both feature_ids and library_ids (either can be an empty list).");
            }

            await mutations.ReplaceProfileAccessAsync(await resolver.ResolveAsync(http, ct), profileId,
                request.FeatureIds.Select(id => new AccountFeatureId(id)).ToHashSet(),
                request.LibraryIds.ToHashSet(), ct);
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

        profiles.MapPost("/{profileId:guid}/own-sign-in", async (Guid profileId,
            GiveOwnSignInRequest request, HttpContext http, IRequestAuthorityResolver resolver,
            IAccountAccessMutationService mutations, IConfigurationLoader configuration,
            [FromServices] RecentSignInGuard recentSignIn,
            CancellationToken ct) => await ExecuteAsync(async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            var given = await mutations.GiveOwnSignInAsync(await resolver.ResolveAsync(http, ct),
                new GiveOwnSignInCommand(profileId, request.Email, request.TemporaryPassword,
                    request.MustChangePassword, request.Invite), ct);
            var invitation = given.Invitation is null
                ? null
                : new AccountInvitationResponse(given.Invitation.AccountId, given.Invitation.Code,
                    given.Invitation.ExpiresAt,
                    PublicAddress.IsValid(configuration.LoadNetwork().Remote.PublicHostname)
                        ? configuration.LoadNetwork().Remote.PublicHostname!.Trim().TrimEnd('/')
                        : null);
            return Results.Ok(new GiveOwnSignInResponse(given.Account.Id, given.Account.Email, invitation,
                given.Account.TemporaryPasswordExpiresAt));
        })).RequireSecuredAccount()
           .RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("GiveOwnSignIn").Produces<GiveOwnSignInResponse>();

        profiles.MapPut("/{profileId:guid}", async (Guid profileId,
            UpdateManagedProfileRequest request, HttpContext http, IRequestAuthorityResolver resolver,
            IAccountAccessMutationService mutations, IAccountRepository accounts,
            [FromServices] RecentSignInGuard recentSignIn,
            CancellationToken ct) => await ExecuteAsync(async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            var profile = await mutations.UpdateProfileAsync(await resolver.ResolveAsync(http, ct),
                profileId, new UpdateManagedProfileCommand(request.DisplayName, request.AvatarColor, request.AvatarIcon,
                    request.ContentLimit, request.ContentLimitAllowUnrated), ct);
            return Results.Ok(MapProfile(profile,
                (await OwnSignInsByProfileAsync(accounts, ct)).GetValueOrDefault(profile.Id)));
        })).RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .Produces<ManagedProfileResponse>();

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
    /// A person invited into a brand-new household is outside everyone else's household, so View starts off (D16).
    /// An administrator switches it on afterwards, per person, once they know the person.
    /// </summary>
    internal static HashSet<AccountFeatureId> FeaturesForNewAccount(IEnumerable<string> requested, bool startsNewHousehold) =>
        requested
            .Where(id => !startsNewHousehold
                || !string.Equals(id, AccountFeatureId.View.Value, StringComparison.OrdinalIgnoreCase))
            .Select(id => new AccountFeatureId(id))
            .ToHashSet();

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

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> operation)
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

    private static async Task<AccountAccessResponse> MapAccount(Account account,
        IAccountRepository accounts, IIdentityRepository identities, IProfileRepository profiles, IProfileAccessRepository profileAccess,
        IConfigurationLoader configuration, CancellationToken ct)
    {
        var libraryNames = configuration.LoadLibraries().Libraries
            .Where(library => library.Kind == LibraryKinds.Catalogued && Guid.TryParse(library.Id, out _))
            .ToDictionary(library => Guid.Parse(library.Id), library => library.Name);
        var sessions = await identities.GetSessionsAsync(account.Id, ct);
        var lastActiveAt = sessions.Count == 0 ? (DateTimeOffset?)null : sessions.Max(session => session.LastSeenAt);
        return new AccountAccessResponse(account.Id, account.Email,
            account.IsEnabled, account.IsAdministrator, account.AuthorizationVersion,
            // A user's access is its own profile's (the profile holds it); a user with no profile has none.
            (account.ProfileId is { } own ? await profileAccess.GetFeatureGrantsAsync(own, ct) : new HashSet<AccountFeatureId>())
                .Select(feature => new AccountFeatureGrantDto(feature.Value, true)).ToList(),
            (account.ProfileId is { } ownLibraries ? await profileAccess.GetLibraryGrantsAsync(ownLibraries, ct) : new HashSet<Guid>())
                .Select(id => new AccountLibraryGrantDto(
                    id, libraryNames.GetValueOrDefault(id, "Unavailable library"), true)).ToList(),
            await MapGrants(account.Id, accounts, profiles, profileAccess, ct), account.CreatedAt, account.UpdatedAt,
            lastActiveAt, account.HouseholdId, account.MustChangePassword, account.TemporaryPasswordExpiresAt,
            await identities.GetAccountTwoStepAsync(account.Id, ct) is { IsEnabled: true },
            account.GrantsInheritFromAccountId, account.HouseholdAdmin);
    }

    private static async Task<IReadOnlyList<AccountProfileGrantDto>> MapGrants(Guid accountId,
        IAccountRepository accounts, IProfileRepository profiles, IProfileAccessRepository profileAccess, CancellationToken ct)
    {
        var result = new List<AccountProfileGrantDto>();
        foreach (var grant in await accounts.GetGrantsAsync(accountId, ct))
        {
            var profile = await profiles.GetByIdAsync(grant.ProfileId, ct);
            if (profile is null)
            {
                continue;
            }

            var protection = await profileAccess.GetAdminProtectionAsync(grant.ProfileId, ct);
            result.Add(new AccountProfileGrantDto(accountId, grant.ProfileId, profile.DisplayName,
                profile.AvatarImagePath, grant.IsDefault, grant.IsEnabled, grant.AdminEnabled,
                new GrantAdminProtectionDto(protection?.IsEnabled == true,
                    protection?.UnlockMode ?? AdminUnlockMode.FixedDuration.ToString(),
                    protection?.UnlockMinutes, protection?.ProtectionVersion ?? 0),
                grant.AuthorizationVersion, grant.GrantedAt,
                profile.Role == MediaEngine.Domain.Enums.ProfileRole.RestrictedProfile));
        }
        return result;
    }

    private static ManagedProfileResponse MapProfile(
        MediaEngine.Domain.Aggregates.Profile profile, ManagedProfileSignInDto? ownSignIn = null) =>
        new(profile.Id, profile.DisplayName, profile.AvatarColor, profile.AvatarImagePath,
            profile.CreatedAt, profile.HouseholdId,
            profile.Role == MediaEngine.Domain.Enums.ProfileRole.RestrictedProfile,
            profile.AvatarIcon, profile.ContentLimit, profile.ContentLimitAllowUnrated, ownSignIn,
            profile.IsServerAdministrator, profile.IsHouseholdAdministrator);

    /// <summary>A profile with its roles, what it may use and the user it opens, for the lists that manage profiles.</summary>
    private static async Task<ManagedProfileResponse> MapProfileDetailAsync(
        MediaEngine.Domain.Aggregates.Profile profile, ManagedProfileSignInDto? ownSignIn, Account? user,
        IProfileAccessRepository profileAccess, CancellationToken ct) =>
        MapProfile(profile, ownSignIn) with
        {
            FeatureIds = (await profileAccess.GetFeatureGrantsAsync(profile.Id, ct)).Select(feature => feature.Value).Order().ToList(),
            LibraryIds = (await profileAccess.GetLibraryGrantsAsync(profile.Id, ct)).Order().ToList(),
            User = user is null ? null : new ManagedProfileUserDto(user.Id, user.Email),
        };

    /// <summary>
    /// The own sign-ins of a household's people, keyed by the person they open. An own sign-in is a non-administrator account
    /// that follows a main sign-in's access and opens exactly one person.
    /// </summary>
    private static async Task<Dictionary<Guid, ManagedProfileSignInDto>> OwnSignInsByProfileAsync(
        IAccountRepository accounts, CancellationToken ct)
    {
        var result = new Dictionary<Guid, ManagedProfileSignInDto>();
        foreach (var account in await accounts.GetAllAsync(ct))
        {
            // A person's own sign-in opens exactly the profile it is attached to.
            if (account.GrantsInheritFromAccountId is null || account.IsAdministrator || account.ProfileId is not { } opened)
            {
                continue;
            }

            result[opened] = new ManagedProfileSignInDto(
                account.Id, account.Email, account.MustChangePassword, account.IsEnabled);
        }

        return result;
    }

    private static ValueTask WriteAuditAsync(IAuthorizationAuditWriter audit, TimeProvider clock,
        RequestAuthority authority, string eventType, Guid loginId, CancellationToken ct) =>
        audit.WriteAsync(new AuthorizationAuditEvent(eventType, clock.GetUtcNow(), authority.AccountId,
            authority.ActiveProfileId, authority.ApplicationId, "external_login", loginId.ToString("D"),
            new Dictionary<string, string?> { ["changed"] = "true" }), ct);
}
