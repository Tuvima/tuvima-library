using MediaEngine.Api.Services.View;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Api.Tests;

public sealed class ViewScopeResolverTests
{
    [Fact]
    public async Task SharedLibraryAccessDoesNotExposeOtherProfileScopes()
    {
        var caller = State(access: true, include: false);
        var included = State(access: false, include: true);
        var excluded = State(access: true, include: false);
        var resolver = new ViewScopeResolver(new ScopeStore(caller, included, excluded));

        var resolution = Assert.IsType<ViewScopeResolution>(await resolver.ResolveAsync(
            Identity(caller),
            ViewScopeRequest.Shared));

        Assert.Equal(ViewScopeKind.Shared, resolution.Scope.Kind);
        Assert.Single(resolution.Scope.LibraryIds);
        Assert.DoesNotContain(resolution.AvailableScopes, option => option.Kind == ViewScopeKind.Profile);
    }

    [Fact]
    public async Task SubmissionPermissionDoesNotGrantCallerSharedAccess()
    {
        var caller = State(access: false, include: true);
        var other = State(access: false, include: true);
        var resolver = new ViewScopeResolver(new ScopeStore(caller, other));

        Assert.Null(await resolver.ResolveAsync(Identity(caller), ViewScopeRequest.Shared));
    }

    [Fact]
    public async Task OtherProfileScopeReturnsNotFoundWithoutEnumeratingIt()
    {
        var caller = State(access: true, include: true);
        var revoked = State(access: false, include: false);
        var resolver = new ViewScopeResolver(new ScopeStore(caller, revoked));

        Assert.Null(await resolver.ResolveAsync(Identity(caller),
            ViewScopeRequest.ForProfile(revoked.Policy.ProfileId)));
    }

    [Fact]
    public async Task DisabledCallerHasNoViewScope()
    {
        var caller = State(access: true, include: true);
        caller = caller with { Policy = caller.Policy with { ViewEnabled = false } };
        var resolver = new ViewScopeResolver(new ScopeStore(caller));

        var result = await resolver.ResolveAsync(
            Identity(caller),
            ViewScopeRequest.Mine);

        Assert.Null(result);
    }

    [Fact]
    public async Task HouseholdMemberGetsExactEnabledProfileWithoutRootScope()
    {
        var household = Guid.NewGuid();
        var caller = State(access: true, include: false, household);
        var target = State(access: false, include: false, household);
        var resolver = new ViewScopeResolver(new ScopeStore(caller, target));

        var resolution = Assert.IsType<ViewScopeResolution>(await resolver.ResolveAsync(
            Identity(caller), ViewScopeRequest.ForProfile(target.Policy.ProfileId)));

        Assert.Equal(ViewScopeKind.Profile, resolution.Scope.Kind);
        Assert.Equal(target.Policy.ProfileId, resolution.Scope.ProfileId);
        Assert.Equal([target.PersonalSpace!.LibraryId], resolution.Scope.LibraryIds);
        Assert.Contains(resolution.AvailableScopes, option =>
            option.Kind == ViewScopeKind.Profile && option.ProfileId == target.Policy.ProfileId);
    }

    [Fact]
    public async Task OwnSpaceStaysFirstAndHouseholdMembersAreListedByName()
    {
        var household = Guid.NewGuid();
        var caller = State(access: true, include: true, household, "Jim");
        var zoe = State(access: true, include: true, household, "Zoe");
        var mary = State(access: true, include: true, household, "Mary");
        var resolver = new ViewScopeResolver(new ScopeStore(caller, zoe, mary));

        var resolution = Assert.IsType<ViewScopeResolution>(await resolver.ResolveAsync(
            Identity(caller), ViewScopeRequest.Mine));

        Assert.Equal(ViewScopeKind.Mine, resolution.AvailableScopes[0].Kind);
        Assert.Equal(["Jim", "Mary", "Zoe"], resolution.AvailableScopes
            .Where(option => option.Kind != ViewScopeKind.Shared).Select(option => option.Label));
    }

    [Fact]
    public async Task AnotherHouseholdsProfileIsNeverResolvedOrListed_EvenForAServerAdministrator()
    {
        var ours = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        var caller = State(access: true, include: true, ours);
        var stranger = State(access: true, include: true, theirs);
        var resolver = new ViewScopeResolver(new ScopeStore(caller, stranger));

        foreach (var authority in new[]
                 {
                     Identity(caller),
                     Identity(caller) with { AccountIsAdministrator = true, GrantAdminEnabled = true },
                     Identity(caller) with { AccountIsHouseholdAdmin = true, AccountHouseholdId = ours },
                 })
        {
            Assert.Null(await resolver.ResolveAsync(authority, ViewScopeRequest.ForProfile(stranger.Policy.ProfileId)));
            var own = Assert.IsType<ViewScopeResolution>(await resolver.ResolveAsync(authority, ViewScopeRequest.Mine));
            Assert.DoesNotContain(own.AvailableScopes, option => option.ProfileId == stranger.Policy.ProfileId);
        }
    }

    [Fact]
    public async Task ProfilesWithNoHouseholdAreNotHouseholdMembersOfEachOther()
    {
        var caller = State(access: true, include: true);
        var other = State(access: true, include: true);
        var resolver = new ViewScopeResolver(new ScopeStore(caller, other));

        Assert.Null(await resolver.ResolveAsync(Identity(caller), ViewScopeRequest.ForProfile(other.Policy.ProfileId)));
    }

    [Fact]
    public async Task SavedStaleSelectionMayFallbackButExplicitSelectionDoesNot()
    {
        var caller = State(access: true, include: false);
        var resolver = new ViewScopeResolver(new ScopeStore(caller));
        var missing = ViewScopeRequest.ForProfile(Guid.NewGuid());

        Assert.Null(await resolver.ResolveAsync(Identity(caller), missing));
        var fallback = Assert.IsType<ViewScopeResolution>(await resolver.ResolveAsync(
            Identity(caller), missing, allowStaleSelectionFallback: true));
        Assert.True(fallback.Scope.WasFallback);
        Assert.Equal(ViewScopeKind.Mine, fallback.Scope.Kind);
    }

    private static ViewScopeStoreEntry State(bool access, bool include, Guid? household = null, string name = "")
    {
        var profileId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        return new ViewScopeStoreEntry(
            new ViewProfilePolicy(profileId, true, access, include, true, now),
            new ViewPersonalSpace(Guid.NewGuid(), profileId, Guid.NewGuid(), now, now),
            DisplayName: name,
            HouseholdId: household);
    }

    private static RequestAuthority Identity(ViewScopeStoreEntry state) =>
        new(PrincipalKind.Human, true, Guid.NewGuid(), state.Policy.ProfileId,
            AccountEnabled: true, GrantEnabled: true);

    internal sealed class ScopeStore(params ViewScopeStoreEntry[] profiles) : IViewScopeStore
    {
        public Task<ViewScopeStoreEntry?> FindProfileAsync(Guid profileId, CancellationToken ct = default) =>
            Task.FromResult(profiles.FirstOrDefault(profile => profile.Policy.ProfileId == profileId));

        public Task<IReadOnlyList<ViewScopeStoreEntry>> GetProfilesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ViewScopeStoreEntry>>(profiles);

        public Task<Guid?> GetSharedLibraryIdAsync(CancellationToken ct = default) =>
            Task.FromResult<Guid?>(Guid.Parse("00000000-0000-0000-0000-000000000003"));
    }
}
