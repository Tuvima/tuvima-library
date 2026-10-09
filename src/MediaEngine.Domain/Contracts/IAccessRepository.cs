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
}

public interface IAccountAccessMutationRepository
{
    Task CreateAccountAsync(Account account, AccountProfileGrant initialGrant,
        IReadOnlySet<AccountFeatureId> features, IReadOnlySet<Guid> libraries,
        CancellationToken ct = default,
        MediaEngine.Domain.Aggregates.Profile? newProfile = null);
    Task UpdateAccountAsync(Account account, CancellationToken ct = default);
    Task DeleteAccountAsync(Guid accountId, CancellationToken ct = default);
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
    Task<GrantAdminProtection> RecordAdminProtectionFailureAsync(Guid accountId, Guid profileId, DateTimeOffset lockedUntilAfterLimit, CancellationToken ct = default);
    Task ResetAdminProtectionAttemptsAsync(Guid accountId, Guid profileId, CancellationToken ct = default);
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
    string? Pin,
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
public sealed record UpdateManagedProfileCommand(string DisplayName, string? AvatarColor);

/// <summary>A new person in a household: a child person gets the restricted role; a PIN is optional.</summary>
public sealed record AddHouseholdPersonCommand(
    Guid HouseholdId,
    string DisplayName,
    string? AvatarColor,
    bool IsChild,
    string? Pin);

/// <summary>
/// Gives a person in a household their own email sign-in. With <paramref name="TemporaryPassword"/> the
/// administrator chooses the first password; without it the person gets an invitation to choose their own.
/// </summary>
public sealed record GiveOwnSignInCommand(Guid ProfileId, string Email, string? TemporaryPassword);

/// <summary>The sign-in just made, and its invitation when one was chosen.</summary>
public sealed record GivenOwnSignIn(Account Account, IssuedAccountInvitation? Invitation);

public interface IAccountAccessMutationService
{
    Task<Account> CreateAsync(RequestAuthority actor, CreateAccountAccessCommand command, CancellationToken ct = default);
    Task<Account> UpdateAsync(RequestAuthority actor, Guid accountId, UpdateAccountAccessCommand command, CancellationToken ct = default);
    /// <summary>Gives an existing account a new temporary password; the person must choose their own at next sign-in.</summary>
    Task SetTemporaryPasswordAsync(RequestAuthority actor, Guid accountId, string temporaryPassword, CancellationToken ct = default);
    /// <summary>Turns two-step codes off for someone who lost their authenticator app and recovery codes. Audited.</summary>
    Task ResetTwoStepAsync(RequestAuthority actor, Guid accountId, CancellationToken ct = default);
    Task DeleteAsync(RequestAuthority actor, Guid accountId, CancellationToken ct = default);
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
    Task DeleteProfileAsync(RequestAuthority actor, Guid profileId, CancellationToken ct = default);
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
