using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Contracts;

public interface IAccessRepository
{
    Task<AccountProfileGrant?> GetGrantAsync(Guid accountId, Guid profileId, CancellationToken ct = default);
    Task<IReadOnlyList<AccountProfileGrant>> GetGrantsAsync(Guid accountId, CancellationToken ct = default);
    Task<IReadOnlySet<AccountFeatureId>> GetFeatureGrantsAsync(Guid accountId, CancellationToken ct = default);
    Task<IReadOnlySet<Guid>> GetLibraryGrantsAsync(Guid accountId, CancellationToken ct = default);
    Task<bool> HasFeatureGrantAsync(Guid accountId, AccountFeatureId feature, CancellationToken ct = default);
    Task<bool> HasLibraryGrantAsync(Guid accountId, Guid libraryId, CancellationToken ct = default);
    Task<GrantAdminProtection?> GetAdminProtectionAsync(Guid accountId, Guid profileId, CancellationToken ct = default);
    Task<GrantAdminUnlock?> GetAdminUnlockAsync(Guid sessionId, Guid accountId, Guid profileId, DateTimeOffset now, CancellationToken ct = default);
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
    Task CreateAccountAsync(Account account, AccountProfileGrant initialGrant,
        IReadOnlySet<AccountFeatureId> features, IReadOnlySet<Guid> libraries,
        CancellationToken ct = default,
        MediaEngine.Domain.Aggregates.Profile? newProfile = null);
    Task UpdateAccountAsync(Account account, CancellationToken ct = default);
    Task DeleteAccountAsync(Guid accountId, CancellationToken ct = default);
    /// <summary>
    /// Works out which profiles go with this sign-in when it is deleted (<see cref="AccountRemovalPlan"/>) and refuses a delete
    /// that would strand someone: the Owner profile can never go, and no other sign-in may be left able to open only a
    /// profile that is going. Changes nothing.
    /// </summary>
    Task<AccountRemovalPlan> PlanAccountRemovalAsync(Guid accountId, CancellationToken ct = default);
    /// <summary>
    /// Deletes the sign-in and its own profiles in one transaction, with the same refusals as
    /// <see cref="PlanAccountRemovalAsync"/> and the last-administrator backstop. A sign-in that used a deleted profile as its
    /// default is moved to its next enabled profile. Everything personal the profiles left behind goes with them.
    /// </summary>
    Task<AccountRemovalOutcome> DeleteAccountWithOwnProfilesAsync(Guid accountId, CancellationToken ct = default);
    Task CreateInvitedAccountAsync(
        Account account,
        IReadOnlyList<AccountProfileGrant> grants,
        AccountInvitation invitation,
        CancellationToken ct = default,
        MediaEngine.Domain.Aggregates.Profile? newProfile = null);
    Task CreateManagedProfileAsync(
        MediaEngine.Domain.Aggregates.Profile profile,
        AccountProfileGrant targetGrant,
        CancellationToken ct = default);
    /// <summary>Adds a person to a household, opened by the given sign-ins (none of them as default). Refused when the household is full.</summary>
    Task CreateHouseholdPersonAsync(
        MediaEngine.Domain.Aggregates.Profile profile,
        Guid householdId,
        IReadOnlyList<AccountProfileGrant> grants,
        CancellationToken ct = default);
    Task UpdateManagedProfileAsync(
        MediaEngine.Domain.Aggregates.Profile profile,
        CancellationToken ct = default);
    /// <summary>Throws the same errors as <see cref="DeleteManagedProfileAsync"/> would, without removing anything.</summary>
    Task ValidateManagedProfileRemovalAsync(Guid profileId, CancellationToken ct = default);
    Task DeleteManagedProfileAsync(Guid profileId, CancellationToken ct = default);
    Task UpsertGrantAsync(AccountProfileGrant grant, CancellationToken ct = default);
    Task RevokeGrantAsync(Guid accountId, Guid profileId, CancellationToken ct = default);
    Task ReplaceAccountAccessAsync(Guid accountId, IReadOnlySet<AccountFeatureId> features, IReadOnlySet<Guid> libraries, DateTimeOffset changedAt, CancellationToken ct = default);
    Task SetAdminProtectionAsync(GrantAdminProtection protection, CancellationToken ct = default);
    /// <summary>
    /// Makes an account the administrator of its household (or takes that away). Making one also names it the
    /// household's primary account when the household has none. Raises the account's authorization version.
    /// </summary>
    Task<bool> SetHouseholdAdminAsync(Guid accountId, bool isHouseholdAdmin, DateTimeOffset changedAt, CancellationToken ct = default);

    /// <summary>True when any sign-in has "Ask for my PIN on admin pages" switched on for this profile.</summary>
    Task<bool> HasEnabledAdminProtectionAsync(Guid profileId, CancellationToken ct = default);
    Task SetAdminUnlockAsync(GrantAdminUnlock unlock, CancellationToken ct = default);
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
    /// Deletes a user: their sign-in and their own profiles (see <see cref="AccountRemovalPlan"/>), with <paramref name="photos"/>
    /// applied to every profile that goes. A server administrator may delete anyone but the last server administrator and a
    /// household's last owner; a household owner may delete another user in their own household (another owner included) but
    /// never a server administrator, themselves, anyone outside the household, or the household's last owner.
    /// </summary>
    Task DeleteAsync(
        RequestAuthority actor,
        Guid accountId,
        ProfilePhotoDisposition photos = ProfilePhotoDisposition.MoveToShared,
        CancellationToken ct = default);
    /// <summary>
    /// The profiles <see cref="DeleteAsync"/> would delete with this user, for the confirmation. Changes nothing. Open to
    /// whoever may delete the user; another household's user looks missing to a household owner.
    /// </summary>
    Task<IReadOnlyList<MediaEngine.Domain.Aggregates.Profile>> PreviewDeleteAsync(
        RequestAuthority actor, Guid accountId, CancellationToken ct = default);
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
    /// Removes a person. Their habits (likes, My List, progress, bookmarks, taste) always go; <paramref name="photos"/> decides
    /// what happens to their personal photos. Library files are never touched.
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
