using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Contracts;

public interface IAccessRepository
{
    /// <summary>
    /// What a user may act as: the profile it signs in as, and every other profile of its household (users, profiles and
    /// households spec: a user switches inside its own household and never into another). One entry per profile; the user's own
    /// profile first. Empty when the user does not exist.
    /// </summary>
    Task<IReadOnlyList<HouseholdProfileAccess>> GetHouseholdProfilesAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>
    /// The profile a user is acting as, with its roles and version, or <see langword="null"/> when that profile does not exist or is
    /// not in the household of the profile the user signs in as. Read fresh on every request; Read/Watch/Listen/View and library
    /// access of the profile come from <see cref="IProfileAccessRepository"/>.
    /// </summary>
    Task<ActiveProfileAccess?> GetActiveProfileAccessAsync(Guid accountId, Guid profileId, CancellationToken ct = default);
    Task WriteAuthorizationAuditAsync(AuthorizationAuditEvent auditEvent, CancellationToken ct = default);

    /// <summary>
    /// Whether the same event (type, acting person, subject) was already recorded at or after <paramref name="since"/>.
    /// Used to record "looked at other people's data" once per viewing session instead of once per request.
    /// </summary>
    Task<bool> HasAuthorizationAuditSinceAsync(
        string eventType,
        Guid? actorProfileId,
        string subjectType,
        string subjectId,
        DateTimeOffset since,
        CancellationToken ct = default);
}

public interface IAccountAccessMutationRepository
{
    /// <summary>
    /// Creates a user (<paramref name="user"/>, whose <see cref="Account.ProfileId"/> names the profile it opens) in one transaction,
    /// with its invitation when one is given. The profile is either one that exists and has no user yet, or the brand-new
    /// <see cref="NewUserPlacement.NewProfile"/> made in the same step. A brand-new profile (and, on a first start, the existing
    /// profile named by <see cref="NewUserPlacement.SetUpProfile"/>) takes the roles and access described by the placement;
    /// attaching a user to a person who already exists never changes what that person may do (only an administrator role left over
    /// from an earlier sign-in is cleared). A new household is made in the same step, so a failure leaves no empty household behind;
    /// its first profile looks after it. Joining a household that already exists never promotes anyone.
    /// </summary>
    Task CreateUserAsync(Account user, NewUserPlacement placement, AccountInvitation? invitation = null, CancellationToken ct = default);
    Task UpdateAccountAsync(Account account, CancellationToken ct = default);
    Task DeleteAccountAsync(Guid accountId, CancellationToken ct = default);
    /// <summary>
    /// Works out what deleting this user does (<see cref="UserRemovalPlan"/>): normally only the sign-in goes; the last enabled user
    /// attached to a household-administrator profile of a household that is not the default one takes the household and every
    /// profile in it. Changes nothing.
    /// </summary>
    Task<UserRemovalPlan> PlanUserRemovalAsync(Guid userId, CancellationToken ct = default);
    /// <summary>
    /// Deletes the user in one transaction. A sign-in-only delete leaves the profile and everything in it (the profile loses its
    /// administrator roles, because an administrator without a sign-in cannot exist) and keeps the last-administrator backstop. A
    /// household removal (see <see cref="PlanUserRemovalAsync"/>) deletes every user of the household, then every profile with
    /// everything personal it left behind, then the household and its empty Shared Library; if the household's Shared Library holds
    /// shared photos nothing is deleted. When <paramref name="confirmedProfileIds"/> is given, the profiles the household removal
    /// would take (none for a sign-in-only delete), worked out inside the transaction, must be exactly those, or nothing is deleted
    /// and <see cref="AccountRemovalChangedException"/> is thrown; <see langword="null"/> skips the check.
    /// </summary>
    Task<UserRemovalOutcome> DeleteUserAsync(
        Guid userId, IReadOnlyCollection<Guid>? confirmedProfileIds = null, CancellationToken ct = default);
    /// <summary>
    /// Adds a person (a profile without a user) to a household. Refused when the household is full.
    /// <paramref name="accessFromProfileId"/> is the profile of whoever is adding them: the new person starts with that profile's
    /// Read/Watch/Listen/View access and libraries (View off and no libraries when it is in another household).
    /// <see langword="null"/> gives every feature and no library. Never an administrator role.
    /// </summary>
    Task CreateHouseholdPersonAsync(
        MediaEngine.Domain.Aggregates.Profile profile,
        Guid householdId,
        CancellationToken ct = default,
        Guid? accessFromProfileId = null);
    Task UpdateManagedProfileAsync(
        MediaEngine.Domain.Aggregates.Profile profile,
        CancellationToken ct = default);
    /// <summary>Throws the same errors as <see cref="DeleteManagedProfileAsync"/> would, without removing anything.</summary>
    Task ValidateManagedProfileRemovalAsync(Guid profileId, CancellationToken ct = default);
    /// <summary>
    /// Deletes a profile and everything personal in it, and the profile's user when it has one (a sign-in without a profile cannot
    /// exist), in one transaction. Refused for the Owner profile, the last household-administrator profile of its household and the
    /// last server administrator that can sign in.
    /// </summary>
    Task<ProfileRemovalOutcome> DeleteManagedProfileAsync(Guid profileId, CancellationToken ct = default);
    /// <summary>True when "Ask for my PIN on admin pages" is switched on for this profile.</summary>
    Task<bool> HasEnabledAdminProtectionAsync(Guid profileId, CancellationToken ct = default);
    /// <summary>Ends the administrator unlock of a session (on sign-out, profile switch or when the session is revoked).</summary>
    Task ClearAdminUnlockAsync(Guid sessionId, CancellationToken ct = default);
}

