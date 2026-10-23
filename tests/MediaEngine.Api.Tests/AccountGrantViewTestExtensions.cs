using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Test shorthand for one entry of the household view <see cref="IAccessRepository.GetHouseholdProfilesAsync"/>: what a user may act as
/// is its own profile and the rest of its household, with the profile's roles and version.
/// </summary>
internal static class AccountGrantViewTestExtensions
{
    public static async Task<HouseholdProfileAccess?> GetGrantAsync(this IAccessRepository accounts, Guid accountId, Guid profileId) =>
        (await accounts.GetHouseholdProfilesAsync(accountId)).FirstOrDefault(entry => entry.ProfileId == profileId);
}
