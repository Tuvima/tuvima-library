using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
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
        MapManagedProfiles(access);
        MapManagedDevices(access);
        return app;
    }

    /// <summary>
    /// Paired phones and TVs for the Apps &amp; devices panel: administrators see every device, anyone else only their
    /// own account's. Revoking ends the device's tokens and tells a connected app to sign out.
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

    private static void MapSelfService(RouteGroupBuilder access)
    {
        var self = access.MapGroup("/self-service").RequireHumanSelfService();
        self.MapGet("/", async (string? originalClientIngress, bool originalClientIsHttps,
            HttpContext http, IRequestAuthorityResolver resolver,
            ISelfServiceAuthorizationService decisions, IAccountRepository accounts,
            IProfileRepository profiles, IIdentityRepository identities,
            IAccountExternalLoginService externalLogins,
            Microsoft.AspNetCore.Identity.UserManager<Account> users,
            AuthenticationProviderConfigurationService providerConfiguration,
            IConfigurationLoader configuration,
            CancellationToken ct) =>
        {
            var authority = await RequireSelfAsync(http, resolver, decisions, ct);
            var account = await accounts.GetByIdAsync(authority.AccountId!.Value, ct)
                ?? throw new UnauthorizedAccessException();
            var grants = await MapGrants(account.Id, accounts, profiles, ct);
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
            IProfileRepository profiles, IConfigurationLoader configuration, CancellationToken ct) =>
        {
            var household = await HouseholdOnlyScopeAsync(http, resolver, ct);
            var values = new List<AccountAccessResponse>();
            foreach (var account in await accounts.GetAllAsync(ct))
            {
                if (household is { } own && account.HouseholdId != own)
                {
                    continue;
                }

                values.Add(await MapAccount(account, accounts, identities, profiles, configuration, ct));
            }

            return Results.Ok(values);
        }).RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersRead)
          .Produces<IReadOnlyList<AccountAccessResponse>>();

        group.MapGet("/{accountId:guid}", async (Guid accountId, HttpContext http, IRequestAuthorityResolver resolver,
            IAccountRepository accounts, IIdentityRepository identities, IProfileRepository profiles,
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
                : Results.Ok(await MapAccount(account, accounts, identities, profiles, configuration, ct));
        }).RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersRead)
          .Produces<AccountAccessResponse>();

        group.MapPost("/", async (CreateManagedAccountRequest request, HttpContext http,
            IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            IAccountRepository accounts, IIdentityRepository identities, IProfileRepository profiles,
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
                await MapAccount(account, accounts, identities, profiles, configuration, ct));
        })).RequireSecuredAccount()
           .RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .Produces<AccountAccessResponse>(StatusCodes.Status201Created);

        group.MapPost("/{accountId:guid}/temporary-password", async (Guid accountId, SetTemporaryPasswordRequest request,
            HttpContext http, IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            IAccountRepository accounts, IIdentityRepository identities, IProfileRepository profiles,
            IConfigurationLoader configuration,
            [FromServices] RecentSignInGuard recentSignIn,
            CancellationToken ct) => await ExecuteAsync(async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            await mutations.SetTemporaryPasswordAsync(await resolver.ResolveAsync(http, ct), accountId,
                request.TemporaryPassword, ct);
            var account = await accounts.GetByIdAsync(accountId, ct)
                ?? throw new KeyNotFoundException("Account not found.");
            return Results.Ok(await MapAccount(account, accounts, identities, profiles, configuration, ct));
        })).RequireSecuredAccount()
           .RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("SetAccountTemporaryPassword")
           .Produces<AccountAccessResponse>();

        // Someone lost their phone and their recovery codes: an administrator turns their two-step codes off so they can
        // sign in with their password and set them up again. It is audited and needs a recent sign-in.
        group.MapPost("/{accountId:guid}/two-step/reset", async (Guid accountId,
            HttpContext http, IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            IAccountRepository accounts, IIdentityRepository identities, IProfileRepository profiles,
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
            return Results.Ok(await MapAccount(account, accounts, identities, profiles, configuration, ct));
        })).RequireSecuredAccount()
           .RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("ResetAccountTwoStep")
           .Produces<AccountAccessResponse>();

        group.MapPut("/{accountId:guid}", async (Guid accountId, UpdateManagedAccountRequest request,
            HttpContext http, IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            IAccountRepository accounts, IIdentityRepository identities, IProfileRepository profiles,
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
            return Results.Ok(await MapAccount(value, accounts, identities, profiles, configuration, ct));
        })).RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .Produces<AccountAccessResponse>();

        group.MapDelete("/{accountId:guid}", async (Guid accountId, HttpContext http,
            IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            [FromServices] RecentSignInGuard recentSignIn,
            CancellationToken ct) => await ExecuteAsync(async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            await mutations.DeleteAsync(await resolver.ResolveAsync(http, ct), accountId, ct);
            return Results.NoContent();
        })).RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .Produces(StatusCodes.Status204NoContent);

        // Only a server administrator decides who looks after a household.
        group.MapPut("/{accountId:guid}/household-admin", async (Guid accountId, SetHouseholdAdminRequest request,
            HttpContext http, IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            IAccountRepository accounts, IIdentityRepository identities, IProfileRepository profiles,
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
            return Results.Ok(await MapAccount(account, accounts, identities, profiles, configuration, ct));
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
        })).RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
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
                    request.Enabled, request.Pin, mode, request.UnlockMinutes), ct);
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

    private static void MapManagedProfiles(RouteGroupBuilder access)
    {
        access.MapGet("/libraries", async (HttpContext http, IRequestAuthorityResolver resolver,
            IAccountRepository accounts, IConfigurationLoader configuration, CancellationToken ct) =>
        {
            // A household administrator can only hand out what the household already has.
            var household = await HouseholdOnlyScopeAsync(http, resolver, ct);
            IReadOnlySet<Guid>? allowed = null;
            if (household is { } own)
            {
                allowed = await accounts.GetHouseholdPrimaryAccountIdAsync(own, ct) is { } primary
                    ? await accounts.GetLibraryGrantsAsync(primary, ct)
                    : new HashSet<Guid>();
            }

            var values = configuration.LoadLibraries().Libraries
                .Where(library => library.Kind == LibraryKinds.Catalogued &&
                    Guid.TryParse(library.Id, out var id) && (allowed is null || allowed.Contains(id)))
                .Select(library => new AccessLibraryOptionDto(Guid.Parse(library.Id),
                    library.Name, library.Category, library.Area))
                .ToList();
            return Results.Ok(values);
        }).RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersRead)
          .Produces<IReadOnlyList<AccessLibraryOptionDto>>();

        var profiles = access.MapGroup("/profiles");
        profiles.MapGet("/", async (HttpContext http, IRequestAuthorityResolver resolver,
            IProfileRepository repository, CancellationToken ct) =>
        {
            var household = await HouseholdOnlyScopeAsync(http, resolver, ct);
            return Results.Ok((await repository.GetAllAsync(ct))
                .Where(profile => household is null || profile.HouseholdId == household)
                .Select(MapProfile).ToList());
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
                new GiveOwnSignInCommand(profileId, request.Email, request.TemporaryPassword), ct);
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
           .RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("GiveOwnSignIn").Produces<GiveOwnSignInResponse>();

        profiles.MapPut("/{profileId:guid}", async (Guid profileId,
            UpdateManagedProfileRequest request, HttpContext http, IRequestAuthorityResolver resolver,
            IAccountAccessMutationService mutations, [FromServices] RecentSignInGuard recentSignIn,
            CancellationToken ct) => await ExecuteAsync(async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            var profile = await mutations.UpdateProfileAsync(await resolver.ResolveAsync(http, ct),
                profileId, new UpdateManagedProfileCommand(request.DisplayName, request.AvatarColor, request.AvatarIcon), ct);
            return Results.Ok(MapProfile(profile));
        })).RequireAdministratorHouseholdOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .Produces<ManagedProfileResponse>();

        // ?photos=move (the default) keeps the person's personal photos in the household's Shared Library; ?photos=delete removes them.
        profiles.MapDelete("/{profileId:guid}", async (Guid profileId, [FromQuery] string? photos, HttpContext http,
            IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            [FromServices] RecentSignInGuard recentSignIn, CancellationToken ct) => await ExecuteAsync(async () =>
        {
            var disposition = photos?.Trim().ToLowerInvariant() switch
            {
                null or "" or "move" => ProfilePhotoDisposition.MoveToShared,
                "delete" => ProfilePhotoDisposition.Delete,
                _ => throw new ArgumentException("photos must be 'move' or 'delete'."),
            };
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

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
            ? authority.AccountHouseholdId
            : null;
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> operation)
    {
        try { return await operation(); }
        catch (ArgumentException ex) { return ApiErrors.BadRequest(ex.Message); }
        catch (KeyNotFoundException ex) { return ApiErrors.NotFound(ex.Message); }
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
        IAccountRepository accounts, IIdentityRepository identities, IProfileRepository profiles,
        IConfigurationLoader configuration, CancellationToken ct)
    {
        var libraryNames = configuration.LoadLibraries().Libraries
            .Where(library => library.Kind == LibraryKinds.Catalogued && Guid.TryParse(library.Id, out _))
            .ToDictionary(library => Guid.Parse(library.Id), library => library.Name);
        var sessions = await identities.GetSessionsAsync(account.Id, ct);
        var lastActiveAt = sessions.Count == 0 ? (DateTimeOffset?)null : sessions.Max(session => session.LastSeenAt);
        return new AccountAccessResponse(account.Id, account.Email,
            account.IsEnabled, account.IsAdministrator, account.AuthorizationVersion,
            (await accounts.GetFeatureGrantsAsync(account.Id, ct))
                .Select(feature => new AccountFeatureGrantDto(feature.Value, true)).ToList(),
            (await accounts.GetLibraryGrantsAsync(account.Id, ct))
                .Select(id => new AccountLibraryGrantDto(
                    id, libraryNames.GetValueOrDefault(id, "Unavailable library"), true)).ToList(),
            await MapGrants(account.Id, accounts, profiles, ct), account.CreatedAt, account.UpdatedAt,
            lastActiveAt, account.HouseholdId, account.MustChangePassword, account.TemporaryPasswordExpiresAt,
            await identities.GetAccountTwoStepAsync(account.Id, ct) is { IsEnabled: true },
            account.GrantsInheritFromAccountId, account.HouseholdAdmin);
    }

    private static async Task<IReadOnlyList<AccountProfileGrantDto>> MapGrants(Guid accountId,
        IAccountRepository accounts, IProfileRepository profiles, CancellationToken ct)
    {
        var result = new List<AccountProfileGrantDto>();
        foreach (var grant in await accounts.GetGrantsAsync(accountId, ct))
        {
            var profile = await profiles.GetByIdAsync(grant.ProfileId, ct);
            if (profile is null)
            {
                continue;
            }

            var protection = await accounts.GetAdminProtectionAsync(accountId, grant.ProfileId, ct);
            result.Add(new AccountProfileGrantDto(accountId, grant.ProfileId, profile.DisplayName,
                profile.AvatarImagePath, grant.IsDefault, grant.IsEnabled, grant.AdminEnabled,
                new GrantAdminProtectionDto(protection?.IsEnabled == true,
                    protection?.UnlockMode ?? AdminUnlockMode.FixedDuration.ToString(),
                    protection?.UnlockMinutes, protection?.ProtectionVersion ?? 0,
                    protection?.LockedUntil > DateTimeOffset.UtcNow, protection?.LockedUntil),
                grant.AuthorizationVersion, grant.GrantedAt,
                profile.Role == MediaEngine.Domain.Enums.ProfileRole.RestrictedProfile));
        }
        return result;
    }

    private static ManagedProfileResponse MapProfile(MediaEngine.Domain.Aggregates.Profile profile) =>
        new(profile.Id, profile.DisplayName, profile.AvatarColor, profile.AvatarImagePath,
            profile.CreatedAt, profile.HouseholdId,
            profile.Role == MediaEngine.Domain.Enums.ProfileRole.RestrictedProfile,
            profile.AvatarIcon);

    private static ValueTask WriteAuditAsync(IAuthorizationAuditWriter audit, TimeProvider clock,
        RequestAuthority authority, string eventType, Guid loginId, CancellationToken ct) =>
        audit.WriteAsync(new AuthorizationAuditEvent(eventType, clock.GetUtcNow(), authority.AccountId,
            authority.ActiveProfileId, authority.ApplicationId, "external_login", loginId.ToString("D"),
            new Dictionary<string, string?> { ["changed"] = "true" }), ct);
}
