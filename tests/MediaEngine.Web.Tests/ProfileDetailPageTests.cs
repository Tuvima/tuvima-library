using System.Net;
using System.Net.Http.Json;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
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
    private static readonly Guid SamAccountId = Guid.NewGuid();
    private static readonly Guid OwnAccountId = Guid.NewGuid();

    private readonly List<RecordedRequest> _requests = [];

    private readonly List<ManagedProfileResponse> _managed = [];
    private string? _lastError;
    private bool _settingsAvailable = true;
    private bool _samHasPin;
    private bool _ownHasPin;
    private readonly Dictionary<Guid, ProfileAdminProtectionResponse> _protections = [];
    private DashboardAuthorityResponse? _authority;
    private HttpStatusCode _profileUpdateStatus = HttpStatusCode.OK;
    private string? _profileUpdateDetail;
    private HttpStatusCode _rolesStatus = HttpStatusCode.NoContent;
    private string? _rolesDetail;
    private HttpStatusCode _profileDeleteStatus = HttpStatusCode.NoContent;
    private string? _profileDeleteDetail;

    public ProfileDetailPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        Services.AddLocalization();
        Services.AddLocalization();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddSingleton<IHttpClientFactory>(new ClientFactory(new ManagedHandler(this)));
        Services.AddScoped<DashboardIdentityClient>();
        Services.AddScoped<IItsYouConfirmer>(_ => new ConfirmedActionRunnerTests.SpyConfirmer(confirmed: false));
        Services.AddScoped<ConfirmedActionRunner>();
        Services.AddSingleton<IUserPlaybackPreferencesAccessor, NoPlaybackPreferences>();
    }

    private sealed class NoPlaybackPreferences : IUserPlaybackPreferencesAccessor
    {
        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) => Task.FromResult<UserPlaybackSettingsDto?>(null);
        public void UpdateCache(UserPlaybackSettingsDto settings) { }
        public void Invalidate() { }
    }

    [Fact]
    public void OwnProfile_ShowsEveryRow_WithLinksThatOmitTheProfileQuery()
    {
        Setup(actions: [], navigation: []);

        var cut = RenderFor(OwnId);

        cut.WaitForAssertion(() => Assert.Equal("Test User", cut.Find(".profile-detail__name").TextContent));
        Assert.Equal("Profile", cut.Find(".profile-detail__role").TextContent);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='devices']")));
        // A member's own Profile tab has no Profile details row: the editor saves through administrator-only Engine actions.
        Assert.Equal("sign-in,devices",
            string.Join(",", cut.FindAll("[data-nav-row]").Select(row => row.GetAttribute("data-nav-row"))));
        Assert.Equal("/settings/security", Row(cut, "sign-in").GetAttribute("href"));
        Assert.Equal("/settings/security/devices", Row(cut, "devices").GetAttribute("href"));
        Assert.DoesNotContain("?profile=", cut.Markup);
        Assert.Empty(cut.FindAll("[data-nav-row='delete']"));
        Assert.Contains("You", cut.FindAll(".profile-detail__badge").Select(badge => badge.TextContent));
    }

    [Fact]
    public void PreferencesTab_HoldsWatchingListeningReadingAndHome_OnYourOwnProfile()
    {
        Setup(actions: [], navigation: []);

        var cut = RenderFor(OwnId, "preferences");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("section#playback")));
        Assert.Equal(["Watching", "Listening", "Reading", "Home"], cut.FindAll(".profile-detail__section-heading").Select(heading => heading.TextContent));
        Assert.Equal("preferences", cut.Find("[data-profile-section='detail']").GetAttribute("data-profile-tab"));
        // Preferences are the only content of this tab: no sign-in, roles or access rows.
        Assert.Empty(cut.FindAll("[data-nav-row]"));
    }

    [Fact]
    public void PreferencesTab_OnSomeoneElsesProfile_HasNoHomeSection()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(SamId, "Sam", false)]);

        var cut = RenderFor(SamId, "preferences");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("section#playback")));
        Assert.Equal(["Watching", "Listening", "Reading"], cut.FindAll(".profile-detail__section-heading").Select(heading => heading.TextContent));
    }

    [Fact]
    public void ProfileDetailsRow_IsHiddenFromAMember_OnTheirOwnProfile()
    {
        Setup(actions: [], navigation: []);

        var cut = RenderFor(OwnId);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='devices']")));
        Assert.Empty(cut.FindAll("[data-nav-row='details']"));
    }

    [Fact]
    public void ProfileDetailsRow_IsShownToAHouseholdAdministrator_OnTheirOwnProfile()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(SamId, "Sam", false)]);

        var cut = RenderFor(OwnId);

        cut.WaitForAssertion(() => Assert.Contains("Member", Row(cut, "details").TextContent));
    }

    [Fact]
    public void ProfileDetailsRow_IsShownToAServerAdministrator()
    {
        Setup(actions: [], navigation: ["settings.administration"]);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(SamId, "Sam", false)]);

        var cut = RenderFor(SamId);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='details']")));
    }

    [Fact]
    public void SignInRow_OnYourOwnProfile_LinksToSecurity()
    {
        Setup(actions: [], navigation: []);

        var cut = RenderFor(OwnId);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='sign-in']")));
        var row = Row(cut, "sign-in");
        Assert.Equal("/settings/security", row.GetAttribute("href"));
        Assert.Null(row.GetAttribute("aria-disabled"));
        Assert.Contains("Managed in Security", row.TextContent);
    }

    [Fact]
    public void Administrator_OpeningAnotherPerson_GetsProfileQueryLinks_AndDelete()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(KidId, "Robin", true), Person(SamId, "Sam", false)]);

        var cut = RenderFor(KidId);

        cut.WaitForAssertion(() => Assert.Equal("Robin", cut.Find(".profile-detail__name").TextContent));
        Assert.Equal("Kids profile", cut.Find(".profile-detail__role").TextContent);
        Assert.Contains("Kids", Row(cut, "details").TextContent);
        Assert.DoesNotContain(cut.FindAll(".profile-detail__badge"), badge => badge.TextContent == "You");

        // Delete profile is on the Access & safety tab.
        var access = RenderFor(KidId, "access");
        access.WaitForAssertion(() => Assert.Single(access.FindAll("[data-nav-row='delete']")));
    }

    [Fact]
    public void Administrator_OpeningTheirOwnProfile_HasNoDeleteRow_AndShowsTheirRole()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true), Person(SamId, "Sam", false)]);

        var cut = RenderFor(OwnId, "access");

        cut.WaitForAssertion(() => Assert.Equal("Household administrator", cut.Find(".profile-detail__role").TextContent));
        Assert.Empty(cut.FindAll("[data-nav-row='delete']"));
        Assert.Contains("Household administrator", cut.FindAll(".profile-detail__badge").Select(badge => badge.TextContent));
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

        var cut = RenderFor(SamId, "preferences");

        cut.WaitForAssertion(() => Assert.Contains("You can't change this person's settings.", cut.Markup));
        Assert.Equal("Sam", cut.Find(".profile-detail__name").TextContent);
        Assert.Empty(cut.FindAll("section#playback"));
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

        var cut = RenderFor(SamId, "access");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='delete']")));
        Row(cut, "delete").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".profile-editor__delete")));
        // The Delete row skips the form: the sheet opens on the photo question and the red Delete button.
        Assert.NotEmpty(cut.FindAll(".profile-editor__confirm"));
    }

    [Fact]
    public void DeleteRefusedWithAConflictAndNoSentence_ExplainsTheUsualReasons()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(SamId, "Sam", false)]);
        _profileDeleteStatus = HttpStatusCode.Conflict;

        var cut = RenderFor(SamId, "access");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='delete']")));
        Row(cut, "delete").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".profile-editor__confirm")));
        cut.Find(".profile-editor__confirm .who-button--danger").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(_requests, request => request.Method == HttpMethod.Delete && request.Path == $"/access/profiles/{SamId:D}");
            Assert.Contains("A household always keeps one household administrator", cut.Find(".who-page__error").TextContent);
        });
    }

    [Fact]
    public void DeleteRefusedWithAnEngineSentence_ShowsThatSentenceInsteadOfTheGenericLine()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(SamId, "Sam", false)]);
        _profileDeleteStatus = HttpStatusCode.Conflict;
        _profileDeleteDetail = "This is the last administrator who can sign in. Make someone else an administrator first.";

        var cut = RenderFor(SamId, "access");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='delete']")));
        Row(cut, "delete").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".profile-editor__confirm")));
        cut.Find(".profile-editor__confirm .who-button--danger").Click();

        cut.WaitForAssertion(() =>
        {
            var message = cut.Find(".who-page__error").TextContent;
            Assert.Contains("This is the last administrator who can sign in.", message);
            Assert.DoesNotContain("A household always keeps one household administrator, and you", message);
        });
    }

    [Fact]
    public void ProfileDetailsRow_OpensTheEditorWithoutTheDeleteConfirmation()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(SamId, "Sam", false)]);

        var cut = RenderFor(SamId);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='details']")));
        Row(cut, "details").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".profile-editor__delete")));
        Assert.Empty(cut.FindAll(".profile-editor__confirm"));
    }

    [Fact]
    public void DevicesRow_ForAnotherPerson_OpensTheDevicesPageNarrowedToThem()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(SamId, "Sam", false)]);

        var cut = RenderFor(SamId);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='devices']")));
        var devices = Row(cut, "devices");
        Assert.Equal($"/settings/security/devices?profile={SamId:D}", devices.GetAttribute("href"));
        Assert.Equal("a", devices.TagName.ToLowerInvariant());
    }

    [Fact]
    public void ViewActivity_ForAnAdministrator_OpensTheActivityPageNarrowedToThatPerson()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(SamId, "Sam", false)]);

        var cut = RenderFor(SamId);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='activity']")));
        var activity = Row(cut, "activity");
        Assert.Equal("a", activity.TagName.ToLowerInvariant());
        Assert.Contains("View activity", activity.TextContent, StringComparison.Ordinal);
        Assert.Equal($"/settings/overview/activity?scope=profile:{SamId:D}", activity.GetAttribute("href"));
    }

    [Fact]
    public void ViewActivity_OnAnAdministratorsOwnPage_OpensTheirOwnActivityWithNoScope()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false)]);

        var cut = RenderFor(OwnId);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='activity']")));
        Assert.Equal("/settings/overview/activity", Row(cut, "activity").GetAttribute("href"));
    }

    [Fact]
    public void ViewActivity_IsNotOfferedToAMember()
    {
        Setup(actions: [], navigation: []);

        var cut = RenderFor(OwnId);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='devices']")));
        Assert.Empty(cut.FindAll("[data-nav-row='activity']"));
        Assert.DoesNotContain("View activity", cut.Markup, StringComparison.Ordinal);
    }

    // ---- sign-in: read-only here, managed by the server administrator in Users & Households

    [Fact]
    public void SignInRow_ForAnotherPerson_ShowsTheirEmailReadOnly_AndLinksAServerAdministratorToUsersAndSignIn()
    {
        Setup(actions: ["access.manage"], navigation: ["settings.administration"]);
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true, serverAdmin: true, signInEmail: "me@home.test"), Person(SamId, "Sam", false, signInEmail: "sam@home.test")]);

        var cut = RenderFor(SamId);

        cut.WaitForAssertion(() => Assert.Contains("sam@home.test", Row(cut, "sign-in").TextContent));
        var row = Row(cut, "sign-in");
        Assert.Contains("Managed by the server administrator in Users & Households", row.TextContent);
        Assert.Equal("/settings/access/users", row.GetAttribute("href"));
        Assert.Empty(cut.FindAll(".sign-in-sheet"));
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Contains("Give own sign-in", StringComparison.Ordinal));
    }

    [Fact]
    public void SignInRow_ForAnotherPerson_IsPlainTextForAHouseholdAdministrator()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true, signInEmail: "me@home.test"), Person(SamId, "Sam", false, signInEmail: "sam@home.test")]);

        var cut = RenderFor(SamId);

        cut.WaitForAssertion(() => Assert.Contains("sam@home.test", Row(cut, "sign-in").TextContent));
        var row = Row(cut, "sign-in");
        Assert.Equal("true", row.GetAttribute("aria-disabled"));
        Assert.Null(row.GetAttribute("href"));
    }

    [Fact]
    public void SignInRow_SaysNoSignIn_ForAPersonWhoOpensOnlyBySwitching()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true, signInEmail: "me@home.test"), Person(SamId, "Sam", false)]);

        var cut = RenderFor(SamId);

        cut.WaitForAssertion(() => Assert.Contains("No sign-in", Row(cut, "sign-in").TextContent));
    }

    // ---- roles

    [Fact]
    public void HouseholdAdministrator_SeesTheHouseholdAdministratorSwitch_ButNoServerAdministratorSwitch()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true, signInEmail: "me@home.test"), Person(SamId, "Sam", false, signInEmail: "sam@home.test")]);

        var cut = RenderFor(SamId);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-profile-section='roles']")));
        var household = RoleSwitch(cut, "household-administrator");
        Assert.False(household.HasAttribute("checked"));
        Assert.False(household.HasAttribute("disabled"));
        Assert.Empty(cut.FindAll("[data-role-switch='server-administrator']"));
    }

    [Fact]
    public void ServerAdministrator_SeesBothSwitches_ForSomeoneInTheLibrarysOwnHousehold()
    {
        Setup(actions: ["access.manage"], navigation: ["settings.administration"]);
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true, serverAdmin: true, signInEmail: "me@home.test"), Person(SamId, "Sam", false, signInEmail: "sam@home.test")]);

        var cut = RenderFor(SamId);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-role-switch='server-administrator']")));
        Assert.False(RoleSwitch(cut, "server-administrator").HasAttribute("disabled"));
        Assert.False(RoleSwitch(cut, "household-administrator").HasAttribute("disabled"));
    }

    [Fact]
    public void ServerAdministratorSwitch_IsNotOffered_ForSomeoneInAnotherHousehold()
    {
        var otherHousehold = Guid.NewGuid();
        Setup(actions: ["access.manage"], navigation: ["settings.administration"]);
        _managed.AddRange([
            Person(OwnId, "Test User", false, householdAdmin: true, serverAdmin: true, signInEmail: "me@home.test"),
            Person(SamId, "Sam", false, signInEmail: "sam@cabin.test", household: otherHousehold)]);

        var cut = RenderFor(SamId);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-role-switch='household-administrator']")));
        Assert.Empty(cut.FindAll("[data-role-switch='server-administrator']"));
    }

    [Fact]
    public void HouseholdAdministratorSwitch_IsDisabledWithAReason_WhenThePersonHasNoSignIn()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true, signInEmail: "me@home.test"), Person(SamId, "Sam", false)]);

        var cut = RenderFor(SamId);

        cut.WaitForAssertion(() => Assert.True(RoleSwitch(cut, "household-administrator").HasAttribute("disabled")));
        Assert.Contains("needs a sign-in", cut.Find("[data-role-switch='household-administrator']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void HouseholdAdministratorSwitch_IsDisabledWithAReason_ForAKidsProfile()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true, signInEmail: "me@home.test"), Person(KidId, "Robin", true, signInEmail: "robin@home.test")]);

        var cut = RenderFor(KidId);

        cut.WaitForAssertion(() => Assert.True(RoleSwitch(cut, "household-administrator").HasAttribute("disabled")));
        Assert.Contains("A Kids profile can't look after a household.", cut.Find("[data-role-switch='household-administrator']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void HouseholdAdministratorSwitch_IsOnButDisabledWithAReason_ForTheLastHouseholdAdministrator()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true, signInEmail: "me@home.test"), Person(SamId, "Sam", false, signInEmail: "sam@home.test")]);

        var cut = RenderFor(OwnId);

        cut.WaitForAssertion(() => Assert.True(RoleSwitch(cut, "household-administrator").HasAttribute("disabled")));
        Assert.True(RoleSwitch(cut, "household-administrator").HasAttribute("checked"));
        Assert.Contains("A household always keeps one household administrator.", cut.Find("[data-role-switch='household-administrator']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ServerAdministratorSwitch_IsDisabledWithAReason_WhenThePersonHasNoSignIn()
    {
        Setup(actions: ["access.manage"], navigation: ["settings.administration"]);
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true, serverAdmin: true, signInEmail: "me@home.test"), Person(SamId, "Sam", false)]);

        var cut = RenderFor(SamId);

        cut.WaitForAssertion(() => Assert.True(RoleSwitch(cut, "server-administrator").HasAttribute("disabled")));
        Assert.Contains("needs a sign-in with an email and password", cut.Find("[data-role-switch='server-administrator']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TurningOnHouseholdAdministrator_SendsTheWantedStateOfBothRoles()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true, signInEmail: "me@home.test"), Person(SamId, "Sam", false, signInEmail: "sam@home.test")]);

        var cut = RenderFor(SamId);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-role-switch='household-administrator']")));
        cut.InvokeAsync(() => RoleSwitch(cut, "household-administrator").Change(true));

        cut.WaitForAssertion(() => Assert.Single(_requests, request => request.Method == HttpMethod.Put && request.Path == $"/access/profiles/{SamId:D}/roles"));
        var put = _requests.Single(request => request.Path.EndsWith("/roles", StringComparison.Ordinal));
        using var body = System.Text.Json.JsonDocument.Parse(put.Body);
        Assert.True(body.RootElement.GetProperty("is_household_admin").GetBoolean());
        Assert.False(body.RootElement.GetProperty("is_server_admin").GetBoolean());
    }

    [Fact]
    public void RoleChangeRefused_ShowsTheEnginesSentenceAsItIs()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true, signInEmail: "me@home.test"), Person(SamId, "Sam", false, signInEmail: "sam@home.test")]);
        _rolesStatus = HttpStatusCode.Conflict;
        _rolesDetail = "A household administrator needs a sign-in. Attach one to this profile first.";

        var cut = RenderFor(SamId);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-role-switch='household-administrator']")));
        cut.InvokeAsync(() => RoleSwitch(cut, "household-administrator").Change(true));

        cut.WaitForAssertion(() => Assert.Contains("A household administrator needs a sign-in. Attach one to this profile first.", cut.Find("[data-profile-section='roles'] [role='alert']").TextContent));
    }

    [Fact]
    public void RolesAndAccess_AreNotOffered_ToAMember()
    {
        Setup(actions: [], navigation: []);
        var member = RenderFor(OwnId);
        member.WaitForAssertion(() => Assert.NotEmpty(member.FindAll("[data-nav-row='devices']")));
        Assert.Empty(member.FindAll("[data-profile-section='roles']"));

        var access = RenderFor(OwnId, "access");
        access.WaitForAssertion(() => Assert.Equal("access", access.Find("[data-profile-section='detail']").GetAttribute("data-profile-tab")));
        Assert.Empty(access.FindAll("[data-profile-section='access']"));
        // A profile carries no library lists or area switches of its own: a library is on or off for the household.
        Assert.Empty(access.FindAll("[data-access-section]"));
        Assert.Empty(access.FindAll("[data-library-id]"));
    }

    // ---- the Kid's profile switch (library access D9)

    [Fact]
    public async Task KidsProfileSwitch_IsOffByDefault_AndTurningItOnSendsIsKidsWithTheirNameAndLook()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true, signInEmail: "me@home.test"), Person(SamId, "Sam", false, signInEmail: "sam@home.test")]);
        var cut = await OpenAccessAsync();

        var kid = KidSwitch(cut);
        Assert.False(kid.HasAttribute("checked"));
        Assert.False(kid.HasAttribute("disabled"));
        Assert.Empty(ProfileUpdates());
        Assert.Contains("Kid's profile", cut.Find("[data-kid-switch]").TextContent);
        // No rating dropdown or "Allow unrated" per person.
        Assert.Empty(cut.FindAll("[data-profile-section='access'] select"));
        Assert.DoesNotContain("unrated", cut.Find("[data-profile-section='access']").TextContent, StringComparison.OrdinalIgnoreCase);

        await cut.InvokeAsync(() => kid.Change(true));

        cut.WaitForAssertion(() => Assert.Single(ProfileUpdates()));
        var update = Assert.Single(ProfileUpdates());
        Assert.Equal($"/access/profiles/{SamId:D}", update.Path);
        using var body = System.Text.Json.JsonDocument.Parse(update.Body);
        Assert.True(body.RootElement.GetProperty("is_kids").GetBoolean());
        Assert.Equal("Sam", body.RootElement.GetProperty("display_name").GetString());
        cut.WaitForAssertion(() => Assert.True(KidSwitch(cut).HasAttribute("checked")));
    }

    [Fact]
    public async Task KidsProfileSwitch_OnAKidsProfile_TurnsOffAndSendsIsKidsFalse()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true, signInEmail: "me@home.test"), Person(KidId, "Robin", true)]);
        var cut = RenderFor(KidId, "access");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-kid-switch]")));

        Assert.True(KidSwitch(cut).HasAttribute("checked"));
        await cut.InvokeAsync(() => KidSwitch(cut).Change(false));

        cut.WaitForAssertion(() => Assert.Single(ProfileUpdates()));
        using var body = System.Text.Json.JsonDocument.Parse(Assert.Single(ProfileUpdates()).Body);
        Assert.False(body.RootElement.GetProperty("is_kids").GetBoolean());
    }

    [Fact]
    public async Task KidsProfileSwitch_IsDisabledForAnAdministrator()
    {
        Setup(actions: ["access.manage"], navigation: ["settings.administration"]);
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true, serverAdmin: true, signInEmail: "me@home.test"),
            Person(SamId, "Sam", false, householdAdmin: true, signInEmail: "sam@home.test")]);
        var cut = await OpenAccessAsync();

        Assert.True(KidSwitch(cut).HasAttribute("disabled"));
    }

    [Fact]
    public async Task KidsProfileSwitch_WhenTheEngineRefuses_ShowsItsSentence()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true, signInEmail: "me@home.test"), Person(SamId, "Sam", false, signInEmail: "sam@home.test")]);
        _profileUpdateStatus = HttpStatusCode.Conflict;
        _profileUpdateDetail = "An administrator can't be a Kid's profile.";
        var cut = await OpenAccessAsync();

        await cut.InvokeAsync(() => KidSwitch(cut).Change(true));

        cut.WaitForAssertion(() => Assert.Contains("An administrator can't be a Kid's profile.", cut.Find("[data-kid-error]").TextContent));
        Assert.False(KidSwitch(cut).HasAttribute("checked"));
    }

    // ---- phones read the profile; roles, access and Delete profile are changed on a computer or tablet

    [Fact]
    public void OnAPhone_RolesAccessAndDeleteAreHidden_WithTheManageOnAComputerNote()
    {
        UsePhone();
        Setup(actions: ["access.manage"], navigation: ["settings.administration"]);
        _managed.AddRange([
            Person(OwnId, "Test User", false, householdAdmin: true, serverAdmin: true, signInEmail: "me@home.test"),
            Person(SamId, "Sam", false, signInEmail: "sam@home.test")]);

        var cut = RenderFor(SamId);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='devices']")));
        Assert.Empty(cut.FindAll("[data-profile-section='roles']"));
        Assert.Empty(cut.FindAll("[data-profile-section='access']"));
        Assert.Empty(cut.FindAll("[data-kid-switch]"));
        Assert.Empty(cut.FindAll("[data-nav-row='delete']"));
        Assert.Contains("Manage on a computer or tablet.", cut.Find("p[role='note']").TextContent);

        var access = RenderFor(SamId, "access");
        access.WaitForAssertion(() => Assert.Contains("Manage on a computer or tablet.", access.Find("p[role='note']").TextContent));
        Assert.Empty(access.FindAll("[data-profile-section='access']"));
        Assert.Empty(access.FindAll("[data-nav-row='delete']"));
    }

    [Fact]
    public void OnADesktop_RolesAccessAndDeleteAreOffered_WithoutThePhoneNote()
    {
        Setup(actions: ["access.manage"], navigation: ["settings.administration"]);
        _managed.AddRange([
            Person(OwnId, "Test User", false, householdAdmin: true, serverAdmin: true, signInEmail: "me@home.test"),
            Person(SamId, "Sam", false, signInEmail: "sam@home.test")]);

        var cut = RenderFor(SamId);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-profile-section='roles']")));
        Assert.DoesNotContain("Manage on a computer or tablet", cut.Markup);

        var access = RenderFor(SamId, "access");
        access.WaitForAssertion(() => Assert.NotEmpty(access.FindAll("[data-profile-section='access']")));
        Assert.NotEmpty(access.FindAll("[data-nav-row='delete']"));
    }

    [Fact]
    public void AccessRow_ForAServerAdministratorsProfile_SaysEverythingAndOpensNothing()
    {
        Setup(actions: ["access.manage"], navigation: ["settings.administration"]);
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true, serverAdmin: true, signInEmail: "me@home.test"), Person(SamId, "Mom", false, serverAdmin: true, signInEmail: "mom@home.test")]);

        var cut = RenderFor(SamId, "access");

        cut.WaitForAssertion(() => Assert.Contains("Everything", Row(cut, "access").TextContent));
        Assert.Equal("true", Row(cut, "access").GetAttribute("aria-disabled"));
        Assert.Empty(cut.FindAll("[data-access-section]"));
    }

    // ---- delete profile

    [Fact]
    public void DeleteConfirmation_NamesWhatGoes_SaysTheSignInGoesToo_AndSelectsMoveToShared()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true, signInEmail: "me@home.test"), Person(SamId, "Sam", false, signInEmail: "sam@home.test")]);

        var cut = RenderFor(SamId, "access");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='delete']")));
        Row(cut, "delete").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".profile-editor__confirm")));
        var text = cut.Find(".profile-editor__confirm").TextContent;
        Assert.Contains("progress, bookmarks, lists and history", text, StringComparison.Ordinal);
        Assert.Contains("Their sign-in (sam@home.test) is removed too.", text, StringComparison.Ordinal);
        Assert.Contains("Move to Shared", text, StringComparison.Ordinal);
        var radios = cut.FindAll(".profile-editor__confirm input[type='radio']");
        Assert.True(radios[0].HasAttribute("checked"));
        Assert.False(radios[1].HasAttribute("checked"));
    }

    [Fact]
    public void DeleteConfirmation_ForAPersonWithoutASignIn_DoesNotMentionOne()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true, signInEmail: "me@home.test"), Person(SamId, "Sam", false)]);

        var cut = RenderFor(SamId, "access");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='delete']")));
        Row(cut, "delete").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".profile-editor__confirm")));
        Assert.DoesNotContain("sign-in", cut.Find(".profile-editor__confirm").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Deleting_MovesThePhotosToSharedByDefault()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true, signInEmail: "me@home.test"), Person(SamId, "Sam", false, signInEmail: "sam@home.test")]);

        var cut = RenderFor(SamId, "access");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='delete']")));
        Row(cut, "delete").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".profile-editor__confirm")));
        cut.Find(".profile-editor__confirm .who-button--danger").Click();

        cut.WaitForAssertion(() => Assert.Contains(_requests, request => request.Method == HttpMethod.Delete && request.Path == $"/access/profiles/{SamId:D}" && request.Query == "?photos=move"));
    }

    [Fact]
    public void Deleting_WithTheDeleteChoice_AsksTheEngineToDeleteThePhotos()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true, signInEmail: "me@home.test"), Person(SamId, "Sam", false, signInEmail: "sam@home.test")]);

        var cut = RenderFor(SamId, "access");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='delete']")));
        Row(cut, "delete").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".profile-editor__confirm")));
        cut.FindAll(".profile-editor__confirm input[type='radio']")[1].Change(true);
        cut.Find(".profile-editor__confirm .who-button--danger").Click();

        cut.WaitForAssertion(() => Assert.Contains(_requests, request => request.Method == HttpMethod.Delete && request.Query == "?photos=delete"));
    }

    [Fact]
    public void DeleteRow_IsNotOffered_ForTheLastHouseholdAdministrator()
    {
        Setup(actions: ["access.manage"], navigation: ["settings.administration"]);
        _managed.AddRange([Person(OwnId, "Test User", false, serverAdmin: true, signInEmail: "me@home.test"), Person(SamId, "Sam", false, householdAdmin: true, signInEmail: "sam@home.test")]);

        var cut = RenderFor(SamId, "access");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-profile-section='access']")));
        Assert.Empty(cut.FindAll("[data-nav-row='delete']"));
    }

    [Fact]
    public void DeleteRow_IsNotOffered_ForAServerAdministratorsProfile_ToAHouseholdAdministrator()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true, signInEmail: "me@home.test"), Person(SamId, "Mom", false, serverAdmin: true, householdAdmin: true, signInEmail: "mom@home.test")]);

        var cut = RenderFor(SamId, "access");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-profile-section='access']")));
        Assert.Empty(cut.FindAll("[data-nav-row='delete']"));
    }

    [Fact]
    public void DeleteRow_IsOffered_ForAnotherHouseholdAdministrator_WhileAnotherRemains()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([
            Person(OwnId, "Test User", false, householdAdmin: true, signInEmail: "me@home.test"),
            Person(SamId, "Sam", false, householdAdmin: true, signInEmail: "sam@home.test")]);

        var cut = RenderFor(SamId, "access");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='delete']")));
    }

    // ---- the administrator PIN lives on the profile

    [Fact]
    public void AdministratorPin_OnYourOwnProfile_IsOffByDefault_AndTurningItOnPutsTheSettingOnThatProfile()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _ownHasPin = true;
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true, signInEmail: "me@home.test"), Person(SamId, "Sam", false)]);
        _protections[OwnId] = Protection(protectionEnabled: false);

        var cut = RenderFor(OwnId, "access");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-profile-section='pin-protection']")));

        Assert.Empty(cut.FindAll("[data-profile-section='pin-protection'] input[type='password']"));
        Assert.False(AdminSwitch(cut, "Ask for a PIN to open administrator settings").HasAttribute("checked"));
        Assert.Empty(ProtectionSaves());

        cut.InvokeAsync(() => AdminSwitch(cut, "Ask for a PIN to open administrator settings").Change(true));

        cut.WaitForAssertion(() => Assert.Contains("Administrator settings now ask for Test User", cut.Markup));
        var request = Assert.Single(ProtectionSaves());
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal($"/access/profiles/{OwnId:D}/admin-protection", request.Path);
        var payload = System.Text.Json.JsonSerializer.Deserialize<SetProfileAdminProtectionRequest>(request.Body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.True(payload.Enabled);
        Assert.Equal("FixedDuration", payload.UnlockMode);
        Assert.Equal(30, payload.UnlockMinutes);
    }

    [Fact]
    public void AdministratorPin_CannotBeTurnedOn_ForAProfileWithoutAPin_AndSaysWhatToDoFirst()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _ownHasPin = false;
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true, signInEmail: "me@home.test"), Person(SamId, "Sam", false)]);
        _protections[OwnId] = Protection(protectionEnabled: false);

        var cut = RenderFor(OwnId, "access");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-profile-section='pin-protection']")));
        Assert.Contains("Needs a PIN on this profile", cut.Markup, StringComparison.Ordinal);

        cut.InvokeAsync(() => AdminSwitch(cut, "Ask for a PIN to open administrator settings").Change(true));

        cut.WaitForAssertion(() => Assert.Contains("Set a PIN for this profile first", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(ProtectionSaves());
    }

    [Fact]
    public void AdministratorPin_ChoosingAnotherDuration_SavesItStraightAway()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _ownHasPin = true;
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true, signInEmail: "me@home.test"), Person(SamId, "Sam", false)]);
        _protections[OwnId] = Protection(protectionEnabled: true);

        var cut = RenderFor(OwnId, "access");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("select[aria-label='How long the PIN unlocks administrator settings for Test User']")));
        cut.Find("select[aria-label='How long the PIN unlocks administrator settings for Test User']").Change("FixedDuration:60");

        cut.WaitForAssertion(() => Assert.Single(ProtectionSaves()));
        var request = Assert.Single(ProtectionSaves());
        var payload = System.Text.Json.JsonSerializer.Deserialize<SetProfileAdminProtectionRequest>(request.Body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.True(payload.Enabled);
        Assert.Equal(60, payload.UnlockMinutes);
    }

    [Fact]
    public void AdministratorPin_CanBeTurnedOff()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _ownHasPin = true;
        _managed.AddRange([Person(OwnId, "Test User", false, householdAdmin: true, signInEmail: "me@home.test"), Person(SamId, "Sam", false)]);
        _protections[OwnId] = Protection(protectionEnabled: true);

        var cut = RenderFor(OwnId, "access");
        cut.WaitForAssertion(() => Assert.True(AdminSwitch(cut, "Ask for a PIN to open administrator settings").HasAttribute("checked")));
        cut.InvokeAsync(() => AdminSwitch(cut, "Ask for a PIN to open administrator settings").Change(false));

        cut.WaitForAssertion(() => Assert.Contains("no longer ask for Test User", cut.Markup));
        var removal = Assert.Single(ProtectionSaves());
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<SetProfileAdminProtectionRequest>(removal.Body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!.Enabled);
    }

    [Fact]
    public void AdministratorPin_IsNotOffered_ToAHouseholdAdministrator_OnSomeoneElsesProfile()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([
            Person(OwnId, "Test User", false, householdAdmin: true, signInEmail: "me@home.test"),
            Person(SamId, "Sam", false, householdAdmin: true, signInEmail: "sam@home.test")]);
        _protections[SamId] = Protection(protectionEnabled: false);

        var cut = RenderFor(SamId, "access");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-profile-section='access']")));
        Assert.Empty(cut.FindAll("[data-profile-section='pin-protection']"));
        Assert.DoesNotContain(_requests, request => request.Path.EndsWith("/admin-protection", StringComparison.Ordinal));
    }

    [Fact]
    public void AdministratorPin_IsOffered_ToAServerAdministrator_OnAnotherAdministratorsProfile()
    {
        Setup(actions: ["access.manage"], navigation: ["settings.administration"]);
        _samHasPin = true;
        _managed.AddRange([
            Person(OwnId, "Test User", false, householdAdmin: true, serverAdmin: true, signInEmail: "me@home.test"),
            Person(SamId, "Mom", false, householdAdmin: true, signInEmail: "mom@home.test")]);
        _protections[SamId] = Protection(protectionEnabled: false);

        var cut = RenderFor(SamId, "access");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-profile-section='pin-protection']")));
        cut.InvokeAsync(() => AdminSwitch(cut, "Ask for a PIN to open administrator settings").Change(true));

        cut.WaitForAssertion(() => Assert.Single(ProtectionSaves(), request => request.Path == $"/access/profiles/{SamId:D}/admin-protection"));
    }

    [Fact]
    public void AdministratorPin_IsNotOffered_ToAMemberOnTheirOwnPage()
    {
        Setup(actions: [], navigation: []);
        _protections[OwnId] = Protection(protectionEnabled: false);

        var cut = RenderFor(OwnId, "access");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".profile-detail__name")));
        Assert.Empty(cut.FindAll("[data-profile-section='pin-protection']"));
        Assert.DoesNotContain(_requests, request => request.Path.EndsWith("/admin-protection", StringComparison.Ordinal));
    }

    private void UsePhone()
    {
        var device = new DeviceContextService(EngineApiClientStub.CreateDefault());
        device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true)).GetAwaiter().GetResult();
        Services.AddScoped(_ => device);
    }

    private static readonly string[] NsfwTokens = ["nsfw", "vault", "sfw", "pin-pad"];

    [Fact]
    public void NsfwMode_IsNotDrawnAtAll_OnTheOwnPageOfAProfileThatIsOff()
    {
        // The Engine's status route answers "not found" (the stub's default), so there is no switch, no check, no PIN row and no word.
        Setup(actions: [], navigation: []);

        var cut = RenderFor(OwnId, "access");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".profile-detail__name")));
        foreach (var token in NsfwTokens)
        {
            Assert.DoesNotContain(token, cut.Markup, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void NsfwAccess_IsNotDrawn_WhenAnAdministratorLooksAtAKidsProfile_ButIsOfferedForAnAdult()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(KidId, "Robin", true), Person(SamId, "Sam", false)]);

        var kid = RenderFor(KidId, "access");
        kid.WaitForAssertion(() => Assert.NotEmpty(kid.FindAll(".profile-detail__name")));
        foreach (var token in NsfwTokens)
        {
            Assert.DoesNotContain(token, kid.Markup, StringComparison.OrdinalIgnoreCase);
        }

        var adult = RenderFor(SamId, "access");
        adult.WaitForAssertion(() => Assert.NotEmpty(adult.FindAll("[data-profile-section='nsfw-access']")));
        Assert.DoesNotContain("Robin", adult.Markup);
    }

    // The profile's administrator PIN setting as the Engine reports it for the profile.
    private static ProfileAdminProtectionResponse Protection(bool protectionEnabled, string mode = "FixedDuration", int? minutes = 30) =>
        new(protectionEnabled, mode, minutes, 1);

    private IEnumerable<RecordedRequest> ProtectionSaves() =>
        _requests.Where(request => request.Method == HttpMethod.Put && request.Path.EndsWith("/admin-protection", StringComparison.Ordinal));

    private IRenderedComponent<ProfileDetailPage> RenderFor(Guid profileId, string tab = "") =>
        Render<ProfileDetailPage>(parameters => parameters
            .Add(page => page.ProfileId, profileId)
            .Add(page => page.Tab, tab));

    private async Task<IRenderedComponent<ProfileDetailPage>> OpenAccessAsync()
    {
        var cut = RenderFor(SamId, "access");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-profile-section='access']")));

        await Task.CompletedTask;
        return cut;
    }

    private static AngleSharp.Dom.IElement KidSwitch(IRenderedComponent<ProfileDetailPage> cut) =>
        cut.Find("[data-kid-switch] input[role='switch']");

    private IEnumerable<RecordedRequest> ProfileUpdates() =>
        _requests.Where(request => request.Method == HttpMethod.Put && request.Path.StartsWith("/access/profiles/", StringComparison.Ordinal)
            && request.Path.Count(character => character == '/') == 3);

    private static AngleSharp.Dom.IElement AdminSwitch(IRenderedComponent<ProfileDetailPage> cut, string label) =>
        cut.FindAll("[data-profile-section='pin-protection'] input[role='switch']").Single(input => input.GetAttribute("aria-label") == label);

    private static AngleSharp.Dom.IElement RoleSwitch(IRenderedComponent<ProfileDetailPage> cut, string role) =>
        cut.Find($"[data-role-switch='{role}'] input[role='switch']");

    private static AngleSharp.Dom.IElement Row(IRenderedComponent<ProfileDetailPage> cut, string key) =>
        cut.Find($"[data-nav-row='{key}']");

    private static Task ClickSaveAsync(IRenderedComponent<ProfileDetailPage> cut) =>
        cut.ClickAsync(page => page.FindAll(".app-save-bar button").Single(button => button.TextContent.Trim() == "Save changes"));

    private void Setup(string[] actions, string[] navigation, bool? serverAdministrator = null)
    {
        var effective = serverAdministrator ?? navigation.Contains("settings.administration");
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ =>
                Task.FromResult(new List<ProfileViewModel>
                {
                    new(OwnId, "Test User", "#C9922E", "Profile", DateTimeOffset.UtcNow, HasPin: _ownHasPin),
                    new(KidId, "Robin", "#2E8BC9", "RestrictedProfile", DateTimeOffset.UtcNow),
                    new(SamId, "Sam", "#2EC98B", "Profile", DateTimeOffset.UtcNow, HasPin: _samHasPin),
                }));
            stub.SetHandler(nameof(IEngineApiClient.GetPlaybackSettingsAsync), args =>
                Task.FromResult<UserPlaybackSettingsDto?>(_settingsAvailable
                    ? UserPlaybackSettingsDto.CreateDefaults((Guid)args![0]!)
                    : null));
            stub.SetHandler("get_LastError", _ => _lastError);
        });
        Services.AddSingleton(api);

        var grants = new[] { OwnId, KidId, SamId }.Select(id => new AccountProfileGrantDto(id, id, "Test User", null,
            true, true, true, new ProfileAdminProtectionResponse(false, "None", null, 1), 1, DateTimeOffset.UtcNow)).ToList();
        var authority = new DashboardAuthorityResponse(OwnAccountId, OwnId, true, true, 1, 1, effective, effective, null, 1,
            grants, navigation, actions);
        _authority = authority;
        var session = new DashboardSessionAccessor();
        session.Set("test-session-token", OwnAccountId, OwnId, Guid.NewGuid(), authority);
        Services.AddScoped(_ => session);
    }

    private static ManagedProfileResponse Person(Guid id, string name, bool restricted, bool householdAdmin = false, bool serverAdmin = false,
        string? signInEmail = null, Guid? userId = null, Guid? household = null) =>
        new(id, name, "#C9922E", null, DateTimeOffset.UtcNow.AddMinutes(id == OwnId ? -3 : 0), household ?? HouseholdId, restricted,
            IsServerAdmin: serverAdmin, IsHouseholdAdmin: householdAdmin,
            User: signInEmail is null ? null : new ManagedProfileUserDto(userId ?? (id == OwnId ? OwnAccountId : SamAccountId), signInEmail));

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("http://engine.test") };
    }

    private sealed record RecordedRequest(HttpMethod Method, string Path, string Body, string Query = "");

    private sealed class ManagedHandler(ProfileDetailPageTests owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            owner._requests.Add(new RecordedRequest(request.Method, path, body, request.RequestUri.Query));

            if (path == "/access/profiles" && request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(owner._managed) };
            }

            if (request.Method == HttpMethod.Get && path.StartsWith("/access/profiles/", StringComparison.Ordinal)
                && path.EndsWith("/admin-protection", StringComparison.Ordinal))
            {
                var profileText = path["/access/profiles/".Length..^"/admin-protection".Length];
                return Guid.TryParse(profileText, out var profileId) && owner._protections.TryGetValue(profileId, out var known)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(known) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
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

            if (path == "/access/households" && request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new[] { new HouseholdResponse(HouseholdId, "Smith household", 3, 2, DateTimeOffset.UtcNow) }),
                };
            }

            if (request.Method == HttpMethod.Put && path.StartsWith("/access/profiles/", StringComparison.Ordinal) && path.Count(character => character == '/') == 3)
            {
                if (owner._profileUpdateStatus != HttpStatusCode.OK)
                {
                    return new HttpResponseMessage(owner._profileUpdateStatus)
                    {
                        Content = owner._profileUpdateDetail is null ? null : JsonContent.Create(new { detail = owner._profileUpdateDetail }),
                    };
                }

                var updatedId = Guid.Parse(path["/access/profiles/".Length..]);
                var index = owner._managed.FindIndex(person => person.Id == updatedId);
                using var document = System.Text.Json.JsonDocument.Parse(body);
                if (index >= 0 && document.RootElement.TryGetProperty("is_kids", out var isKids) && isKids.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                {
                    owner._managed[index] = owner._managed[index] with { IsKids = isKids.GetBoolean() };
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(index >= 0 ? owner._managed[index] : null) };
            }

            if (request.Method == HttpMethod.Put && path.EndsWith("/roles", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(owner._rolesStatus)
                {
                    Content = owner._rolesDetail is null ? null : JsonContent.Create(new { detail = owner._rolesDetail }),
                };
            }

            if (request.Method == HttpMethod.Delete && path.StartsWith("/access/profiles/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(owner._profileDeleteStatus)
                {
                    Content = owner._profileDeleteDetail is null ? null : JsonContent.Create(new { detail = owner._profileDeleteDetail }),
                };
            }

            if (request.Method == HttpMethod.Delete || request.Method == HttpMethod.Put)
            {
                return new HttpResponseMessage(request.Method == HttpMethod.Delete ? HttpStatusCode.NoContent : HttpStatusCode.OK)
                {
                    Content = request.Method == HttpMethod.Put ? JsonContent.Create(new { }) : null,
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { }) };
        }
    }
}
