using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Contracts;

public interface IAccountRepository : IAccessRepository, IAccountAccessMutationRepository
{
    Task<Account?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Account?> GetByNormalizedEmailAsync(string normalizedEmail, CancellationToken ct = default);
    Task<IReadOnlyList<Account>> GetAllAsync(CancellationToken ct = default);
    Task InsertAsync(Account account, CancellationToken ct = default);
    Task<bool> UpdateAsync(Account account, CancellationToken ct = default);
    Task GrantProfileAsync(AccountProfileGrant grant, CancellationToken ct = default);
    Task<bool> RevokeProfileAsync(Guid accountId, Guid profileId, CancellationToken ct = default);
    Task<bool> HasProfileAccessAsync(Guid accountId, Guid profileId, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> GetProfileIdsAsync(Guid accountId, CancellationToken ct = default);
    Task<Guid?> GetDefaultProfileIdAsync(Guid accountId, CancellationToken ct = default);
    Task InsertInvitationAsync(AccountInvitation invitation, CancellationToken ct = default);
    Task<AccountInvitation?> GetActiveInvitationAsync(string tokenHash, DateTimeOffset now, CancellationToken ct = default);
    Task<bool> ConsumeInvitationAsync(Guid invitationId, DateTimeOffset consumedAt, CancellationToken ct = default);

    /// <summary>
    /// Records that the account's password is (or no longer is) an administrator-set temporary one, and when it runs
    /// out. Raises the account's authorization version so open screens re-check.
    /// </summary>
    Task<bool> SetTemporaryPasswordStateAsync(Guid accountId, bool mustChangePassword, DateTimeOffset? expiresAt, DateTimeOffset changedAt, CancellationToken ct = default);
}
