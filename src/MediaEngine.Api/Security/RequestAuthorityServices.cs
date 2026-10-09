using System.Security.Claims;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using DomainAuthorizationEvaluator = MediaEngine.Domain.Contracts.IAuthorizationEvaluator;

namespace MediaEngine.Api.Security;

public interface IRequestAuthorityResolver
{
    ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default);
}

public sealed class RequestAuthorityResolver(
    IAccountRepository accounts,
    IApplicationRepository applications,
    IProfileRepository profiles) : IRequestAuthorityResolver
{
    public async ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            return new(PrincipalKind.Anonymous, false);
        }

        var kind = Enum.TryParse<PrincipalKind>(
            user.FindFirstValue(TuvimaClaimTypes.PrincipalKind), out var parsed)
            ? parsed
            : PrincipalKind.Anonymous;
        var accountId = ClaimGuid(user, TuvimaClaimTypes.AccountId);
        var profileId = ClaimGuid(user, TuvimaClaimTypes.ActiveProfileId);
        var applicationId = ClaimGuid(user, TuvimaClaimTypes.ApplicationId);
        var sessionId = ClaimGuid(user, TuvimaClaimTypes.SessionId);
        var deviceId = ClaimGuid(user, TuvimaClaimTypes.DeviceId);

        var account = accountId is { } accountKey
            ? await accounts.GetByIdAsync(accountKey, ct).ConfigureAwait(false)
            : null;
        var grant = accountId is { } grantAccount && profileId is { } grantProfile
            ? await accounts.GetGrantAsync(grantAccount, grantProfile, ct).ConfigureAwait(false)
            : null;
        var application = applicationId is { } applicationKey
            ? await applications.GetApplicationAsync(applicationKey, ct).ConfigureAwait(false)
            : null;

        // A child (restricted) profile never gets administrator authority, whatever its grant says.
        var activeProfile = profileId is { } activeProfileKey
            ? await profiles.GetByIdAsync(activeProfileKey, ct).ConfigureAwait(false)
            : null;

        return new RequestAuthority(
            kind, true, accountId, profileId, applicationId, sessionId, deviceId,
            account?.IsEnabled == true,
            grant?.IsEnabled == true,
            application?.IsEnabled == true,
            account?.AuthorizationVersion ?? 0,
            grant?.AuthorizationVersion ?? 0,
            application?.AuthorizationVersion ?? 0,
            account?.IsAdministrator == true,
            grant?.AdminEnabled == true,
            application?.IsAdministrator == true,
            activeProfile?.Role == ProfileRole.RestrictedProfile);
    }

    private static Guid? ClaimGuid(ClaimsPrincipal user, string type) =>
        Guid.TryParse(user.FindFirstValue(type), out var id) && id != Guid.Empty ? id : null;
}

