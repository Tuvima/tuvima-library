using System.Collections.Frozen;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Domain.Authorization;

/// <summary>
/// Who is acting and what they may do, worked out fresh on every request. Access comes from the <b>active profile</b> (users, profiles
/// and households spec): the person signed in is a user, the profile they are acting as holds the roles and access. A user acts as
/// its own profile or any profile of its own household, and never as another household's.
/// </summary>
/// <param name="ActiveProfileInUserHousehold">
/// True when the active profile exists and sits in the same household as the profile the user signs in as. Everything a person may do
/// as a human requires it.
/// </param>
/// <param name="ProfileAuthorizationVersion">The active profile's authorization version; it changes whenever that profile's access does.</param>
/// <param name="ActiveProfileIsServerAdministrator">The active profile holds the server administrator role.</param>
/// <param name="ActiveProfileHouseholdId">The household of the active profile.</param>
/// <param name="ActiveProfileIsHouseholdAdministrator">
/// The active profile holds the household administrator role and a user is attached to it (a household administrator always has a user).
/// </param>
public sealed record RequestAuthority(
    PrincipalKind PrincipalKind,
    bool IsAuthenticated,
    Guid? AccountId = null,
    Guid? ActiveProfileId = null,
    Guid? ApplicationId = null,
    Guid? SessionId = null,
    Guid? DeviceId = null,
    bool AccountEnabled = false,
    bool ActiveProfileInUserHousehold = false,
    bool ApplicationEnabled = false,
    long AccountAuthorizationVersion = 0,
    long ProfileAuthorizationVersion = 0,
    long ApplicationAuthorizationVersion = 0,
    bool ActiveProfileIsServerAdministrator = false,
    bool ApplicationIsAdministrator = false,
    bool ActiveProfileIsRestricted = false,
    Guid? ActiveProfileHouseholdId = null,
    bool ActiveProfileIsHouseholdAdministrator = false)
{
    public bool HasHumanContext =>
        PrincipalKind is PrincipalKind.Human or PrincipalKind.DelegatedUserClient &&
        HasValue(AccountId) && HasValue(ActiveProfileId);

    public bool HasApplicationContext =>
        PrincipalKind is PrincipalKind.DelegatedUserClient or PrincipalKind.ServiceApplication &&
        HasValue(ApplicationId);

    /// <summary>
    /// An enabled user acting as a server administrator profile of its own household, which is not a child profile. Administrator
    /// rights apply only while the person is on a profile that holds them (a household administrator switches to the server
    /// administrator's profile to act as one).
    /// </summary>
    public bool IsEffectiveAdministrator =>
        IsAuthenticated && HasHumanContext && HasValidHumanPrincipal && AccountEnabled && ActiveProfileInUserHousehold &&
        ActiveProfileIsServerAdministrator && !ActiveProfileIsRestricted;

    /// <summary>
    /// A person (never an app) who looks after their own household: an enabled user acting as a household administrator profile
    /// that has a user attached, which is not a child profile. It says nothing about which household an action targets; compare
    /// <see cref="ActiveProfileHouseholdId"/> with the target.
    /// </summary>
    public bool IsEffectiveHouseholdAdministrator =>
        IsAuthenticated && PrincipalKind == PrincipalKind.Human && HasHumanContext && AccountEnabled && ActiveProfileInUserHousehold &&
        ActiveProfileIsHouseholdAdministrator && HasValue(ActiveProfileHouseholdId) && !ActiveProfileIsRestricted;

    /// <summary>
    /// This authority acting as <paramref name="profile"/> (read fresh from the data store): the one place that turns what a profile
    /// holds into the fields above, so every way of building an authority (a request, a paired app, an HLS or cast grant, the Dashboard
    /// projection) applies the same rules. A profile that is missing or outside the user's household gives no profile rights at all,
    /// and a server or household administrator role only counts while an enabled user is attached to the profile.
    /// </summary>
    public RequestAuthority WithActiveProfile(ActiveProfileAccess? profile) => this with
    {
        ActiveProfileInUserHousehold = profile is not null,
        ProfileAuthorizationVersion = profile?.AuthorizationVersion ?? 0,
        ActiveProfileIsServerAdministrator = profile is { IsServerAdministrator: true, HasEnabledUser: true },
        ActiveProfileIsRestricted = profile?.Role == ProfileRole.RestrictedProfile,
        ActiveProfileHouseholdId = profile?.HouseholdId,
        ActiveProfileIsHouseholdAdministrator = profile is { IsHouseholdAdministrator: true, HasEnabledUser: true },
    };

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
        long profileAuthorizationVersion,
        long applicationAuthorizationVersion)
    {
        ApplicationId = RequireId(applicationId, nameof(applicationId));
        AccountId = RequireId(accountId, nameof(accountId));
        ProfileId = RequireId(profileId, nameof(profileId));
        DeviceId = RequireId(deviceId, nameof(deviceId));
        ConsentPermissions = (consentPermissions ?? throw new ArgumentNullException(nameof(consentPermissions)))
            .ToFrozenSet();
        AccountAuthorizationVersion = accountAuthorizationVersion;
        ProfileAuthorizationVersion = profileAuthorizationVersion;
        ApplicationAuthorizationVersion = applicationAuthorizationVersion;
    }

    public Guid ApplicationId { get; }
    public Guid AccountId { get; }
    public Guid ProfileId { get; }
    public Guid DeviceId { get; }
    public IReadOnlySet<ApplicationPermissionId> ConsentPermissions { get; }
    public long AccountAuthorizationVersion { get; }
    public long ProfileAuthorizationVersion { get; }
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
