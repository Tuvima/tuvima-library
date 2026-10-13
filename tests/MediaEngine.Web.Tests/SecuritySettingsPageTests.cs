using System.Net;
using System.Net.Http.Json;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class SecuritySettingsPageTests : AsyncBunitContext
{
    private readonly SecurityHandler _handler = new();
    private readonly DashboardSessionAccessor _session = new();

    public SecuritySettingsPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddLogging();
        Services.AddNativeUiServices();
        Services.AddSingleton<IHttpClientFactory>(new SecurityClientFactory(_handler));
        Services.AddScoped<DashboardIdentityClient>();
        Services.AddSingleton(_session);
        Services.AddSingleton(System.Reflection.DispatchProxy.Create<IEngineApiClient, AccountSecurityRenderTests.EmptyEngineApiClient>());
        Services.AddScoped<IItsYouConfirmer>(_ => new ConfirmedActionRunnerTests.SpyConfirmer(confirmed: false));
        Services.AddScoped<ConfirmedActionRunner>();
        Services.AddSingleton<IReadOnlyList<RegisteredExternalAuthProvider>>([]);
        Render<AppPopoverHost>();
    }

    [Fact]
    public void WithoutAPassword_OnlyPasswordAndEmailRowsRender()
    {
        _handler.HasPassword = false;
        var cut = Render<SecuritySettingsPage>();

        cut.WaitForAssertion(() => Assert.Equal(["password", "email"], RowKeys(cut)));
        Assert.Contains("Not set", cut.Find("[data-nav-row='password']").TextContent, StringComparison.Ordinal);
        Assert.Contains("owner@example.test", cut.Find("[data-nav-row='email']").TextContent, StringComparison.Ordinal);
        Assert.Single(cut.FindAll(".security-settings__group-title"));
    }

    [Fact]
    public void WithAPassword_ExtraProtectionAppearsAndAccessStaysHiddenForNonAdministrators()
    {
        _handler.HasPassword = true;
        var cut = Render<SecuritySettingsPage>();

        cut.WaitForAssertion(() => Assert.Equal(["password", "email", "extra-protection"], RowKeys(cut)));
        Assert.Contains("Off", cut.Find("[data-nav-row='extra-protection']").TextContent, StringComparison.Ordinal);
        Assert.Single(cut.FindAll(".security-settings__group-title"));
        Assert.Empty(cut.FindAll("[role='dialog']"));
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
    public async Task Administrator_SeesAccessRows_AndSignOutEverywhereElseRevokesAfterConfirmation()
    {
        _handler.HasPassword = true;
        _session.Set("session", _handler.Account, _handler.Profile, _handler.CurrentSession, _handler.AdminAuthority());
        var dialogs = Render<AppDialogHost>();
        var cut = Render<SecuritySettingsPage>();

        cut.WaitForAssertion(() => Assert.Equal(["password", "email", "extra-protection", "sign-out-others", "devices"], RowKeys(cut)));
        cut.WaitForAssertion(() => Assert.Contains("1 other session", cut.Find("[data-nav-row='sign-out-others']").TextContent, StringComparison.Ordinal));
        Assert.Equal("/settings/network/apps", cut.Find("[data-nav-row='devices']").GetAttribute("href"));

        await cut.InvokeAsync(() => cut.Find("[data-nav-row='sign-out-others']").Click());
        Assert.Equal(0, _handler.Revokes);
        dialogs.WaitForAssertion(() => Assert.Contains("Sign out other sessions", dialogs.Markup, StringComparison.Ordinal));
        await dialogs.InvokeAsync(() => dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Sign out other sessions").Click());

        cut.WaitForAssertion(() => Assert.Equal(1, _handler.Revokes));
        cut.WaitForAssertion(() => Assert.Contains("No other sessions", cut.Find("[data-nav-row='sign-out-others']").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void PhotoViewsPanel_IsNoLongerReferencedAnywhereInTheDashboard()
    {
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");
        Assert.False(File.Exists(Path.Combine(web, "Components", "Settings", "PhotoViewsSection.razor")));
        var references = Directory.EnumerateFiles(web, "*.*", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && (path.EndsWith(".razor", StringComparison.Ordinal) || path.EndsWith(".cs", StringComparison.Ordinal)))
            .Where(path => File.ReadAllText(path).Contains("PhotoViewsSection", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(references);
    }

    private static string[] RowKeys(IRenderedComponent<SecuritySettingsPage> cut) =>
        cut.FindAll("[data-nav-row]").Select(row => row.GetAttribute("data-nav-row")!).ToArray();

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
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
        public int Revokes { get; private set; }

        public DashboardAuthorityResponse AdminAuthority() => new(
            Account, Profile, true, true, 1, 1, true, true, null, 1, [],
            ["settings.administration"], ["household.manage"]);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Delete && path == "/auth/sessions/others")
            {
                Revokes++;
                _revoked = true;
                return Task.FromResult(Json(new RevokeOtherSessionsResponse(1)));
            }

            var response = path switch
            {
                "/access/self-service" => Json(new AccountSelfServiceResponse(
                    Account, "owner@example.test", Profile, Profile, [], [],
                    new AccountSecurityCapabilitiesResponse(HasPassword, false, false, HasPassword, false, false, false, []))),
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