/// <summary>
/// P02 authority reads are intentionally uncached. Mutations increment the applicable
/// authorization version in their transaction, so the next resolution observes the change.
/// </summary>
public sealed class AuthorizationInvalidationService : IAuthorizationInvalidationService
{
    public ValueTask InvalidateAccountAsync(Guid accountId, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
    public ValueTask InvalidateGrantAsync(Guid accountId, Guid profileId, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
    public ValueTask InvalidateApplicationAsync(Guid applicationId, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
    public ValueTask InvalidateSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}

public sealed class AuthorizationAuditWriter(IAccountRepository repository) : IAuthorizationAuditWriter
{
    public ValueTask WriteAsync(
        AuthorizationAuditEvent auditEvent,
        CancellationToken cancellationToken = default) =>
        new(repository.WriteAuthorizationAuditAsync(auditEvent, cancellationToken));
}

internal static class AuthorityValidity
{
    public static AuthorizationDecision? Validate(RequestAuthority authority)
    {
        if (!authority.IsAuthenticated)
        {
            return AuthorizationDecision.Deny(AuthorizationDenialReason.Unauthenticated);
        }

        return authority.PrincipalKind switch
        {
            PrincipalKind.Human when authority.HasHumanContext &&
                authority.AccountEnabled && authority.GrantEnabled => null,
            PrincipalKind.DelegatedUserClient when authority.HasHumanContext &&
                authority.HasApplicationContext && authority.AccountEnabled &&
                authority.GrantEnabled && authority.ApplicationEnabled => null,
            PrincipalKind.ServiceApplication when authority.HasApplicationContext &&
                authority.ApplicationEnabled => null,
            PrincipalKind.Human or PrincipalKind.DelegatedUserClient or PrincipalKind.ServiceApplication =>
                AuthorizationDecision.Deny(AuthorizationDenialReason.DisabledPrincipal),
            _ => AuthorizationDecision.Deny(AuthorizationDenialReason.WrongPrincipalKind),
        };
    }

    public static AuthorizationDecision? ValidateHuman(RequestAuthority authority)
    {
        var invalid = Validate(authority);
        if (invalid is not null)
        {
            return invalid;
        }

        return authority.PrincipalKind is PrincipalKind.Human or PrincipalKind.DelegatedUserClient
            ? null
            : AuthorizationDecision.Deny(AuthorizationDenialReason.MissingHumanContext);
    }
}

public sealed class AccountAccessDecisionService(
    IAccountRepository accounts,
    IGrantAdminUnlockService unlocks) : IAccountAccessDecisionService, ISelfServiceAuthorizationService
{
    public async ValueTask<AuthorizationDecision> EvaluateFeatureAsync(
        RequestAuthority authority,
        AccountFeatureId feature,
        CancellationToken ct = default)
    {
        var invalid = AuthorityValidity.ValidateHuman(authority);
        if (invalid is not null)
        {
            return invalid.Value;
        }

        if (authority.IsEffectiveAdministrator)
        {
            return AuthorizationDecision.Allow();
        }

        return await accounts.HasFeatureGrantAsync(authority.AccountId!.Value, feature, ct).ConfigureAwait(false)
            ? AuthorizationDecision.Allow()
            : AuthorizationDecision.Deny(AuthorizationDenialReason.MissingFeatureGrant);
    }

    public async ValueTask<AuthorizationDecision> EvaluateLibraryAsync(
        RequestAuthority authority,
        Guid libraryId,
        CancellationToken ct = default)
    {
        var invalid = AuthorityValidity.ValidateHuman(authority);
        if (invalid is not null)
        {
            return invalid.Value;
        }

        if (authority.IsEffectiveAdministrator)
        {
            return AuthorizationDecision.Allow();
        }

        return await accounts.HasLibraryGrantAsync(authority.AccountId!.Value, libraryId, ct).ConfigureAwait(false)
            ? AuthorizationDecision.Allow()
            : AuthorizationDecision.Deny(AuthorizationDenialReason.MissingLibraryGrant);
    }

    public async ValueTask<AuthorizationDecision> EvaluateAdministratorAsync(
        RequestAuthority authority,
        bool requireSurfaceUnlock,
        CancellationToken ct = default)
    {
        if (AuthorityValidity.ValidateHuman(authority) is not null || !authority.IsEffectiveAdministrator)
        {
            return AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorRequired);
        }

        if (!requireSurfaceUnlock)
        {
            return AuthorizationDecision.Allow();
        }

        var state = await unlocks.GetStateAsync(authority, ct).ConfigureAwait(false);
        return !state.ProtectionEnabled || state.IsUnlocked
            ? AuthorizationDecision.Allow()
            : AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorUnlockRequired);
    }

    public ValueTask<AuthorizationDecision> EvaluateAccountAsync(
        RequestAuthority authority,
        Guid accountId,
        CancellationToken ct = default)
    {
        var invalid = AuthorityValidity.ValidateHuman(authority);
        return ValueTask.FromResult(invalid ?? (authority.AccountId == accountId
            ? AuthorizationDecision.Allow()
            : AuthorizationDecision.Deny(AuthorizationDenialReason.ResourceDenied)));
    }

    public ValueTask<AuthorizationDecision> EvaluateProfileAsync(
        RequestAuthority authority,
        Guid profileId,
        CancellationToken ct = default)
    {
        var invalid = AuthorityValidity.ValidateHuman(authority);
        return ValueTask.FromResult(invalid ?? (authority.ActiveProfileId == profileId
            ? AuthorizationDecision.Allow()
            : AuthorizationDecision.Deny(AuthorizationDenialReason.ResourceDenied)));
    }
}

public sealed class AuthorizationEvaluator(
    IAccountAccessDecisionService accounts,
    IApplicationRepository applications,
    IPermissionRegistry registry,
    IAuthorizationAuditWriter audit,
    IHttpContextAccessor http) : DomainAuthorizationEvaluator
{
    public async ValueTask<AuthorizationDecision> EvaluateAsync(
        RequestAuthority authority,
        AuthorizationRequirement requirement,
        ResourceAuthorizationContext? resource,
        CancellationToken ct = default)
    {
        var invalid = AuthorityValidity.Validate(authority);
        if (invalid is not null)
        {
            return invalid.Value;
        }

        if (IsEmpty(requirement))
        {
            return AuthorizationDecision.Deny(AuthorizationDenialReason.ResourceDenied);
        }

        if (requirement.RequiresHumanContext && !authority.HasHumanContext)
        {
            return AuthorizationDecision.Deny(AuthorizationDenialReason.MissingHumanContext);
        }

        if (requirement.RequiresAdministrator)
        {
            var decision = await accounts.EvaluateAdministratorAsync(
                authority, requirement.RequiresAdministratorSurfaceUnlock, ct).ConfigureAwait(false);
            if (!decision.IsAllowed)
            {
                return decision;
            }
        }

        if (requirement.AccountFeature is { } feature)
        {
            var decision = await accounts.EvaluateFeatureAsync(authority, feature, ct).ConfigureAwait(false);
            if (!decision.IsAllowed)
            {
                return decision;
            }
        }

        if (requirement.RequiresLibraryGrant)
        {
            if (resource?.LibraryId is not { } libraryId)
            {
                return AuthorizationDecision.Deny(AuthorizationDenialReason.MissingLibraryGrant);
            }

            var decision = await accounts.EvaluateLibraryAsync(authority, libraryId, ct).ConfigureAwait(false);
            if (!decision.IsAllowed)
            {
                return decision;
            }
        }

        return requirement.ApplicationPermission is { } permission
            ? await EvaluateApplicationPermissionAsync(authority, permission, resource, ct).ConfigureAwait(false)
            : AuthorizationDecision.Allow();
    }

    private async ValueTask<AuthorizationDecision> EvaluateApplicationPermissionAsync(
        RequestAuthority authority,
        ApplicationPermissionId permission,
        ResourceAuthorizationContext? resource,
        CancellationToken ct)
    {
        if (!authority.HasApplicationContext || !authority.ApplicationEnabled)
        {
            return AuthorizationDecision.Deny(AuthorizationDenialReason.DisabledPrincipal);
        }

        if (!registry.TryGet(permission, out var definition) || !definition.IsAvailable)
        {
            return AuthorizationDecision.Deny(AuthorizationDenialReason.PermissionUnavailable);
        }

        var application = await applications.GetApplicationAsync(authority.ApplicationId!.Value, ct)
            .ConfigureAwait(false);
        if (application is null || !application.IsEnabled ||
            !definition.ApplicationTypes.Contains(application.ApplicationType))
        {
            return AuthorizationDecision.Deny(AuthorizationDenialReason.WrongPrincipalKind);
        }

        if (definition.RequiresUserContext && !authority.HasHumanContext)
        {
            var exception = authority.IsAdministratorApplication &&
                IsAdministratorViewRead(permission) &&
                resource?.ResourceType == "view-profile-admin-read" &&
                Guid.TryParse(resource.ResourceId, out var targetProfileId) &&
                targetProfileId != Guid.Empty;
            if (!exception)
            {
                return AuthorizationDecision.Deny(AuthorizationDenialReason.MissingHumanContext);
            }

            await audit.WriteAsync(new AuthorizationAuditEvent(
                "view.admin_application_profile_read",
                DateTimeOffset.UtcNow,
                null,
                null,
                authority.ApplicationId,
                "profile",
                resource!.ResourceId,
                new Dictionary<string, string?> { ["permission"] = permission.Value }), ct).ConfigureAwait(false);
        }

        if (authority.PrincipalKind == PrincipalKind.ServiceApplication && application.IsAdministrator)
        {
            return AuthorizationDecision.Allow();
        }

        if (!application.IsAdministrator)
        {
            var granted = await applications.GetApplicationPermissionsAsync(application.Id, ct).ConfigureAwait(false);
            if (!granted.Contains(permission))
            {
                return AuthorizationDecision.Deny(AuthorizationDenialReason.MissingPermission);
            }
        }
        if (authority.PrincipalKind == PrincipalKind.DelegatedUserClient && !HasDelegatedConsent(permission))
        {
            return AuthorizationDecision.Deny(AuthorizationDenialReason.MissingPermission);
        }

        return AuthorizationDecision.Allow();
    }

    private bool HasDelegatedConsent(ApplicationPermissionId permission) =>
        http.HttpContext?.User.HasClaim(TuvimaClaimTypes.Scope, permission.Value) == true;

    private static bool IsAdministratorViewRead(ApplicationPermissionId permission) =>
        permission == ApplicationPermissionIds.ViewPersonalRead ||
        permission == ApplicationPermissionIds.ViewOriginalsRead ||
        permission == ApplicationPermissionIds.ViewGalleriesRead;

    private static bool IsEmpty(AuthorizationRequirement requirement) =>
        requirement.ApplicationPermission is null &&
        requirement.AccountFeature is null &&
        !requirement.RequiresHumanContext &&
        !requirement.RequiresAdministrator &&
        !requirement.RequiresLibraryGrant;
}

public sealed class GrantAdminUnlockService(
    IAccountRepository accounts,
    IPasswordHasher<GrantAdminProtection> hasher,
    TimeProvider clock) : IGrantAdminUnlockService
{
    public async ValueTask<GrantAdminUnlockState> GetStateAsync(
        RequestAuthority authority,
        CancellationToken ct = default)
    {
        var ids = RequireHuman(authority);
        var protection = await accounts.GetAdminProtectionAsync(ids.Account, ids.Profile, ct).ConfigureAwait(false);
        if (protection?.IsEnabled != true)
        {
            return new(ids.Account, ids.Profile, false, AdminUnlockMode.FixedDuration, true, null,
                protection?.ProtectionVersion ?? 0);
        }

        var mode = ParseMode(protection.UnlockMode);
        if (authority.SessionId is not { } sessionId)
        {
            return new(ids.Account, ids.Profile, true, mode, false, null, protection.ProtectionVersion);
        }

        var unlock = await accounts.GetAdminUnlockAsync(
            sessionId, ids.Account, ids.Profile, clock.GetUtcNow(), ct).ConfigureAwait(false);
        var valid = unlock is not null && unlock.ProtectionVersion == protection.ProtectionVersion;
        return new(ids.Account, ids.Profile, true, mode, valid, valid ? unlock!.ExpiresAt : null,
            protection.ProtectionVersion);
    }

    public async ValueTask<GrantAdminUnlockState> UnlockAsync(
        RequestAuthority authority,
        string pin,
        CancellationToken ct = default)
    {
        var ids = RequireHuman(authority);
        if (authority.SessionId is not { } sessionId)
        {
            throw new UnauthorizedAccessException("A human session is required.");
        }

        var protection = await accounts.GetAdminProtectionAsync(ids.Account, ids.Profile, ct).ConfigureAwait(false);
        if (protection?.IsEnabled != true)
        {
            return await GetStateAsync(authority, ct).ConfigureAwait(false);
        }

        var mode = ParseMode(protection.UnlockMode);
        var now = clock.GetUtcNow();
        if (protection.LockedUntil > now)
        {
            throw new InvalidOperationException("Administrator unlock is temporarily locked.");
        }

        if (string.IsNullOrWhiteSpace(protection.PinHash) ||
            hasher.VerifyHashedPassword(protection, protection.PinHash, pin) == PasswordVerificationResult.Failed)
        {
            await accounts.RecordAdminProtectionFailureAsync(
                ids.Account, ids.Profile, now.AddMinutes(15), ct).ConfigureAwait(false);
            throw new UnauthorizedAccessException("Invalid administrator PIN.");
        }

        await accounts.ResetAdminProtectionAttemptsAsync(ids.Account, ids.Profile, ct).ConfigureAwait(false);
        DateTimeOffset? expiresAt = mode switch
        {
            AdminUnlockMode.FixedDuration => now.AddMinutes(Math.Clamp(protection.UnlockMinutes ?? 30, 1, 120)),
            AdminUnlockMode.LockOnLeave => now.AddMinutes(5),
            AdminUnlockMode.UntilProfileSwitch => null,
            _ => throw new InvalidOperationException("Unsupported administrator unlock mode."),
        };
        await accounts.SetAdminUnlockAsync(new GrantAdminUnlock
        {
            SessionId = sessionId,
            AccountId = ids.Account,
            ProfileId = ids.Profile,
            ProtectionVersion = protection.ProtectionVersion,
            Method = "GrantPin",
            GrantedAt = now,
            ExpiresAt = expiresAt,
        }, ct).ConfigureAwait(false);
        return await GetStateAsync(authority, ct).ConfigureAwait(false);
    }

    public ValueTask LockAsync(RequestAuthority authority, CancellationToken ct = default)
    {
        RequireHuman(authority);
        return authority.SessionId is { } sessionId
            ? new ValueTask(accounts.ClearAdminUnlockAsync(sessionId, ct))
            : ValueTask.CompletedTask;
    }

    private static AdminUnlockMode ParseMode(string value) =>
        Enum.TryParse<AdminUnlockMode>(value, ignoreCase: false, out var mode) && Enum.IsDefined(mode)
            ? mode
            : throw new InvalidOperationException($"Unknown administrator unlock mode '{value}'.");

    private static (Guid Account, Guid Profile) RequireHuman(RequestAuthority authority)
    {
        if (!authority.IsEffectiveAdministrator ||
            authority.AccountId is not { } account ||
            authority.ActiveProfileId is not { } profile)
        {
            throw new UnauthorizedAccessException("Effective administrator authority is required.");
        }

        return (account, profile);
    }
}

public sealed class EffectiveAdministratorRequirement(bool surfaceUnlock) : IAuthorizationRequirement
{
    public bool SurfaceUnlock { get; } = surfaceUnlock;
}

public sealed class EffectiveAdministratorHandler(
    IRequestAuthorityResolver resolver,
    IAccountAccessDecisionService decisions) : AuthorizationHandler<EffectiveAdministratorRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        EffectiveAdministratorRequirement requirement)
    {
        if (context.Resource is not HttpContext http)
        {
            return;
        }

        var authority = await resolver.ResolveAsync(http, http.RequestAborted).ConfigureAwait(false);
        if ((await decisions.EvaluateAdministratorAsync(
                authority, requirement.SurfaceUnlock, http.RequestAborted).ConfigureAwait(false)).IsAllowed)
        {
            context.Succeed(requirement);
        }
    }
}

