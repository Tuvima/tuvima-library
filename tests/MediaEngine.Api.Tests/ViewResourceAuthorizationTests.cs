using MediaEngine.Api.Services.View;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Api.Tests;

public sealed class ViewResourceAuthorizationTests
{
    [Fact]
    public async Task PermissionCheckAllowsPersonalSpaceBootstrapWithoutEnumeratingScopes()
    {
        var caller = State(access: true, include: false) with { PersonalSpace = null };
        var store = new CountingScopeStore([caller]);
        var service = new ViewResourceAuthorizationService(
            new ViewScopeResolver(store), new ResourceStore(), new AllowEvaluator());
        var request = new ViewResourceRequest(
            ViewScopeRequest.Mine, ViewResourceKind.Search, null);

        var permission = await service.AuthorizePersonalScopeBootstrapAsync(Identity(caller));
        var unresolvedScope = await service.AuthorizeAsync(Identity(caller), request);

        Assert.Equal(ViewAccessOutcome.Allowed, permission);
        Assert.Equal(ViewAccessOutcome.NotFound, unresolvedScope.Outcome);
        Assert.Equal(1, store.ProfileReads);
    }

    [Theory]
    [InlineData(ViewResourceKind.Asset)]
    [InlineData(ViewResourceKind.Thumbnail)]
    [InlineData(ViewResourceKind.Original)]
    public async Task DerivativeAndOriginalDecisionsUseTheResolvedAssetScope(ViewResourceKind kind)
    {
        var caller = State(access: true, include: false);
        var sharedOwner = State(access: false, include: true);
        var resourceId = Guid.NewGuid();
        var service = Create(
            [caller, sharedOwner],
            new ViewResourceDescriptor(
                kind,
                resourceId,
                null,
                Guid.Parse("00000000-0000-0000-0000-000000000003"),
                IsSharedLibraryAsset: true));

        var allowed = await service.AuthorizeAsync(
            Identity(caller),
            new ViewResourceRequest(ViewScopeRequest.Shared, kind, resourceId));
        var denied = await service.AuthorizeAsync(
            Identity(caller),
            new ViewResourceRequest(ViewScopeRequest.Mine, kind, resourceId));

        Assert.True(allowed.IsAllowed);
        Assert.Equal(ViewAccessOutcome.NotFound, denied.Outcome);
    }

    [Fact]
    public async Task SearchReceivesOnlyAuthorizedScopeLibraries()
    {
        var caller = State(access: true, include: false);
        var included = State(access: false, include: true);
        var excluded = State(access: false, include: false);
        var service = Create([caller, included, excluded]);

        var decision = await service.AuthorizeAsync(
            Identity(caller),
            new ViewResourceRequest(ViewScopeRequest.Shared, ViewResourceKind.Search, null));

        Assert.True(decision.IsAllowed);
        Assert.Single(decision.Scope!.LibraryIds);
    }

    [Fact]
    public async Task SharedGalleryRequiresGalleryGrantAndAnAuthorizedOwnerScope()
    {
        var caller = State(access: true, include: false);
        var owner = State(access: false, include: true);
        var galleryId = Guid.NewGuid();
        var shared = new ViewResourceDescriptor(
            ViewResourceKind.Gallery,
            galleryId,
            owner.Policy.ProfileId,
            owner.PersonalSpace!.LibraryId,
            new HashSet<Guid> { caller.Policy.ProfileId });
        var service = Create([caller, owner], shared);

        var read = await service.AuthorizeAsync(
            Identity(caller),
            new ViewResourceRequest(ViewScopeRequest.Shared, ViewResourceKind.Gallery, galleryId));
        var contribute = await service.AuthorizeAsync(
            Identity(caller),
            new ViewResourceRequest(
                ViewScopeRequest.Shared,
                ViewResourceKind.Gallery,
                galleryId,
                ViewResourceAction.Contribute));

        Assert.True(read.IsAllowed);
        Assert.Equal(ViewAccessOutcome.NotFound, contribute.Outcome);
    }

