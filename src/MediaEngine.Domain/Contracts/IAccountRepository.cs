using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Contracts;

public interface IAccountRepository : IAccessRepository, IAccountAccessMutationRepository
{
    Task<Account?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Account?> GetByNormalizedEmailAsync(string normalizedEmail, CancellationToken ct = default);
    Task<IReadOnlyList<Account>> GetAllAsync(CancellationToken ct = default);
    /// <summary>The household's primary sign-in (the one the server administrator set up), or null when it has none.</summary>
    Task<Guid?> GetHouseholdPrimaryAccountIdAsync(Guid householdId, CancellationToken ct = default);
    Task InsertAsync(Account account, CancellationToken ct = default);
    Task<bool> UpdateAsync(Account account, CancellationToken ct = default);
    Task GrantProfileAsync(AccountProfileGrant grant, CancellationToken ct = default);
    Task<bool> RevokeProfileAsync(Guid accountId, Guid profileId, CancellationToken ct = default);
    /// <summary>
    /// True when the profile is one the user may act as: the profile it signs in as, or any other profile of the same household.
    /// A profile of another household, a profile that does not exist and a user without a profile all answer false.
    /// </summary>
    Task<bool> HasProfileAccessAsync(Guid accountId, Guid profileId, CancellationToken ct = default);
    /// <summary>The profiles the user may act as: its own first, then the rest of its household in the order they were made.</summary>
    Task<IReadOnlyList<Guid>> GetProfileIdsAsync(Guid accountId, CancellationToken ct = default);
    /// <summary>The profile the user signs in as (the one it is attached to), or <see langword="null"/> when it has none.</summary>
    Task<Guid?> GetDefaultProfileIdAsync(Guid accountId, CancellationToken ct = default);
    /// <summary>
    /// True when every server administrator profile has a user with an email and a password or passkey (users, profiles and households
    /// D10). Judged on all of them, enabled or not. A Library with no server administrator yet answers true.
    /// </summary>
    Task<bool> AreServerAdministratorsSecuredAsync(CancellationToken ct = default);
    /// <summary>True when the user has an email and a password or passkey (the sign-in a server administrator needs, D10, D17).</summary>
    Task<bool> IsUserSecuredAsync(Guid userId, CancellationToken ct = default);
    Task InsertInvitationAsync(AccountInvitation invitation, CancellationToken ct = default);
    Task<AccountInvitation?> GetActiveInvitationAsync(string tokenHash, DateTimeOffset now, CancellationToken ct = default);
    Task<bool> ConsumeInvitationAsync(Guid invitationId, DateTimeOffset consumedAt, CancellationToken ct = default);

    /// <summary>
    /// Records that the account's password is (or no longer is) an administrator-set temporary one, and when it runs
    /// out. Raises the account's authorization version so open screens re-check.
    /// </summary>
    Task<bool> SetTemporaryPasswordStateAsync(Guid accountId, bool mustChangePassword, DateTimeOffset? expiresAt, DateTimeOffset changedAt, CancellationToken ct = default);
}
