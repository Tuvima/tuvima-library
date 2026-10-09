using MediaEngine.Domain.Authorization;

namespace MediaEngine.Domain.Contracts;

public interface IPermissionRegistry
{
    IReadOnlyList<PermissionDefinition> GetAll();
    IReadOnlyList<PermissionDefinition> GetAvailableFor(ApplicationType applicationType);
    bool TryGet(ApplicationPermissionId id, out PermissionDefinition definition);
}

public interface IAuthorizationEvaluator
{
    ValueTask<AuthorizationDecision> EvaluateAsync(
        RequestAuthority authority,
        AuthorizationRequirement requirement,
        ResourceAuthorizationContext? resource,
        CancellationToken cancellationToken = default);
}

public interface IAccountAccessDecisionService
{
    ValueTask<AuthorizationDecision> EvaluateFeatureAsync(
        RequestAuthority authority,
        AccountFeatureId feature,
        CancellationToken cancellationToken = default);

    ValueTask<AuthorizationDecision> EvaluateLibraryAsync(
        RequestAuthority authority,
        Guid libraryId,
        CancellationToken cancellationToken = default);

    ValueTask<AuthorizationDecision> EvaluateAdministratorAsync(
        RequestAuthority authority,
        bool requireSurfaceUnlock,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the caller may act as a household administrator (for their own household only; the caller still checks
    /// the target household). The default allows an effective household administrator; the real service also applies
    /// the administrator PIN when the person has turned protection on.
    /// </summary>
    ValueTask<AuthorizationDecision> EvaluateHouseholdAdministratorAsync(
        RequestAuthority authority,
        bool requireSurfaceUnlock,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(authority.IsEffectiveHouseholdAdministrator
            ? AuthorizationDecision.Allow()
            : AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorRequired));
}

public interface ISelfServiceAuthorizationService
{
    ValueTask<AuthorizationDecision> EvaluateAccountAsync(
        RequestAuthority authority,
        Guid accountId,
        CancellationToken cancellationToken = default);

    ValueTask<AuthorizationDecision> EvaluateProfileAsync(
        RequestAuthority authority,
        Guid profileId,
        CancellationToken cancellationToken = default);
}

public interface IPrivateResourceAuthorizationService
{
    ValueTask<AuthorizationDecision> EvaluateAsync(
        RequestAuthority authority,
        PrivateResourceAction action,
        ResourceAuthorizationContext resource,
        CancellationToken cancellationToken = default);
}

public interface IGrantAdminUnlockService
{
    ValueTask<GrantAdminUnlockState> GetStateAsync(
        RequestAuthority authority,
        CancellationToken cancellationToken = default);

    ValueTask<GrantAdminUnlockState> UnlockAsync(
        RequestAuthority authority,
        string pin,
        CancellationToken cancellationToken = default);

    ValueTask LockAsync(
        RequestAuthority authority,
        CancellationToken cancellationToken = default);
}

public interface IAuthorizationInvalidationService
{
    ValueTask InvalidateAccountAsync(Guid accountId, CancellationToken cancellationToken = default);
    ValueTask InvalidateGrantAsync(Guid accountId, Guid profileId, CancellationToken cancellationToken = default);
    ValueTask InvalidateApplicationAsync(Guid applicationId, CancellationToken cancellationToken = default);
    ValueTask InvalidateSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

public interface IAuthorizationAuditWriter
{
    ValueTask WriteAsync(AuthorizationAuditEvent auditEvent, CancellationToken cancellationToken = default);
}