    [Fact]
    public async Task GalleryGrantRemainsIndependentOfOwnerSharedInclusion()
    {
        var caller = State(access: true, include: false);
        var owner = State(access: false, include: false);
        var galleryId = Guid.NewGuid();
        var service = Create(
            [caller, owner],
            new ViewResourceDescriptor(
                ViewResourceKind.Gallery,
                galleryId,
                owner.Policy.ProfileId,
                owner.PersonalSpace!.LibraryId,
                new HashSet<Guid> { caller.Policy.ProfileId }));

        var decision = await service.AuthorizeAsync(
            Identity(caller),
            new ViewResourceRequest(ViewScopeRequest.Shared, ViewResourceKind.Gallery, galleryId));

        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public async Task ExplicitGalleryShareDoesNotRequireSharedViewAccess()
    {
        var caller = State(access: false, include: false);
        var owner = State(access: false, include: false);
        var galleryId = Guid.NewGuid();
        var service = Create(
            [caller, owner],
            new ViewResourceDescriptor(
                ViewResourceKind.Gallery,
                galleryId,
                owner.Policy.ProfileId,
                owner.PersonalSpace!.LibraryId,
                new HashSet<Guid> { caller.Policy.ProfileId }));

        var decision = await service.AuthorizeAsync(
            Identity(caller),
            new ViewResourceRequest(ViewScopeRequest.Mine, ViewResourceKind.Gallery, galleryId));

        Assert.True(decision.IsAllowed);
        Assert.Equal(ViewScopeKind.Profile, decision.Scope!.Kind);
        Assert.Equal([owner.PersonalSpace!.LibraryId], decision.Scope.LibraryIds);
    }

    [Fact]
    public async Task ViewOnlyGalleryShareCannotContribute()
    {
        var caller = State(access: false, include: false);
        var owner = State(access: false, include: false);
        var galleryId = Guid.NewGuid();
        var service = Create([caller, owner], new ViewResourceDescriptor(
            ViewResourceKind.Gallery, galleryId, owner.Policy.ProfileId,
            owner.PersonalSpace!.LibraryId,
            new HashSet<Guid> { caller.Policy.ProfileId },
            new HashSet<Guid>()));

        var decision = await service.AuthorizeAsync(Identity(caller), new ViewResourceRequest(
            ViewScopeRequest.Mine, ViewResourceKind.Gallery, galleryId, ViewResourceAction.Contribute));

        Assert.Equal(ViewAccessOutcome.NotFound, decision.Outcome);
    }

    [Fact]
    public async Task ContributeGalleryShareNarrowsMutationToTheGalleryOwnersLibrary()
    {
        var caller = State(access: false, include: false);
        var owner = State(access: false, include: false);
        var galleryId = Guid.NewGuid();
        var grants = new HashSet<Guid> { caller.Policy.ProfileId };
        var service = Create([caller, owner], new ViewResourceDescriptor(
            ViewResourceKind.Gallery, galleryId, owner.Policy.ProfileId,
            owner.PersonalSpace!.LibraryId, grants, grants));

        var decision = await service.AuthorizeAsync(Identity(caller), new ViewResourceRequest(
            ViewScopeRequest.Mine, ViewResourceKind.Gallery, galleryId, ViewResourceAction.Contribute));

        Assert.True(decision.IsAllowed);
        Assert.Equal([owner.PersonalSpace.LibraryId], decision.Scope!.LibraryIds);
    }

    [Theory]
    [InlineData(ViewResourceKind.Asset)]
    [InlineData(ViewResourceKind.Thumbnail)]
    [InlineData(ViewResourceKind.Original)]
    public async Task ExplicitGalleryShareCanAuthorizeOnlyItsIndividualAssets(ViewResourceKind kind)
    {
        var caller = State(access: false, include: false);
        var owner = State(access: false, include: false);
        var assetId = Guid.NewGuid();
        var service = Create(
            [caller, owner],
            new ViewResourceDescriptor(
                kind,
                assetId,
                owner.Policy.ProfileId,
                owner.PersonalSpace!.LibraryId,
                new HashSet<Guid> { caller.Policy.ProfileId }));

        var decision = await service.AuthorizeAsync(
            Identity(caller),
            new ViewResourceRequest(ViewScopeRequest.Mine, kind, assetId));

        Assert.True(decision.IsAllowed);
        Assert.Equal([owner.PersonalSpace!.LibraryId], decision.Scope!.LibraryIds);
    }

    [Fact]
    public async Task MissingTrustedCallerIsUnauthenticated()
    {
        var caller = State(access: true, include: true);
        var service = Create([caller]);

        var decision = await service.AuthorizeAsync(
            new RequestAuthority(PrincipalKind.Anonymous, false),
            new ViewResourceRequest(ViewScopeRequest.Mine, ViewResourceKind.Search, null));

        Assert.Equal(ViewAccessOutcome.Unauthenticated, decision.Outcome);
    }

    [Fact]
    public async Task AdministratorApplicationMayReadOneExplicitProfileWithPermissionAndAudit()
    {
        var target = State(access: false, include: false);
        var evaluator = new AllowEvaluator();
        var service = Create([target], evaluator);
        var applicationId = Guid.NewGuid();
        var authority = new RequestAuthority(PrincipalKind.ServiceApplication, true,
            ApplicationId: applicationId, ApplicationEnabled: true, ApplicationIsAdministrator: true);
        Assert.True(authority.IsAdministratorApplication);

        var decision = await service.AuthorizeAsync(authority, new ViewResourceRequest(
            ViewScopeRequest.ForProfile(target.Policy.ProfileId), ViewResourceKind.Search, null));

        Assert.True(decision.IsAllowed);
        Assert.Equal(target.Policy.ProfileId, decision.Scope!.ProfileId);
        Assert.Equal(ApplicationPermissionIds.ViewPersonalRead, evaluator.LastRequirement!.ApplicationPermission);
        Assert.Equal("view-profile-admin-read", evaluator.LastResource!.ResourceType);
        Assert.Equal(target.Policy.ProfileId.ToString("D"), evaluator.LastResource.ResourceId);
    }

    [Fact]
    public async Task OrdinaryServiceApplicationCannotReadPersonalProfile()
    {
        var target = State(access: false, include: false);
        var service = Create([target]);
        var authority = new RequestAuthority(PrincipalKind.ServiceApplication, true,
            ApplicationId: Guid.NewGuid(), ApplicationEnabled: true);

        var decision = await service.AuthorizeAsync(authority, new ViewResourceRequest(
            ViewScopeRequest.ForProfile(target.Policy.ProfileId), ViewResourceKind.Search, null));

        Assert.Equal(ViewAccessOutcome.Forbidden, decision.Outcome);
    }

    [Fact]
    public async Task GalleryRevocationAppliesOnTheNextResourceDecision()
    {
        var caller = State(access: false, include: false);
        var owner = State(access: false, include: false);
        var galleryId = Guid.NewGuid();
        var store = new MutableResourceStore(new ViewResourceDescriptor(
            ViewResourceKind.Gallery, galleryId, owner.Policy.ProfileId,
            owner.PersonalSpace!.LibraryId,
            new HashSet<Guid> { caller.Policy.ProfileId }));
        var service = new ViewResourceAuthorizationService(
            new ViewScopeResolver(new ViewScopeResolverTests.ScopeStore(caller, owner)),
            store,
            new AllowEvaluator());

        var before = await service.AuthorizeAsync(Identity(caller),
            new ViewResourceRequest(ViewScopeRequest.Mine, ViewResourceKind.Gallery, galleryId));
        store.Resource = store.Resource! with { SharedWithProfileIds = new HashSet<Guid>() };
        var after = await service.AuthorizeAsync(Identity(caller),
            new ViewResourceRequest(ViewScopeRequest.Mine, ViewResourceKind.Gallery, galleryId));

        Assert.True(before.IsAllowed);
        Assert.Equal(ViewAccessOutcome.NotFound, after.Outcome);
    }

    [Fact]
    public async Task RevokedGalleryShareImmediatelyDeniesItsDerivative()
    {
        var caller = State(access: false, include: false);
        var owner = State(access: false, include: false);
        var derivativeId = Guid.NewGuid();
        var store = new MutableResourceStore(new ViewResourceDescriptor(
            ViewResourceKind.Thumbnail, derivativeId, owner.Policy.ProfileId,
            owner.PersonalSpace!.LibraryId,
            new HashSet<Guid> { caller.Policy.ProfileId }));
        var service = new ViewResourceAuthorizationService(
            new ViewScopeResolver(new ViewScopeResolverTests.ScopeStore(caller, owner)),
            store,
            new AllowEvaluator());

        var before = await service.AuthorizeAsync(Identity(caller), new ViewResourceRequest(
            ViewScopeRequest.Mine, ViewResourceKind.Thumbnail, derivativeId));
        store.Resource = store.Resource! with { SharedWithProfileIds = new HashSet<Guid>() };
        var after = await service.AuthorizeAsync(Identity(caller), new ViewResourceRequest(
            ViewScopeRequest.Mine, ViewResourceKind.Thumbnail, derivativeId));

        Assert.True(before.IsAllowed);
        Assert.Equal(ViewAccessOutcome.NotFound, after.Outcome);
    }

    [Fact]
    public async Task SharedMarkerCannotCrossTheAuthorizedSharedLibrary()
    {
        var caller = State(access: true, include: false);
        var resourceId = Guid.NewGuid();
        var service = Create([caller], new ViewResourceDescriptor(
            ViewResourceKind.Asset, resourceId, null, Guid.NewGuid(),
            IsSharedLibraryAsset: true));

        var decision = await service.AuthorizeAsync(Identity(caller), new ViewResourceRequest(
            ViewScopeRequest.Shared, ViewResourceKind.Asset, resourceId));

        Assert.Equal(ViewAccessOutcome.NotFound, decision.Outcome);
    }

    [Fact]
    public async Task SharedFolderPolicyRequiresAdministratorSurfaceUnlock()
    {
        var caller = State(access: true, include: false);
        var evaluator = new DenySurfaceEvaluator();
        var service = new ViewResourceAuthorizationService(
            new ViewScopeResolver(new ViewScopeResolverTests.ScopeStore(caller)),
            new ResourceStore(), evaluator);

        var authority = Identity(caller) with
        {
            AccountIsAdministrator = true,
            GrantAdminEnabled = true,
        };
        var decision = await service.AuthorizeAsync(authority, new ViewResourceRequest(
            ViewScopeRequest.Shared, ViewResourceKind.FolderPolicy, null,
            ViewResourceAction.Manage));

        Assert.Equal(ViewAccessOutcome.Forbidden, decision.Outcome);
        Assert.True(evaluator.LastRequirement!.RequiresAdministrator);
        Assert.True(evaluator.LastRequirement.RequiresAdministratorSurfaceUnlock);
    }

    [Fact]
    public async Task DelegatedClientWithoutViewPermissionCannotReadPersonalResources()
    {
        var caller = State(access: true, include: false);
        var evaluator = new AllowEvaluator(ApplicationPermissionIds.ViewPersonalRead);
        var store = new CountingScopeStore([caller]);
        var service = new ViewResourceAuthorizationService(
            new ViewScopeResolver(store), new ResourceStore(), evaluator);
        var authority = new RequestAuthority(PrincipalKind.DelegatedUserClient, true,
            Guid.NewGuid(), caller.Policy.ProfileId, Guid.NewGuid(),
            AccountEnabled: true, GrantEnabled: true, ApplicationEnabled: true);

        var decision = await service.AuthorizeAsync(authority,
            new ViewResourceRequest(ViewScopeRequest.Mine, ViewResourceKind.Search, null));

        Assert.Equal(ViewAccessOutcome.Forbidden, decision.Outcome);
        Assert.Equal(ApplicationPermissionIds.ViewPersonalRead,
            evaluator.LastRequirement!.ApplicationPermission);
        Assert.Equal(0, store.ProfileReads);
    }

    [Fact]
    public async Task MissingAccountViewFeatureDoesNotEnumerateScopes()
    {
        var caller = State(access: true, include: false);
        var store = new CountingScopeStore([caller]);
        var service = new ViewResourceAuthorizationService(
            new ViewScopeResolver(store), new ResourceStore(), new DenyEvaluator());

        var decision = await service.AuthorizeAsync(Identity(caller),
            new ViewResourceRequest(ViewScopeRequest.Mine, ViewResourceKind.Search, null));

        Assert.Equal(ViewAccessOutcome.Forbidden, decision.Outcome);
        Assert.Equal(0, store.ProfileReads);
    }

    private static ViewResourceAuthorizationService Create(
        ViewScopeStoreEntry[] profiles,
        params ViewResourceDescriptor[] resources) =>
        Create(profiles, new AllowEvaluator(), resources);

    private static ViewResourceAuthorizationService Create(
        ViewScopeStoreEntry[] profiles,
        AllowEvaluator evaluator,
        params ViewResourceDescriptor[] resources) =>
        new(
            new ViewScopeResolver(new ViewScopeResolverTests.ScopeStore(profiles)),
            new ResourceStore(resources),
            evaluator);

    private static RequestAuthority Identity(ViewScopeStoreEntry state) =>
        new(PrincipalKind.Human, true, Guid.NewGuid(), state.Policy.ProfileId,
            AccountEnabled: true, GrantEnabled: true);

    private static ViewScopeStoreEntry State(bool access, bool include)
    {
        var profileId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        return new ViewScopeStoreEntry(
            new ViewProfilePolicy(profileId, true, access, include, true, now),
            new ViewPersonalSpace(Guid.NewGuid(), profileId, Guid.NewGuid(), now, now));
    }

    private sealed class ResourceStore(params ViewResourceDescriptor[] resources) : IViewResourceStore
    {
        public Task<ViewResourceDescriptor?> FindAsync(
            ViewResourceKind kind,
            Guid resourceId,
            Guid requestingProfileId,
            CancellationToken ct = default) =>
            Task.FromResult(resources.FirstOrDefault(resource =>
                resource.Kind == kind && resource.ResourceId == resourceId));
    }

    private sealed class MutableResourceStore(ViewResourceDescriptor resource) : IViewResourceStore
    {
        public ViewResourceDescriptor? Resource { get; set; } = resource;

        public Task<ViewResourceDescriptor?> FindAsync(
            ViewResourceKind kind,
            Guid resourceId,
            Guid requestingProfileId,
            CancellationToken ct = default) =>
            Task.FromResult(Resource is { } candidate
                && candidate.Kind == kind && candidate.ResourceId == resourceId
                    ? candidate
                    : null);
    }

    private sealed class CountingScopeStore(IReadOnlyList<ViewScopeStoreEntry> profiles) : IViewScopeStore
    {
        public int ProfileReads { get; private set; }

        public Task<ViewScopeStoreEntry?> FindProfileAsync(Guid profileId, CancellationToken ct = default) =>
            Task.FromResult(profiles.FirstOrDefault(profile => profile.Policy.ProfileId == profileId));

        public Task<IReadOnlyList<ViewScopeStoreEntry>> GetProfilesAsync(CancellationToken ct = default)
        {
            ProfileReads++;
            return Task.FromResult(profiles);
        }

        public Task<Guid?> GetSharedLibraryIdAsync(CancellationToken ct = default) =>
            Task.FromResult<Guid?>(Guid.Parse("00000000-0000-0000-0000-000000000003"));
    }

    private sealed class DenyEvaluator : IAuthorizationEvaluator
    {
        public ValueTask<AuthorizationDecision> EvaluateAsync(
            RequestAuthority authority,
            AuthorizationRequirement requirement,
            ResourceAuthorizationContext? resource,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(AuthorizationDecision.Deny(AuthorizationDenialReason.MissingFeatureGrant));
    }

    private sealed class DenySurfaceEvaluator : IAuthorizationEvaluator
    {
        public AuthorizationRequirement? LastRequirement { get; private set; }

        public ValueTask<AuthorizationDecision> EvaluateAsync(
            RequestAuthority authority,
            AuthorizationRequirement requirement,
            ResourceAuthorizationContext? resource,
            CancellationToken cancellationToken = default)
        {
            LastRequirement = requirement;
            return ValueTask.FromResult(requirement.RequiresAdministratorSurfaceUnlock
                ? AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorUnlockRequired)
                : AuthorizationDecision.Allow());
        }
    }

    private sealed class AllowEvaluator(ApplicationPermissionId? deniedPermission = null) : IAuthorizationEvaluator
    {
        public AuthorizationRequirement? LastRequirement { get; private set; }
        public ResourceAuthorizationContext? LastResource { get; private set; }
        public ValueTask<AuthorizationDecision> EvaluateAsync(RequestAuthority authority,
            AuthorizationRequirement requirement, ResourceAuthorizationContext? resource,
            CancellationToken cancellationToken = default)
        {
            LastRequirement = requirement;
            LastResource = resource;
            return ValueTask.FromResult(deniedPermission.HasValue
                && requirement.ApplicationPermission == deniedPermission
                ? AuthorizationDecision.Deny(AuthorizationDenialReason.MissingPermission)
                : AuthorizationDecision.Allow());
        }
    }
}
