using MediaEngine.Api.Services.View;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.PersonalMedia;
using Microsoft.AspNetCore.Http;

namespace MediaEngine.Api.Tests;

public sealed class ViewQueryOrchestratorTests
{
    [Fact]
    public async Task QueryBackendReceivesOnlyResolverAuthorizedLibraries()
    {
        var caller = State(access: true, include: false);
        var included = State(access: false, include: true);
        var excluded = State(access: false, include: false);
        var http = new DefaultHttpContext();
        var context = new HttpViewRequestProfileContext(
            new HttpContextAccessor { HttpContext = http }, TestViewAuthorityResolver.Human(caller.Policy.ProfileId));
        var resolver = new ViewScopeResolver(
            new ViewScopeResolverTests.ScopeStore(caller, included, excluded));
        var authorization = new ViewResourceAuthorizationService(resolver, new EmptyResourceStore(), new TestAllowAuthorizationEvaluator());
        var backend = new CapturingBackend();
        var orchestrator = new ViewQueryOrchestrator(context, authorization, backend);

        var result = await orchestrator.QueryAsync(new ViewAssetQueryRequest(
            ViewScopeRequest.Shared,
            Search: "lake"));

        Assert.Equal(ViewAccessOutcome.Allowed, result.Outcome);
        var plan = Assert.IsType<ViewAssetQueryPlan>(backend.Plan);
        Assert.Equal([Guid.Parse("00000000-0000-0000-0000-000000000003")], plan.Scope.LibraryIds);
        Assert.True(plan.IncludeSharedLibraryAssets);
        Assert.Equal("lake", plan.Search);
    }

    [Fact]
    public async Task QueryDoesNotReachBackendWithoutTrustedProfileContext()
    {
        var context = new HttpViewRequestProfileContext(new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext(),
        }, TestViewAuthorityResolver.Anonymous);
        var resolver = new ViewScopeResolver(new ViewScopeResolverTests.ScopeStore());
        var authorization = new ViewResourceAuthorizationService(resolver, new EmptyResourceStore(), new TestAllowAuthorizationEvaluator());
        var backend = new CapturingBackend();
        var orchestrator = new ViewQueryOrchestrator(context, authorization, backend);

        var result = await orchestrator.QueryAsync(new ViewAssetQueryRequest(ViewScopeRequest.Mine));