public sealed class HumanSelfServiceRequirement : IAuthorizationRequirement;

public sealed class HumanSelfServiceHandler(IRequestAuthorityResolver resolver)
    : AuthorizationHandler<HumanSelfServiceRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        HumanSelfServiceRequirement requirement)
    {
        if (context.Resource is not HttpContext http)
        {
            return;
        }

        var authority = await resolver.ResolveAsync(http, http.RequestAborted).ConfigureAwait(false);
        if (authority.PrincipalKind == PrincipalKind.Human && AuthorityValidity.ValidateHuman(authority) is null)
        {
            context.Succeed(requirement);
        }
    }
}

public sealed class ApplicationPermissionRequirement(ApplicationPermissionId permission) : IAuthorizationRequirement
{
    public ApplicationPermissionId Permission { get; } = permission;
}

public sealed class ApplicationPermissionHandler(
    IRequestAuthorityResolver resolver,
    DomainAuthorizationEvaluator evaluator) : AuthorizationHandler<ApplicationPermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ApplicationPermissionRequirement requirement)
    {
        if (context.Resource is not HttpContext http)
        {
            return;
        }

        var authority = await resolver.ResolveAsync(http, http.RequestAborted).ConfigureAwait(false);
        var decision = await evaluator.EvaluateAsync(
            authority, new AuthorizationRequirement(ApplicationPermission: requirement.Permission), null,
            http.RequestAborted).ConfigureAwait(false);
        if (decision.IsAllowed)
        {
            context.Succeed(requirement);
        }
    }
}

