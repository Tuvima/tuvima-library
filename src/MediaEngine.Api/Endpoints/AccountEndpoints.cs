using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity.Contracts;

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
        return app;
    }

    private static void MapSelfService(RouteGroupBuilder access)
    {
        var self = access.MapGroup("/self-service").RequireHumanSelfService();
        self.MapGet("/", async (bool originalClientIsLocal, bool originalClientIsHttps,
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
            var hasPassword = !account.IsLocalOnly && await identities.GetAccountCredentialAsync(
                account.Id, AccountCredentialKind.Password, ct).ConfigureAwait(false) is not null;
            var passkeys = account.IsLocalOnly
                ? []
                : await users.GetPasskeysAsync(account).ConfigureAwait(false);
            var linkedLogins = account.IsLocalOnly
                ? []
                : await externalLogins.GetByAccountAsync(account.Id, ct).ConfigureAwait(false);
            var hasProfilePin = account.IsLocalOnly && await identities.GetCredentialAsync(
                authority.ActiveProfileId!.Value, ProfileCredentialKind.ProfilePin, ct).ConfigureAwait(false) is not null;

            var passkeyReady = policy.PasskeySignInEnabled
                && !AuthenticationEndpoints.IsLocalOnlyMode(policy)
                && AuthenticationEndpoints.IsCanonicalOriginReady(policy);
            var externalAvailable = !account.IsLocalOnly && AuthenticationEndpoints.AllowsClient(
                configuration.LoadNetwork(), originalClientIsLocal, originalClientIsHttps,
                AuthenticationEndpoints.IsExternalSignInEnabled(policy));
            var availableProviders = externalAvailable
                ? policy.ExternalProviders
                    .Where(provider => provider.Enabled && AuthenticationEndpoints.IsConfiguredProvider(
                        policy,
                        provider.Id,
                        provider.Kind.Equals(ExternalAuthProviderKinds.OpenIdConnect, StringComparison.OrdinalIgnoreCase)
                            ? string.IsNullOrWhiteSpace(provider.Issuer) ? provider.Authority : provider.Issuer
                            : provider.Issuer))
                    .Select(provider => new AccountExternalProviderResponse(provider.Id, provider.DisplayName))
                    .ToList()
                : [];

            var methods = AttachedAuthenticationMethods(
                account.IsLocalOnly, hasProfilePin, hasPassword, passkeys.Count > 0, linkedLogins.Count > 0);

            var capabilities = new AccountSecurityCapabilitiesResponse(
                hasPassword,
                passkeys.Count > 0,
                linkedLogins.Count > 0,
                hasPassword && AuthenticationEndpoints.AllowsClient(
                    configuration.LoadNetwork(), originalClientIsLocal, originalClientIsHttps,
                    policy.PasswordSignInEnabled && !AuthenticationEndpoints.IsLocalOnlyMode(policy)),
                !account.IsLocalOnly && AuthenticationEndpoints.IsPasskeyAvailable(
                    policy, configuration.LoadNetwork(), originalClientIsLocal, originalClientIsHttps),
                passkeyReady,
                availableProviders.Count > 0,
                availableProviders);
            return Results.Ok(new AccountSelfServiceResponse(account.Id, account.Email, account.IsLocalOnly,
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
            IAccountRepository accounts, IIdentityRepository identities,
            IAccountExternalLoginService externalLogins,
            Microsoft.AspNetCore.Identity.UserManager<Account> users,
            TimeProvider clock, CancellationToken ct) =>
        {
            var authority = await RequireSelfAsync(http, resolver, decisions, ct);
            var accountId = authority.AccountId!.Value;
            using var mutation = await mutationGate.EnterAsync(ct).ConfigureAwait(false);
            var linked = await externalLogins.GetByAccountAsync(accountId, ct).ConfigureAwait(false);
            if (!linked.Any(login => login.Id == loginId))
            {
                return ApiErrors.NotFound("External login not found.");
            }

            if (!await AuthenticationEndpoints.HasUsableAccountSignInAsync(
                    providerConfiguration.LoadWithSecrets(), accountId, accounts, identities,
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
        bool isLocalOnly,
        bool hasProfilePin,
        bool hasPassword,
        bool hasPasskeys,
        bool hasExternalLogins)
    {
        if (isLocalOnly)
        {
            return hasProfilePin ? ["profile_pin"] : ["profile_entry"];
        }

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
        }).RequireEffectiveAdministrator(false).Produces<GrantAdminUnlockResponse>();

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
        }).RequireEffectiveAdministrator(false).RequireRateLimiting("authentication")
          .Produces<GrantAdminUnlockResponse>();

        access.MapDelete("/admin-unlock", async (HttpContext http, IRequestAuthorityResolver resolver,
            IGrantAdminUnlockService unlocks, CancellationToken ct) =>
        {
            var authority = await resolver.ResolveAsync(http, ct);
            await unlocks.LockAsync(authority, ct);
            return Results.NoContent();
        }).RequireEffectiveAdministrator(false).WithName("ExitAdministratorSurface")
          .Produces(StatusCodes.Status204NoContent);
    }

    private static void MapManagedAccounts(RouteGroupBuilder access)
    {
        var group = access.MapGroup("/accounts");
        group.MapGet("/", async (IAccountRepository accounts, IIdentityRepository identities,
            IProfileRepository profiles, IConfigurationLoader configuration, CancellationToken ct) =>
        {
            var values = new List<AccountAccessResponse>();
            foreach (var account in await accounts.GetAllAsync(ct))
            {
                values.Add(await MapAccount(account, accounts, identities, profiles, configuration, ct));
            }

            return Results.Ok(values);
        }).RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersRead)
          .Produces<IReadOnlyList<AccountAccessResponse>>();

        group.MapGet("/{accountId:guid}", async (Guid accountId, IAccountRepository accounts,
            IIdentityRepository identities, IProfileRepository profiles,
            IConfigurationLoader configuration, CancellationToken ct) =>
        {
            var account = await accounts.GetByIdAsync(accountId, ct);
            return account is null ? ApiErrors.NotFound("Account not found.")
                : Results.Ok(await MapAccount(account, accounts, identities, profiles, configuration, ct));
        }).RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersRead)
          .Produces<AccountAccessResponse>();

        group.MapPost("/", async (CreateManagedAccountRequest request, HttpContext http,
            IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            IAccountRepository accounts, IIdentityRepository identities, IProfileRepository profiles,
            IConfigurationLoader configuration,
            CancellationToken ct) => await ExecuteAsync(async () =>
        {
            var command = new CreateAccountAccessCommand(request.Email, request.IsLocalOnly,
                request.IsAdministrator, request.ProfileId,
                request.NewProfile is null ? null : new NewAccountProfileCommand(
                    request.NewProfile.DisplayName, request.NewProfile.AvatarColor),
                request.FeatureIds.Select(id => new AccountFeatureId(id)).ToHashSet(),
                request.LibraryIds.ToHashSet());
            var account = await mutations.CreateAsync(await resolver.ResolveAsync(http, ct), command, ct);
            return Results.Created($"/access/accounts/{account.Id:D}",
                await MapAccount(account, accounts, identities, profiles, configuration, ct));
        })).RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .Produces<AccountAccessResponse>(StatusCodes.Status201Created);

        group.MapPut("/{accountId:guid}", async (Guid accountId, UpdateManagedAccountRequest request,
            HttpContext http, IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            IAccountRepository accounts, IIdentityRepository identities, IProfileRepository profiles,
            IConfigurationLoader configuration,
            CancellationToken ct) => await ExecuteAsync(async () =>
        {
            var value = await mutations.UpdateAsync(await resolver.ResolveAsync(http, ct), accountId,
                new UpdateAccountAccessCommand(request.Email, request.IsLocalOnly,
                    request.IsEnabled, request.IsAdministrator), ct);
            return Results.Ok(await MapAccount(value, accounts, identities, profiles, configuration, ct));
        })).RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .Produces<AccountAccessResponse>();

        group.MapDelete("/{accountId:guid}", async (Guid accountId, HttpContext http,
            IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            CancellationToken ct) => await ExecuteAsync(async () =>
        {
            await mutations.DeleteAsync(await resolver.ResolveAsync(http, ct), accountId, ct);
            return Results.NoContent();
        })).RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .Produces(StatusCodes.Status204NoContent);

        group.MapPut("/{accountId:guid}/access", async (Guid accountId,
            ReplaceAccountAccessRequest request, HttpContext http, IRequestAuthorityResolver resolver,
            IAccountAccessMutationService mutations, CancellationToken ct) => await ExecuteAsync(async () =>
        {
            await mutations.ReplaceAccessAsync(await resolver.ResolveAsync(http, ct), accountId,
                request.FeatureIds.Select(id => new AccountFeatureId(id)).ToHashSet(),
                request.LibraryIds.ToHashSet(), ct);
            return Results.NoContent();
        })).RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("ReplaceAccountAccess").Produces(StatusCodes.Status204NoContent);

        group.MapPut("/{accountId:guid}/grants/{profileId:guid}", async (Guid accountId,
            Guid profileId, SetAccountProfileGrantAccessRequest request, HttpContext http,
            IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            CancellationToken ct) => await ExecuteAsync(async () =>
        {
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
        })).RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("SetAccountProfileGrant").Produces(StatusCodes.Status204NoContent);

        group.MapDelete("/{accountId:guid}/grants/{profileId:guid}", async (Guid accountId,
            Guid profileId, HttpContext http, IRequestAuthorityResolver resolver,
            IAccountAccessMutationService mutations, CancellationToken ct) => await ExecuteAsync(async () =>
        {
            await mutations.RevokeGrantAsync(await resolver.ResolveAsync(http, ct), accountId, profileId, ct);
            return Results.NoContent();
        })).RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("RevokeAccountProfileGrant").Produces(StatusCodes.Status204NoContent);

        group.MapPut("/{accountId:guid}/grants/{profileId:guid}/admin-protection", async (
            Guid accountId, Guid profileId, SetGrantAdminProtectionRequest request, HttpContext http,
            IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            CancellationToken ct) => await ExecuteAsync(async () =>
        {
            if (!Enum.TryParse<AdminUnlockMode>(request.UnlockMode, true, out var mode) || !Enum.IsDefined(mode))
            {
                return ApiErrors.BadRequest("Unknown unlock mode.");
            }

            await mutations.SetAdminProtectionAsync(await resolver.ResolveAsync(http, ct), accountId,
                profileId, new GrantAdminProtectionCommand(
                    request.Enabled, request.Pin, mode, request.UnlockMinutes), ct);
            return Results.NoContent();
        })).RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .WithName("SetAccountProfileAdminProtection").Produces(StatusCodes.Status204NoContent);

        access.MapPost("/invitations", async (CreateAccountInvitationRequest request, HttpContext http,
            IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            CancellationToken ct) => await ExecuteAsync(async () =>
        {
            var issued = await mutations.IssueInvitationAsync(await resolver.ResolveAsync(http, ct),
                new IssueAccountInvitationCommand(request.Email, request.ProfileIds,
                    request.DefaultProfileId), ct);
            return Results.Ok(new AccountInvitationResponse(
                issued.AccountId, issued.PlaintextToken, issued.ExpiresAt));
        })).RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .Produces<AccountInvitationResponse>();
    }

    private static void MapManagedProfiles(RouteGroupBuilder access)
    {
        access.MapGet("/libraries", (IConfigurationLoader configuration) =>
        {
            var values = configuration.LoadLibraries().Libraries
                .Where(library => library.Kind == LibraryKinds.Catalogued &&
                    Guid.TryParse(library.Id, out _))
                .Select(library => new AccessLibraryOptionDto(Guid.Parse(library.Id),
                    library.Name, library.Category, library.Area))
                .ToList();
            return Results.Ok(values);
        }).RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersRead)
          .Produces<IReadOnlyList<AccessLibraryOptionDto>>();

        var profiles = access.MapGroup("/profiles");
        profiles.MapGet("/", async (IProfileRepository repository, CancellationToken ct) =>
            Results.Ok((await repository.GetAllAsync(ct)).Select(MapProfile).ToList()))
            .RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersRead)
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

        profiles.MapPut("/{profileId:guid}", async (Guid profileId,
            UpdateManagedProfileRequest request, HttpContext http, IRequestAuthorityResolver resolver,
            IAccountAccessMutationService mutations, CancellationToken ct) => await ExecuteAsync(async () =>
        {
            var profile = await mutations.UpdateProfileAsync(await resolver.ResolveAsync(http, ct),
                profileId, new UpdateManagedProfileCommand(request.DisplayName, request.AvatarColor), ct);
            return Results.Ok(MapProfile(profile));
        })).RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .Produces<ManagedProfileResponse>();

        profiles.MapDelete("/{profileId:guid}", async (Guid profileId, HttpContext http,
            IRequestAuthorityResolver resolver, IAccountAccessMutationService mutations,
            CancellationToken ct) => await ExecuteAsync(async () =>
        {
            await mutations.DeleteProfileAsync(await resolver.ResolveAsync(http, ct), profileId, ct);
            return Results.NoContent();
        })).RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityUsersWrite)
           .Produces(StatusCodes.Status204NoContent);
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
        return new AccountAccessResponse(account.Id, account.Email, account.IsLocalOnly,
            account.IsEnabled, account.IsAdministrator, account.AuthorizationVersion,
            (await accounts.GetFeatureGrantsAsync(account.Id, ct))
                .Select(feature => new AccountFeatureGrantDto(feature.Value, true)).ToList(),
            (await accounts.GetLibraryGrantsAsync(account.Id, ct))
                .Select(id => new AccountLibraryGrantDto(
                    id, libraryNames.GetValueOrDefault(id, "Unavailable library"), true)).ToList(),
            await MapGrants(account.Id, accounts, profiles, ct), account.CreatedAt, account.UpdatedAt,
            lastActiveAt);
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
                grant.AuthorizationVersion, grant.GrantedAt));
        }
        return result;
    }

    private static ManagedProfileResponse MapProfile(MediaEngine.Domain.Aggregates.Profile profile) =>
        new(profile.Id, profile.DisplayName, profile.AvatarColor, profile.AvatarImagePath,
            profile.CreatedAt);

    private static ValueTask WriteAuditAsync(IAuthorizationAuditWriter audit, TimeProvider clock,
        RequestAuthority authority, string eventType, Guid loginId, CancellationToken ct) =>
        audit.WriteAsync(new AuthorizationAuditEvent(eventType, clock.GetUtcNow(), authority.AccountId,
            authority.ActiveProfileId, authority.ApplicationId, "external_login", loginId.ToString("D"),
            new Dictionary<string, string?> { ["changed"] = "true" }), ct);
}