        Assert.Equal(ViewAccessOutcome.Unauthenticated, result.Outcome);
        Assert.Null(backend.Plan);
    }

    [Fact]
    public async Task StalePersistedProfileScopeFallsBackOnlyWhenMarkedPersisted()
    {
        var caller = State(access: false, include: false);
        var staleProfileId = Guid.NewGuid();
        var http = new DefaultHttpContext();
        var context = new HttpViewRequestProfileContext(new HttpContextAccessor { HttpContext = http },
            TestViewAuthorityResolver.Human(caller.Policy.ProfileId));
        var resolver = new ViewScopeResolver(new ViewScopeResolverTests.ScopeStore(caller));
        var authorization = new ViewResourceAuthorizationService(resolver, new EmptyResourceStore(), new TestAllowAuthorizationEvaluator());

        var explicitBackend = new CapturingBackend();
        var explicitQuery = new ViewQueryOrchestrator(context, authorization, explicitBackend);
        var explicitResult = await explicitQuery.QueryAsync(new ViewAssetQueryRequest(
            ViewScopeRequest.ForProfile(staleProfileId)));
        Assert.Equal(ViewAccessOutcome.NotFound, explicitResult.Outcome);
        Assert.Null(explicitBackend.Plan);

        var persistedBackend = new CapturingBackend();
        var persistedQuery = new ViewQueryOrchestrator(context, authorization, persistedBackend);
        var persistedResult = await persistedQuery.QueryAsync(new ViewAssetQueryRequest(
            ViewScopeRequest.ForProfile(staleProfileId), AllowStaleSelectionFallback: true));
        Assert.Equal(ViewAccessOutcome.Allowed, persistedResult.Outcome);
        Assert.True(persistedResult.Scope!.WasFallback);
        Assert.Equal(caller.PersonalSpace!.LibraryId, Assert.Single(persistedBackend.Plan!.Scope.LibraryIds));
    }

    [Fact]
    public async Task GalleryQueryDoesNotReachBackendUntilGalleryIsAuthorized()
    {
        var caller = State(access: false, include: false);
        var http = new DefaultHttpContext();
        var context = new HttpViewRequestProfileContext(
            new HttpContextAccessor { HttpContext = http }, TestViewAuthorityResolver.Human(caller.Policy.ProfileId));
        var resolver = new ViewScopeResolver(new ViewScopeResolverTests.ScopeStore(caller));
        var authorization = new ViewResourceAuthorizationService(resolver, new EmptyResourceStore(), new TestAllowAuthorizationEvaluator());
        var backend = new CapturingBackend();
        var orchestrator = new ViewQueryOrchestrator(context, authorization, backend);

        var result = await orchestrator.QueryAsync(new ViewAssetQueryRequest(
            ViewScopeRequest.Mine,
            GalleryId: Guid.NewGuid()));

        Assert.Equal(ViewAccessOutcome.NotFound, result.Outcome);
        Assert.Null(backend.Plan);
    }

    [Fact]
    public async Task ExplicitSharedGalleryQueryNarrowsPlanToGalleryOwnersLibrary()
    {
        var caller = State(access: false, include: false);
        var owner = State(access: false, include: false);
        var galleryId = Guid.NewGuid();
        var http = new DefaultHttpContext();
        var context = new HttpViewRequestProfileContext(new HttpContextAccessor { HttpContext = http },
            TestViewAuthorityResolver.Human(caller.Policy.ProfileId));
        var resolver = new ViewScopeResolver(new ViewScopeResolverTests.ScopeStore(caller, owner));
        var resource = new ViewResourceDescriptor(ViewResourceKind.Gallery, galleryId,
            owner.Policy.ProfileId, owner.PersonalSpace!.LibraryId,
            new HashSet<Guid> { caller.Policy.ProfileId });
        var authorization = new ViewResourceAuthorizationService(resolver, new EmptyResourceStore(resource), new TestAllowAuthorizationEvaluator());
        var backend = new CapturingBackend();
        var orchestrator = new ViewQueryOrchestrator(context, authorization, backend, new StubSmartGalleryService());

        var result = await orchestrator.QueryAsync(new ViewAssetQueryRequest(
            ViewScopeRequest.Mine, GalleryId: galleryId));

        Assert.Equal(ViewAccessOutcome.Allowed, result.Outcome);
        Assert.Equal(galleryId, backend.Plan!.GalleryId);
        Assert.Equal([owner.PersonalSpace.LibraryId], backend.Plan.Scope.LibraryIds);
    }

    [Fact]
    public async Task AuthorizedSmartGalleryUsesDynamicRuleInsteadOfManualMembershipRows()
    {
        var caller = State(access: false, include: false);
        var galleryId = Guid.NewGuid();
        var http = new DefaultHttpContext();
        var context = new HttpViewRequestProfileContext(new HttpContextAccessor { HttpContext = http },
            TestViewAuthorityResolver.Human(caller.Policy.ProfileId));
        var resolver = new ViewScopeResolver(new ViewScopeResolverTests.ScopeStore(caller));
        var resource = new ViewResourceDescriptor(ViewResourceKind.Gallery, galleryId,
            caller.Policy.ProfileId, caller.PersonalSpace!.LibraryId);
        var authorization = new ViewResourceAuthorizationService(resolver, new EmptyResourceStore(resource), new TestAllowAuthorizationEvaluator());
        var backend = new CapturingBackend();
        var rule = CollectionRuleDefinition.SingleGroup(
            [new CollectionRulePredicate { Field = "favorite", Op = "eq", Value = "true" }]);
        var smart = new StubSmartGalleryService(rule);
        var orchestrator = new ViewQueryOrchestrator(context, authorization, backend, smart);

        var result = await orchestrator.QueryAsync(new ViewAssetQueryRequest(
            ViewScopeRequest.Mine, GalleryId: galleryId));

        Assert.Equal(ViewAccessOutcome.Allowed, result.Outcome);
        Assert.Null(backend.Plan!.GalleryId);
        Assert.Same(rule, backend.Plan.SmartRule);
        Assert.Equal(1, smart.CallCount);
    }

    [Fact]
    public async Task UnauthorizedGalleryNeverResolvesOrEvaluatesItsRule()
    {
        var caller = State(access: false, include: false);
        var http = new DefaultHttpContext();
        var context = new HttpViewRequestProfileContext(new HttpContextAccessor { HttpContext = http },
            TestViewAuthorityResolver.Human(caller.Policy.ProfileId));
        var resolver = new ViewScopeResolver(new ViewScopeResolverTests.ScopeStore(caller));
        var authorization = new ViewResourceAuthorizationService(resolver, new EmptyResourceStore(), new TestAllowAuthorizationEvaluator());
        var backend = new CapturingBackend();
        var smart = new StubSmartGalleryService(CollectionRuleDefinition.SingleGroup(
            [new CollectionRulePredicate { Field = "favorite", Value = "true" }]));
        var orchestrator = new ViewQueryOrchestrator(context, authorization, backend, smart);

        var result = await orchestrator.QueryAsync(new ViewAssetQueryRequest(
            ViewScopeRequest.Mine, GalleryId: Guid.NewGuid()));

        Assert.Equal(ViewAccessOutcome.NotFound, result.Outcome);
        Assert.Equal(0, smart.CallCount);
        Assert.Null(backend.Plan);
    }

    [Fact]
    public async Task SharedLibraryAssetCanBeReadOnlyFromSharedScope()
    {
        var caller = State(access: true, include: false);
        var owner = State(access: false, include: false);
        var itemId = Guid.NewGuid();
        var resolver = new ViewScopeResolver(new ViewScopeResolverTests.ScopeStore(caller, owner));
        var resource = new ViewResourceDescriptor(
            ViewResourceKind.Thumbnail,
            itemId,
            null,
            Guid.Parse("00000000-0000-0000-0000-000000000003"),
            IsSharedLibraryAsset: true);
        var authorization = new ViewResourceAuthorizationService(resolver, new EmptyResourceStore(resource), new TestAllowAuthorizationEvaluator());

        var shared = await authorization.AuthorizeAsync(
            TestViewAuthorityResolver.Human(caller.Policy.ProfileId).Authority,
            new ViewResourceRequest(ViewScopeRequest.Shared, ViewResourceKind.Thumbnail, itemId));
        var mine = await authorization.AuthorizeAsync(
            TestViewAuthorityResolver.Human(caller.Policy.ProfileId).Authority,
            new ViewResourceRequest(ViewScopeRequest.Mine, ViewResourceKind.Thumbnail, itemId));

        Assert.Equal(ViewAccessOutcome.Allowed, shared.Outcome);
        Assert.Equal([Guid.Parse("00000000-0000-0000-0000-000000000003")], shared.Scope!.LibraryIds);
        Assert.Equal(ViewAccessOutcome.NotFound, mine.Outcome);
    }

    [Fact]
    public async Task AddedAtMineUsesOnlyCurrentPersonalSpaceAndIncludesNonTimelineFolders()
    {
        var caller = State(access: true, include: true); var other = State(access: true, include: true);
        var context = new HttpViewRequestProfileContext(new HttpContextAccessor { HttpContext = new DefaultHttpContext() }, TestViewAuthorityResolver.Human(caller.Policy.ProfileId));
        var authorization = new ViewResourceAuthorizationService(new ViewScopeResolver(new ViewScopeResolverTests.ScopeStore(caller, other)), new EmptyResourceStore(), new TestAllowAuthorizationEvaluator());
        var backend = new CapturingBackend(); var orchestrator = new ViewQueryOrchestrator(context, authorization, backend);
        var boundary = DateTimeOffset.UtcNow;
        await orchestrator.QueryAsync(new ViewAssetQueryRequest(ViewScopeRequest.Mine, SortByAddedAt: true, AddedBefore: boundary, AddedAfterKey: "catalogue:00000000000000000000000000000001"));
        var plan = Assert.IsType<ViewAssetQueryPlan>(backend.Plan);
        Assert.Equal(caller.PersonalSpace!.LibraryId, Assert.Single(plan.Scope.LibraryIds));
        Assert.DoesNotContain(other.PersonalSpace!.LibraryId, plan.Scope.LibraryIds);
        Assert.False(plan.IncludeSharedLibraryAssets); Assert.False(plan.TimelineEligibleOnly);
        Assert.True(plan.SortByAddedAt); Assert.Equal(boundary, plan.AddedBefore);
        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.QueryAsync(new ViewAssetQueryRequest(ViewScopeRequest.Mine, Cursor: "wrong", SortByAddedAt: true)));
    }

    private static ViewScopeStoreEntry State(bool access, bool include)
    {
        var profileId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        return new ViewScopeStoreEntry(
            new ViewProfilePolicy(profileId, true, access, include, false, true, now),
            new ViewPersonalSpace(Guid.NewGuid(), profileId, Guid.NewGuid(), now, now));
    }

    private sealed class EmptyResourceStore(ViewResourceDescriptor? resource = null) : IViewResourceStore
    {
        public Task<ViewResourceDescriptor?> FindAsync(
            ViewResourceKind kind,
            Guid resourceId,
            Guid requestingProfileId,
            CancellationToken ct = default) => Task.FromResult(
                resource is not null && resource.Kind == kind && resource.ResourceId == resourceId
                    ? resource
                    : null);
    }

    private sealed class CapturingBackend : IViewAssetQueryBackend
    {
        public ViewAssetQueryPlan? Plan { get; private set; }

        public Task<ViewAssetTimelinePageDto> QueryAsync(
            ViewAssetQueryPlan plan,
            CancellationToken ct = default)
        {
            Plan = plan;
            return Task.FromResult(new ViewAssetTimelinePageDto([], null, false));
        }

        public Task<ViewTimelineIndexDto> IndexAsync(
            ViewAssetQueryPlan plan,
            CancellationToken ct = default)
        {
            Plan = plan;
            return Task.FromResult(new ViewTimelineIndexDto([]));
        }
    }

    private sealed class StubSmartGalleryService(CollectionRuleDefinition? rule = null)
        : IViewSmartGalleryQueryService
    {
        public int CallCount { get; private set; }

        public Task<CollectionRuleDefinition?> ResolveRuleAsync(Guid galleryId, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(rule);
        }
    }
}