public sealed class AdministratorOrApplicationRequirement(ApplicationPermissionId permission) : IAuthorizationRequirement
{
    public ApplicationPermissionId Permission { get; } = permission;
}

public sealed class HumanOrApplicationPermissionRequirement(ApplicationPermissionId permission)
    : IAuthorizationRequirement
{
    public ApplicationPermissionId Permission { get; } = permission;
}

public sealed class HumanOrApplicationPermissionHandler(
    IRequestAuthorityResolver resolver,
    DomainAuthorizationEvaluator evaluator)
    : AuthorizationHandler<HumanOrApplicationPermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        HumanOrApplicationPermissionRequirement requirement)
    {
        if (context.Resource is not HttpContext http)
        {
            return;
        }

        var authority = await resolver.ResolveAsync(http, http.RequestAborted).ConfigureAwait(false);
        if (authority.PrincipalKind == PrincipalKind.Human)
        {
            if (AuthorityValidity.ValidateHuman(authority) is null)
            {
                context.Succeed(requirement);
            }

            return;
        }

        if (authority.PrincipalKind is not (PrincipalKind.DelegatedUserClient or PrincipalKind.ServiceApplication))
        {
            return;
        }

        var decision = await evaluator.EvaluateAsync(
            authority,
            new AuthorizationRequirement(ApplicationPermission: requirement.Permission),
            null,
            http.RequestAborted).ConfigureAwait(false);
        if (decision.IsAllowed)
        {
            context.Succeed(requirement);
        }
    }
}

