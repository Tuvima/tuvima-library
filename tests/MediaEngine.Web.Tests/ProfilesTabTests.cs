using System.Net;
using System.Net.Http.Json;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Components.Profiles;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>Settings &gt; Profiles: the open profile at a glance and, for administrators, the people in the household.</summary>
public sealed class ProfilesTabTests : AsyncBunitContext
{
    private static readonly Guid HouseholdId = Guid.NewGuid();
    private static readonly Guid OwnId = Guid.NewGuid();
    private static readonly Guid KidId = Guid.NewGuid();
    private static readonly Guid SamId = Guid.NewGuid();

    private readonly List<ManagedProfileResponse> _managed = [];
    private readonly List<string> _requests = [];
    private ProfileOverviewViewModel? _overview;

    public ProfilesTabTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _overview = new ProfileOverviewViewModel();
        Services.AddLogging();
        Services.AddNativeUiServices();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddSingleton<IHttpClientFactory>(new ClientFactory(new ManagedHandler(this)));
        Services.AddScoped<DashboardIdentityClient>();
        Services.AddScoped<IItsYouConfirmer>(_ => new ConfirmedActionRunnerTests.SpyConfirmer(confirmed: false));
        Services.AddScoped<ConfirmedActionRunner>();
    }

    [Fact]
    public void NonAdministrator_SeesTheirCardAndNoHouseholdList()
    {
        Setup(actions: [], navigation: []);

        var cut = Render<ProfilesTab>();

        cut.WaitForAssertion(() => Assert.Equal("Test User", cut.Find(".profiles-tab__name").TextContent));
        Assert.Equal("Profile", cut.Find(".profiles-tab__role").TextContent);
        Assert.Empty(cut.FindAll(".profiles-tab__people"));
        Assert.DoesNotContain("People in your household", cut.Markup);
        Assert.DoesNotContain(_requests, path => path.StartsWith("/access/profiles", StringComparison.Ordinal));
    }

    [Fact]
    public void HouseholdAdministrator_SeesRowsWithTheRightBadges_AndAnAddRow()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(KidId, "Robin", true), Person(SamId, "Sam", false)]);

        var cut = Render<ProfilesTab>();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".profiles-tab__row:not(.profiles-tab__row--add)").Count));
        var rows = cut.FindAll(".profiles-tab__row:not(.profiles-tab__row--add)");
        Assert.Contains("You", rows[0].TextContent);
        Assert.Contains("Admin", rows[0].TextContent);
        Assert.Contains("Kids", rows[1].TextContent);
        Assert.DoesNotContain("You", rows[1].TextContent);
        Assert.Single(cut.FindAll(".profiles-tab__row--add"));
        Assert.Equal("list", cut.Find(".profiles-tab__list").GetAttribute("role"));
    }

    [Fact]
    public void AddRow_DisappearsOnceTheHouseholdHasEightPeople()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.Add(Person(OwnId, "Test User", false));
        for (var i = 1; i < 8; i++)
        {
            _managed.Add(Person(Guid.NewGuid(), $"Person {i}", false));
        }

        var cut = Render<ProfilesTab>();

        cut.WaitForAssertion(() => Assert.Equal(8, cut.FindAll(".profiles-tab__row").Count));
        Assert.Empty(cut.FindAll(".profiles-tab__row--add"));
    }

    [Fact]
    public void TappingARow_OpensTheEditorSheetForThatPerson()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(SamId, "Sam", false)]);

        var cut = Render<ProfilesTab>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".profiles-tab__row:not(.profiles-tab__row--add)").Count));
        cut.FindAll(".profiles-tab__row:not(.profiles-tab__row--add)")[1].Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Edit profile", cut.Find("#profile-editor-title").TextContent);
            Assert.Equal("Sam", cut.Find(".profile-editor__name").GetAttribute("value"));
        });
    }

    [Fact]
    public void StatRow_ShowsDashesWhenTheOverviewIsUnavailable_AndNoGenreChips()
    {
        Setup(actions: [], navigation: []);
        _overview = null;

        var cut = Render<ProfilesTab>();

        cut.WaitForAssertion(() => Assert.Contains("profiles-tab__stats", cut.Markup));
        var values = cut.FindAll(".profiles-tab__stat dd").Take(3).Select(value => value.TextContent).ToList();
        Assert.All(values, value => Assert.Equal("—", value));
        Assert.Empty(cut.FindAll(".profiles-tab__genre"));
    }

    [Fact]
    public void GenreChips_AppearOnlyOnceThereIsEnoughActivity()
    {
        Setup(actions: [], navigation: []);
        _overview = Overview(recentActivity: 2);
        var sparse = Render<ProfilesTab>();
        sparse.WaitForAssertion(() => Assert.Contains("profiles-tab__stats", sparse.Markup));
        Assert.Empty(sparse.FindAll(".profiles-tab__genre"));

        _overview = Overview(recentActivity: 6);
        var busy = Render<ProfilesTab>();
        busy.WaitForAssertion(() => Assert.Equal(["Drama", "Sci-Fi", "Comedy"], busy.FindAll(".profiles-tab__genre").Select(chip => chip.TextContent)));
        Assert.Equal("3", busy.FindAll(".profiles-tab__stat dd")[0].TextContent);
    }

    [Fact]
    public void PickerManageButton_GoesToSettingsForAdministrators_AndStaysInPlaceOnTelevision()
    {
        var picker = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Pages", "ProfilePickerPage.razor"));
        var menu = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Navigation", "TopNavAccountMenu.razor"));

        Assert.Contains("OnClick=\"StartManagingAsync\"", picker, StringComparison.Ordinal);
        Assert.Contains("Navigation.NavigateTo(\"/settings/profiles\");", picker, StringComparison.Ordinal);
        Assert.Contains("_managing = true;", picker, StringComparison.Ordinal);
        Assert.Contains("\"television\"", picker, StringComparison.Ordinal);
        Assert.Contains("ManageProfilesHref = \"/settings/profiles\"", menu, StringComparison.Ordinal);
    }

    private void Setup(string[] actions, string[] navigation)
    {
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ =>
                Task.FromResult(new List<ProfileViewModel>
                {
                    new(OwnId, "Test User", "#C9922E", "Profile", DateTimeOffset.UtcNow),
                    new(KidId, "Robin", "#2E8BC9", "RestrictedProfile", DateTimeOffset.UtcNow),
                    new(SamId, "Sam", "#2EC98B", "Profile", DateTimeOffset.UtcNow, HasPin: true),
                }));
            stub.SetHandler(nameof(IEngineApiClient.GetProfileOverviewAsync), _ => Task.FromResult(_overview));
        });
        Services.AddSingleton(api);

        var grants = new[] { OwnId, KidId, SamId }.Select(id => new AccountProfileGrantDto(id, id, "Test User", null,
            true, true, true, new GrantAdminProtectionDto(false, "None", null, 1, false, null), 1, DateTimeOffset.UtcNow)).ToList();
        var authority = new DashboardAuthorityResponse(OwnId, OwnId, true, true, 1, 1, actions.Length > 0, actions.Length > 0, null, 1,
            grants, navigation, actions);
        var session = new DashboardSessionAccessor();
        session.Set("test-session-token", OwnId, OwnId, Guid.NewGuid(), authority);
        Services.AddScoped(_ => session);
    }

    private static ManagedProfileResponse Person(Guid id, string name, bool restricted) =>
        new(id, name, "#C9922E", null, DateTimeOffset.UtcNow.AddMinutes(id == OwnId ? -3 : 0), HouseholdId, restricted);

    private static ProfileOverviewViewModel Overview(int recentActivity)
    {
        var overview = new ProfileOverviewViewModel();
        overview.Stats.InProgress = 3;
        overview.Stats.Completed = 7;
        overview.Stats.ConsumedSeconds = 7500;
        overview.Stats.RecentActivity = recentActivity;
        overview.Stats.TopGenres = new Dictionary<string, int> { ["Comedy"] = 2, ["Drama"] = 9, ["Sci-Fi"] = 5, ["Horror"] = 1 };
        return overview;
    }

    private static string GetRepoFile(params string[] parts)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return Path.Combine([root, .. parts]);
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("http://engine.test") };
    }

    private sealed class ManagedHandler(ProfilesTabTests owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            owner._requests.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(request.RequestUri.AbsolutePath == "/access/profiles"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(owner._managed) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { }) });
        }
    }
}
