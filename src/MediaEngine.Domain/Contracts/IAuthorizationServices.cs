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

/// <summary>
/// Who may see whose Activity and Taste (Settings > Personal). Members and child profiles see only their own; a
/// household administrator also sees their own household; a server administrator sees everyone. The Engine decides,
/// never the page. Opening a scope that includes other people is recorded once per viewing session.
/// </summary>
public interface IPersonalScopeAuthorizer
{
    /// <summary>The window within which opening the same scope again is not recorded again.</summary>
    public static readonly TimeSpan AuditWindow = TimeSpan.FromMinutes(30);

    /// <summary>Decides the scope and, when it includes other people, records the view (<c>activity.viewed_others</c> or <c>taste.viewed_others</c>).</summary>
    Task<PersonalScopeDecision> AuthorizeAsync(
        RequestAuthority authority,
        PersonalScopeKind kind,
        PersonalScope requested,
        CancellationToken ct = default);

    /// <summary>Only a server administrator (unlocked, when they use an administrator PIN) removes activity history.</summary>
    ValueTask<bool> CanDeletePersonalHistoryAsync(RequestAuthority authority, CancellationToken ct = default);

    /// <summary>Taste is edited by the person it belongs to and nobody else; other people's taste is read-only.</summary>
    bool CanEditTaste(RequestAuthority authority, Guid profileId);
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
