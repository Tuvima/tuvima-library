using System.Collections.Frozen;

namespace MediaEngine.Domain.Authorization;

public sealed record RequestAuthority(
    PrincipalKind PrincipalKind,
    bool IsAuthenticated,
    Guid? AccountId = null,
    Guid? ActiveProfileId = null,
    Guid? ApplicationId = null,
    Guid? SessionId = null,
    Guid? DeviceId = null,
    bool AccountEnabled = false,
    bool GrantEnabled = false,
    bool ApplicationEnabled = false,
    long AccountAuthorizationVersion = 0,
    long GrantAuthorizationVersion = 0,
    long ApplicationAuthorizationVersion = 0,
    bool AccountIsAdministrator = false,
    bool GrantAdminEnabled = false,
    bool ApplicationIsAdministrator = false,
    bool ActiveProfileIsRestricted = false,
    Guid? AccountHouseholdId = null,
    bool AccountIsHouseholdAdmin = false)
{
    public bool HasHumanContext =>
        PrincipalKind is PrincipalKind.Human or PrincipalKind.DelegatedUserClient &&
        HasValue(AccountId) && HasValue(ActiveProfileId);

    public bool HasApplicationContext =>
        PrincipalKind is PrincipalKind.DelegatedUserClient or PrincipalKind.ServiceApplication &&
        HasValue(ApplicationId);

    public bool IsEffectiveAdministrator =>
        IsAuthenticated && HasHumanContext && HasValidHumanPrincipal && AccountEnabled && GrantEnabled &&
        AccountIsAdministrator && GrantAdminEnabled && !ActiveProfileIsRestricted;

    /// <summary>
    /// A person (never an app) who looks after their own household: an enabled sign-in flagged as household
    /// administrator, acting from a person who is not a child profile. It says nothing about which household an
    /// action targets; compare <see cref="AccountHouseholdId"/> with the target.
    /// </summary>
    public bool IsEffectiveHouseholdAdministrator =>
        IsAuthenticated && PrincipalKind == PrincipalKind.Human && HasHumanContext && AccountEnabled && GrantEnabled &&
        AccountIsHouseholdAdmin && HasValue(AccountHouseholdId) && !ActiveProfileIsRestricted;

    public bool IsAdministratorApplication =>
        IsAuthenticated && PrincipalKind == PrincipalKind.ServiceApplication &&
        HasApplicationContext && ApplicationEnabled && ApplicationIsAdministrator;

    private static bool HasValue(Guid? value) => value is { } id && id != Guid.Empty;

    private bool HasValidHumanPrincipal =>
        PrincipalKind == PrincipalKind.Human ||
        PrincipalKind == PrincipalKind.DelegatedUserClient && HasApplicationContext && ApplicationEnabled;
}

public sealed record AuthorizationRequirement(
    ApplicationPermissionId? ApplicationPermission = null,
    AccountFeatureId? AccountFeature = null,
    bool RequiresHumanContext = false,
    bool RequiresAdministrator = false,
    bool RequiresAdministratorSurfaceUnlock = false,
    bool RequiresLibraryGrant = false);

public sealed record ResourceAuthorizationContext(
    string ResourceType,
    string ResourceId,
    Guid? LibraryId = null,
    Guid? OwnerProfileId = null,
    bool IsPrivate = false);

public sealed record DelegatedClientBinding
{
    public DelegatedClientBinding(
        Guid applicationId,
        Guid accountId,
        Guid profileId,
        Guid deviceId,
        IEnumerable<ApplicationPermissionId> consentPermissions,
        long accountAuthorizationVersion,
        long grantAuthorizationVersion,
        long applicationAuthorizationVersion)
    {
        ApplicationId = RequireId(applicationId, nameof(applicationId));
        AccountId = RequireId(accountId, nameof(accountId));
        ProfileId = RequireId(profileId, nameof(profileId));
        DeviceId = RequireId(deviceId, nameof(deviceId));
        ConsentPermissions = (consentPermissions ?? throw new ArgumentNullException(nameof(consentPermissions)))
            .ToFrozenSet();
        AccountAuthorizationVersion = accountAuthorizationVersion;
        GrantAuthorizationVersion = grantAuthorizationVersion;
        ApplicationAuthorizationVersion = applicationAuthorizationVersion;
    }

    public Guid ApplicationId { get; }
    public Guid AccountId { get; }
    public Guid ProfileId { get; }
    public Guid DeviceId { get; }
    public IReadOnlySet<ApplicationPermissionId> ConsentPermissions { get; }
    public long AccountAuthorizationVersion { get; }
    public long GrantAuthorizationVersion { get; }
    public long ApplicationAuthorizationVersion { get; }

    private static Guid RequireId(Guid value, string parameterName) =>
        value == Guid.Empty ? throw new ArgumentException("A non-empty identifier is required.", parameterName) : value;
}

public sealed record GrantAdminUnlockState(
    Guid AccountId,
    Guid ProfileId,
    bool ProtectionEnabled,
    AdminUnlockMode Mode,
    bool IsUnlocked,
    DateTimeOffset? ExpiresAt,
    long ProtectionVersion);

public enum AuthorizationDenialReason
{
    None,
    Unauthenticated,
    DisabledPrincipal,
    WrongPrincipalKind,
    MissingHumanContext,
    MissingPermission,
    PermissionUnavailable,
    MissingFeatureGrant,
    MissingLibraryGrant,
    ResourceDenied,
    AdministratorRequired,
    AdministratorUnlockRequired,
    StaleAuthority,
}

public readonly record struct AuthorizationDecision
{
    private AuthorizationDecision(bool isAllowed, AuthorizationDenialReason denialReason)
    {
        IsAllowed = isAllowed;
        DenialReason = denialReason;
    }

    public bool IsAllowed { get; }
    public AuthorizationDenialReason DenialReason { get; }

    public static AuthorizationDecision Allow() => new(true, AuthorizationDenialReason.None);
    public static AuthorizationDecision Deny(AuthorizationDenialReason reason) =>
        reason == AuthorizationDenialReason.None
            ? throw new ArgumentException("A denial reason is required.", nameof(reason))
            : new(false, reason);
}