public sealed class AdministratorOrApplicationHandler(
    IRequestAuthorityResolver resolver,
    IAccountAccessDecisionService administrators,
    DomainAuthorizationEvaluator evaluator) : AuthorizationHandler<AdministratorOrApplicationRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AdministratorOrApplicationRequirement requirement)
    {
        if (context.Resource is not HttpContext http)
        {
            return;
        }

        var authority = await resolver.ResolveAsync(http, http.RequestAborted).ConfigureAwait(false);

        if (authority.PrincipalKind == PrincipalKind.Human)
        {
            if ((await administrators.EvaluateAdministratorAsync(
                    authority, true, http.RequestAborted).ConfigureAwait(false)).IsAllowed)
            {
                context.Succeed(requirement);
            }

            return;
        }

        var decision = await evaluator.EvaluateAsync(
            authority, new AuthorizationRequirement(ApplicationPermission: requirement.Permission), null,
            http.RequestAborted).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            return;
        }

        if (authority.PrincipalKind == PrincipalKind.ServiceApplication)
        {
            context.Succeed(requirement);
            return;
        }

        if (authority.PrincipalKind == PrincipalKind.DelegatedUserClient &&
            (await administrators.EvaluateAdministratorAsync(
                authority, true, http.RequestAborted).ConfigureAwait(false)).IsAllowed)
        {
            context.Succeed(requirement);
        }
    }
}

