using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

/// <summary>Settings &gt; Profiles: the administrators' people list. Household administrators see their own household; server administrators see every one.</summary>
public sealed class ProfilesTabTests : AsyncBunitContext
{
    private static readonly Guid HouseholdId = Guid.NewGuid();
    private static readonly Guid OtherHouseholdId = Guid.NewGuid();
    private static readonly Guid OwnId = Guid.NewGuid();
    private static readonly Guid KidId = Guid.NewGuid();
    private static readonly Guid SamId = Guid.NewGuid();
    private static readonly Guid AlexId = Guid.NewGuid();

    private readonly List<ManagedProfileResponse> _managed = [];
    private readonly List<HouseholdResponse> _households = [];
    private readonly List<RequestRecord> _requests = [];
    private DashboardAuthorityResponse? _authority;
    private string? _addPersonDetail;
    private ConfirmedActionRunnerTests.SpyConfirmer _confirmer = new(confirmed: false);

    public ProfilesTabTests()
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
        Services.AddScoped<IItsYouConfirmer>(_ => _confirmer);
        Services.AddScoped<ConfirmedActionRunner>();
    }

    [Fact]
    public void Member_IsToldTheyCannotOpenProfiles_AndNothingIsRequested()
    {
        Setup(serverAdministrator: false, householdAdministrator: false);

        var cut = Render<ProfilesTab>();

        Assert.Contains("Not available", cut.Markup);
        Assert.Empty(cut.FindAll(".profiles-tab__list"));
        Assert.DoesNotContain(_requests, request => request.Path.StartsWith("/access/", StringComparison.Ordinal));
    }

    [Fact]
    public void HouseholdAdministrator_SeesOnlyTheirHouseholdsPeople_WithBadges_EditAndAddProfile_ButNoAddHousehold()
    {
        Setup(serverAdministrator: false, householdAdministrator: true);
        _households.Add(new HouseholdResponse(HouseholdId, "Home", 3, 1, DateTimeOffset.UtcNow));
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true), Person(KidId, "Robin", true), Person(SamId, "Sam", false)]);

        var cut = Render<ProfilesTab>();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".profiles-tab__line").Count));
        var rows = cut.FindAll(".profiles-tab__line");
        Assert.Contains("PIN", rows[2].TextContent);
        Assert.Contains("You", rows[0].TextContent);
        Assert.Contains("Household administrator", rows[0].TextContent);
        Assert.DoesNotContain("Server administrator", rows[0].TextContent);
        Assert.Contains("Kids", rows[1].TextContent);
        Assert.DoesNotContain("You", rows[1].TextContent);
        Assert.Equal(["Home"], cut.FindAll(".profiles-tab__group .settings-section-card__title").Select(title => title.TextContent));
        Assert.Equal("list", cut.Find(".profiles-tab__list").GetAttribute("role"));
        Assert.All(rows, row => Assert.Single(row.QuerySelectorAll("button"), button => button.TextContent.Trim() == "Edit"));
        Assert.Single(cut.FindAll(".profiles-tab__row--add"));

        // Only a server administrator adds households, and nobody switches profile from this page.
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Contains("Add household", StringComparison.Ordinal));
        Assert.DoesNotContain(cut.FindAll("button, a"), control => control.TextContent.Contains("Switch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ServerAdministrator_SeesEveryHouseholdGroupedByName_OwnFirst_WithAddHousehold()
    {
        Setup(serverAdministrator: true, householdAdministrator: false);
        _households.AddRange([
            new HouseholdResponse(OtherHouseholdId, "Cabin", 1, 1, DateTimeOffset.UtcNow),
            new HouseholdResponse(HouseholdId, "Home", 2, 1, DateTimeOffset.UtcNow),
        ]);
        _managed.AddRange([Person(AlexId, "Alex", false, OtherHouseholdId), Person(OwnId, "Test User", false), Person(SamId, "Sam", false)]);

        var cut = Render<ProfilesTab>();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".profiles-tab__line").Count));
        Assert.Equal(["Home", "Cabin"], cut.FindAll(".profiles-tab__group .settings-section-card__title").Select(title => title.TextContent));
        var cabin = cut.Find($"[data-household-group='{OtherHouseholdId}']");
        Assert.Contains("Alex", cabin.TextContent);
        Assert.DoesNotContain("Sam", cabin.TextContent);
        Assert.Single(cut.FindAll("button"), button => button.TextContent.Trim() == "Add household");
        Assert.Equal(2, cut.FindAll(".profiles-tab__row--add").Count);
        Assert.DoesNotContain(cut.FindAll("button, a"), control => control.TextContent.Contains("Switch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OnAPhone_ThePeopleAreReadOnly_WithNoAddHouseholdOrAddProfile_AndTheManageOnAComputerNote()
    {
        UsePhone();
        Setup(serverAdministrator: true, householdAdministrator: false);
        _households.Add(new HouseholdResponse(HouseholdId, "Home", 2, 1, DateTimeOffset.UtcNow));
        _managed.AddRange([Person(OwnId, "Test User", false), Person(SamId, "Sam", false)]);

        var cut = Render<ProfilesTab>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".profiles-tab__line").Count));
        Assert.Contains("Manage on a computer or tablet.", cut.Find("p[role='note']").TextContent);
        Assert.Empty(cut.FindAll(".profiles-tab__row--add"));
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Contains("Add household", StringComparison.Ordinal));
        Assert.Equal(2, cut.FindAll(".profiles-tab__edit").Count);
    }

    [Fact]
    public void AddHousehold_OpensTheHouseholdsPage_WithTheNameBoxOpen()
    {
        Setup(serverAdministrator: true, householdAdministrator: false);
        _households.Add(new HouseholdResponse(HouseholdId, "Home", 1, 1, DateTimeOffset.UtcNow));
        _managed.Add(Person(OwnId, "Test User", false));

        var cut = Render<ProfilesTab>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".profiles-tab__line")));
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Add household").Click();

        Assert.EndsWith("/settings/access/households?add=1", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void AddRow_DisappearsOnceTheHouseholdHasEightPeople()
    {
        Setup(serverAdministrator: false, householdAdministrator: true);
        _managed.Add(Person(OwnId, "Test User", false));
        for (var i = 1; i < 8; i++)
        {
            _managed.Add(Person(Guid.NewGuid(), $"Person {i}", false));
        }

        var cut = Render<ProfilesTab>();

        cut.WaitForAssertion(() => Assert.Equal(8, cut.FindAll(".profiles-tab__line").Count));
        Assert.Empty(cut.FindAll(".profiles-tab__row--add"));
    }

    [Fact]
    public void EditButton_OpensThatPersonsProfilePage()
    {
        Setup(serverAdministrator: false, householdAdministrator: true);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(SamId, "Sam", false)]);

        var cut = Render<ProfilesTab>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".profiles-tab__line").Count));
        cut.Find("button[aria-label='Edit Sam']").Click();

        Assert.EndsWith($"/settings/profiles/{SamId:D}", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void AddRow_OpensTheSheet_AndAddsThePersonToTheHouseholdAdministratorsOwnHousehold()
    {
        Setup(serverAdministrator: false, householdAdministrator: true);
        _households.Add(new HouseholdResponse(HouseholdId, "Home", 1, 1, DateTimeOffset.UtcNow));
        _managed.Add(Person(OwnId, "Test User", false));

        var cut = Render<ProfilesTab>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".profiles-tab__row--add")));
        cut.Find(".profiles-tab__row--add").Click();
        cut.WaitForAssertion(() => Assert.Equal("Add profile", cut.Find("#profile-editor-title").TextContent));
        cut.Find("input.profile-editor__name").Input("Sam");
        cut.Find(".who-button--primary").Click();

        cut.WaitForAssertion(() => Assert.Contains(_requests, request => request.Method == HttpMethod.Post && request.Path == $"/access/households/{HouseholdId:D}/people"));
    }

    [Fact]
    public void AddRow_ForAServerAdministrator_AddsToTheHouseholdWhoseRowWasChosen()
    {
        Setup(serverAdministrator: true, householdAdministrator: false);
        _households.AddRange([
            new HouseholdResponse(HouseholdId, "Home", 1, 1, DateTimeOffset.UtcNow),
            new HouseholdResponse(OtherHouseholdId, "Cabin", 1, 1, DateTimeOffset.UtcNow),
        ]);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(AlexId, "Alex", false, OtherHouseholdId)]);

        var cut = Render<ProfilesTab>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".profiles-tab__row--add").Count));
        cut.Find("button[aria-label='Add profile to Cabin']").Click();
        cut.WaitForAssertion(() => Assert.Equal("Add profile", cut.Find("#profile-editor-title").TextContent));
        cut.Find("input.profile-editor__name").Input("Casey");
        cut.Find(".who-button--primary").Click();

        cut.WaitForAssertion(() => Assert.Contains(_requests, request => request.Method == HttpMethod.Post && request.Path == $"/access/households/{OtherHouseholdId:D}/people"));
        Assert.DoesNotContain(_requests, request => request.Method == HttpMethod.Post && request.Path == $"/access/households/{HouseholdId:D}/people");
    }

    [Fact]
    public void HouseholdAdministrator_HasNoUsersPanel_AndNoDeleteUserAction()
    {
        Setup(serverAdministrator: false, householdAdministrator: true);
        _households.Add(new HouseholdResponse(HouseholdId, "Home", 3, 2, DateTimeOffset.UtcNow));
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true, email: "me@home.test"), Person(KidId, "Robin", true), Person(SamId, "Sam", false, email: "sam@home.test")]);

        var cut = Render<ProfilesTab>();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".profiles-tab__line").Count));
        Assert.DoesNotContain("Users in your household", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(cut.FindAll("button"), button => (button.GetAttribute("aria-label") ?? string.Empty).StartsWith("Delete user", StringComparison.Ordinal));
        Assert.DoesNotContain(_requests, request => request.Path == "/access/accounts");
    }

    [Fact]
    public void EachPerson_ShowsTheirSignInEmailReadOnly_OrSaysTheyHaveNone()
    {
        Setup(serverAdministrator: false, householdAdministrator: true);
        _households.Add(new HouseholdResponse(HouseholdId, "Home", 3, 2, DateTimeOffset.UtcNow));
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true, email: "me@home.test"), Person(KidId, "Robin", true), Person(SamId, "Sam", false, email: "sam@home.test")]);

        var cut = Render<ProfilesTab>();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".profiles-tab__line").Count));
        Assert.Equal("me@home.test", cut.Find($"[data-profile-signin='{OwnId}']").TextContent);
        Assert.Equal("No sign-in", cut.Find($"[data-profile-signin='{KidId}']").TextContent);
        Assert.Equal("sam@home.test", cut.Find($"[data-profile-signin='{SamId}']").TextContent);

        // Read-only: no control on the row changes a sign-in.
        var line = cut.Find($"[data-profile-id='{SamId}']");
        Assert.Single(line.QuerySelectorAll("button"));
        Assert.Equal("Edit", line.QuerySelector("button")!.TextContent.Trim());
    }

    [Fact]
    public void ServerAdministrator_SeesTheRoleBadgesOfEveryHouseholdsPeople()
    {
        Setup(serverAdministrator: true, householdAdministrator: false);
        _households.AddRange([
            new HouseholdResponse(OtherHouseholdId, "Cabin", 1, 1, DateTimeOffset.UtcNow),
            new HouseholdResponse(HouseholdId, "Home", 2, 1, DateTimeOffset.UtcNow),
        ]);
        _managed.AddRange([
            Person(OwnId, "Test User", false, householdAdmin: true, serverAdmin: true, email: "me@home.test"),
            Person(SamId, "Sam", false),
            Person(AlexId, "Alex", false, OtherHouseholdId, householdAdmin: true, email: "alex@cabin.test")]);

        var cut = Render<ProfilesTab>();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".profiles-tab__line").Count));
        var own = cut.Find($"[data-profile-id='{OwnId}']");
        Assert.NotNull(own.QuerySelector("[data-role='server-administrator']"));
        Assert.NotNull(own.QuerySelector("[data-role='household-administrator']"));
        var alex = cut.Find($"[data-profile-id='{AlexId}']");
        Assert.NotNull(alex.QuerySelector("[data-role='household-administrator']"));
        Assert.Null(alex.QuerySelector("[data-role='server-administrator']"));
        Assert.Null(cut.Find($"[data-profile-id='{SamId}']").QuerySelector("[data-role]"));
    }

    [Fact]
    public void AddProfile_AtEight_IsRefusedWithTheEnginesSentence()
    {
        Setup(serverAdministrator: false, householdAdministrator: true);
        _households.Add(new HouseholdResponse(HouseholdId, "Home", 1, 1, DateTimeOffset.UtcNow));
        _managed.Add(Person(OwnId, "Test User", false, householdAdmin: true, email: "me@home.test"));
        _addPersonDetail = "A household holds up to 8 people.";

        var cut = Render<ProfilesTab>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".profiles-tab__row--add")));
        cut.Find(".profiles-tab__row--add").Click();
        cut.WaitForAssertion(() => Assert.Equal("Add profile", cut.Find("#profile-editor-title").TextContent));
        cut.Find("input.profile-editor__name").Input("Sam");
        cut.Find(".who-button--primary").Click();

        cut.WaitForAssertion(() => Assert.Contains("A household holds up to 8 people.", cut.Find(".who-page__error").TextContent));
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

    private void UsePhone()
    {
        var device = new DeviceContextService(EngineApiClientStub.CreateDefault());
        device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true)).GetAwaiter().GetResult();
        Services.AddScoped(_ => device);
    }

    private void Setup(bool serverAdministrator, bool householdAdministrator)
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
        });
        Services.AddSingleton(api);

        var grants = new[] { OwnId, KidId, SamId }.Select(id => new AccountProfileGrantDto(id, id, "Test User", null,
            true, true, true, new ProfileAdminProtectionResponse(false, "None", null, 1), 1, DateTimeOffset.UtcNow)).ToList();
        string[] actions = serverAdministrator ? ["access.manage"] : householdAdministrator ? ["household.manage"] : [];
        string[] navigation = serverAdministrator ? ["settings.administration"] : [];
        var authority = new DashboardAuthorityResponse(OwnId, OwnId, true, true, 1, 1, serverAdministrator, serverAdministrator, null, 1,
            grants, navigation, actions);
        var session = new DashboardSessionAccessor();
        _authority = authority;
        session.Set("test-session-token", OwnId, OwnId, Guid.NewGuid(), authority);
        Services.AddScoped(_ => session);
    }

    private static ManagedProfileResponse Person(Guid id, string name, bool restricted, Guid? household = null,
        bool householdAdmin = false, bool serverAdmin = false, string? email = null) =>
        new(id, name, "#C9922E", null, DateTimeOffset.UtcNow.AddMinutes(id == OwnId ? -3 : 0), household ?? HouseholdId, restricted,
            IsServerAdmin: serverAdmin, IsHouseholdAdmin: householdAdmin,
            User: email is null ? null : new ManagedProfileUserDto(Guid.NewGuid(), email));

    private static string GetRepoFile(params string[] parts)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return Path.Combine([root, .. parts]);
    }

    private sealed record RequestRecord(HttpMethod Method, string Path, string Body);

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("http://engine.test") };
    }

    private sealed class ManagedHandler(ProfilesTabTests owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            owner._requests.Add(new RequestRecord(request.Method, path, body));

            if (request.Method == HttpMethod.Get && path == "/access/profiles")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(owner._managed) };
            }

            if (request.Method == HttpMethod.Get && path == "/access/households")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(owner._households) };
            }

            if (request.Method == HttpMethod.Post && path == "/auth/session/validate" && owner._authority is { } authority)
            {
                // The same person is still signed in with the same access when the page refreshes its authority.
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new SessionValidationResponse
                    {
                        SessionId = Guid.NewGuid(),
                        AccountId = authority.AccountId,
                        ActiveProfileId = authority.ActiveProfileId,
                        Authority = authority,
                    }),
                };
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/people", StringComparison.Ordinal) && owner._addPersonDetail is { } refusal)
            {
                return new HttpResponseMessage(HttpStatusCode.Conflict) { Content = JsonContent.Create(new { detail = refusal }) };
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/people", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(new ManagedProfileResponse(Guid.NewGuid(), "New", "#7C4DFF", null, DateTimeOffset.UtcNow)),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { }) };
        }
    }
}
