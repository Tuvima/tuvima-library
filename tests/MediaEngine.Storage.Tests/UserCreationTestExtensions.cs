using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Storage.Tests;

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
    /// <paramref name="features"/>, <paramref name="libraries"/> and (when the grant says so) the server administrator role.
    /// </summary>
    public static Task CreateAccountAsync(
        this IAccountAccessMutationRepository accounts,
        Account account,
        TestGrant grant,
        IReadOnlySet<AccountFeatureId> features,
        IReadOnlySet<Guid> libraries,
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
                IsServerAdministrator: grant.AdminEnabled,
                Features: features,
                Libraries: libraries),
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
            new HashSet<AccountFeatureId>(),
            new HashSet<Guid>(),
            ct,
            newProfile,
            preserveProfileAccess,
            newHouseholdName,
            householdId: householdId,
            invitation: invitation);
}
