using System.Net;
using System.Net.Http.Json;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>Settings &gt; Profiles &gt; one person: the short list of rows for your own profile and, for administrators, anyone in the household.</summary>
public sealed class ProfileDetailPageTests : AsyncBunitContext
{
    private static readonly Guid HouseholdId = Guid.NewGuid();
    private static readonly Guid OwnId = Guid.NewGuid();
    private static readonly Guid KidId = Guid.NewGuid();
    private static readonly Guid SamId = Guid.NewGuid();

    private readonly List<ManagedProfileResponse> _managed = [];
    private string? _lastError;
    private bool _settingsAvailable = true;

    public ProfileDetailPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
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
    public void OwnProfile_ShowsEveryRow_WithLinksThatOmitTheProfileQuery()
    {
        Setup(actions: [], navigation: []);

        var cut = RenderFor(OwnId);

        cut.WaitForAssertion(() => Assert.Equal("Test User", cut.Find(".profile-detail__name").TextContent));
        Assert.Equal("Profile", cut.Find(".profile-detail__role").TextContent);
        Assert.Equal("details,sign-in,playback,listening,reading,subtitles",
            string.Join(",", cut.FindAll("[data-nav-row]").Select(row => row.GetAttribute("data-nav-row"))));
        Assert.Equal("/settings/playback", Row(cut, "playback").GetAttribute("href"));
        Assert.Equal("/settings/listening", Row(cut, "listening").GetAttribute("href"));
        Assert.Equal("/settings/reading", Row(cut, "reading").GetAttribute("href"));
        Assert.Equal("/settings/playback#subtitles", Row(cut, "subtitles").GetAttribute("href"));
        Assert.DoesNotContain("?profile=", cut.Markup);
        Assert.Empty(cut.FindAll("[data-nav-row='delete']"));
        Assert.Contains("You", cut.FindAll(".profile-detail__badge").Select(badge => badge.TextContent));
    }

    [Fact]
    public void Rows_ShowAOneLineStatusFromTheirSavedSettings()
    {
        Setup(actions: [], navigation: []);

        var cut = RenderFor(OwnId);

        cut.WaitForAssertion(() => Assert.Contains("Autoplay on", Row(cut, "playback").TextContent));
        Assert.Contains("Autoplay on · Skip 10 s / 30 s", Row(cut, "playback").TextContent);
        Assert.Contains("Skip 15 s / 15 s · Sleep 30 min", Row(cut, "listening").TextContent);
        Assert.Contains("Paginated", Row(cut, "reading").TextContent);
        Assert.Contains("English · Medium", Row(cut, "subtitles").TextContent);
        Assert.Contains("Everything", Row(cut, "details").TextContent);
    }

