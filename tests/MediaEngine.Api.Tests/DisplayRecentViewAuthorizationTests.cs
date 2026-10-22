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
        var backend = new ScopedBackend([Asset(first.PersonalSpace!.LibraryId, "Mine"), Asset(second.PersonalSpace!.LibraryId, "Other"), Asset(shared, "Shared")]);
        var resolver = new ViewScopeResolver(new ViewScopeResolverTests.ScopeStore(first, second));
        var authorization = new ViewResourceAuthorizationService(resolver, new EmptyStore(), new TestAllowAuthorizationEvaluator());
        async Task<MediaEngine.Contracts.Display.DisplayRecentPageDto> Load(ViewScopeStoreEntry profile)
        {
            var context = new HttpViewRequestProfileContext(new HttpContextAccessor { HttpContext = new DefaultHttpContext() }, TestViewAuthorityResolver.Human(profile.Policy.ProfileId));
            var view = new ViewQueryOrchestrator(context, authorization, backend);
            return await new DisplayRecentComposerService(null!, view).LoadAsync("view", null, 18, profile.Policy.ProfileId, default);
        }
        var page = await Load(first);
        Assert.Equal("Mine", Assert.Single(page.Items).ViewAsset!.Title);
        Assert.Null(page.Items[0].Catalogue); Assert.False(backend.LastPlan!.IncludeSharedLibraryAssets);
        Assert.True(backend.LastPlan.SortByAddedAt);
        Assert.Equal(first.PersonalSpace.LibraryId, Assert.Single(backend.LastPlan.Scope.LibraryIds));
        Assert.Equal("Other", Assert.Single((await Load(second)).Items).ViewAsset!.Title);
    }
    [Fact]
    public async Task HomeRecentlyAddedNeverShowsADocumentFromPersonalOrSharedButStillShowsPhotosAndVideos()
    {
        var first = State(); var second = State(); var shared = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var backend = new ScopedBackend([
            Asset(first.PersonalSpace!.LibraryId, "Holiday", "image"), Asset(first.PersonalSpace.LibraryId, "Clip", "video"),
            Asset(first.PersonalSpace.LibraryId, "Passport scan", "document"), Asset(shared, "Shared lease", "document"),
            Asset(shared, "Shared photo", "image")]);
        var resolver = new ViewScopeResolver(new ViewScopeResolverTests.ScopeStore(first, second));
        var authorization = new ViewResourceAuthorizationService(resolver, new EmptyStore(), new TestAllowAuthorizationEvaluator());
        var context = new HttpViewRequestProfileContext(new HttpContextAccessor { HttpContext = new DefaultHttpContext() }, TestViewAuthorityResolver.Human(first.Policy.ProfileId));
        var view = new ViewQueryOrchestrator(context, authorization, backend);

        var page = await new DisplayRecentComposerService(null!, view).LoadAsync("view", null, 18, first.Policy.ProfileId, default);

        Assert.Equal(["Clip", "Holiday"], page.Items.Select(item => item.ViewAsset!.Title).OrderBy(title => title, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(page.Items, item => item.ViewAsset!.MediaKind == "document");
        Assert.Equal(["image", "video"], backend.LastPlan!.MediaKinds!.OrderBy(kind => kind, StringComparer.Ordinal).ToArray());
    }
    private static ViewScopeStoreEntry State() { var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow; return new(new ViewProfilePolicy(id, true, true, true, true, now), new ViewPersonalSpace(Guid.NewGuid(), id, Guid.NewGuid(), now, now)); }
    private static LocalAssetDto Asset(Guid library, string title, string kind = "image") => JsonSerializer.Deserialize<LocalAssetDto>(JsonSerializer.Serialize(new { id = Guid.NewGuid(), library_id = library, title, file_name = "photo.jpg", media_kind = kind, created_at = DateTimeOffset.UtcNow, files = Array.Empty<object>(), tags = Array.Empty<string>() }))!;
    private sealed class EmptyStore : IViewResourceStore { public Task<ViewResourceDescriptor?> FindAsync(ViewResourceKind kind, Guid resourceId, Guid requestingProfileId, CancellationToken ct = default) => Task.FromResult<ViewResourceDescriptor?>(null); }
    private sealed class ScopedBackend(IReadOnlyList<LocalAssetDto> rows) : IViewAssetQueryBackend
    {
        public ViewAssetQueryPlan? LastPlan { get; private set; }
        public Task<ViewAssetTimelinePageDto> QueryAsync(ViewAssetQueryPlan plan, CancellationToken ct = default) { LastPlan = plan; return Task.FromResult(new ViewAssetTimelinePageDto(rows.Where(r => plan.Scope.LibraryIds.Contains(r.LibraryId) && (plan.MediaKinds is not { Count: > 0 } kinds || kinds.Contains(r.MediaKind))).ToList(), null, false)); }
        public Task<ViewTimelineIndexDto> IndexAsync(ViewAssetQueryPlan plan, CancellationToken ct = default) => Task.FromResult(new ViewTimelineIndexDto([]));
        public Task<IReadOnlyList<ViewAddedByDto>> AddedByAsync(ViewAssetQueryPlan plan, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ViewAddedByDto>>([]);
    }
}
