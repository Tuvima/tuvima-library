using MediaEngine.Api.Security;
using MediaEngine.Api.Services.View;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using Microsoft.AspNetCore.Http;

namespace MediaEngine.Api.Tests;

internal sealed class TestViewAuthorityResolver(RequestAuthority authority) : IRequestAuthorityResolver
{
    public RequestAuthority Authority => authority;
    public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
        ValueTask.FromResult(authority);

    public static TestViewAuthorityResolver Anonymous { get; } =
        new(new RequestAuthority(PrincipalKind.Anonymous, false));

    public static TestViewAuthorityResolver Human(Guid profileId) =>
        new(new RequestAuthority(PrincipalKind.Human, true, Guid.NewGuid(), profileId,
            AccountEnabled: true, GrantEnabled: true));
}

internal sealed class TestAllowAuthorizationEvaluator : IAuthorizationEvaluator
{
    public AuthorizationRequirement? LastRequirement { get; private set; }

    public ValueTask<AuthorizationDecision> EvaluateAsync(RequestAuthority authority,
        AuthorizationRequirement requirement, ResourceAuthorizationContext? resource,
        CancellationToken cancellationToken = default)
    {
        LastRequirement = requirement;
        return ValueTask.FromResult(
            authority.IsAuthenticated
                && (!authority.HasHumanContext || authority.AccountEnabled && authority.GrantEnabled)
                && (!authority.HasApplicationContext || authority.ApplicationEnabled)
                ? AuthorizationDecision.Allow()
                : AuthorizationDecision.Deny(AuthorizationDenialReason.DisabledPrincipal));
    }
}

/// <summary>Allows exactly what the real rules allow: an effective server administrator, or an effective household administrator.</summary>
internal sealed class AdministratorDecisions : IAccountAccessDecisionService
{
    public bool LastRequiredSurfaceUnlock { get; private set; }

    public ValueTask<AuthorizationDecision> EvaluateFeatureAsync(RequestAuthority authority, AccountFeatureId feature,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());

    public ValueTask<AuthorizationDecision> EvaluateLibraryAsync(RequestAuthority authority, Guid libraryId,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());

    public ValueTask<AuthorizationDecision> EvaluateAdministratorAsync(RequestAuthority authority,
        bool requireSurfaceUnlock, CancellationToken cancellationToken = default)
    {
        LastRequiredSurfaceUnlock = requireSurfaceUnlock;
        return ValueTask.FromResult(authority.IsEffectiveAdministrator
            ? AuthorizationDecision.Allow()
            : AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorRequired));
    }

    public ValueTask<AuthorizationDecision> EvaluateHouseholdAdministratorAsync(RequestAuthority authority,
        bool requireSurfaceUnlock, CancellationToken cancellationToken = default)
    {
        LastRequiredSurfaceUnlock = requireSurfaceUnlock;
        return ValueTask.FromResult(authority.IsEffectiveHouseholdAdministrator
            ? AuthorizationDecision.Allow()
            : AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorRequired));
    }
}

internal sealed class RecordingAuditWriter : IAuthorizationAuditWriter
{
    public List<AuthorizationAuditEvent> Events { get; } = [];

    public ValueTask WriteAsync(AuthorizationAuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        Events.Add(auditEvent);
        return ValueTask.CompletedTask;
    }
}
