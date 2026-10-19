using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Contracts;

/// <summary>Households, who belongs to them, and the few changes made to a household itself.</summary>
public interface IHouseholdRepository
{
    Task<Household?> GetByIdAsync(Guid householdId, CancellationToken ct = default);

    /// <summary>The household an account belongs to, or <see langword="null"/> when the account is unknown.</summary>
    Task<Household?> GetForAccountAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>The profiles in a household, oldest first.</summary>
    Task<IReadOnlyList<Profile>> ListProfilesAsync(Guid householdId, CancellationToken ct = default);

    /// <summary>The sign-ins (accounts) in a household, oldest first.</summary>
    Task<IReadOnlyList<Account>> ListAccountsAsync(Guid householdId, CancellationToken ct = default);

    /// <summary>Every household with its people and sign-in counts, by name.</summary>
    Task<IReadOnlyList<HouseholdSummary>> ListSummariesAsync(CancellationToken ct = default);

    /// <summary>One household with its counts, or <see langword="null"/> when it does not exist.</summary>
    Task<HouseholdSummary?> GetSummaryAsync(Guid householdId, CancellationToken ct = default);

    /// <summary>Creates an empty household.</summary>
    Task<Household> CreateAsync(string name, DateTimeOffset createdAt, CancellationToken ct = default);

    /// <summary>Renames a household. Returns <see langword="false"/> when it does not exist.</summary>
    Task<bool> RenameAsync(Guid householdId, string name, CancellationToken ct = default);

    /// <summary>
    /// Removes a household only when nothing belongs to it. The check and the removal are one step, so a person added
    /// at the same moment is never left without a household.
    /// </summary>
    Task<HouseholdDeleteOutcome> DeleteWhenEmptyAsync(Guid householdId, CancellationToken ct = default);
}
