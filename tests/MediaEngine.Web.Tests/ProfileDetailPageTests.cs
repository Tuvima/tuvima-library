using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
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
    private static readonly Guid MainAccountId = Guid.NewGuid();

    private readonly List<RecordedRequest> _requests = [];

    private readonly List<ManagedProfileResponse> _managed = [];
    private string? _lastError;
    private bool _settingsAvailable = true;
    private readonly List<AccountAccessResponse> _accounts = [];
    private readonly List<AccessLibraryOptionDto> _libraries = [];
    private HttpStatusCode _accessSaveStatus = HttpStatusCode.OK;
    private HttpStatusCode _profileDeleteStatus = HttpStatusCode.NoContent;
    private string? _profileDeleteDetail;
    private bool _confirmTwoStepOff = true;
    private int _confirmCount;
    private static readonly Guid LibraryA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid LibraryB = Guid.Parse("00000000-0000-0000-0000-00000000000b");

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
        var dialogs = DispatchProxy.Create<IAppDialogService, ConfirmSpy>();
        ((ConfirmSpy)(object)dialogs).Owner = this;
        Services.AddScoped(_ => dialogs);
    }

    private class ConfirmSpy : DispatchProxy
    {
        public ProfileDetailPageTests? Owner { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "ShowMessageBoxAsync")
            {
                Owner!._confirmCount++;
                return Task.FromResult<bool?>(Owner._confirmTwoStepOff);
            }

            return null;
        }
    }

    // An own sign-in the Engine creates always follows the household's main sign-in (grants_inherit_from_account_id is set); the plain
    // account here keeps its own access, which is the only kind whose access is edited from the page.
    private static AccountAccessResponse SamAccess(bool twoStep = true, string[]? lanes = null, Guid[]? libraries = null, bool inherits = false) =>
        new(SamAccountId, "sam@home.test", true, false, 1,
            (lanes ?? ["read", "watch"]).Select(lane => new AccountFeatureGrantDto(lane, true)).ToList(),
            (libraries ?? [LibraryA]).Select(id => new AccountLibraryGrantDto(id, id == LibraryA ? "Books" : "Films", true)).ToList(),
            [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, HouseholdId, HasTwoStep: twoStep,
            GrantsInheritFromAccountId: inherits ? MainAccountId : null);

    private void SamWithSignIn(bool twoStep = true, bool inherits = false, string[]? navigation = null)
    {
        Setup(actions: ["household.manage"], navigation: navigation ?? []);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(SamId, "Sam", false, new ManagedProfileSignInDto(SamAccountId, "sam@home.test", false, true))]);
        _accounts.Add(SamAccess(twoStep, inherits: inherits));
        _libraries.AddRange([new AccessLibraryOptionDto(LibraryA, "Books", null, "Read")]);
    }

    [Fact]
    public void LibrariesAndLanes_ForAnInheritingOwnSignIn_IsReadOnly_WithALinkToTheHouseholdAndNoSave()
    {
        SamWithSignIn(inherits: true, navigation: ["settings.administration"]);

        var cut = RenderFor(SamId);

        cut.WaitForAssertion(() => Assert.Contains("Read, Watch · 1 library", Row(cut, "access").TextContent));
        var row = Row(cut, "access");
        Assert.Contains("Follows the household's access", row.TextContent);
        Assert.Equal("/settings/access/users", row.GetAttribute("href"));
        Assert.Equal("a", row.TagName.ToLowerInvariant());

        // A plain link: no sheet to open and nothing to save.
        Assert.Empty(cut.FindAll(".profile-access-sheet"));
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Trim() == "Save");
        Assert.DoesNotContain(_requests, request => request.Method == HttpMethod.Put && request.Path.EndsWith("/access", StringComparison.Ordinal));
    }

    [Fact]
    public void LibrariesAndLanes_ForAnInheritingOwnSignIn_IsPlainTextForAHouseholdAdministrator()
    {
        // Users & Access is a server-administrator page, so a household administrator is told where it changes but not linked to it.
        SamWithSignIn(inherits: true);

        var cut = RenderFor(SamId);

        cut.WaitForAssertion(() => Assert.Contains("Follows the household's access", Row(cut, "access").TextContent));
        var row = Row(cut, "access");
        Assert.Equal("true", row.GetAttribute("aria-disabled"));
        Assert.Null(row.GetAttribute("href"));
        Assert.Contains("Read, Watch · 1 library", row.TextContent);
    }

    [Fact]
    public void HouseholdAccess_IsShownToAnAdministrator_ForSomeoneWithTheirOwnSignIn()
    {
        SamWithSignIn();

        var cut = RenderFor(SamId);

        cut.WaitForAssertion(() => Assert.Contains("Read, Watch · 1 library", Row(cut, "access").TextContent));
        Assert.Single(cut.FindAll("[data-profile-section='household-access']"));
        Assert.Contains("On", Row(cut, "two-step").TextContent);
    }

    [Fact]
    public void HouseholdAccess_IsShownToAServerAdministrator()
    {
        Setup(actions: [], navigation: ["settings.administration"]);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(SamId, "Sam", false, new ManagedProfileSignInDto(SamAccountId, "sam@home.test", false, true))]);
        _accounts.Add(SamAccess());

        var cut = RenderFor(SamId);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='access']")));
    }

    [Fact]
    public void HouseholdAccess_IsHiddenFromAMember()
    {
        Setup(actions: [], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(SamId, "Sam", false, new ManagedProfileSignInDto(SamAccountId, "sam@home.test", false, true))]);

        var cut = RenderFor(OwnId);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='playback']")));
        Assert.Empty(cut.FindAll("[data-profile-section='household-access']"));
    }

    [Fact]
    public void HouseholdAccess_IsHidden_ForSomeoneWithoutTheirOwnSignIn()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(SamId, "Sam", false)]);

        var cut = RenderFor(SamId);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='details']")));
        Assert.Empty(cut.FindAll("[data-profile-section='household-access']"));
        Assert.Empty(cut.FindAll("[data-nav-row='access']"));
    }

    [Fact]
    public async Task LibrariesAndLanes_OffersOnlyTheLibrariesTheEngineListed_AndSavesOrderedIdsForThatAccount()
    {
        SamWithSignIn();
        var cut = RenderFor(SamId);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='access']")));

        await cut.ClickAsync(page => Row(page, "access"));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".profile-access-sheet .access-user-permissions__libraries")));
        var libraryLabels = cut.FindAll(".profile-access-sheet .access-user-permissions__libraries label").Select(label => label.TextContent.Trim()).ToList();
        Assert.Equal(["Books"], libraryLabels);
        Assert.Contains(_requests, request => request.Method == HttpMethod.Get && request.Path == "/access/libraries");

        // Add the Listen lane; Read and Watch (and the Books library) were already on.
        await cut.InvokeAsync(() => cut.FindAll(".profile-access-sheet input[role='switch']").Single(input => input.GetAttribute("aria-label") == "Listen").Change(true));
        await ClickSaveAsync(cut);

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".profile-access-sheet")));
        var put = Assert.Single(_requests, request => request.Method == HttpMethod.Put && request.Path == $"/access/accounts/{SamAccountId:D}/access");
        using var body = System.Text.Json.JsonDocument.Parse(put.Body);
        Assert.Equal("listen,read,watch", string.Join(",", body.RootElement.GetProperty("feature_ids").EnumerateArray().Select(item => item.GetString())));
        Assert.Equal(LibraryA.ToString("D"), string.Join(",", body.RootElement.GetProperty("library_ids").EnumerateArray().Select(item => item.GetString())));
    }

    [Fact]
    public async Task LibrariesAndLanes_WhenTheEngineRefuses_StaysOpenWithAReason()
    {
        SamWithSignIn();
        _accessSaveStatus = HttpStatusCode.Forbidden;
        var cut = RenderFor(SamId);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='access']")));
        await cut.ClickAsync(page => Row(page, "access"));
        // The sheet draws its buttons first and re-renders when the Engine's library list arrives, which hands them new
        // handler ids; the click waits for that list and is then made on the renderer thread.
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".profile-access-sheet .access-user-permissions__libraries")));

        await ClickSaveAsync(cut);

        cut.WaitForAssertion(() => Assert.Contains("only give what your household has", cut.Find(".profile-access-sheet [role='alert']").TextContent));
        Assert.NotEmpty(cut.FindAll(".profile-access-sheet"));
    }

    [Fact]
    public void TwoStepCodes_AsksFirst_ThenResetsThatAccountOnce()
    {
        SamWithSignIn();
        var cut = RenderFor(SamId);
        cut.WaitForAssertion(() => Assert.Contains("On", Row(cut, "two-step").TextContent));

        Row(cut, "two-step").Click();

        cut.WaitForAssertion(() => Assert.Single(_requests, request => request.Method == HttpMethod.Post && request.Path == $"/access/accounts/{SamAccountId:D}/two-step/reset"));
        Assert.Equal(1, _confirmCount);
    }

    [Fact]
    public void TwoStepCodes_WhenDeclined_ChangesNothing()
    {
        SamWithSignIn();
        _confirmTwoStepOff = false;
        var cut = RenderFor(SamId);
        cut.WaitForAssertion(() => Assert.Contains("On", Row(cut, "two-step").TextContent));

        Row(cut, "two-step").Click();

        cut.WaitForAssertion(() => Assert.Equal(1, _confirmCount));
        Assert.DoesNotContain(_requests, request => request.Path.EndsWith("/two-step/reset", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoStepCodes_WhenAlreadyOff_ShowsOffAndOpensNothing()
    {
        SamWithSignIn(twoStep: false);

        var cut = RenderFor(SamId);

        cut.WaitForAssertion(() => Assert.Contains("Off", Row(cut, "two-step").TextContent));
        Assert.Equal("true", Row(cut, "two-step").GetAttribute("aria-disabled"));
    }

    [Fact]
    public void OwnProfile_ShowsEveryRow_WithLinksThatOmitTheProfileQuery()
    {
        Setup(actions: [], navigation: []);

        var cut = RenderFor(OwnId);

        cut.WaitForAssertion(() => Assert.Equal("Test User", cut.Find(".profile-detail__name").TextContent));
        Assert.Equal("Profile", cut.Find(".profile-detail__role").TextContent);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='playback']")));
        // A member's own page has no Profile details row: the editor saves through administrator-only Engine actions.
        Assert.Equal("sign-in,devices,playback,listening,reading,subtitles",
            string.Join(",", cut.FindAll("[data-nav-row]").Select(row => row.GetAttribute("data-nav-row"))));
        Assert.Equal("/settings/security/devices", Row(cut, "devices").GetAttribute("href"));
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
    }

    [Fact]
    public void ProfileDetailsRow_IsHiddenFromAMember_OnTheirOwnProfile()
    {
        Setup(actions: [], navigation: []);

        var cut = RenderFor(OwnId);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='playback']")));
        Assert.Empty(cut.FindAll("[data-nav-row='details']"));
    }

    [Fact]
    public void ProfileDetailsRow_IsShownToAHouseholdAdministrator_OnTheirOwnProfile()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(SamId, "Sam", false)]);

        var cut = RenderFor(OwnId);

        cut.WaitForAssertion(() => Assert.Contains("Everything", Row(cut, "details").TextContent));
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
    public void SignInRow_ForSomeoneWithoutOwnSignIn_SaysTheySwitchFromWhosWatching_AndOpensTheGiveSheet()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(SamId, "Sam", false)]);

        var cut = RenderFor(SamId);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='sign-in']")));
        Assert.Contains("Switches from Who's watching", Row(cut, "sign-in").TextContent);
        Assert.Empty(cut.FindAll(".sign-in-sheet"));

        Row(cut, "sign-in").Click();

        cut.WaitForAssertion(() => Assert.Equal("Give own sign-in", cut.Find("#sign-in-sheet-title").TextContent));
        var sheet = cut.Find(".sign-in-sheet");
        Assert.Equal("manage", sheet.GetAttribute("data-sign-in-view"));
        Assert.Contains(cut.FindAll(".sign-in-sheet button"), button => button.TextContent.Trim() == "Generate");
        Assert.Contains(cut.FindAll(".sign-in-sheet button"), button => button.TextContent.Trim() == "Send an invitation link instead");
        // "Must change it at first sign-in" starts on.
        var mustChange = cut.Find(".sign-in-sheet input[role='switch']");
        Assert.NotNull(mustChange.GetAttribute("checked"));
        Assert.Contains("Must change it at first sign-in", cut.Find(".sign-in-sheet").TextContent);
    }

    [Fact]
    public void SignInRow_ForSomeoneWithOwnSignIn_ShowsTheEmailAndAPendingChangeNote()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(SamId, "Sam", false, new ManagedProfileSignInDto(SamAccountId, "sam@home.test", true, true))]);

        var cut = RenderFor(SamId);

        cut.WaitForAssertion(() => Assert.Contains("sam@home.test", Row(cut, "sign-in").TextContent));
        Assert.Contains("Must change password at first sign-in", Row(cut, "sign-in").TextContent);
        Assert.DoesNotContain("Switches from Who's watching", Row(cut, "sign-in").TextContent);
    }

    [Fact]
    public void SignInRow_WithoutThePendingChange_ShowsNoNote()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(SamId, "Sam", false, new ManagedProfileSignInDto(SamAccountId, "sam@home.test", false, true))]);

        var cut = RenderFor(SamId);

        cut.WaitForAssertion(() => Assert.Contains("sam@home.test", Row(cut, "sign-in").TextContent));
        Assert.DoesNotContain("Must change password", Row(cut, "sign-in").TextContent);
    }

    [Fact]
    public void ExistingSignIn_OffersChangeEmailSetPasswordAndRemove()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(SamId, "Sam", false, new ManagedProfileSignInDto(SamAccountId, "sam@home.test", false, true))]);

        var cut = RenderFor(SamId);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='sign-in']")));
        Row(cut, "sign-in").Click();

        cut.WaitForAssertion(() => Assert.Equal("Sign-in", cut.Find("#sign-in-sheet-title").TextContent));
        Assert.Equal("change-email,set-password,remove-sign-in",
            string.Join(",", cut.FindAll(".sign-in-sheet [data-nav-row]").Select(row => row.GetAttribute("data-nav-row"))));
    }

    [Fact]
    public void GivingASignIn_SendsThePasswordAndFlag_ShowsThePasswordOnce_AndDoesNotCloseByAccident()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(SamId, "Sam", false)]);

        var cut = RenderFor(SamId);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='sign-in']")));
        Row(cut, "sign-in").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".sign-in-sheet input")));
        var inputs = cut.FindAll(".sign-in-sheet input").Where(input => input.GetAttribute("role") != "switch").ToList();
        inputs[0].Input("sam@home.test");
        inputs[1].Input("a long starting password");
        cut.Find(".sign-in-sheet input[role='switch']").Change(false);
        cut.FindAll(".sign-in-sheet button").Single(button => button.TextContent.Trim() == "Give own sign-in").Click();

        cut.WaitForAssertion(() => Assert.Contains("a long starting password", cut.Find(".sign-in-sheet").TextContent));
        var post = Assert.Single(_requests, request => request.Method == HttpMethod.Post && request.Path == $"/access/profiles/{SamId:D}/own-sign-in");
        using var body = System.Text.Json.JsonDocument.Parse(post.Body);
        Assert.Equal("sam@home.test", body.RootElement.GetProperty("email").GetString());
        Assert.Equal("a long starting password", body.RootElement.GetProperty("temporary_password").GetString());
        Assert.False(body.RootElement.GetProperty("must_change_password").GetBoolean());
        Assert.False(body.RootElement.GetProperty("invite").GetBoolean());

        // A password on screen is shown once: clicking outside or pressing Escape must not throw it away.
        cut.Find(".settings-sheet-backdrop").Click();
        cut.Find(".sign-in-sheet").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        Assert.Contains("a long starting password", cut.Find(".sign-in-sheet").TextContent);

        cut.FindAll(".sign-in-sheet button").Single(button => button.TextContent.Trim() == "Done").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".sign-in-sheet")));
    }

    [Fact]
    public void SendingAnInvitationInstead_SendsInviteWithNoPassword()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(SamId, "Sam", false)]);

        var cut = RenderFor(SamId);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='sign-in']")));
        Row(cut, "sign-in").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".sign-in-sheet input")));
        cut.FindAll(".sign-in-sheet input").First(input => input.GetAttribute("role") != "switch").Input("sam@home.test");
        cut.FindAll(".sign-in-sheet button").Single(button => button.TextContent.Trim() == "Send an invitation link instead").Click();

        cut.WaitForAssertion(() => Assert.Contains("KQ7M4-XH2TA", cut.Find(".sign-in-sheet").TextContent));
        var post = Assert.Single(_requests, request => request.Method == HttpMethod.Post && request.Path == $"/access/profiles/{SamId:D}/own-sign-in");
        using var body = System.Text.Json.JsonDocument.Parse(post.Body);
        Assert.True(body.RootElement.GetProperty("invite").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, body.RootElement.GetProperty("temporary_password").ValueKind);
    }

    [Fact]
    public void ChangingTheEmail_PutsTheNewEmailForThatAccount()
    {
        Setup(actions: ["household.manage"], navigation: ["settings.administration"]);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(SamId, "Sam", false, new ManagedProfileSignInDto(SamAccountId, "sam@home.test", false, true))]);

        var cut = RenderFor(SamId);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='sign-in']")));
        Row(cut, "sign-in").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='change-email']")));
        cut.Find("[data-nav-row='change-email']").Click();
        cut.WaitForAssertion(() => Assert.Equal("email", cut.Find(".sign-in-sheet").GetAttribute("data-sign-in-view")));
        cut.Find(".sign-in-sheet input").Input("sam.new@home.test");
        cut.FindAll(".sign-in-sheet button").Single(button => button.TextContent.Trim() == "Change email").Click();

        cut.WaitForAssertion(() => Assert.Contains(_requests, request => request.Method == HttpMethod.Put && request.Path == $"/access/accounts/{SamAccountId:D}/email"));
        var put = _requests.Single(request => request.Method == HttpMethod.Put && request.Path == $"/access/accounts/{SamAccountId:D}/email");
        using var body = System.Text.Json.JsonDocument.Parse(put.Body);
        Assert.Equal("sam.new@home.test", body.RootElement.GetProperty("email").GetString());
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".sign-in-sheet")));
    }

    [Fact]
    public void SettingANewPassword_SendsTheFlagAndShowsItOnce()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(SamId, "Sam", false, new ManagedProfileSignInDto(SamAccountId, "sam@home.test", false, true))]);

        var cut = RenderFor(SamId);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='sign-in']")));
        Row(cut, "sign-in").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='set-password']")));
        cut.Find("[data-nav-row='set-password']").Click();
        cut.WaitForAssertion(() => Assert.Equal("password", cut.Find(".sign-in-sheet").GetAttribute("data-sign-in-view")));
        cut.FindAll(".sign-in-sheet input").First(input => input.GetAttribute("role") != "switch").Input("brand new starting password");
        cut.FindAll(".sign-in-sheet button").Single(button => button.TextContent.Trim() == "Set password").Click();

        cut.WaitForAssertion(() => Assert.Contains("brand new starting password", cut.Find(".sign-in-sheet").TextContent));
        var post = Assert.Single(_requests, request => request.Method == HttpMethod.Post && request.Path == $"/access/accounts/{SamAccountId:D}/temporary-password");
        using var body = System.Text.Json.JsonDocument.Parse(post.Body);
        Assert.True(body.RootElement.GetProperty("must_change_password").GetBoolean());
    }

    [Fact]
    public void RemovingTheSignIn_AsksFirst_ThenDeletesTheAccountsOwnSignIn()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(SamId, "Sam", false, new ManagedProfileSignInDto(SamAccountId, "sam@home.test", false, true))]);

        var cut = RenderFor(SamId);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='sign-in']")));
        Row(cut, "sign-in").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='remove-sign-in']")));
        cut.Find("[data-nav-row='remove-sign-in']").Click();
        cut.WaitForAssertion(() => Assert.Contains("stays in the household with everything they have saved", cut.Find(".sign-in-sheet").TextContent));
        Assert.DoesNotContain(_requests, request => request.Method == HttpMethod.Delete);

        cut.FindAll(".sign-in-sheet button").Single(button => button.TextContent.Trim() == "Remove sign-in").Click();

        cut.WaitForAssertion(() => Assert.Contains(_requests, request => request.Method == HttpMethod.Delete && request.Path == $"/access/accounts/{SamAccountId:D}/own-sign-in"));
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
        // The Delete row skips the form: the sheet opens on the photo question and the red Delete button.
        Assert.NotEmpty(cut.FindAll(".profile-editor__confirm"));
    }

    [Fact]
    public void DeleteRefusedWithAConflict_TellsTheAdministratorToRemoveOrMoveTheSignInFirst()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(SamId, "Sam", false)]);
        _profileDeleteStatus = HttpStatusCode.Conflict;

        var cut = RenderFor(SamId);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='delete']")));
        Row(cut, "delete").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".profile-editor__confirm")));
        cut.Find(".profile-editor__confirm .who-button--danger").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(_requests, request => request.Method == HttpMethod.Delete && request.Path == $"/access/profiles/{SamId:D}");
            Assert.Contains("Remove or move their sign-in first", cut.Find(".who-page__error").TextContent);
        });
    }

    [Fact]
    public void DeleteRefusedWithAnEngineSentence_ShowsThatSentenceInsteadOfTheGenericLine()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(SamId, "Sam", false)]);
        _profileDeleteStatus = HttpStatusCode.Conflict;
        _profileDeleteDetail = "This is the last administrator who can sign in. Make someone else an administrator first.";

        var cut = RenderFor(SamId);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='delete']")));
        Row(cut, "delete").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".profile-editor__confirm")));
        cut.Find(".profile-editor__confirm .who-button--danger").Click();

        cut.WaitForAssertion(() =>
        {
            var message = cut.Find(".who-page__error").TextContent;
            Assert.Contains("This is the last administrator who can sign in.", message);
            Assert.DoesNotContain("Remove or move their sign-in first", message);
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

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='playback']")));
        Assert.Empty(cut.FindAll("[data-nav-row='activity']"));
        Assert.DoesNotContain("View activity", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void PinProtection_ForAnAdministrator_StagesPinAndChosenDurationBeforeEnabling()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(SamId, "Sam", false)]);
        _accounts.Add(AccountWithAdminGrantFor(SamId, "Sam", protectionEnabled: false));

        var cut = RenderFor(SamId);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-profile-section='pin-protection']")));

        cut.Find("[data-profile-section='pin-protection'] input[type='password']").Input("2468");
        cut.Find("select[aria-label='PIN unlock duration for Sam']").Change("FixedDuration:60");
        cut.FindAll("[data-profile-section='pin-protection'] button").Single(button => button.TextContent.Trim() == "Enable protection").Click();

        cut.WaitForAssertion(() => Assert.Contains("PIN protection for Sam was updated.", cut.Markup));
        var request = Assert.Single(_requests, request => request.Path.EndsWith("/admin-protection", StringComparison.Ordinal));
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal($"/access/accounts/{SamAccountId:D}/grants/{SamId:D}/admin-protection", request.Path);
        var payload = System.Text.Json.JsonSerializer.Deserialize<SetGrantAdminProtectionRequest>(request.Body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.True(payload.Enabled);
        Assert.Equal("2468", payload.Pin);
        Assert.Equal("FixedDuration", payload.UnlockMode);
        Assert.Equal(60, payload.UnlockMinutes);
    }

    [Fact]
    public void PinProtection_CanBeRemoved_AndIsNotOfferedWhenNoAccountLetsThePersonOpenAdminSettings()
    {
        Setup(actions: ["household.manage"], navigation: []);
        _managed.AddRange([Person(OwnId, "Test User", false), Person(SamId, "Sam", false)]);
        _accounts.Add(AccountWithAdminGrantFor(SamId, "Sam", protectionEnabled: true));

        var protectedPage = RenderFor(SamId);
        protectedPage.WaitForAssertion(() => Assert.Contains("Admin settings are protected", protectedPage.Markup));
        protectedPage.FindAll("[data-profile-section='pin-protection'] button").Single(button => button.TextContent.Trim() == "Remove protection").Click();
        protectedPage.WaitForAssertion(() => Assert.Contains("PIN protection for Sam was removed.", protectedPage.Markup));
        var removal = Assert.Single(_requests, request => request.Path.EndsWith("/admin-protection", StringComparison.Ordinal));
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<SetGrantAdminProtectionRequest>(removal.Body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!.Enabled);

        _accounts.Clear();
        _accounts.Add(AccountWithAdminGrantFor(SamId, "Sam", protectionEnabled: false, adminEnabled: false));
        var without = RenderFor(SamId);
        without.WaitForAssertion(() => Assert.NotEmpty(without.FindAll("[data-nav-row='playback']")));
        Assert.Empty(without.FindAll("[data-profile-section='pin-protection']"));
    }

    [Fact]
    public void PinProtection_IsNotOfferedToAMemberOnTheirOwnPage()
    {
        Setup(actions: [], navigation: []);
        _accounts.Add(AccountWithAdminGrantFor(OwnId, "Test User", protectionEnabled: false));

        var cut = RenderFor(OwnId);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-nav-row='playback']")));
        Assert.Empty(cut.FindAll("[data-profile-section='pin-protection']"));
        Assert.DoesNotContain(_requests, request => request.Path == "/access/accounts");
    }

    private static AccountAccessResponse AccountWithAdminGrantFor(Guid profileId, string name, bool protectionEnabled, bool adminEnabled = true) =>
        new(SamAccountId, "admin@home.test", true, true, 1, [], [],
            [new AccountProfileGrantDto(SamAccountId, profileId, name, null, true, true, adminEnabled,
                new GrantAdminProtectionDto(protectionEnabled, "FixedDuration", 30, 1, false, null), 1, DateTimeOffset.UtcNow)],
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, HouseholdId);

    private IRenderedComponent<ProfileDetailPage> RenderFor(Guid profileId) =>
        Render<ProfileDetailPage>(parameters => parameters.Add(page => page.ProfileId, profileId));

    private static AngleSharp.Dom.IElement Row(IRenderedComponent<ProfileDetailPage> cut, string key) =>
        cut.Find($"[data-nav-row='{key}']");

    private static Task ClickSaveAsync(IRenderedComponent<ProfileDetailPage> cut) =>
        cut.ClickAsync(page => page.FindAll(".profile-access-sheet button").Single(button => button.TextContent.Trim() == "Save"));

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

    private static ManagedProfileResponse Person(Guid id, string name, bool restricted, ManagedProfileSignInDto? ownSignIn = null) =>
        new(id, name, "#C9922E", null, DateTimeOffset.UtcNow.AddMinutes(id == OwnId ? -3 : 0), HouseholdId, restricted,
            ContentLimit: restricted ? "PG" : null, OwnSignIn: ownSignIn);

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("http://engine.test") };
    }

    private sealed record RecordedRequest(HttpMethod Method, string Path, string Body);

    private sealed class ManagedHandler(ProfileDetailPageTests owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            owner._requests.Add(new RecordedRequest(request.Method, path, body));

            if (path == "/access/profiles" && request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(owner._managed) };
            }

            if (path == "/access/accounts" && request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(owner._accounts) };
            }

            if (path == "/access/libraries" && request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(owner._libraries) };
            }

            if (request.Method == HttpMethod.Put && path.EndsWith("/access", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(owner._accessSaveStatus) { Content = JsonContent.Create(new { }) };
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/two-step/reset", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(SamAccess(twoStep: false)) };
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/own-sign-in", StringComparison.Ordinal))
            {
                using var document = System.Text.Json.JsonDocument.Parse(body);
                var invite = document.RootElement.GetProperty("invite").GetBoolean();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new GiveOwnSignInResponse(Guid.NewGuid(), "sam@home.test",
                        invite ? new AccountInvitationResponse(Guid.NewGuid(), "KQ7M4-XH2TA", DateTimeOffset.UtcNow.AddDays(1), null) : null,
                        null)),
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