public static class AuthorityEndpointExtensions
{
    public static RouteHandlerBuilder RequireEffectiveAdministrator(
        this RouteHandlerBuilder builder,
        bool surfaceUnlock = true) =>
        builder.RequireAuthorization(surfaceUnlock ? AuthPolicies.Administrator : AuthPolicies.AdministratorEligibility);

    public static RouteGroupBuilder RequireEffectiveAdministrator(
        this RouteGroupBuilder builder,
        bool surfaceUnlock = true) =>
        builder.RequireAuthorization(surfaceUnlock ? AuthPolicies.Administrator : AuthPolicies.AdministratorEligibility);

    public static RouteHandlerBuilder RequireApplicationPermission(
        this RouteHandlerBuilder builder,
        ApplicationPermissionId permission) =>
        builder.RequireAuthorization(new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new ApplicationPermissionRequirement(permission))
            .Build());

    public static RouteHandlerBuilder RequireAdministratorOrApplication(
        this RouteHandlerBuilder builder,
        ApplicationPermissionId permission) =>
        builder.RequireAuthorization(new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new AdministratorOrApplicationRequirement(permission))
            .Build());

    public static RouteGroupBuilder RequireAdministratorOrApplication(
        this RouteGroupBuilder builder,
        ApplicationPermissionId permission) =>
        builder.RequireAuthorization(new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new AdministratorOrApplicationRequirement(permission))
            .Build());

    public static RouteHandlerBuilder RequireHumanOrApplicationPermission(
        this RouteHandlerBuilder builder,
        ApplicationPermissionId permission) =>
        builder.RequireAuthorization(new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new HumanOrApplicationPermissionRequirement(permission))
            .Build());

    public static RouteGroupBuilder RequireHumanOrApplicationPermission(
        this RouteGroupBuilder builder,
        ApplicationPermissionId permission) =>
        builder.RequireAuthorization(new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new HumanOrApplicationPermissionRequirement(permission))
            .Build());
}
