using MediaEngine.Api.Endpoints;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity.Contracts;

namespace MediaEngine.Api.Security;

public enum ManagedSessionRevokeOutcome
{
    Revoked,
    NotFound,
    Forbidden,
    OwnSession,
}

/// <summary>
/// Lists and signs out the sessions of the people an administrator looks after, for the Devices page. A server
/// administrator reaches every account; a household administrator reaches the accounts of their own household, except
/// the sign-ins of server administrators and of other household administrators, which only a server administrator may
/// touch (the same rule that protects those sign-ins elsewhere).
/// </summary>
public sealed class ManagedSessionService(
    IAccountRepository accounts,
    IIdentityRepository identities,
    IProfileRepository profiles,
    IAccountAccessDecisionService decisions,
    IFirstPartyIdentityService firstParty,
    IAuthorizationAuditWriter audit,
    TimeProvider clock)
{
    public const string RevokedReason = "admin_revoked";
    public const string OwnSessionMessage = "Use Sign out for your own session";

    public async Task<IReadOnlyList<ManagedAccountSessionsResponse>> ListAsync(
        RequestAuthority actor, CancellationToken ct = default)
    {
        var scope = await ResolveScopeAsync(actor, ct).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var result = new List<ManagedAccountSessionsResponse>();
        foreach (var account in await accounts.GetAllAsync(ct).ConfigureAwait(false))
        {
            if (!InScope(scope, actor, account))
            {
                continue;
            }

            var active = (await identities.GetSessionsAsync(account.Id, ct).ConfigureAwait(false))
                .Where(session => session.IsActive(now))
                .OrderByDescending(session => session.LastSeenAt)
                .ToList();
            if (active.Count == 0)
            {
                continue;
            }

            var (profileName, isOwnSignIn) = await PersonNameAsync(account, ct).ConfigureAwait(false);
            var email = account.HasEmail ? account.Email : null;
            result.Add(new ManagedAccountSessionsResponse(
                account.Id,
                email ?? profileName ?? "No email",
                email,
                account.HouseholdId,
                isOwnSignIn ? profileName : null,
                AuthenticationEndpoints.ToActiveSessionResponses(active, now)));
        }

        return result
            .OrderBy(account => account.AccountLabel, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<ManagedSessionRevokeOutcome> RevokeAsync(
        RequestAuthority actor, Guid sessionId, CancellationToken ct = default)
    {
        var scope = await ResolveScopeAsync(actor, ct).ConfigureAwait(false);
        if (actor.SessionId == sessionId)
        {
            return ManagedSessionRevokeOutcome.OwnSession;
        }

        var session = await identities.GetSessionByIdAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null || !session.IsActive(clock.GetUtcNow()))
        {
            return ManagedSessionRevokeOutcome.NotFound;
        }

        var owner = await accounts.GetByIdAsync(session.AccountId, ct).ConfigureAwait(false);
        if (owner is null || !InScope(scope, actor, owner))
        {
            return ManagedSessionRevokeOutcome.Forbidden;
        }

        if (!await firstParty.RevokeSessionAsync(sessionId, RevokedReason, ct).ConfigureAwait(false))
        {
            return ManagedSessionRevokeOutcome.NotFound;
        }

        await audit.WriteAsync(new AuthorizationAuditEvent(
            "account.session_revoked_by_administrator",
            clock.GetUtcNow(),
            actor.AccountId,
            actor.ActiveProfileId,
            actor.ApplicationId,
            "session",
            sessionId.ToString("D"),
            new Dictionary<string, string?> { ["account_id"] = owner.Id.ToString("D") }), ct).ConfigureAwait(false);
        return ManagedSessionRevokeOutcome.Revoked;
    }

    /// <summary>
    /// <see langword="null"/> for a server administrator (every household), the actor's household for a household
    /// administrator. Anyone else, and any app, is refused.
    /// </summary>
    private async Task<Guid?> ResolveScopeAsync(RequestAuthority actor, CancellationToken ct)
    {
        if (actor.PrincipalKind != PrincipalKind.Human)
        {
            throw new UnauthorizedAccessException("Sign-ins can only be managed by a person.");
        }

        if ((await decisions.EvaluateAdministratorAsync(actor, true, ct).ConfigureAwait(false)).IsAllowed)
        {
            return null;
        }

        if (!actor.IsEffectiveAdministrator && actor.AccountHouseholdId is { } household &&
            (await decisions.EvaluateHouseholdAdministratorAsync(actor, true, ct).ConfigureAwait(false)).IsAllowed)
        {
            return household;
        }

        throw new UnauthorizedAccessException("Unlocked administrator authority is required.");
    }

    private static bool InScope(Guid? household, RequestAuthority actor, Account account)
    {
        if (household is not { } own)
        {
            return true;
        }

        if (account.HouseholdId != own)
        {
            return false;
        }

        // Another administrator's sign-in is not a household administrator's to manage; their own always is.
        return account.Id == actor.AccountId || !(account.IsAdministrator || account.HouseholdAdmin);
    }

    /// <summary>
    /// The person an account opens: a person's own sign-in opens exactly one, any other account is named by its
    /// default person. The flag says the account is a person's own sign-in.
    /// </summary>
    private async Task<(string? Name, bool IsOwnSignIn)> PersonNameAsync(Account account, CancellationToken ct)
    {
        var grants = (await accounts.GetGrantsAsync(account.Id, ct).ConfigureAwait(false))
            .Where(grant => grant.IsEnabled).ToArray();
        var isOwn = account.GrantsInheritFromAccountId is not null && !account.IsAdministrator && grants.Length == 1;
        var grant = isOwn ? grants[0] : grants.FirstOrDefault(candidate => candidate.IsDefault) ?? grants.FirstOrDefault();
        var profile = grant is null ? null : await profiles.GetByIdAsync(grant.ProfileId, ct).ConfigureAwait(false);
        return (profile?.DisplayName, isOwn && profile is not null);
    }
}
