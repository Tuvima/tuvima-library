using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Tests;

/// <summary>
/// How a test wants a new user's profile set up: the profile the user opens, and whether that profile becomes a server administrator.
/// Test shorthand only; the data store has no such thing as a per-user grant.
/// </summary>
internal sealed record TestGrant(Guid AccountId, Guid ProfileId, bool IsDefault = true, bool AdminEnabled = false);

/// <summary>Test shorthand over <see cref="IAccountAccessMutationRepository.CreateUserAsync"/>.</summary>
internal static class UserCreationTestExtensions
{
    /// <summary>
    /// Creates the user in <paramref name="account"/> opening <paramref name="grant"/>'s profile. <paramref name="preserveProfileAccess"/>
    /// attaches it to a person who already exists without changing them; otherwise the profile is set up with
    /// (when the grant says so) the server administrator role.
    /// </summary>
    public static Task CreateAccountAsync(
        this IAccountAccessMutationRepository accounts,
        Account account,
        TestGrant grant,
        CancellationToken ct = default,
        Profile? newProfile = null,
        bool preserveProfileAccess = false,
        string? newHouseholdName = null,
        Guid? householdId = null,
        AccountInvitation? invitation = null)
    {
        account.ProfileId = grant.ProfileId;
        return accounts.CreateUserAsync(
            account,
            new NewUserPlacement(
                newProfile,
                newProfile is null ? null : householdId,
                newHouseholdName,
                SetUpProfile: newProfile is null && !preserveProfileAccess,
                IsServerAdministrator: grant.AdminEnabled),
            invitation,
            ct);
    }

    /// <summary>As <see cref="CreateAccountAsync"/>, with an invitation made at the same time.</summary>
    public static Task CreateInvitedAccountAsync(
        this IAccountAccessMutationRepository accounts,
        Account account,
        IReadOnlyList<TestGrant> grants,
        AccountInvitation invitation,
        CancellationToken ct = default,
        Profile? newProfile = null,
        bool preserveProfileAccess = false,
        string? newHouseholdName = null,
        Guid? householdId = null) =>
        accounts.CreateAccountAsync(
            account,
            grants.Single(grant => grant.IsDefault),
            ct,
            newProfile,
            preserveProfileAccess,
            newHouseholdName,
            householdId: householdId,
            invitation: invitation);

    /// <summary>Makes (or stops) the profile a user opens being a household administrator.</summary>
    public static async Task SetHouseholdAdminAsync(
        this IAccountRepository accounts, IDatabaseConnection database, Guid userId, bool isHouseholdAdmin, DateTimeOffset changedAt)
    {
        var user = await accounts.GetByIdAsync(userId) ?? throw new KeyNotFoundException("User not found.");
        await new MediaEngine.Storage.ProfileAccessRepository(database).SetHouseholdAdministratorAsync(user.ProfileId, isHouseholdAdmin, changedAt);
    }

    /// <summary>The household of the profile a user opens.</summary>
    public static async Task<Guid?> GetHouseholdIdAsync(this IAccountRepository accounts, IDatabaseConnection database, Guid userId) =>
        (await new MediaEngine.Storage.HouseholdRepository(database).GetForAccountAsync(userId))?.Id;
}
