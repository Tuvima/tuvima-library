using MediaEngine.Api.Services.Collections;
using MediaEngine.Api.Services.View;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using MediaEngine.TestSupport;

namespace MediaEngine.Api.Tests;

public sealed class CollectionPersonalMediaAuthorizationTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"tuvima-collection-view-auth-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;

    public CollectionPersonalMediaAuthorizationTests()
    {
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
    }

    [Fact]
    public async Task DelegatedCollectionsReadWithoutViewReadIsDeniedBeforeCollectionLookup()
    {
        var evaluator = new PermissionEvaluator(ApplicationPermissionIds.ViewPersonalRead);
        var viewAuthorization = new ViewResourceAuthorizationService(
            new ThrowingScopeResolver(), new ThrowingResourceStore(), evaluator);
        var service = new CollectionPersonalMediaService(
            new CollectionRepository(_database),
            new ProfileRepository(_database),
            new ViewProfileRepository(_database),
            new ViewGalleryRepository(_database),
            new CollectionViewSourceRepository(_database),
            evaluator,
            viewAuthorization);
        var authority = new RequestAuthority(
            PrincipalKind.DelegatedUserClient,
            true,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            AccountEnabled: true,
            GrantEnabled: true,
            ApplicationEnabled: true);

        var result = await service.ListForViewerAsync(Guid.NewGuid(), authority);

        Assert.False(result.Found);
        Assert.False(result.Allowed);
        Assert.Equal(
            [ApplicationPermissionIds.CollectionsRead, ApplicationPermissionIds.ViewPersonalRead],
            evaluator.Permissions);
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.ReleaseSqlitePool(_databasePath);
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    private sealed class PermissionEvaluator(ApplicationPermissionId denied) : IAuthorizationEvaluator
    {
        public List<ApplicationPermissionId?> Permissions { get; } = [];

        public ValueTask<AuthorizationDecision> EvaluateAsync(
            RequestAuthority authority,
            AuthorizationRequirement requirement,
            ResourceAuthorizationContext? resource,
            CancellationToken cancellationToken = default)
        {
            Permissions.Add(requirement.ApplicationPermission);
            return ValueTask.FromResult(requirement.ApplicationPermission == denied
                ? AuthorizationDecision.Deny(AuthorizationDenialReason.MissingPermission)
                : AuthorizationDecision.Allow());
        }
    }

    private sealed class ThrowingScopeResolver : IViewScopeResolver
    {
        public Task<ViewScopeResolution?> ResolveAsync(
            RequestAuthority caller,
            ViewScopeRequest requested,
            bool allowStaleSelectionFallback = false,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("View scope lookup must not run after permission denial.");
    }

    private sealed class ThrowingResourceStore : IViewResourceStore
    {
        public Task<ViewResourceDescriptor?> FindAsync(
            ViewResourceKind kind,
            Guid resourceId,
            Guid requestingProfileId,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("View resource lookup must not run after permission denial.");
    }
}
