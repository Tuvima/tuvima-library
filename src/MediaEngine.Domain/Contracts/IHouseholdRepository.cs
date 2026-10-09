using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Contracts;

/// <summary>Read access to households and who belongs to them.</summary>
public interface IHouseholdRepository
{
    Task<Household?> GetByIdAsync(Guid householdId, CancellationToken ct = default);

    /// <summary>The household an account belongs to, or <see langword="null"/> when the account is unknown.</summary>
    Task<Household?> GetForAccountAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>The profiles in a household, oldest first.</summary>
    Task<IReadOnlyList<Profile>> ListProfilesAsync(Guid householdId, CancellationToken ct = default);

    /// <summary>The sign-ins (accounts) in a household, oldest first.</summary>
    Task<IReadOnlyList<Account>> ListAccountsAsync(Guid householdId, CancellationToken ct = default);
}
