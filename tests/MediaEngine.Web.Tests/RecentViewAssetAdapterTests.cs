using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;

namespace MediaEngine.Web.Tests;

public sealed class RecentViewAssetAdapterTests
{
    [Fact]
    public void ReloadMintsFreshBoundedMineGrantAfterExpiryAndProfileChange()
    {
        var clock = new Clock(); var grants = new ViewMediaGrantService(new byte[32], TimeSpan.FromSeconds(30), clock);
        var adapter = new RecentViewAssetAdapter(grants, EngineApiClientStub.CreateDefault());
        var profile = Guid.NewGuid();
        var asset = new DisplayRecentViewAssetDto(Guid.NewGuid(), Guid.NewGuid(), "Photo", "photo.jpg", "image", 4000, 3000, null, clock.GetUtcNow());
        var old = Token(adapter.ThumbnailUrl(asset, profile));
        Assert.True(grants.TryValidate(old, out var first));
        Assert.Equal(ViewMediaResourceKind.Thumbnail, first!.ResourceKind);
        Assert.Equal(ViewScopeKind.Mine, first.ScopeKind); Assert.Equal(profile, first.ProfileId);
        clock.Now = clock.Now.AddSeconds(31);
        Assert.False(grants.TryValidate(old, out _));
        var refreshed = Token(adapter.ThumbnailUrl(asset, profile));
        Assert.NotEqual(old, refreshed); Assert.True(grants.TryValidate(refreshed, out _));
        var other = Guid.NewGuid(); Assert.True(grants.TryValidate(Token(adapter.ThumbnailUrl(asset, other)), out var next));
        Assert.Equal(other, next!.ProfileId); Assert.Equal(asset.AssetId, next.AssetId); Assert.Equal(asset.LibraryId, next.LibraryId);
    }
    [Fact]
    public async Task OpenRechecksExactViewMineIdentity()
    {
        var asset = new DisplayRecentViewAssetDto(Guid.NewGuid(), Guid.NewGuid(), "Photo", "photo.jpg", "image", null, null, null, DateTimeOffset.UtcNow);
        var called = false;
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.GetViewItemAsync), args => {
            called = true; Assert.Equal(asset.AssetId, args![0]); Assert.Equal(ViewScopeKind.Mine, args[1]); Assert.Null(args[2]);
            return Task.FromResult<LocalAssetDto?>(null);
        }));
        var adapter = new RecentViewAssetAdapter(new ViewMediaGrantService(new byte[32], TimeSpan.FromMinutes(1)), api);
        Assert.Null(await adapter.OpenAsync(asset)); Assert.True(called);
    }
    private static string Token(string url) => url["/view-media/".Length..];
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
