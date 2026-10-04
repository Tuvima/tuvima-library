using Bunit;
using System.Security.Claims;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Web.Components.MediaTiles;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
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
    private Func<string, Task<DisplayRecentPageDto?>> _read = type => Task.FromResult<DisplayRecentPageDto?>(new(type, [], null, false));
    public RecentMediaFeedInteractionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose; Services.AddLogging();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton<AuthenticationStateProvider>(new ProfileAuth(_profile));
        Services.AddSingleton(EngineApiClientStub.Create(stub => {
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => Task.FromResult(new List<ProfileViewModel> { new(_profile,"One","#aaa","RestrictedProfile",DateTimeOffset.UtcNow), new(_secondProfile,"Two","#bbb","RestrictedProfile",DateTimeOffset.UtcNow) }));
            stub.SetHandler(nameof(IEngineApiClient.GetDisplayRecentAsync), args => { var type = (string)args![0]!; _requests.Add(type); return _read(type); });
        }));
        Services.AddScoped<ActiveProfileSessionService>(); Services.AddScoped<UniverseStateContainer>(); Services.AddScoped<UIOrchestratorService>();
        Services.AddSingleton(new ViewMediaGrantService(new byte[32], TimeSpan.FromMinutes(1))); Services.AddScoped<RecentViewAssetAdapter>(); Services.AddScoped<UserProgressChangeNotifier>();
    }
    [Fact]
    public void ControlledFilterFetchesOnceAndKeepsSelectedScopeAndLink()
    {
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
        var cut=Render<RecentMediaFeed>(p=>p.Add(c=>c.Browse,true));
        cut.WaitForAssertion(()=>Assert.Single(_requests));
        await cut.InvokeAsync(()=>Services.GetRequiredService<UniverseStateContainer>().PushMediaAdded(new MediaAddedEvent(Guid.NewGuid(),null,"Book","New book")));
        cut.WaitForAssertion(()=>Assert.Equal(2,_requests.Count));
        await cut.InvokeAsync(()=>Services.GetRequiredService<UserProgressChangeNotifier>().Publish(_secondProfile,Guid.NewGuid()));
        Assert.Equal(2,_requests.Count);
        await cut.InvokeAsync(()=>Services.GetRequiredService<UserProgressChangeNotifier>().Publish(_profile,Guid.NewGuid()));
        cut.WaitForAssertion(()=>Assert.Equal(3,_requests.Count));
    }
    private static DisplayRecentPageDto Page(string type, string title) { var id = Guid.NewGuid(); var added = DateTimeOffset.UtcNow; return new(type, [new("view:"+id.ToString("N"), added, null, new(id,Guid.NewGuid(),title,"photo.jpg","image",800,600,null,added))],null,false); }
    private sealed class ProfileAuth(Guid profile) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim("tuvima:active_profile_id",profile.ToString("D"))], "test"))));
    }
}
