using System.Net;
using System.Security.Claims;
using System.Text;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Setup;
using MediaEngine.Web.Components.Navigation;
using MediaEngine.Web.Components.Setup;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class ThisComputerDashboardTests : AsyncBunitContext
{
    private static readonly RegisteredExternalAuthProvider[] NoProviders = [];

    private IRenderedComponent<SetupAdministratorStage> RenderStage(bool available, string mode, Action<string>? onMode = null) =>
        Render<SetupAdministratorStage>(parameters => parameters
            .Add(stage => stage.ThisComputerAvailable, available)
            .Add(stage => stage.SignInMode, mode)
            .Add(stage => stage.SignInModeChanged, new Microsoft.AspNetCore.Components.EventCallbackFactory()
                .Create<string>(this, value => onMode?.Invoke(value))));

    [Fact]
    public void Setup_ShowsTheTwoChoicesOnlyWhenThisComputerIsAvailableAndNothingIsChosenYet()
    {
        var choices = RenderStage(available: true, mode: string.Empty);

        Assert.Contains("Start without a password", choices.Markup);
        Assert.Contains("Set a password now", choices.Markup);
        Assert.DoesNotContain("Confirm password", choices.Markup);

        var elsewhere = RenderStage(available: false, mode: string.Empty);

        Assert.DoesNotContain("Start without a password", elsewhere.Markup);
        Assert.Contains("Confirm password", elsewhere.Markup);
    }

    [Fact]
    public void Setup_UseOnThisComputerAsksOnlyForAName()
    {
        var form = RenderStage(available: true, mode: SetupSignInModes.ThisComputer);

        Assert.Contains("Display name", form.Markup);
        Assert.DoesNotContain("Email", form.Markup.Replace("Add an email and password any time", string.Empty, StringComparison.Ordinal));
        Assert.Contains("Add an email and password any time to use it away from home", form.Markup);
        Assert.DoesNotContain("Password", form.Markup);
        Assert.DoesNotContain("PIN", form.Markup);
    }

    [Fact]
    public void Setup_SettingAPasswordNowKeepsTodaysForm()
    {
        var form = RenderStage(available: true, mode: SetupSignInModes.Password);

        Assert.Contains("Confirm password", form.Markup);
        Assert.Contains("Email", form.Markup);
        Assert.DoesNotContain("Start without a password", form.Markup);
    }

    [Fact]
    public void Setup_UseOnThisComputer_CanBeCreatedWithJustAName()
    {
        var form = RenderStage(available: true, mode: SetupSignInModes.ThisComputer);
        var create = () => form.FindAll("button").Single(button => button.TextContent.Contains("Create administrator", StringComparison.Ordinal));
        Assert.True(create().HasAttribute("disabled"));

        var named = Render<SetupAdministratorStage>(parameters => parameters
            .Add(stage => stage.ThisComputerAvailable, true)
            .Add(stage => stage.SignInMode, SetupSignInModes.ThisComputer)
            .Add(stage => stage.DisplayName, "Owner"));

        Assert.False(named.FindAll("button").Single(button => button.TextContent.Contains("Create administrator", StringComparison.Ordinal)).HasAttribute("disabled"));
    }

    [Fact]
    public void Setup_ChoosingAnOptionReportsTheMode()
    {
        string? chosen = null;
        var choices = RenderStage(available: true, mode: string.Empty, value => chosen = value);

        choices.FindAll("button.app-choice-card")[0].Click();

        Assert.Equal(SetupSignInModes.ThisComputer, chosen);
    }

    [Fact]
    public void SetupPage_SendsThisComputerOnlyFromThisComputerNotThroughAProxyAndNotInAContainer()
    {
        var source = File.ReadAllText(RepoPath("src/MediaEngine.Web/Components/Pages/SetupPage.razor")).Replace("\r\n", "\n");

        Assert.Contains("!= MediaEngine.Web.Services.Configuration.IngressKind.Remote\n        && !ClientForwarded;", source, StringComparison.Ordinal);
        Assert.Contains("SignIn = UsesThisComputer ? SetupSignInModes.ThisComputer : SetupSignInModes.Password,", source, StringComparison.Ordinal);
        Assert.Contains("OriginalClientIngress = ClientForwarded ? ClientIngressValues.Remote : (ClientIngress ?? ClientIngressValues.Remote),", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LoginPage_OffersContinueAsOnlyWhenTheEngineNamedAnAccount()
    {
        var here = LoginPage("Sam");
        var elsewhere = LoginPage(null);

        Assert.Contains("Go back in as Sam", here);
        Assert.Contains("value=\"this-computer\"", here);
        Assert.Contains("name=\"password\"", here);
        Assert.DoesNotContain("Go back in as", elsewhere);
        Assert.DoesNotContain("this-computer", elsewhere);
    }

    [Fact]
    public void LoginPage_EncodesTheAccountName()
    {
        Assert.Contains("Go back in as &lt;b&gt;Sam&lt;/b&gt;", LoginPage("<b>Sam</b>"));
    }

    private static string LoginPage(string? name) =>
        DashboardAuthenticationEndpoints.LoginPage(
            "token", new SignInMethodsResponse(true, false, [], true), NoProviders,
            "11111111-2222-3333-4444-555555555555", "/", atPublicOrigin: false, thisComputerName: name);

    [Fact]
    public void SecureAccountPrompt_IsOnlyForAThisComputerOnlyAccount()
    {
        Assert.True(SecureAccountPrompt.IsThisComputerOnly(PrincipalWith(ThisComputerRequests.AuthenticationMethod)));

        Assert.False(SecureAccountPrompt.IsThisComputerOnly(PrincipalWith("Password")));
        Assert.False(SecureAccountPrompt.IsThisComputerOnly(PrincipalWith("Passkey")));
        Assert.False(SecureAccountPrompt.IsThisComputerOnly(PrincipalWith("")));
        Assert.False(SecureAccountPrompt.IsThisComputerOnly(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    [Fact]
    public void AccountMenu_OffersSecureAccountOnlyWhileTheAccountIsThisComputerOnly()
    {
        Services.AddLocalization();
        var offered = Render<TopNavAccountMenu>(parameters => parameters
            .Add(menu => menu.DisplayName, "Owner")
            .Add(menu => menu.ShowSecureAccount, true));
        offered.Find(".top-nav-account-menu__trigger").Click();

        Assert.Single(offered.FindAll(".top-nav-account-menu__trigger .top-nav-account-menu__attention-dot"));
        Assert.Contains(offered.FindAll("a[role=menuitem]"), link => link.GetAttribute("href") == SecureAccountPrompt.SecureAccountHref);

        var secured = Render<TopNavAccountMenu>(parameters => parameters
            .Add(menu => menu.DisplayName, "Owner"));
        secured.Find(".top-nav-account-menu__trigger").Click();

        Assert.Empty(secured.FindAll(".top-nav-account-menu__attention-dot"));
        Assert.DoesNotContain(secured.FindAll("a[role=menuitem]"), link => link.GetAttribute("href") == SecureAccountPrompt.SecureAccountHref);
    }

    [Fact]
    public void ThisComputerBar_IsGoneFromTheDashboardSource()
    {
        var webRoot = Path.GetDirectoryName(Path.GetDirectoryName(RepoPath("src/MediaEngine.Web/Shared/MainLayout.razor")))!;

        Assert.False(File.Exists(Path.Combine(webRoot, "Components", "Navigation", "ThisComputerOnlyBanner.razor")));
        Assert.False(File.Exists(Path.Combine(webRoot, "Components", "Navigation", "ThisComputerOnlyBanner.razor.css")));

        var offenders = Directory.EnumerateFiles(webRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".razor", StringComparison.Ordinal) || path.EndsWith(".css", StringComparison.Ordinal) || path.EndsWith(".cs", StringComparison.Ordinal))
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(segment => segment is "obj" or "bin"))
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                return source.Contains("No password set", StringComparison.Ordinal)
                    || source.Contains("ThisComputerOnlyBanner", StringComparison.Ordinal)
                    || source.Contains("this-computer-banner", StringComparison.Ordinal);
            })
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void MainLayout_ShowsTheAccountPromptInsteadOfABar()
    {
        var layout = File.ReadAllText(RepoPath("src/MediaEngine.Web/Shared/MainLayout.razor"));
        var css = File.ReadAllText(RepoPath("src/MediaEngine.Web/Shared/MainLayout.razor.css"));

        // No top bar, so no padding or engine-banner offset for one.
        Assert.DoesNotContain("tl-this-computer-banner", layout + css, StringComparison.Ordinal);
        Assert.DoesNotContain("with-banner", layout + css, StringComparison.Ordinal);
        Assert.Contains("_thisComputerOnly = SecureAccountPrompt.IsThisComputerOnly(", layout, StringComparison.Ordinal);
        Assert.Contains("ShowSecureAccount=\"@_thisComputerOnly\"", layout, StringComparison.Ordinal);
        Assert.Contains("OnSecureAccountRequired", layout, StringComparison.Ordinal);
    }

    [Fact]
    public void Ingress_IsSentOnlyForAVisitorOnThisComputerOrTheHomeNetwork()
    {
        using var here = new HttpRequestMessage();
        ThisComputerRequests.AddIngress(here, ClientIngressValues.ThisComputer);
        using var home = new HttpRequestMessage();
        ThisComputerRequests.AddIngress(home, ClientIngressValues.HomeNetwork);
        using var unknown = new HttpRequestMessage();
        ThisComputerRequests.AddIngress(unknown, null);

        Assert.Equal(ClientIngressValues.ThisComputer, Assert.Single(here.Headers.GetValues(ClientIngressValues.ValidateHeader)));
        Assert.Equal(ClientIngressValues.HomeNetwork, Assert.Single(home.Headers.GetValues(ClientIngressValues.ValidateHeader)));
        Assert.False(unknown.Headers.Contains(ClientIngressValues.ValidateHeader));
    }

    [Fact]
    public void Ingress_DoesNotOverwriteAHeaderTheCallerAlreadySet()
    {
        using var request = new HttpRequestMessage();
        request.Headers.TryAddWithoutValidation(ClientIngressValues.ValidateHeader, ClientIngressValues.ThisComputer);

        ThisComputerRequests.AddIngress(request, ClientIngressValues.ThisComputer);

        Assert.Single(request.Headers.GetValues(ClientIngressValues.ValidateHeader));
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "{\"code\":\"secure_account_first\",\"detail\":\"x\"}", true)]
    [InlineData(HttpStatusCode.Conflict, "{\"code\":\"not_available_here\"}", false)]
    [InlineData(HttpStatusCode.Conflict, "<html>proxy</html>", false)]
    [InlineData(HttpStatusCode.BadRequest, "{\"code\":\"secure_account_first\"}", false)]
    public async Task SecureAccountRefusal_IsRecognisedOnlyByItsCode(HttpStatusCode status, string body, bool expected)
    {
        using var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        Assert.Equal(expected, await ThisComputerRequests.IsSecureAccountRefusalAsync(response, CancellationToken.None));
        // The caller can still read the body afterwards.
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CircuitCalls_SayWhereTheVisitorIsAndSurfaceTheSecureAccountRefusal()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<DashboardSessionAccessor>();
        var captured = new List<string?>();
        var respond = new Func<HttpRequestMessage, HttpResponseMessage>(request =>
        {
            captured.Add(request.Headers.TryGetValues(ClientIngressValues.ValidateHeader, out var values) ? values.Single() : null);
            return request.RequestUri!.AbsolutePath == "/locked"
                ? new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = new StringContent("{\"code\":\"secure_account_first\"}", Encoding.UTF8, "application/json"),
                }
                : new HttpResponseMessage(HttpStatusCode.OK);
        });
        services.AddHttpClient("EngineApi", client => client.BaseAddress = new Uri("http://engine.test"))
            .ConfigurePrimaryHttpMessageHandler(() => new RespondingHandler(respond));
        services.AddScoped<DashboardCircuitHttpClientFactory>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<DashboardSessionAccessor>();
        session.Set("token", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null);
        var notices = 0;
        session.OnSecureAccountRequired += () => notices++;
        var factory = scope.ServiceProvider.GetRequiredService<DashboardCircuitHttpClientFactory>();

        session.LastIngress = ClientIngressValues.ThisComputer;
        using var client = factory.CreateClient("EngineApi");
        using (await client.GetAsync("/ok")) { }
        using (var locked = await client.GetAsync("/locked"))
        {
            Assert.Equal(HttpStatusCode.Conflict, locked.StatusCode);
        }

        session.LastIngress = ClientIngressValues.HomeNetwork;
        using (await client.GetAsync("/ok")) { }

        Assert.Equal(new string?[] { ClientIngressValues.ThisComputer, ClientIngressValues.ThisComputer, ClientIngressValues.HomeNetwork }, captured);
        Assert.Equal(1, notices);
    }

    private static string RepoPath(string relative)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"{relative} was not found above the test output folder.");
    }

    private sealed class RespondingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private static ClaimsPrincipal PrincipalWith(string method) =>
        new(new ClaimsIdentity([new Claim("tuvima:authentication_method", method)], "Test"));
}
