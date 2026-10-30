using System.Security.Claims;
using Bunit;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Web.Components.MediaTiles;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class RecentMediaFeedInteractionTests : AsyncBunitContext
{
    private readonly Guid _profile = Guid.NewGuid();
    private readonly Guid _secondProfile = Guid.NewGuid();
    private readonly List<string> _requests = [];
    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private Func<string, Task<DisplayRecentPageDto?>> _read = type => Task.FromResult<DisplayRecentPageDto?>(new(type, [], null, false));
    public RecentMediaFeedInteractionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose; Services.AddLogging();
        Services.AddSingleton(_device);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton<AuthenticationStateProvider>(new ProfileAuth(_profile));
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => Task.FromResult(new List<ProfileViewModel> { new(_profile, "One", "#aaa", "RestrictedProfile", DateTimeOffset.UtcNow), new(_secondProfile, "Two", "#bbb", "RestrictedProfile", DateTimeOffset.UtcNow) }));
            stub.SetHandler(nameof(IEngineApiClient.GetDisplayRecentAsync), args => { var type = (string)args![0]!; _requests.Add(type); return _read(type); });
        }));
        Services.AddScoped<ActiveProfileSessionService>(); Services.AddScoped<UniverseStateContainer>(); Services.AddScoped<UIOrchestratorService>();
        Services.AddSingleton(new ViewMediaGrantService(new byte[32], TimeSpan.FromMinutes(1))); Services.AddScoped<RecentViewAssetAdapter>(); Services.AddScoped<UserProgressChangeNotifier>();
    }
    [Fact]
    public void ControlledFilterFetchesOnceAndKeepsSelectedScopeAndLink()
    {
        _read = type => Task.FromResult<DisplayRecentPageDto?>(Page(type, "Recent"));
        IRenderedComponent<RecentMediaFeed>? cut = null;
        cut = Render<RecentMediaFeed>(p => p.Add(c => c.Type, "all").Add(c => c.TypeChanged, type => cut!.Render(p => p.Add(c => c.Type, type))));
        cut.WaitForAssertion(() => Assert.Single(_requests));
        cut.FindAll(".recent-filters button").Single(b => b.TextContent == "View").Click();
        cut.WaitForAssertion(() => Assert.Equal(new[] { "all", "view" }, _requests));
        Assert.Equal("true", cut.FindAll(".recent-filters button").Single(b => b.TextContent == "View").GetAttribute("aria-pressed"));
        Assert.Equal("/recently-added?type=view", cut.Find(".recent-view-all").GetAttribute("href"));
        cut.Render(p => p.Add(c => c.Type, "view").Add(c => c.RefreshKey, 1L));
        cut.WaitForAssertion(() => Assert.Equal(3, _requests.Count));
    }
    [Fact]
    public void PillsAreTogglesAndOnlyTypesWithItemsGetOne()
    {
        _read = type => Task.FromResult<DisplayRecentPageDto?>(Page(type, "Recent"));
        IRenderedComponent<RecentMediaFeed>? cut = null;
        cut = Render<RecentMediaFeed>(p => p.Add(c => c.Type, "all").Add(c => c.TypeChanged, type => cut!.Render(p => p.Add(c => c.Type, type))));
        cut.WaitForAssertion(() => Assert.Single(_requests));
        Assert.Equal(new[] { "All", "Watch", "Read", "View" }, cut.FindAll(".recent-filters button").Select(b => b.TextContent));
        cut.FindAll(".recent-filters button").Single(b => b.TextContent == "View").Click();
        cut.WaitForAssertion(() => Assert.Equal(new[] { "all", "view" }, _requests));
        cut.FindAll(".recent-filters button").Single(b => b.TextContent == "Read").Click();
        cut.WaitForAssertion(() => Assert.Equal(new[] { "all", "view", "read,view" }, _requests));
        Assert.Equal("true", cut.FindAll(".recent-filters button").Single(b => b.TextContent == "View").GetAttribute("aria-pressed"));
        Assert.Equal("true", cut.FindAll(".recent-filters button").Single(b => b.TextContent == "Read").GetAttribute("aria-pressed"));
        Assert.Equal("false", cut.FindAll(".recent-filters button").Single(b => b.TextContent == "All").GetAttribute("aria-pressed"));
        cut.FindAll(".recent-filters button").Single(b => b.TextContent == "View").Click();
        cut.WaitForAssertion(() => Assert.Equal("read", _requests[^1]));
    }
    [Fact]
    public void NoPillsAtAllWhenOnlyOneTypeHasItems()
    {
        _read = type => Task.FromResult<DisplayRecentPageDto?>(Page(type, "Recent", ["view"]));
        var cut = Render<RecentMediaFeed>();
        cut.WaitForAssertion(() => Assert.Single(_requests));
        cut.WaitForAssertion(() => Assert.Contains("Recent", cut.Markup));
        Assert.Empty(cut.FindAll(".recent-filters"));
    }
    [Fact]
    public async Task ObsoleteFilterAndProfileResponsesNeverReplaceCurrentItems()
    {
        var delayed = new TaskCompletionSource<DisplayRecentPageDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _read = type => type == "all" ? delayed.Task : Task.FromResult<DisplayRecentPageDto?>(Page(type, "Current"));
        var cut = Render<RecentMediaFeed>();
        cut.WaitForAssertion(() => Assert.Single(_requests));
        cut.Render(p => p.Add(c => c.Type, "view"));
        cut.WaitForAssertion(() => Assert.Contains("Current", cut.Markup));
        delayed.SetResult(Page("all", "Obsolete filter"));
        await Task.Yield();
        cut.WaitForAssertion(() => Assert.DoesNotContain("Obsolete filter", cut.Markup));
        var oldProfile = new TaskCompletionSource<DisplayRecentPageDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _read = _ => oldProfile.Task;
        cut.Render(p => p.Add(c => c.RefreshKey, 1L));
        cut.WaitForAssertion(() => Assert.Equal(3, _requests.Count));
        _read = type => Task.FromResult<DisplayRecentPageDto?>(Page(type, "Second profile"));
        await cut.InvokeAsync(() => Services.GetRequiredService<UIOrchestratorService>().SetActiveProfileAsync(_secondProfile));
        cut.WaitForAssertion(() => Assert.Contains("Second profile", cut.Markup));
        oldProfile.SetResult(Page("view", "Obsolete profile")); await Task.Yield();
        cut.WaitForAssertion(() => Assert.DoesNotContain("Obsolete profile", cut.Markup));
    }
    [Fact]
    public async Task StandaloneFeedRefreshesAfterAdditionAndMatchingSavedProgressOnly()
    {
        var cut = Render<RecentMediaFeed>(p => p.Add(c => c.Browse, true));
        cut.WaitForAssertion(() => Assert.Single(_requests));
        await cut.InvokeAsync(() => Services.GetRequiredService<UniverseStateContainer>().PushMediaAdded(new MediaAddedEvent(Guid.NewGuid(), null, "Book", "New book")));
        cut.WaitForAssertion(() => Assert.Equal(2, _requests.Count));
        await cut.InvokeAsync(() => Services.GetRequiredService<UserProgressChangeNotifier>().Publish(_secondProfile, Guid.NewGuid()));
        Assert.Equal(2, _requests.Count);
        await cut.InvokeAsync(() => Services.GetRequiredService<UserProgressChangeNotifier>().Publish(_profile, Guid.NewGuid()));
        cut.WaitForAssertion(() => Assert.Equal(3, _requests.Count));
    }
    [Fact]
    public async Task BackgroundRefreshRetainsCardsAndCoalescesNotifications()
    {
        _read = type => Task.FromResult<DisplayRecentPageDto?>(Page(type, "Visible card"));
        var cut = Render<RecentMediaFeed>();
        cut.WaitForAssertion(() => Assert.Contains("Visible card", cut.Markup));
        var pending = new TaskCompletionSource<DisplayRecentPageDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _read = _ => pending.Task;
        cut.Render(p => p.Add(c => c.RefreshKey, 1L));
        cut.WaitForAssertion(() => Assert.Equal(2, _requests.Count));
        Assert.Contains("Visible card", cut.Markup);
        Assert.DoesNotContain("Loading recent additions", cut.Markup);
        cut.Render(p => p.Add(c => c.RefreshKey, 2L));
        cut.Render(p => p.Add(c => c.RefreshKey, 3L));
        Assert.Equal(2, _requests.Count);
        _read = type => Task.FromResult<DisplayRecentPageDto?>(Page(type, "Latest card"));
        pending.SetResult(Page("all", "Updated card"));
        await Task.Yield();
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(3, _requests.Count);
            Assert.Contains("Latest card", cut.Markup);
            Assert.DoesNotContain("Loading recent additions", cut.Markup);
        });
    }
    [Fact]
    public void FailedBackgroundRefreshRetainsVisibleDataWithoutBlockingError()
    {
        _read = type => Task.FromResult<DisplayRecentPageDto?>(Page(type, "Visible card"));
        var cut = Render<RecentMediaFeed>();
        cut.WaitForAssertion(() => Assert.Contains("Visible card", cut.Markup));
        _read = _ => Task.FromResult<DisplayRecentPageDto?>(null);
        cut.Render(p => p.Add(c => c.RefreshKey, 1L));
        cut.WaitForAssertion(() => Assert.Equal(2, _requests.Count));
        Assert.Contains("Visible card", cut.Markup);
        Assert.DoesNotContain("unavailable", cut.Markup);
    }
    [Fact]
    public void FilterChangeClearsPreviousScopeAndShowsInitialLoading()
    {
        _read = type => Task.FromResult<DisplayRecentPageDto?>(Page(type, "Old scope"));
        var cut = Render<RecentMediaFeed>();
        cut.WaitForAssertion(() => Assert.Contains("Old scope", cut.Markup));
        _read = _ => new TaskCompletionSource<DisplayRecentPageDto?>().Task;
        cut.Render(p => p.Add(c => c.Type, "view"));
        cut.WaitForAssertion(() => Assert.Equal(2, _requests.Count));
        Assert.DoesNotContain("Old scope", cut.Markup);
        Assert.Contains("Loading recent additions", cut.Markup);
    }
    private static DisplayCardDto Card(string title, bool settling)
    {
        var asset = Guid.NewGuid(); var added = DateTimeOffset.UtcNow;
        var fields = typeof(DisplayArtworkDto).GetProperties().Where(p => p.Name.EndsWith("Url", StringComparison.Ordinal)).ToDictionary(p => p.Name, p => $"/stream/artwork/{asset:D}?variant={p.Name}");
        var artwork = System.Text.Json.JsonSerializer.Deserialize<DisplayArtworkDto>(System.Text.Json.JsonSerializer.Serialize(fields))!;
        return new DisplayCardDto(asset, asset, asset, null, "Book", "work", title, null, [], artwork, "portrait", "default", "caption", "bottom", null, [], new(false, true, false, false), added) { IsSettling = settling };
    }

    [Fact]
    public async Task PhoneHeaderHasSeeAllAndItemsStillBeingMatchedComeAfterMatchedOnes()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true));
        _read = type =>
        {
            var added = DateTimeOffset.UtcNow;
            return Task.FromResult<DisplayRecentPageDto?>(new(type,
            [
                new("catalogue:a", added, Card("Unsettled First", true), null),
                new("catalogue:b", added, Card("Matched Second", false), null),
                new("catalogue:c", added, Card("Unsettled Third", true), null),
                new("catalogue:d", added, Card("Matched Fourth", false), null),
            ], null, false));
        };
        var cut = Render<RecentMediaFeed>();
        cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll(".recent-item").Count));
        var order = cut.FindAll(".recent-item").Select(item => item.QuerySelector(".media-tile-caption__title, .media-tile-caption__title *")?.TextContent ?? item.TextContent).ToList();
        Assert.Contains("Matched Second", order[0]);
        Assert.Contains("Matched Fourth", order[1]);
        Assert.Contains("Unsettled First", order[2]);
        Assert.Contains("Unsettled Third", order[3]);
        Assert.Equal(2, cut.FindAll(".recent-item--settling").Count);
        var heading = cut.Find(".recent-feed-heading");
        Assert.Equal("H2", heading.Children[0].TagName);
        Assert.Equal("See All", cut.Find(".recent-view-all").TextContent.Trim());
    }

    [Fact]
    public void DesktopHeaderKeepsViewAllAndTheEngineOrder()
    {
        _read = type => Task.FromResult<DisplayRecentPageDto?>(new(type, [new("catalogue:a", DateTimeOffset.UtcNow, Card("Settling", true), null)], null, false));
        var cut = Render<RecentMediaFeed>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".recent-item")));
        Assert.Equal("View all", cut.Find(".recent-view-all").TextContent.Trim());
    }

    private static DisplayRecentPageDto Page(string type, string title, string[]? available = null) { var id = Guid.NewGuid(); var added = DateTimeOffset.UtcNow; return new(type, [new("view:" + id.ToString("N"), added, null, new(id, Guid.NewGuid(), title, "photo.jpg", "image", 800, 600, null, added))], null, false) { AvailableTypes = available ?? ["watch", "read", "view"] }; }
    private sealed class ProfileAuth(Guid profile) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim("tuvima:active_profile_id", profile.ToString("D"))], "test"))));
    }

    [Fact]
    public void EmptyLibraryHomeFeedShowsNoHeadingButAFilteredEmptyScopeStillExplainsItself()
    {
        var cut = Render<RecentMediaFeed>();
        cut.WaitForAssertion(() => Assert.Single(_requests));
        cut.WaitForAssertion(() => Assert.DoesNotContain("Recently Added", cut.Markup));

        cut.Render(p => p.Add(c => c.Type, "view"));
        cut.WaitForAssertion(() => Assert.Contains("No accessible recent additions", cut.Markup));
    }
}