public interface IApplicationRepository
{
    Task<Application?> GetApplicationAsync(Guid id, CancellationToken ct = default);
    Task<Application?> GetApplicationByClientIdAsync(string clientId, CancellationToken ct = default);
    Task<IReadOnlySet<string>> GetApplicationClientBindingsAsync(Guid applicationId, CancellationToken ct = default);
    Task<IReadOnlyList<Application>> GetApplicationsAsync(CancellationToken ct = default);
    Task<IReadOnlySet<ApplicationPermissionId>> GetApplicationPermissionsAsync(Guid applicationId, CancellationToken ct = default);
    Task<ApplicationCredentialIdentity?> FindApplicationCredentialAsync(string hash, DateTimeOffset now, CancellationToken ct = default);
    Task<IReadOnlyList<ApplicationCredential>> GetApplicationCredentialsAsync(Guid applicationId, CancellationToken ct = default);
    Task InsertApplicationAsync(Application application, IReadOnlySet<ApplicationPermissionId> permissions, CancellationToken ct = default);
    Task UpdateApplicationAsync(Application application, CancellationToken ct = default);
    Task<bool> DeleteApplicationAsync(Guid applicationId, DateTimeOffset deletedAt, CancellationToken ct = default);
    Task ReplacePermissionsAsync(Guid applicationId, IReadOnlySet<ApplicationPermissionId> permissions, DateTimeOffset changedAt, CancellationToken ct = default);
    Task ReplaceClientBindingsAsync(Guid applicationId, IReadOnlySet<string> clientIds, DateTimeOffset changedAt, CancellationToken ct = default);
    Task InsertCredentialAsync(ApplicationCredential credential, CancellationToken ct = default);
    Task<bool> RevokeCredentialAsync(Guid applicationId, Guid credentialId, DateTimeOffset revokedAt, CancellationToken ct = default);
    Task<bool> RotateCredentialAsync(Guid applicationId, Guid priorCredentialId, ApplicationCredential replacement, DateTimeOffset rotatedAt, CancellationToken ct = default);
    Task TouchCredentialUsageAsync(Guid applicationId, Guid credentialId, DateTimeOffset usedAt, CancellationToken ct = default);
}

/// <summary>The wanted state of "Ask for my PIN on admin pages" for one profile.</summary>
public sealed record ProfileAdminProtectionCommand(
    bool Enabled,
    AdminUnlockMode UnlockMode,
    int? UnlockMinutes);

/// <summary>
/// Where a new user's profile comes from and what it starts with. <see cref="NewProfile"/> is a brand-new profile made in the same
/// step; it goes into <see cref="HouseholdId"/> (an existing household) or into a new household named
/// <see cref="NewHouseholdName"/> whose first profile looks after it. With neither, a new household with the default name is made
/// (first start only). <see cref="SetUpProfile"/> applies the same set-up to the existing profile a user opens (the first start,
/// where the Owner profile becomes the first server administrator's). A profile that is set up takes <see cref="IsServerAdministrator"/>,
/// the household administrator role when its household was just made for it, and <see cref="Features"/> and <see cref="Libraries"/>
/// (the Vault is refused as a library). Any other existing profile keeps exactly what it has.
/// </summary>
public sealed record NewUserPlacement(
    MediaEngine.Domain.Aggregates.Profile? NewProfile = null,
    Guid? HouseholdId = null,
    string? NewHouseholdName = null,
    bool SetUpProfile = false,
    bool IsServerAdministrator = false,
    IReadOnlySet<AccountFeatureId>? Features = null,
    IReadOnlySet<Guid>? Libraries = null);

