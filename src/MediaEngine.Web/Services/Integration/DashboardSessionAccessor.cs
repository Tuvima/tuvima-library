using System.Security.Claims;
using MediaEngine.Contracts.Authentication;

namespace MediaEngine.Web.Services.Integration;

public sealed class DashboardSessionAccessor
{
    private readonly object _gate = new();
    private long _refreshGeneration;
    private bool _hasEstablishedSessionState;
    public event Action? OnAuthorityChanged;

    /// <summary>
    /// Raised when the Engine refuses an action with <c>secure_account_first</c>: the account still works only on this
    /// computer, so the screen can say so and point to Secure account instead of showing a bare failure.
    /// </summary>
    public event Action? OnSecureAccountRequired;

    public void NotifySecureAccountRequired() => OnSecureAccountRequired?.Invoke();
    public string? SessionToken { get; private set; }
    public Guid? AccountId { get; private set; }
    public Guid? ActiveProfileId { get; private set; }
    public Guid? SessionId { get; private set; }
    public long Revision { get; private set; }

    /// <summary>The ingress (<c>this_computer</c>, <c>home_network</c>, <c>remote</c>) of the last request that validated this session.</summary>
    public string? LastIngress { get; set; }
    public DashboardAuthorityResponse? Authority { get; private set; }

    public void Set(string? token, Guid? accountId, Guid? activeProfileId, Guid? sessionId, DashboardAuthorityResponse? authority)
    {
        Action? changedHandler;
        lock (_gate)
        {
            changedHandler = SetCore(token, accountId, activeProfileId, sessionId, authority);
        }
        changedHandler?.Invoke();
    }

    /// <summary>
    /// Seeds a new Blazor circuit from its authenticated cookie principal. The
    /// principal supplies only the session identity; capabilities always come
    /// from a subsequent Engine validation.
    /// </summary>
    public bool InitializeFromPrincipal(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var token = principal.FindFirstValue(DashboardEngineAuthenticationHandler.SessionTokenClaim);
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        Action? changedHandler;
        lock (_gate)
        {
            // A circuit mutation or an earlier validation is authoritative over
            // a retained request principal.
            if (_hasEstablishedSessionState)
            {
                return !string.IsNullOrWhiteSpace(SessionToken);
            }

            changedHandler = SetCore(
                token,
                ParseGuid(principal.FindFirstValue("tuvima:account_id")),
                ParseGuid(principal.FindFirstValue("tuvima:active_profile_id")),
                ParseGuid(principal.FindFirstValue("tuvima:session_id")),
                null);
            // A circuit has no request of its own, so it re-checks from where the cookie last validated.
            LastIngress = principal.FindFirstValue(DashboardPrincipalFactory.ClientIngressClaim);
        }
        changedHandler?.Invoke();
        return true;
    }

    public DashboardSessionRefresh SnapshotForRefresh()
    {
        lock (_gate)
        {
            return new(new(SessionToken, AccountId, ActiveProfileId, SessionId, Revision), ++_refreshGeneration);
        }
    }

    public DashboardSessionSnapshot CurrentSnapshot()
    {
        lock (_gate)
        {
            return new(SessionToken, AccountId, ActiveProfileId, SessionId, Revision);
        }
    }

    public DashboardSessionForwardingState CurrentForwardingState()
    {
        lock (_gate)
        {
            return new(SessionToken, _hasEstablishedSessionState);
        }
    }

    public bool TrySet(DashboardSessionRefresh refresh, Guid accountId, Guid profileId, Guid sessionId, DashboardAuthorityResponse authority)
    {
        Action? changedHandler;
        lock (_gate)
        {
            if (refresh.Generation != _refreshGeneration || !Matches(refresh.Snapshot))
            {
                return false;
            }

            changedHandler = SetCore(refresh.Snapshot.SessionToken, accountId, profileId, sessionId, authority);
        }
        changedHandler?.Invoke();
        return true;
    }

    public bool ClearIfCurrent(DashboardSessionRefresh refresh)
    {
        Action? changedHandler;
        lock (_gate)
        {
            if (refresh.Generation != _refreshGeneration || !Matches(refresh.Snapshot))
            {
                return false;
            }

            changedHandler = SetCore(null, null, null, null, null);
        }
        changedHandler?.Invoke();
        return true;
    }

