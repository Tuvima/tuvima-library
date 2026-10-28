using MediaEngine.Api.Security;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Builds the one library visibility rule for tests that are not about Selected-profiles libraries: no library is Selected, so the rule
/// behaves exactly as for Standard libraries. Tests about Selected libraries build the rule with a real grant repository and a
/// <see cref="FixedLibraryAccessCatalog"/> themselves.
/// </summary>
internal static class TestLibraryVisibility
{
    public static LibraryVisibilityRule Rule(IProfileAccessRepository access, IVaultLibraryRepository vaultLibraries) =>
        new(access, vaultLibraries, new NoLibraryProfileGrants(), new FixedLibraryAccessCatalog());

    internal sealed class NoLibraryProfileGrants : ILibraryProfileGrantRepository
    {
        public Task<IReadOnlySet<Guid>> GetGrantedLibrariesAsync(Guid profileId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

        public Task<IReadOnlySet<Guid>> GetGrantedProfilesAsync(Guid libraryId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

        public Task ReplaceGrantedProfilesAsync(
            Guid libraryId, IReadOnlySet<Guid> profileIds, DateTimeOffset changedAt, Guid? changedByProfileId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ClearLibraryAsync(Guid libraryId, CancellationToken ct = default) => Task.CompletedTask;
    }
}

/// <summary>A catalog that names exactly the libraries a test says are Selected-profiles libraries.</summary>
internal sealed class FixedLibraryAccessCatalog(params Guid[] selected) : ILibraryAccessCatalog
{
    public HashSet<Guid> Selected { get; } = [.. selected];

    public ValueTask<IReadOnlySet<Guid>> GetSelectedLibraryIdsAsync(CancellationToken ct = default) =>
        new(Selected.ToHashSet());
}