/// <summary>An invitation just made. <paramref name="Code"/> is shown as <c>XXXXX-XXXXX</c> and is never available again.</summary>
public sealed record IssuedAccountInvitation(
    Guid AccountId,
    string Code,
    DateTimeOffset ExpiresAt);

public sealed record UpdateManagedProfileCommand(
    string DisplayName,
    string? AvatarColor,
    string? AvatarIcon = null,
    string? ContentLimit = null,
    bool? ContentLimitAllowUnrated = null);

/// <summary>A new person in a household: a child person gets the restricted role; a PIN is optional.</summary>
public sealed record AddHouseholdPersonCommand(
    Guid HouseholdId,
    string DisplayName,
    string? AvatarColor,
    bool IsChild,
    string? Pin,
    string? AvatarIcon = null,
    string? ContentLimit = null,
    bool ContentLimitAllowUnrated = false);

/// <summary>How a new user first signs in: an invitation (link and QR code) to choose their own password, or a starting password.</summary>
public enum NewUserSignInMethod
{
    Invitation,
    TemporaryPassword,
}

/// <summary>
/// A new profile made together with its user. The profile goes into <paramref name="HouseholdId"/> or into a new household called
/// <paramref name="NewHouseholdName"/> (exactly one of the two); a new household's first profile looks after it. A Kids profile
/// (<paramref name="IsKids"/>) has the Kids content limit and cannot look after a household.
/// </summary>
public sealed record NewUserProfileCommand(
    string DisplayName,
    string? AvatarColor,
    Guid? HouseholdId,
    string? NewHouseholdName,
    bool IsKids = false);

/// <summary>
/// Attaches a new user (a sign-in) to an existing profile that has none (<paramref name="ProfileId"/>) or to a new profile
/// (<paramref name="NewProfile"/>); exactly one of the two. Roles and access are not set here.
/// </summary>
public sealed record CreateUserCommand(
    string? Email,
    Guid? ProfileId,
    NewUserProfileCommand? NewProfile,
    NewUserSignInMethod Method,
    string? TemporaryPassword = null);

/// <summary>The user just made, the profile it opens and its invitation when that was the chosen sign-in method.</summary>
public sealed record CreatedUser(Account User, MediaEngine.Domain.Aggregates.Profile Profile, IssuedAccountInvitation? Invitation);

/// <summary>
/// What deleting a user would do, for the confirmation. <paramref name="RefusedReason"/> is one plain sentence when the delete
/// would be refused (null when it is allowed); <paramref name="HouseholdHasPersonalPhotos"/> says a household removal would meet
/// personal photos in its profiles.
/// </summary>
public sealed record UserDeletionPreview(UserRemovalPlan Plan, string? RefusedReason, bool HouseholdHasPersonalPhotos);