    [Fact]
    public void SignInRow_IsPresentButOpensNothing()
    {
        Setup(actions: [], navigation: []);

        var cut = RenderFor(OwnId);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='sign-in']")));
        var row = Row(cut, "sign-in");
        Assert.Equal("true", row.GetAttribute("aria-disabled"));
        Assert.Null(row.GetAttribute("href"));
        Assert.NotEqual("button", row.TagName.ToLowerInvariant());
    }

    [Fact]
    public void Administrator_OpeningAnotherPerson_GetsProfileQueryLinks_AndDelete()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(KidId, "Robin", true), Person(SamId, "Sam", false)]);

        var cut = RenderFor(KidId);

        cut.WaitForAssertion(() => Assert.Equal("Robin", cut.Find(".profile-detail__name").TextContent));
        Assert.Equal("Kids profile", cut.Find(".profile-detail__role").TextContent);
        Assert.Contains("Kids · PG", Row(cut, "details").TextContent);
        Assert.Equal($"/settings/playback?profile={KidId:D}", Row(cut, "playback").GetAttribute("href"));
        Assert.Equal($"/settings/listening?profile={KidId:D}", Row(cut, "listening").GetAttribute("href"));
        Assert.Equal($"/settings/reading?profile={KidId:D}", Row(cut, "reading").GetAttribute("href"));
        Assert.Equal($"/settings/playback?profile={KidId:D}#subtitles", Row(cut, "subtitles").GetAttribute("href"));
        Assert.Single(cut.FindAll("[data-nav-row='delete']"));
        Assert.DoesNotContain(cut.FindAll(".profile-detail__badge"), badge => badge.TextContent == "You");
    }

    [Fact]
    public void Administrator_OpeningTheirOwnProfile_HasNoDeleteRow_AndShowsAdmin()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(SamId, "Sam", false)]);

        var cut = RenderFor(OwnId);

        cut.WaitForAssertion(() => Assert.Equal("Administrator", cut.Find(".profile-detail__role").TextContent));
        Assert.Empty(cut.FindAll("[data-nav-row='delete']"));
        Assert.Contains("Admin", cut.FindAll(".profile-detail__badge").Select(badge => badge.TextContent));
        Assert.DoesNotContain("?profile=", cut.Markup);
    }

    [Fact]
    public void NonAdministrator_OpeningAnotherPerson_IsSentBackToTheProfilesList()
    {
        Setup(actions: [], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(SamId, "Sam", false)]);

        var cut = RenderFor(SamId);

        cut.WaitForAssertion(() => Assert.EndsWith("/settings/profiles", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal));
        Assert.DoesNotContain("Sam", cut.Markup);
    }

    [Fact]
    public void Administrator_OpeningSomeoneTheyCannotSee_IsSentBackToTheProfilesList()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.Add(Person(OwnId, "Test User", false));

        RenderFor(SamId);

        Assert.EndsWith("/settings/profiles", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusedSettings_ShowAnExplanationInsteadOfABlankPage()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(SamId, "Sam", false)]);
        _settingsAvailable = false;
        _lastError = "Response status code does not indicate success: 403 (Forbidden).";

        var cut = RenderFor(SamId);

        cut.WaitForAssertion(() => Assert.Contains("You can't change this person's settings.", cut.Markup));
        Assert.Equal("Sam", cut.Find(".profile-detail__name").TextContent);
        Assert.Empty(cut.FindAll("[data-nav-row='playback']"));
    }

    [Fact]
    public void ProfileDetailsRow_OpensTheEditorSheetForThatPerson()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(SamId, "Sam", false)]);

        var cut = RenderFor(SamId);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='details']")));
        Row(cut, "details").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Edit profile", cut.Find("#profile-editor-title").TextContent);
            Assert.Equal("Sam", cut.Find(".profile-editor__name").GetAttribute("value"));
        });
    }

    [Fact]
    public void DeleteRow_OpensTheSheetWhereDeletingIsConfirmed()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(SamId, "Sam", false)]);

        var cut = RenderFor(SamId);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='delete']")));
        Row(cut, "delete").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".profile-editor__delete")));
    }

    private IRenderedComponent<ProfileDetailPage> RenderFor(Guid profileId) =>
        Render<ProfileDetailPage>(parameters => parameters.Add(page => page.ProfileId, profileId));

    private static AngleSharp.Dom.IElement Row(IRenderedComponent<ProfileDetailPage> cut, string key) =>
        cut.Find($"[data-nav-row='{key}']");

    private void Setup(string[] actions, string[] navigation)
    {
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ =>
                Task.FromResult(new List<ProfileViewModel>
                {
                    new(OwnId, "Test User", "#C9922E", "Profile", DateTimeOffset.UtcNow),
                    new(KidId, "Robin", "#2E8BC9", "RestrictedProfile", DateTimeOffset.UtcNow, ContentLimit: "PG"),
                    new(SamId, "Sam", "#2EC98B", "Profile", DateTimeOffset.UtcNow),
                }));
            stub.SetHandler(nameof(IEngineApiClient.GetPlaybackSettingsAsync), args =>
                Task.FromResult<UserPlaybackSettingsDto?>(_settingsAvailable
                    ? UserPlaybackSettingsDto.CreateDefaults((Guid)args![0]!)
                    : null));
            stub.SetHandler("get_LastError", _ => _lastError);
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
        new(id, name, "#C9922E", null, DateTimeOffset.UtcNow.AddMinutes(id == OwnId ? -3 : 0), HouseholdId, restricted,
            ContentLimit: restricted ? "PG" : null);

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("http://engine.test") };
    }

    private sealed class ManagedHandler(ProfileDetailPageTests owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.RequestUri!.AbsolutePath == "/access/profiles"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(owner._managed) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { }) });
    }
}
