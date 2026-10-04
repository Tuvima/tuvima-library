using System.Text.Json;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.View;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.PersonalMedia;
using Microsoft.AspNetCore.Http;

namespace MediaEngine.Api.Tests;

public sealed class DisplayRecentViewAuthorizationTests
{
    [Fact]
    public async Task RealRecentComposerUsesAuthorizedMineAndNeverSecondProfileOrShared()
    {
        var first = State(); var second = State(); var shared = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var backend = new ScopedBackend([Asset(first.PersonalSpace!.LibraryId, "Mine"), Asset(second.PersonalSpace!.LibraryId,"Other"), Asset(shared,"Shared")]);
        var resolver = new ViewScopeResolver(new ViewScopeResolverTests.ScopeStore(first,second));
        var authorization = new ViewResourceAuthorizationService(resolver,new EmptyStore(),new TestAllowAuthorizationEvaluator());
        async Task<MediaEngine.Contracts.Display.DisplayRecentPageDto> Load(ViewScopeStoreEntry profile)
        {
            var context = new HttpViewRequestProfileContext(new HttpContextAccessor { HttpContext = new DefaultHttpContext() }, TestViewAuthorityResolver.Human(profile.Policy.ProfileId));
            var view = new ViewQueryOrchestrator(context, authorization, backend);
            return await new DisplayRecentComposerService(null!,view).LoadAsync("view",null,18,profile.Policy.ProfileId,default);
        }
        var page = await Load(first);
        Assert.Equal("Mine",Assert.Single(page.Items).ViewAsset!.Title);
        Assert.Null(page.Items[0].Catalogue); Assert.False(backend.LastPlan!.IncludeSharedLibraryAssets);
        Assert.True(backend.LastPlan.SortByAddedAt);
        Assert.Equal(first.PersonalSpace.LibraryId,Assert.Single(backend.LastPlan.Scope.LibraryIds));
        Assert.Equal("Other",Assert.Single((await Load(second)).Items).ViewAsset!.Title);
    }
    private static ViewScopeStoreEntry State() { var id = Guid.NewGuid(); var now=DateTimeOffset.UtcNow; return new(new ViewProfilePolicy(id,true,true,true,false,true,now),new ViewPersonalSpace(Guid.NewGuid(),id,Guid.NewGuid(),now,now)); }
    private static LocalAssetDto Asset(Guid library, string title) => JsonSerializer.Deserialize<LocalAssetDto>(JsonSerializer.Serialize(new { id=Guid.NewGuid(), library_id=library, title, file_name="photo.jpg",media_kind="image", created_at=DateTimeOffset.UtcNow, files=Array.Empty<object>(), tags=Array.Empty<string>() }))!;
    private sealed class EmptyStore : IViewResourceStore { public Task<ViewResourceDescriptor?> FindAsync(ViewResourceKind kind,Guid resourceId,Guid requestingProfileId,CancellationToken ct=default) => Task.FromResult<ViewResourceDescriptor?>(null); }
    private sealed class ScopedBackend(IReadOnlyList<LocalAssetDto> rows) : IViewAssetQueryBackend
    {
        public ViewAssetQueryPlan? LastPlan {get;private set;}
        public Task<ViewAssetTimelinePageDto> QueryAsync(ViewAssetQueryPlan plan,CancellationToken ct=default) { LastPlan=plan; return Task.FromResult(new ViewAssetTimelinePageDto(rows.Where(r=>plan.Scope.LibraryIds.Contains(r.LibraryId)).ToList(),null,false)); }
        public Task<ViewTimelineIndexDto> IndexAsync(ViewAssetQueryPlan plan,CancellationToken ct=default)=>Task.FromResult(new ViewTimelineIndexDto([]));
    }
}
