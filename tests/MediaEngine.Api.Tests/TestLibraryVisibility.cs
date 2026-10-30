using MediaEngine.Api.Security;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Builds the one library visibility rule for tests that are not about household limits: no household hides anything, so the rule
/// shows every library of the profile's areas. Tests about the household ceiling build the rule with a real
/// household repository themselves.
/// </summary>
internal static class TestLibraryVisibility
{
    public static LibraryVisibilityRule Rule(IVaultLibraryRepository vaultLibraries) =>
        new(new NoHouseholdLibraries(), vaultLibraries);

    /// <summary>A household list that hides nothing.</summary>
    internal sealed class NoHouseholdLibraries : IHouseholdLibraryAccessRepository
    {
        public Task<IReadOnlySet<Guid>> GetHiddenLibrariesAsync(Guid householdId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

        public Task<IReadOnlySet<Guid>> GetHiddenLibrariesForProfileAsync(Guid profileId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

        public Task<bool> ReplaceHiddenLibrariesAsync(
            Guid householdId, IReadOnlySet<Guid> hiddenLibraries, DateTimeOffset changedAt, Guid? changedByProfileId, CancellationToken ct = default) =>
            Task.FromResult(true);
    }
}