public interface IAccountAccessMutationService
{
    /// <summary>
    /// Gives an existing account a new password. By default it is temporary and the person must choose their own at next
    /// sign-in; with <paramref name="mustChangePassword"/> off it is an ordinary password.
    /// </summary>
    Task SetTemporaryPasswordAsync(RequestAuthority actor, Guid accountId, string temporaryPassword, bool mustChangePassword = true, CancellationToken ct = default);
    /// <summary>Changes the email a person signs in with. A server administrator, or the household administrator of the account's household. Audited.</summary>
    Task<Account> ChangeEmailAsync(RequestAuthority actor, Guid accountId, string email, CancellationToken ct = default);
    /// <summary>Turns two-step codes off for someone who lost their authenticator app and recovery codes. Audited.</summary>
    Task ResetTwoStepAsync(RequestAuthority actor, Guid accountId, CancellationToken ct = default);
    /// <summary>
    /// Makes a new user (a sign-in), only for a server administrator (or an application trusted with identity.users.write). It
    /// opens an existing profile that has no sign-in yet, or a new profile made with it (a new profile is an ordinary member, Kids
    /// only when asked). Starting a household for the new profile is refused until every server administrator has an email and a
    /// password or passkey (D10).
    /// </summary>
    Task<CreatedUser> CreateUserAsync(RequestAuthority actor, CreateUserCommand command, CancellationToken ct = default);
    /// <summary>Lists every user (sign-in), only for an unlocked server administrator or an application trusted with identity.users.read.</summary>
    Task<IReadOnlyList<Account>> ListUsersAsync(RequestAuthority actor, CancellationToken ct = default);
    /// <summary>
    /// Deletes a user (D5, D6), only for a server administrator (or a trusted application). Normally only the sign-in goes and the
    /// profile with everything in it stays. The last enabled user attached to a household-administrator profile of a non-default
    /// household takes the household and every profile in it, with <paramref name="photos"/> applied to each profile. Never the
    /// actor's own user, a server administrator that would be the last one who can sign in, or the default household. A caller that
    /// confirmed a list of profiles (<paramref name="confirmedProfileIds"/>, empty for a sign-in-only delete) has it checked against
    /// what would go, before anything moves and again inside the delete: if they differ, nothing changes and
    /// <see cref="AccountRemovalChangedException"/> is thrown. <see langword="null"/> skips the check only for a sign-in-only
    /// delete: a delete that would remove a household always needs the confirmed list.
    /// </summary>
    Task DeleteUserAsync(
        RequestAuthority actor,
        Guid userId,
        ProfilePhotoDisposition photos = ProfilePhotoDisposition.MoveToShared,
        IReadOnlyCollection<Guid>? confirmedProfileIds = null,
        CancellationToken ct = default);
    /// <summary>What <see cref="DeleteUserAsync"/> would do, and whether it would be refused, for the confirmation. Changes nothing.</summary>
    Task<UserDeletionPreview> PreviewDeleteUserAsync(RequestAuthority actor, Guid userId, CancellationToken ct = default);
    /// <summary>
    /// Replaces the Read/Watch/Listen/View and library access of a profile. A server administrator may set anything; a household
    /// administrator only within their own household, only what their own profile holds, and never on a server administrator's profile.
    /// </summary>
    Task ReplaceProfileAccessAsync(RequestAuthority actor, Guid profileId, IReadOnlySet<AccountFeatureId> features, IReadOnlySet<Guid> libraries, CancellationToken ct = default);
    /// <summary>
    /// Sets the household administrator and server administrator roles of a profile (D15, D17). A household administrator may change
    /// the household administrator role of profiles in their own household; only a server administrator changes the server
    /// administrator role.
    /// </summary>
    Task SetProfileRolesAsync(RequestAuthority actor, Guid profileId, bool isHouseholdAdministrator, bool isServerAdministrator, CancellationToken ct = default);
    Task<MediaEngine.Domain.Aggregates.Profile> UpdateProfileAsync(
        RequestAuthority actor,
        Guid profileId,
        UpdateManagedProfileCommand command,
        CancellationToken ct = default);
    /// <summary>
    /// Removes a person and their user too, if they have one (D7, D8). Their habits (likes, My List, progress, bookmarks, taste) always
    /// go; <paramref name="photos"/> decides what happens to their personal photos. Library files are never touched. A household
    /// administrator removes people of their own household; never the last household-administrator profile of a household, a server
    /// administrator's profile (unless the actor is a server administrator and another one can still sign in), or the profile the actor
    /// is using.
    /// </summary>
    Task DeleteProfileAsync(
        RequestAuthority actor,
        Guid profileId,
        ProfilePhotoDisposition photos = ProfilePhotoDisposition.MoveToShared,
        CancellationToken ct = default);
    /// <summary>Throws unless the actor may change this person (their photo, for example); a missing person and another household's person look the same.</summary>
    Task RequireCanChangeProfileAsync(RequestAuthority actor, Guid profileId, CancellationToken ct = default);
    /// <summary>Adds a person to a household. The household's main sign-ins can open them; the person has no sign-in of their own yet.</summary>
    Task<MediaEngine.Domain.Aggregates.Profile> AddHouseholdPersonAsync(
        RequestAuthority actor, AddHouseholdPersonCommand command, CancellationToken ct = default);
    /// <summary>
    /// Switches "Ask for my PIN on admin pages" on or off for a profile that holds an administrator role (its own PIN is the one asked
    /// for). A person changes it on their own profile; a server administrator on any; a household administrator never on another
    /// administrator's profile.
    /// </summary>
    Task SetProfileAdminProtectionAsync(RequestAuthority actor, Guid profileId, ProfileAdminProtectionCommand command, CancellationToken ct = default);
    /// <summary>
    /// The admin PIN question of a profile (switched off, with the default unlock choice, when never set). Readable by the person
    /// themselves and by a server administrator; refused for anyone else, a household administrator included.
    /// </summary>
    Task<ProfileAdminProtection> GetProfileAdminProtectionAsync(RequestAuthority actor, Guid profileId, CancellationToken ct = default);
    /// <summary>Sets or clears a person's PIN. A household administrator may only do this for their own household.</summary>
    Task SetProfilePinAsync(RequestAuthority actor, Guid profileId, string? pin, CancellationToken ct = default);
}
