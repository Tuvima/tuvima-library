using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Contracts.Paging;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class SecuritySettingsPageTests : AsyncBunitContext
{
    private readonly SecurityHandler _handler = new();
    private readonly DashboardSessionAccessor _session = new();
    private readonly IEngineApiClient _photoViews;
    private readonly SwitchableAccountState _accountState = new();

    private PhotoViewsApiClient Views => (PhotoViewsApiClient)(object)_photoViews;

    public SecuritySettingsPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddLogging();
        Services.AddNativeUiServices();
        Services.AddSingleton<IHttpClientFactory>(new SecurityClientFactory(_handler));
        Services.AddScoped<DashboardIdentityClient>();
        Services.AddScoped<AccountNoticeState>();
        Services.AddSingleton(_session);
        Services.AddSingleton<AuthenticationStateProvider>(_accountState);
        _photoViews = System.Reflection.DispatchProxy.Create<IEngineApiClient, PhotoViewsApiClient>();
        Services.AddSingleton(_photoViews);
        Services.AddScoped<IItsYouConfirmer>(_ => new ConfirmedActionRunnerTests.SpyConfirmer(confirmed: false));
        Services.AddScoped<ConfirmedActionRunner>();
        Services.AddSingleton<IReadOnlyList<RegisteredExternalAuthProvider>>([]);
        Render<AppPopoverHost>();
    }

    [Fact]
    public void ThisComputerOnlyAccount_StartsWithAnAddAPasswordRow()
    {
        _handler.HasPassword = false;
        _accountState.ThisComputerOnly = true;
        var cut = Render<SecuritySettingsPage>();

        cut.WaitForAssertion(() => Assert.Equal(["add-password", "password", "email", "sign-out-others", "photo-views"], RowKeys(cut)));
        Assert.Contains("Anyone who can reach Library can use this account until you do", cut.Find("[data-nav-row='add-password']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ThisComputerOnlyAccount_BeforeNotNow_HasNoAwayFromHomeNote()
    {
        _handler.HasPassword = false;
        _accountState.ThisComputerOnly = true;
        var cut = Render<SecuritySettingsPage>();

        cut.WaitForAssertion(() => Assert.Contains("add-password", RowKeys(cut)));
        Assert.Empty(cut.FindAll(".security-settings__note"));
    }

    [Fact]
    public void ThisComputerOnlyAccount_AfterNotNow_ShowsAQuietAwayFromHomeNote()
    {
        _handler.HasPassword = false;
        _handler.DismissedNotices = [AccountNoticeKeys.SecureAccount];
        _accountState.ThisComputerOnly = true;
        var cut = Render<SecuritySettingsPage>();

        cut.WaitForAssertion(() => Assert.Equal(
            "Your account has no password, so it only works at home. Add one any time below.",
            cut.Find(".security-settings__note").TextContent.Trim()));
        // A calm line, not an alert, and the Add a password row stays.
        Assert.DoesNotContain("role=\"alert\"", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("add-password", RowKeys(cut));
    }

    [Fact]
    public void AccountWithAPassword_NeverShowsTheAwayFromHomeNote()
    {
        _handler.HasPassword = true;
        _handler.DismissedNotices = [AccountNoticeKeys.SecureAccount];
        _accountState.ThisComputerOnly = true;
        _session.Set("session", _handler.Account, _handler.Profile, _handler.CurrentSession, _handler.MemberAuthority());
        var cut = Render<SecuritySettingsPage>();

        cut.WaitForAssertion(() => Assert.Contains("password", RowKeys(cut)));
        Assert.Empty(cut.FindAll(".security-settings__note"));
    }

    [Fact]
    public async Task AddAPasswordSheet_SaysThePasswordIsForUsingTheLibraryAwayFromHome()
    {
        _handler.HasPassword = false;
        _accountState.ThisComputerOnly = true;
        var cut = Render<SecuritySettingsPage>();
        cut.WaitForElement("[data-nav-row='add-password']");

        await cut.InvokeAsync(() => cut.Find("[data-nav-row='add-password']").Click());

        var sheet = cut.Find("[role='dialog']");
        Assert.Contains("use the Library away from home", sheet.TextContent, StringComparison.Ordinal);
        Assert.Contains("Phones and TVs at home keep working without one", sheet.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("your phone, TV and other devices at home", sheet.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ThisComputerOnlyAccount_DropsAddAPasswordOnceAPasswordExists()
    {
        _handler.HasPassword = true;
        _accountState.ThisComputerOnly = true;
        _session.Set("session", _handler.Account, _handler.Profile, _handler.CurrentSession, _handler.MemberAuthority());
        var cut = Render<SecuritySettingsPage>();

        cut.WaitForAssertion(() => Assert.Equal(["password", "email", "extra-protection", "sign-out-others", "photo-views"], RowKeys(cut)));
    }

    [Fact]
    public void WithoutAPassword_PasswordEmailAndSignOutEverywhereElseRowsRender()
    {
        _handler.HasPassword = false;
        var cut = Render<SecuritySettingsPage>();

        cut.WaitForAssertion(() => Assert.Equal(["password", "email", "sign-out-others", "photo-views"], RowKeys(cut)));
        Assert.Contains("Not set", cut.Find("[data-nav-row='password']").TextContent, StringComparison.Ordinal);
        Assert.Contains("owner@example.test", cut.Find("[data-nav-row='email']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void WithAPassword_ExtraProtectionAppears_AndMembersGetSignOutEverywhereElseButNotTheDevicesLink()
    {
        _handler.HasPassword = true;
        _session.Set("session", _handler.Account, _handler.Profile, _handler.CurrentSession, _handler.MemberAuthority());
        var cut = Render<SecuritySettingsPage>();

        cut.WaitForAssertion(() => Assert.Equal(["password", "email", "extra-protection", "sign-out-others", "photo-views"], RowKeys(cut)));
        Assert.Contains("Off", cut.Find("[data-nav-row='extra-protection']").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-nav-row='devices']"));
        Assert.Empty(cut.FindAll("[role='dialog']"));
    }

    [Fact]
    public async Task Member_SignOutEverywhereElse_EndsOnlyTheirOtherSessionsAfterConfirmation()
    {
        _handler.HasPassword = true;
        _session.Set("session", _handler.Account, _handler.Profile, _handler.CurrentSession, _handler.MemberAuthority());
        var dialogs = Render<AppDialogHost>();
        var cut = Render<SecuritySettingsPage>();

        cut.WaitForAssertion(() => Assert.Contains("1 other session", cut.Find("[data-nav-row='sign-out-others']").TextContent, StringComparison.Ordinal));
        await cut.InvokeAsync(() => cut.Find("[data-nav-row='sign-out-others']").Click());
        Assert.Equal(0, _handler.Revokes);
        dialogs.WaitForAssertion(() => Assert.Contains("Sign out other sessions", dialogs.Markup, StringComparison.Ordinal));
        await dialogs.InvokeAsync(() => dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Sign out other sessions").Click());

        cut.WaitForAssertion(() => Assert.Equal(1, _handler.Revokes));
        cut.WaitForAssertion(() => Assert.Contains("No other sessions", cut.Find("[data-nav-row='sign-out-others']").TextContent, StringComparison.Ordinal));
        // Only the person's own bulk route is used; the administrators' route for other people's sessions never is.
        Assert.DoesNotContain(_handler.Paths, path => path.StartsWith("/access/sessions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtraProtectionRow_OpensOneSheetWithPasskeysTwoStepAndRecoveryCodes()
    {
        _handler.HasPassword = true;
        var cut = Render<SecuritySettingsPage>();
        cut.WaitForElement("[data-nav-row='extra-protection']");

        await cut.InvokeAsync(() => cut.Find("[data-nav-row='extra-protection']").Click());

        var sheet = cut.Find("[role='dialog']");
        Assert.Contains("Passkeys", sheet.TextContent, StringComparison.Ordinal);
        Assert.Contains("Two-step codes", sheet.TextContent, StringComparison.Ordinal);
        Assert.Contains("Recovery codes", sheet.TextContent, StringComparison.Ordinal);
        await cut.InvokeAsync(() => cut.FindAll("button").Single(b => b.TextContent.Trim() == "Done").Click());
        Assert.Empty(cut.FindAll("[role='dialog']"));
    }

    [Fact]
    public async Task PasswordRow_OpensTheChangePasswordFormWhenAPasswordExists()
    {
        _handler.HasPassword = true;
        var cut = Render<SecuritySettingsPage>();
        cut.WaitForElement("[data-nav-row='password']");

        await cut.InvokeAsync(() => cut.Find("[data-nav-row='password']").Click());

        Assert.Contains("Change password", cut.Find("[role='dialog']").TextContent, StringComparison.Ordinal);
        Assert.Contains("New password", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClosingTheSheet_ClearsTypedPasswords()
    {
        _handler.HasPassword = true;
        var cut = Render<SecuritySettingsPage>();
        cut.WaitForElement("[data-nav-row='password']");
        await cut.InvokeAsync(() => cut.Find("[data-nav-row='password']").Click());
        cut.Find("input[type='password']").Input("a-long-test-password");
        await cut.InvokeAsync(() => cut.Find(".settings-sheet-backdrop").Click());
        await cut.InvokeAsync(() => cut.Find("[data-nav-row='password']").Click());
        Assert.Equal(string.Empty, cut.Find("input[type='password']").GetAttribute("value") ?? string.Empty);
    }

    [Fact]
    public async Task EmailRow_ShowsTheAddressReadOnlyWithAnAdministratorHint()
    {
        var cut = Render<SecuritySettingsPage>();
        cut.WaitForElement("[data-nav-row='email']");

        await cut.InvokeAsync(() => cut.Find("[data-nav-row='email']").Click());

        var sheet = cut.Find("[role='dialog']");
        Assert.Contains("owner@example.test", sheet.TextContent, StringComparison.Ordinal);
        Assert.Contains("Ask an administrator to change it", sheet.TextContent, StringComparison.Ordinal);
        Assert.Empty(sheet.QuerySelectorAll("input"));
    }

    [Fact]
    public async Task SecureSheet_AsksForAnEmail_WhenTheAccountHasNone()
    {
        _handler.HasPassword = false;
        _handler.Email = null;
        var cut = Render<SecuritySettingsPage>();
        cut.WaitForElement("[data-nav-row='password']");
        Assert.Contains("None", cut.Find("[data-nav-row='email']").TextContent, StringComparison.Ordinal);

        await cut.InvokeAsync(() => cut.Find("[data-nav-row='password']").Click());

        var form = cut.Find("[role='dialog'] form[action='/account/secure']");
        Assert.NotNull(form.QuerySelector("input[name='email']"));
        var submit = form.QuerySelector("button[type='submit']")!;
        Assert.True(submit.HasAttribute("disabled"));
        cut.Find("input[name='email']").Input("not an email");
        cut.Find("input[name='password']").Input("a-long-test-password");
        cut.Find("input[name='confirmPassword']").Input("a-long-test-password");
        Assert.True(cut.Find("[role='dialog'] button[type='submit']").HasAttribute("disabled"));
        cut.Find("input[name='email']").Input("owner@example.test");
        Assert.False(cut.Find("[role='dialog'] button[type='submit']").HasAttribute("disabled"));
    }

    [Fact]
    public async Task SecureSheet_DoesNotAskForAnEmail_WhenTheAccountAlreadyHasOne()
    {
        _handler.HasPassword = false;
        var cut = Render<SecuritySettingsPage>();
        cut.WaitForElement("[data-nav-row='password']");

        await cut.InvokeAsync(() => cut.Find("[data-nav-row='password']").Click());

        Assert.Null(cut.Find("[role='dialog'] form[action='/account/secure']").QuerySelector("input[name='email']"));
    }

    [Fact]
    public async Task Administrator_SeesAccessRows_AndSignOutEverywhereElseRevokesAfterConfirmation()
    {
        _handler.HasPassword = true;
        _session.Set("session", _handler.Account, _handler.Profile, _handler.CurrentSession, _handler.AdminAuthority());
        var dialogs = Render<AppDialogHost>();
        var cut = Render<SecuritySettingsPage>();

        cut.WaitForAssertion(() => Assert.Equal(["password", "email", "extra-protection", "sign-out-others", "photo-views"], RowKeys(cut)));
        cut.WaitForAssertion(() => Assert.Contains("1 other session", cut.Find("[data-nav-row='sign-out-others']").TextContent, StringComparison.Ordinal));
        // Paired phones and TVs live on the Devices page (a child of Security in the menu); this page no longer lists or loads them.
        Assert.DoesNotContain("Paired devices", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(_handler.Paths, path => path.StartsWith("/access/devices", StringComparison.Ordinal));

        await cut.InvokeAsync(() => cut.Find("[data-nav-row='sign-out-others']").Click());
        Assert.Equal(0, _handler.Revokes);
        dialogs.WaitForAssertion(() => Assert.Contains("Sign out other sessions", dialogs.Markup, StringComparison.Ordinal));
        await dialogs.InvokeAsync(() => dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Sign out other sessions").Click());

        cut.WaitForAssertion(() => Assert.Equal(1, _handler.Revokes));
        cut.WaitForAssertion(() => Assert.Contains("No other sessions", cut.Find("[data-nav-row='sign-out-others']").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void HouseholdAdministrator_GetsTheSameRowsAsAnyoneSignedIn()
    {
        _handler.HasPassword = true;
        _session.Set("session", _handler.Account, _handler.Profile, _handler.CurrentSession, _handler.HouseholdAdminAuthority());
        var cut = Render<SecuritySettingsPage>();

        cut.WaitForAssertion(() => Assert.Equal(["password", "email", "extra-protection", "sign-out-others", "photo-views"], RowKeys(cut)));
        Assert.Empty(cut.FindAll("[data-nav-row='devices']"));
    }

    [Fact]
    public async Task PhotoViewsRow_OpensAReadOnlyListNewestFirst_WithNoDeleteOrClearControls()
    {
        Views.Page = Page(25, hasMore: false,
            View(2, "Server admin", "Your photos", new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero)),
            View(1, "Guest admin", "Your photos", new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero)));
        var cut = Render<SecuritySettingsPage>();
        cut.WaitForElement("[data-nav-row='photo-views']");
        Assert.Empty(cut.FindAll("[role='dialog']"));

        await cut.InvokeAsync(() => cut.Find("[data-nav-row='photo-views']").Click());

        var sheet = cut.WaitForElement("[role='dialog']");
        Assert.Contains("Who viewed your photos", sheet.TextContent, StringComparison.Ordinal);
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[role='dialog'] tbody tr").Count));
        var rows = cut.FindAll("[role='dialog'] tbody tr");
        Assert.Contains("Server admin", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Guest admin", rows[1].TextContent, StringComparison.Ordinal);
        Assert.Contains("Your photos", rows[0].TextContent, StringComparison.Ordinal);
        // Times are labelled UTC: the Dashboard has no per-person time zone for an audit trail.
        Assert.Contains("UTC", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("9:00", rows[0].TextContent, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[role='dialog']").QuerySelector(".app-table-shell--cards"));
        // Read-only: the only control in the sheet is Done.
        Assert.Equal(["Done"], cut.FindAll("[role='dialog'] button").Select(button => button.TextContent.Trim()).ToArray());
        Assert.Equal([(0, 25)], Views.Requests);
    }

    [Fact]
    public async Task PhotoViewsRow_ShowsTheEmptyState_WhenNobodyElseHasLooked()
    {
        Views.Page = Page(25, hasMore: false);
        var cut = Render<SecuritySettingsPage>();
        cut.WaitForElement("[data-nav-row='photo-views']");

        await cut.InvokeAsync(() => cut.Find("[data-nav-row='photo-views']").Click());

        cut.WaitForAssertion(() => Assert.Contains("Nobody else has looked at your photos", cut.Find("[role='dialog']").TextContent, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[role='dialog'] table"));
    }

    [Fact]
    public async Task PhotoViewsRow_ListsNothingAndSaysSo_WhenTheEngineRefusesTheRequest()
    {
        // A person with no right to the list (or a failing Engine) never gets entries: the Dashboard renders only what the Engine returns.
        Views.Page = null;
        var cut = Render<SecuritySettingsPage>();
        cut.WaitForElement("[data-nav-row='photo-views']");

        await cut.InvokeAsync(() => cut.Find("[data-nav-row='photo-views']").Click());

        cut.WaitForAssertion(() => Assert.Contains("could not be loaded", cut.Find("[role='dialog']").TextContent, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[role='dialog'] table"));
    }

    [Fact]
    public async Task PhotoViewsRow_LoadsMoreInPagesAndStopsAtTheLatestHundred()
    {
        Views.PageFor = (offset, limit) => Page(limit, hasMore: true,
            Enumerable.Range(offset, limit).Select(i => View(1000 - i, $"Admin {i}", "Your photos", new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero).AddMinutes(-i))).ToArray());
        var cut = Render<SecuritySettingsPage>();
        cut.WaitForElement("[data-nav-row='photo-views']");
        await cut.InvokeAsync(() => cut.Find("[data-nav-row='photo-views']").Click());
        cut.WaitForAssertion(() => Assert.Equal(25, cut.FindAll("[role='dialog'] tbody tr").Count));

        for (var expected = 50; expected <= 100; expected += 25)
        {
            await cut.InvokeAsync(() => cut.FindAll("[role='dialog'] button").Single(b => b.TextContent.Trim() == "Show more").Click());
            var count = expected;
            cut.WaitForAssertion(() => Assert.Equal(count, cut.FindAll("[role='dialog'] tbody tr").Count));
        }

        Assert.DoesNotContain(cut.FindAll("[role='dialog'] button"), b => b.TextContent.Trim() == "Show more");
        Assert.Contains("Showing the latest 100", cut.Find("[role='dialog']").TextContent, StringComparison.Ordinal);
        Assert.Equal([(0, 25), (25, 25), (50, 25), (75, 25)], Views.Requests);
    }

    [Fact]
    public async Task PhotoViewsRow_ShowsAnInlineError_WhenLoadingMoreFails_AndKeepsWhatWasLoaded()
    {
        Views.PageFor = (offset, limit) => offset > 0
            ? null
            : Page(limit, hasMore: true, View(1, "Server admin", "Your photos", new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero)));
        var cut = Render<SecuritySettingsPage>();
        cut.WaitForElement("[data-nav-row='photo-views']");
        await cut.InvokeAsync(() => cut.Find("[data-nav-row='photo-views']").Click());
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[role='dialog'] tbody tr")));

        await cut.InvokeAsync(() => cut.FindAll("[role='dialog'] button").Single(b => b.TextContent.Trim() == "Show more").Click());

        cut.WaitForAssertion(() => Assert.Contains("Couldn't load more", cut.Find("[role='dialog']").TextContent, StringComparison.Ordinal));
        Assert.Single(cut.FindAll("[role='dialog'] tbody tr"));
        Assert.Contains(cut.FindAll("[role='dialog'] button"), b => b.TextContent.Trim() == "Show more");
    }

    private static ViewPhotoViewDto View(long id, string viewer, string opened, DateTimeOffset when) =>
        new(id, when, viewer, opened, "personal");

    private static ViewPhotoViewsPageDto Page(int limit, bool hasMore, params ViewPhotoViewDto[] items) =>
        new(new PagedResponse<ViewPhotoViewDto>(items, 0, limit, hasMore), false);

    public class PhotoViewsApiClient : AccountSecurityRenderTests.EmptyEngineApiClient
    {
        public ViewPhotoViewsPageDto? Page { get; set; }
        public Func<int, int, ViewPhotoViewsPageDto?>? PageFor { get; set; }
        public List<(int Offset, int Limit)> Requests { get; } = [];

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IEngineApiClient.GetViewPhotoViewsAsync))
            {
                return base.Invoke(targetMethod, args);
            }

            var offset = (int)args![0]!;
            var limit = (int)args[1]!;
            Requests.Add((offset, limit));
            return Task.FromResult(PageFor is null ? Page : PageFor(offset, limit));
        }
    }

    private static string[] RowKeys(IRenderedComponent<SecuritySettingsPage> cut) =>
        cut.FindAll("[data-nav-row]").Select(row => row.GetAttribute("data-nav-row")!).ToArray();

    // The signed-in account is an ordinary password account unless a test marks it as this-computer-only.
    private sealed class SwitchableAccountState : AuthenticationStateProvider
    {
        public bool ThisComputerOnly { get; set; }

        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "Owner"), new Claim("tuvima:authentication_method", ThisComputerOnly ? ThisComputerRequests.AuthenticationMethod : "Password")], "Test"))));
    }

    private sealed class SecurityClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("https://engine.example.test") };
    }

    private sealed class SecurityHandler : HttpMessageHandler
    {
        private bool _revoked;
        public Guid Account { get; } = Guid.NewGuid();
        public Guid Profile { get; } = Guid.NewGuid();
        public Guid CurrentSession { get; } = Guid.NewGuid();
        public bool HasPassword { get; set; }
        public string[] DismissedNotices { get; set; } = [];
        public string? Email { get; set; } = "owner@example.test";
        public int Revokes { get; private set; }

        public System.Collections.Concurrent.ConcurrentQueue<string> Paths { get; } = new();

        public DashboardAuthorityResponse HouseholdAdminAuthority() => new(
            Account, Profile, true, true, 1, 1, false, false, null, 1, [],
            [], ["household.manage"]);

        public DashboardAuthorityResponse MemberAuthority() => new(
            Account, Profile, true, true, 1, 1, false, false, null, 1, [],
            [], []);

        public DashboardAuthorityResponse AdminAuthority() => new(
            Account, Profile, true, true, 1, 1, true, true, null, 1, [],
            ["settings.administration"], ["household.manage"]);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Enqueue(path);
            if (request.Method == HttpMethod.Delete && path == "/auth/sessions/others")
            {
                Revokes++;
                _revoked = true;
                return Task.FromResult(Json(new RevokeOtherSessionsResponse(1)));
            }

            var response = path switch
            {
                "/access/self-service" => Json(new AccountSelfServiceResponse(
                    Account, Email, Profile, Profile, [], [],
                    new AccountSecurityCapabilitiesResponse(HasPassword, false, false, HasPassword, false, false, false, []))),
                "/access/self-service/notices" => Json(new AccountNoticesResponse(DismissedNotices)),
                "/auth/sessions" => Json(Sessions()),
                "/auth/passkeys" => Json(Array.Empty<PasskeyCredentialResponse>()),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
            return Task.FromResult(response);
        }

        private DeviceSessionResponse[] Sessions()
        {
            var current = new DeviceSessionResponse { Id = CurrentSession, AccountId = Account, DeviceName = "This computer", Client = "Test" };
            return _revoked
                ? [current]
                : [current, new DeviceSessionResponse { Id = Guid.NewGuid(), AccountId = Account, DeviceName = "Living room TV", Client = "Test" }];
        }

        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
