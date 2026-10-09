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
        CancellationToken ct = default);
    Task CreateManagedProfileAsync(
        MediaEngine.Domain.Aggregates.Profile profile,
        AccountProfileGrant targetGrant,
        CancellationToken ct = default);
    Task UpdateManagedProfileAsync(
        MediaEngine.Domain.Aggregates.Profile profile,
        CancellationToken ct = default);
    Task DeleteManagedProfileAsync(Guid profileId, CancellationToken ct = default);
    Task UpsertGrantAsync(AccountProfileGrant grant, CancellationToken ct = default);
    Task RevokeGrantAsync(Guid accountId, Guid profileId, CancellationToken ct = default);
    Task ReplaceAccountAccessAsync(Guid accountId, IReadOnlySet<AccountFeatureId> features, IReadOnlySet<Guid> libraries, DateTimeOffset changedAt, CancellationToken ct = default);
    Task SetAdminProtectionAsync(GrantAdminProtection protection, CancellationToken ct = default);
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
    IReadOnlySet<Guid> Libraries);

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
    Guid? DefaultProfileId);

public sealed record IssuedAccountInvitation(
    Guid AccountId,
    string PlaintextToken,
    DateTimeOffset ExpiresAt);

public sealed record CreateManagedProfileCommand(
    Guid AccountId,
    string DisplayName,
    string? AvatarColor,
    bool IsDefault);
public sealed record UpdateManagedProfileCommand(string DisplayName, string? AvatarColor);

public interface IAccountAccessMutationService
{
    Task<Account> CreateAsync(RequestAuthority actor, CreateAccountAccessCommand command, CancellationToken ct = default);
    Task<Account> UpdateAsync(RequestAuthority actor, Guid accountId, UpdateAccountAccessCommand command, CancellationToken ct = default);
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
    Task ReplaceAccessAsync(RequestAuthority actor, Guid accountId, IReadOnlySet<AccountFeatureId> features, IReadOnlySet<Guid> libraries, CancellationToken ct = default);
    Task UpsertGrantAsync(RequestAuthority actor, AccountProfileGrant grant, CancellationToken ct = default);
    Task RevokeGrantAsync(RequestAuthority actor, Guid accountId, Guid profileId, CancellationToken ct = default);
    Task SetAdminProtectionAsync(RequestAuthority actor, Guid accountId, Guid profileId, GrantAdminProtectionCommand command, CancellationToken ct = default);
}