    public bool ClearAuthorityIfCurrent(DashboardSessionRefresh refresh)
    {
        Action? changedHandler;
        lock (_gate)
        {
            if (refresh.Generation != _refreshGeneration || !Matches(refresh.Snapshot))
            {
                return false;
            }

            changedHandler = SetCore(
                refresh.Snapshot.SessionToken,
                refresh.Snapshot.AccountId,
                refresh.Snapshot.ActiveProfileId,
                refresh.Snapshot.SessionId,
                null);
        }
        changedHandler?.Invoke();
        return true;
    }

    public void ClearAdministratorActions()
    {
        Action? changedHandler;
        lock (_gate)
        {
            if (Authority is null)
            {
                return;
            }

            var actions = Authority.ActionCapabilities
                .Where(action => action is not "access.manage" and not "applications.manage" and not "administrator.lock")
                .ToArray();
            changedHandler = SetCore(SessionToken, AccountId, ActiveProfileId, SessionId,
                Authority with { AdministratorSurfaceUnlocked = false, AdministratorUnlockExpiresAt = null, ActionCapabilities = actions });
        }
        changedHandler?.Invoke();
    }

    private Action? SetCore(string? token, Guid? accountId, Guid? activeProfileId, Guid? sessionId, DashboardAuthorityResponse? authority)
    {
        _hasEstablishedSessionState = true;
        _refreshGeneration++;
        var changed = SessionToken != token
            || AccountId != accountId
            || ActiveProfileId != activeProfileId
            || SessionId != sessionId
            || !AuthorityEquivalent(Authority, authority);
        SessionToken = token;
        AccountId = accountId;
        ActiveProfileId = activeProfileId;
        SessionId = sessionId;
        Authority = authority;
        Revision++;
        return changed ? OnAuthorityChanged : null;
    }

    private static bool AuthorityEquivalent(DashboardAuthorityResponse? left, DashboardAuthorityResponse? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return left.AccountId == right.AccountId
            && left.ActiveProfileId == right.ActiveProfileId
            && left.AccountEnabled == right.AccountEnabled
            && left.GrantEnabled == right.GrantEnabled
            && left.AccountAuthorizationVersion == right.AccountAuthorizationVersion
            && left.GrantAuthorizationVersion == right.GrantAuthorizationVersion
            && left.EffectiveAdministrator == right.EffectiveAdministrator
            && left.AdministratorSurfaceUnlocked == right.AdministratorSurfaceUnlocked
            && left.AdministratorUnlockExpiresAt == right.AdministratorUnlockExpiresAt
            && left.AdministratorProtectionVersion == right.AdministratorProtectionVersion
            && left.ProfileGrants.SequenceEqual(right.ProfileGrants)
            && left.NavigationCapabilities.SequenceEqual(right.NavigationCapabilities, StringComparer.Ordinal)
            && left.ActionCapabilities.SequenceEqual(right.ActionCapabilities, StringComparer.Ordinal);
    }

    private bool Matches(DashboardSessionSnapshot snapshot) =>
        Revision == snapshot.Revision
        && SessionToken == snapshot.SessionToken
        && AccountId == snapshot.AccountId
        && ActiveProfileId == snapshot.ActiveProfileId
        && SessionId == snapshot.SessionId;

    private static Guid? ParseGuid(string? value) => Guid.TryParse(value, out var parsed) ? parsed : null;

    public bool HasNavigation(string capability) =>
        Authority?.NavigationCapabilities.Contains(capability, StringComparer.Ordinal) == true;

    public bool HasAction(string capability) =>
        Authority?.ActionCapabilities.Contains(capability, StringComparer.Ordinal) == true;

    public bool ShouldLockOnLeave => Authority?.ProfileGrants
        .FirstOrDefault(grant => grant.ProfileId == ActiveProfileId)
        ?.AdminProtection.UnlockMode.Equals("LockOnLeave", StringComparison.OrdinalIgnoreCase) == true;
}

public sealed record DashboardSessionSnapshot(string? SessionToken, Guid? AccountId, Guid? ActiveProfileId, Guid? SessionId, long Revision);
public sealed record DashboardSessionRefresh(DashboardSessionSnapshot Snapshot, long Generation);
public readonly record struct DashboardSessionForwardingState(string? SessionToken, bool HasEstablishedSessionState);
