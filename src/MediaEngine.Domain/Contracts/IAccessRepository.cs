using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Contracts;

public interface IAccessRepository
{
    /// <summary>
    /// What a user may act as: the profile it signs in as, and every other profile of its household (users, profiles and
    /// households spec: a user switches inside its own household and never into another). One entry per profile, always enabled;
    /// <c>IsDefault</c> marks the profile the user signs in as, <c>AdminEnabled</c> the profiles that hold the server administrator
    /// role, and <c>AuthorizationVersion</c> is the profile's. Empty when the user does not exist.
    /// </summary>
    Task<IReadOnlyList<AccountProfileGrant>> GetGrantsAsync(Guid accountId, CancellationToken ct = default);

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
    /// Creates a user attached to the profile named by <paramref name="initialGrant"/> (or to <paramref name="newProfile"/>, created
    /// in the same step). The profile takes the roles and access described by the arguments, unless
    /// <paramref name="preserveProfileAccess"/> is set: attaching a sign-in to a person who already exists never changes what that
    /// person may do (their roles, features and libraries stay exactly as they are). A brand-new profile always takes the arguments.
    /// <paramref name="newHouseholdName"/> makes a brand-new household for the new profile in the same step (so a failure leaves no
    /// empty household behind); its first profile looks after it. Joining a household that already exists never promotes anyone.
    /// </summary>
    Task CreateAccountAsync(Account account, AccountProfileGrant initialGrant,
        IReadOnlySet<AccountFeatureId> features, IReadOnlySet<Guid> libraries,
        CancellationToken ct = default,
        MediaEngine.Domain.Aggregates.Profile? newProfile = null,
        bool preserveProfileAccess = false,
        string? newHouseholdName = null);
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
    Task CreateInvitedAccountAsync(
        Account account,
        IReadOnlyList<AccountProfileGrant> grants,
        AccountInvitation invitation,
        CancellationToken ct = default,
        MediaEngine.Domain.Aggregates.Profile? newProfile = null,
        bool preserveProfileAccess = false,
        string? newHouseholdName = null);
    /// <summary>
    /// Creates a person for an existing sign-in. <paramref name="accessFromProfileId"/> is the profile of whoever is creating them: the
    /// new person starts with that profile's Read/Watch/Listen/View access and libraries (View off and no libraries when it is in
    /// another household). <see langword="null"/> gives every feature and no library. Never an administrator role.
    /// </summary>
    Task CreateManagedProfileAsync(
        MediaEngine.Domain.Aggregates.Profile profile,
        AccountProfileGrant targetGrant,
        CancellationToken ct = default,
        Guid? accessFromProfileId = null);
    /// <summary>
    /// Adds a person to a household, opened by the given sign-ins (none of them as default). Refused when the household is full.
    /// <paramref name="accessFromProfileId"/> works as in <see cref="CreateManagedProfileAsync"/>.
    /// </summary>
    Task CreateHouseholdPersonAsync(
        MediaEngine.Domain.Aggregates.Profile profile,
        Guid householdId,
        IReadOnlyList<AccountProfileGrant> grants,
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
    Task UpsertGrantAsync(AccountProfileGrant grant, CancellationToken ct = default);
    Task RevokeGrantAsync(Guid accountId, Guid profileId, CancellationToken ct = default);
    Task ReplaceAccountAccessAsync(Guid accountId, IReadOnlySet<AccountFeatureId> features, IReadOnlySet<Guid> libraries, DateTimeOffset changedAt, CancellationToken ct = default);
    /// <summary>
    /// Makes an account the administrator of its household (or takes that away). Making one also names it the
    /// household's primary account when the household has none. Raises the account's authorization version.
    /// </summary>
    Task<bool> SetHouseholdAdminAsync(Guid accountId, bool isHouseholdAdmin, DateTimeOffset changedAt, CancellationToken ct = default);

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

public sealed record CreateAccountAccessCommand(
    string? Email,
    bool IsAdministrator,
    Guid? ProfileId,
    NewAccountProfileCommand? NewProfile,
    IReadOnlySet<AccountFeatureId> Features,
    IReadOnlySet<Guid> Libraries,
    string? TemporaryPassword = null);

public sealed record NewAccountProfileCommand(string DisplayName, string? AvatarColor);

public sealed record UpdateAccountAccessCommand(
    string? Email,
    bool IsEnabled,
    bool IsAdministrator);

public sealed record GrantAdminProtectionCommand(
    bool Enabled,
    AdminUnlockMode UnlockMode,
    int? UnlockMinutes);

public sealed record IssueAccountInvitationCommand(
    string Email,
    IReadOnlyList<Guid> ProfileIds,
    Guid? DefaultProfileId,
    string? NewHouseholdPersonName = null);

/// <summary>An invitation just made. <paramref name="Code"/> is shown as <c>XXXXX-XXXXX</c> and is never available again.</summary>
public sealed record IssuedAccountInvitation(
    Guid AccountId,
    string Code,
    DateTimeOffset ExpiresAt);

public sealed record CreateManagedProfileCommand(
    Guid AccountId,
    string DisplayName,
    string? AvatarColor,
    bool IsDefault);
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

/// <summary>
/// Gives a person in a household their own email sign-in. With <paramref name="TemporaryPassword"/> the
/// administrator chooses the first password; with <paramref name="Invite"/> the person gets an invitation to choose their own
/// instead. An own sign-in always has a password, so one of the two is required. <paramref name="MustChangePassword"/> decides
/// whether the starting password is temporary (changed at first sign-in) or an ordinary one.
/// </summary>
public sealed record GiveOwnSignInCommand(
    Guid ProfileId,
    string Email,
    string? TemporaryPassword,
    bool MustChangePassword = true,
    bool Invite = false);

/// <summary>The sign-in just made, and its invitation when one was chosen.</summary>
public sealed record GivenOwnSignIn(Account Account, IssuedAccountInvitation? Invitation);

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
    Task<Account> CreateAsync(RequestAuthority actor, CreateAccountAccessCommand command, CancellationToken ct = default);
    Task<Account> UpdateAsync(RequestAuthority actor, Guid accountId, UpdateAccountAccessCommand command, CancellationToken ct = default);
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
    Task<IssuedAccountInvitation> IssueInvitationAsync(
        RequestAuthority actor,
        IssueAccountInvitationCommand command,
        CancellationToken ct = default);
    Task<MediaEngine.Domain.Aggregates.Profile> CreateProfileAsync(
        RequestAuthority actor,
        CreateManagedProfileCommand command,
        CancellationToken ct = default);
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
    /// <summary>Gives a person their own sign-in. It opens only that person, is never an administrator, and follows the household's library access.</summary>
    Task<GivenOwnSignIn> GiveOwnSignInAsync(
        RequestAuthority actor, GiveOwnSignInCommand command, CancellationToken ct = default);
    /// <summary>Removes a person's own sign-in. The person and everything they own stay in the household.</summary>
    Task RemoveOwnSignInAsync(RequestAuthority actor, Guid accountId, CancellationToken ct = default);
    Task ReplaceAccessAsync(RequestAuthority actor, Guid accountId, IReadOnlySet<AccountFeatureId> features, IReadOnlySet<Guid> libraries, CancellationToken ct = default);
    Task UpsertGrantAsync(RequestAuthority actor, AccountProfileGrant grant, CancellationToken ct = default);
    Task RevokeGrantAsync(RequestAuthority actor, Guid accountId, Guid profileId, CancellationToken ct = default);
    Task SetAdminProtectionAsync(RequestAuthority actor, Guid accountId, Guid profileId, GrantAdminProtectionCommand command, CancellationToken ct = default);
    /// <summary>Makes a main sign-in the administrator of its household, or takes that away. Only a server administrator may.</summary>
    Task SetHouseholdAdminAsync(RequestAuthority actor, Guid accountId, bool isHouseholdAdmin, CancellationToken ct = default);
    /// <summary>Sets or clears a person's PIN. A household administrator may only do this for their own household.</summary>
    Task SetProfilePinAsync(RequestAuthority actor, Guid profileId, string? pin, CancellationToken ct = default);
}
