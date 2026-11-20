using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Configuration;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Settings;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The own-domain wizard (spec remote-access WP5, D11 and D12): Address, Point it here, Secure it and Check against a spy Engine client.
/// Each way in (tunnel, Caddy file, router door) reaches a green Check; a failed check says why with one next step; the one-click fix is
/// asked of the Engine; the Caddy file is the person's address filled in; every help link goes to the public guide.
/// </summary>
public sealed class RemoteAccessOwnDomainTests : AsyncBunitContext
{
    private readonly NetworkSettingsDto _settings = new();
    private readonly List<string> _savedModes = [];
    private readonly List<string> _calls = [];
    private RemoteCheckResultDto? _check = new();
    private RemoteFixResultDto? _fix = new() { Action = RemoteCheckFixActions.TrustProxy, Changed = true, ProxyPort = 5017, RestartRequired = true };

    public RemoteAccessOwnDomainTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
    }

    private IRenderedComponent<RemoteAccessSettingsPanel> RenderToTheOwnDomainWizard()
    {
        _settings.Remote.ConnectionMode = "local-only";
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetRemoteCheckAsync), _ =>
            {
                _calls.Add(nameof(IEngineApiClient.GetRemoteCheckAsync));
                return Task.FromResult(_check);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetOutsideSignInsAsync), _ => Task.FromResult<IReadOnlyList<OutsideSignInDto>?>([]));
            stub.SetHandler(nameof(IEngineApiClient.GetNetworkAppAddressesAsync), _ =>
                Task.FromResult<NetworkAppAddressesDto?>(new NetworkAppAddressesDto()));
            stub.SetHandler(nameof(IEngineApiClient.TrustRemoteProxyAsync), _ =>
            {
                _calls.Add(nameof(IEngineApiClient.TrustRemoteProxyAsync));
                // The fix changes what the next check finds.
                _check = new RemoteCheckResultDto();
                return Task.FromResult(_fix);
            });
        });
        Services.AddSingleton(api);
        Services.AddScoped<RemoteAccessFlowState>();
        Services.AddSingleton<IFriendInviteService>(new FriendInviteServiceSpy());

        var cut = Render<RemoteAccessSettingsPanel>(parameters => parameters
            .Add(p => p.Settings, _settings)
            .Add(p => p.SaveRequested, EventCallback.Factory.Create(this, () => _savedModes.Add(_settings.Remote.ConnectionMode))));
        Click(cut, "set-up");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-remote-action='see-options']")));
        Click(cut, "see-options");
        Click(cut, "choose-own-address");
        Click(cut, "continue");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-remote-screen='connect-own-address']")));
        return cut;
    }

    private static void Click(IRenderedComponent<RemoteAccessSettingsPanel> cut, string action) =>
        cut.Find($"[data-remote-action='{action}']").Click();

    private static string DomainScreen(IRenderedComponent<RemoteAccessSettingsPanel> cut) =>
        cut.Find("[data-remote-domain-screen]").GetAttribute("data-remote-domain-screen")!;

    private static void GiveTheAddress(IRenderedComponent<RemoteAccessSettingsPanel> cut, string domain = "example.com")
    {
        Click(cut, "use-my-domain");
        cut.Find("input[placeholder='yourname.com']").Input(domain);
        Click(cut, "continue");
        cut.WaitForAssertion(() => Assert.Equal("point", DomainScreen(cut)));
    }

    private static void ToCheck(IRenderedComponent<RemoteAccessSettingsPanel> cut)
    {
        Click(cut, "continue");
        cut.WaitForAssertion(() => Assert.Equal("secure", DomainScreen(cut)));
        Click(cut, "continue");
        cut.WaitForAssertion(() => Assert.Equal("check", DomainScreen(cut)));
        cut.WaitForAssertion(() => Assert.DoesNotContain("Checking", cut.Find("[data-remote-screen='domain-check']").TextContent, StringComparison.Ordinal));
    }

    private static RemoteCheckFindingDto Finding(string key, string status, string title, string detail, string? nextStep = null, string? fix = null) =>
        new() { Key = key, Status = status, Title = title, Detail = detail, NextStep = nextStep, FixAction = fix };

    [Fact]
    public void TheAddressScreen_BuildsTheFullAddressFromTheFrontNameAndTheDomain_AndSavesItBeforeMovingOn()
    {
        var cut = RenderToTheOwnDomainWizard();

        Assert.Equal("address", DomainScreen(cut));
        Assert.True(cut.Find("[data-remote-action='continue']").HasAttribute("disabled"));
        Click(cut, "use-my-domain");
        cut.Find("input[placeholder='yourname.com']").Input("Example.com");

        Assert.Contains("https://library.example.com", cut.Find("[data-remote-built-address]").TextContent, StringComparison.Ordinal);
        Click(cut, "continue");
        cut.WaitForAssertion(() => Assert.Equal("point", DomainScreen(cut)));
        Assert.Equal("https://library.example.com", _settings.Remote.PublicHostname);
        // Choosing the own-address way saved "custom" first; saving the address is a second save of the same way.
        Assert.All(_savedModes, mode => Assert.Equal("custom", mode));
        Assert.True(_savedModes.Count >= 2);
    }

    [Fact]
    public void ADomainThatCannotBeAWebAddress_SaysSo_AndContinueStaysOff()
    {
        var cut = RenderToTheOwnDomainWizard();
        Click(cut, "use-my-domain");

        cut.Find("input[placeholder='yourname.com']").Input("not a domain");

        Assert.NotEmpty(cut.FindAll("[data-remote-address-problem]"));
        Assert.True(cut.Find("[data-remote-action='continue']").HasAttribute("disabled"));
        Assert.Null(_settings.Remote.PublicHostname);
    }

    [Fact]
    public void TheDomainHelpLink_GoesToTheDocsSite_AndOpensInANewTab()
    {
        var cut = RenderToTheOwnDomainWizard();

        var link = cut.Find("[data-remote-link='domain-help']");

        Assert.Equal(ProjectLinks.DocsSite + "guides/away-from-home/domain-names/", link.GetAttribute("href"));
        Assert.Equal("_blank", link.GetAttribute("target"));
        Assert.Contains("noopener", link.GetAttribute("rel"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheTunnelRoute_ListsTickingSteps_WarnsAboutCloudflare_AndReachesAGreenCheck()
    {
        var cut = RenderToTheOwnDomainWizard();
        GiveTheAddress(cut);

        Click(cut, "route-tunnel");

        var rows = cut.FindAll("[data-remote-card='steps'] [data-remote-row]").Select(row => row.GetAttribute("data-remote-row")!).ToArray();
        Assert.Equal(["tunnel-account", "tunnel-connector", "tunnel-hostname", "route-live"], rows);
        Assert.Contains("Cloudflare can see this traffic", cut.Find("[data-remote-card='tunnel-warning']").TextContent, StringComparison.Ordinal);
        Assert.Contains("http://localhost:5017", cut.Find("[data-remote-row='tunnel-hostname']").TextContent, StringComparison.Ordinal);
        Assert.Equal("working", cut.Find("[data-remote-row='route-live']").GetAttribute("data-remote-status"));
        ToCheck(cut);
        Assert.NotEmpty(cut.FindAll("[data-remote-all-green]"));
        Assert.All(cut.FindAll("[data-remote-screen='domain-check'] [data-remote-row]"), row => Assert.Equal("working", row.GetAttribute("data-remote-status")));
    }

    [Fact]
    public void TheCaddyRoute_ShowsTheFileWithTheAddressFilledIn_ADownloadAndACopyButton_AndReachesAGreenCheck()
    {
        var cut = RenderToTheOwnDomainWizard();
        GiveTheAddress(cut);

        Click(cut, "route-caddy");

        var file = cut.Find("[data-remote-caddy-file]").TextContent;
        Assert.Contains("library.example.com {", file, StringComparison.Ordinal);
        Assert.Contains("reverse_proxy localhost:5017", file, StringComparison.Ordinal);
        var download = cut.Find("[data-remote-action='download-caddy']");
        Assert.Equal("Caddyfile", download.GetAttribute("download"));
        Assert.StartsWith("data:text/plain", download.GetAttribute("href"), StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll("[data-remote-action='copy-caddy']"));
        ToCheck(cut);
        Assert.NotEmpty(cut.FindAll("[data-remote-all-green]"));
    }

    [Fact]
    public void TheRouterDoorRoute_TriesTheRouterForTheUser_SavesTheDirectWay_AndReachesAGreenCheck()
    {
        var cut = RenderToTheOwnDomainWizard();
        GiveTheAddress(cut);

        Click(cut, "route-routerdoor");
        Click(cut, "try-router-door");

        cut.WaitForAssertion(() => Assert.True(_settings.Remote.AutomaticRouterConfiguration));
        Assert.Equal("direct-only", _settings.Remote.ConnectionMode);
        Assert.Equal(443, _settings.Remote.ExternalPort);
        Assert.Equal(443, _settings.Remote.TlsTerminationPort);
        ToCheck(cut);
        Assert.NotEmpty(cut.FindAll("[data-remote-all-green]"));
    }

    [Fact]
    public void AFailedCertificate_SaysWhyAndGivesTheSingleNextStep_WithALinkToItsTroubleshootingEntry_AndNoGreenTick()
    {
        _check = new RemoteCheckResultDto
        {
            Findings =
            [
                Finding(RemoteCheckFindingKeys.CertificateInvalid, RemoteCheckStatuses.Fail, "The certificate is not valid",
                    "The certificate for library.example.com has expired.", "Restart Caddy so it can renew the certificate."),
            ],
        };
        var cut = RenderToTheOwnDomainWizard();
        GiveTheAddress(cut);
        Click(cut, "route-caddy");

        ToCheck(cut);

        var row = cut.Find("[data-remote-screen='domain-check'] [data-remote-row='certificate']");
        Assert.Equal("broken", row.GetAttribute("data-remote-status"));
        Assert.Contains("has expired", row.TextContent, StringComparison.Ordinal);
        Assert.Equal("Restart Caddy so it can renew the certificate.", row.QuerySelector("[data-remote-next-step]")!.TextContent.Trim());
        Assert.Single(row.QuerySelectorAll("[data-remote-next-step]"));
        Assert.Equal(ProjectLinks.DocsSite + "guides/away-from-home/testing-and-troubleshooting/#certificate_invalid",
            row.QuerySelector("[data-remote-link='help-certificate_invalid']")!.GetAttribute("href"));
        Assert.Empty(cut.FindAll("[data-remote-all-green]"));
    }

    [Fact]
    public void WhenLibraryDoesNotTrustTheProxy_TheCheckSaysSo_AndFixItForMeAsksTheEngine_ThenChecksAgain()
    {
        _check = new RemoteCheckResultDto
        {
            Findings =
            [
                Finding(RemoteCheckFindingKeys.ProxyNotTrusted, RemoteCheckStatuses.Warn, "Library does not trust your proxy yet",
                    "Visitors would look like they are on this computer.", "Let Library trust your proxy.", RemoteCheckFixActions.TrustProxy),
            ],
        };
        var cut = RenderToTheOwnDomainWizard();
        GiveTheAddress(cut);
        Click(cut, "route-caddy");
        ToCheck(cut);
        var visitors = cut.Find("[data-remote-screen='domain-check'] [data-remote-row='visitors']");
        Assert.Equal("broken", visitors.GetAttribute("data-remote-status"));
        Assert.Equal("Fix it for me", cut.Find("[data-remote-action='fix-trust-proxy']").TextContent.Trim());

        Click(cut, "fix-trust-proxy");

        cut.WaitForAssertion(() => Assert.Contains(nameof(IEngineApiClient.TrustRemoteProxyAsync), _calls));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-remote-all-green]")));
        Assert.Equal(5017, _settings.Remote.ProxyPort);
    }

    [Fact]
    public void TheFixButton_IsOnlyOfferedWhereTheEngineNamedAFix()
    {
        _check = new RemoteCheckResultDto
        {
            Findings = [Finding(RemoteCheckFindingKeys.NameNotPointingHere, RemoteCheckStatuses.Fail, "The name does not point here", "It points somewhere else.", "Change the record at your domain.")],
        };
        var cut = RenderToTheOwnDomainWizard();
        GiveTheAddress(cut);
        Click(cut, "route-tunnel");

        ToCheck(cut);

        Assert.Empty(cut.FindAll("[data-remote-action='fix-trust-proxy']"));
        Assert.Equal("broken", cut.Find("[data-remote-screen='domain-check'] [data-remote-row='resolves']").GetAttribute("data-remote-status"));
    }

    [Fact]
    public void TheCaddyFile_HasTheAddressFilledIn_AndSendsVisitorsToTheProxyPort()
    {
        var text = CaddyFile.Render("https://library.example.com", 5017);

        Assert.Contains("library.example.com {", text, StringComparison.Ordinal);
        Assert.Contains("\treverse_proxy localhost:5017", text, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", text.Split('\n').Where(line => !line.StartsWith('#')).Aggregate(string.Empty, (all, line) => all + line), StringComparison.Ordinal);
        Assert.StartsWith("data:text/plain", CaddyFile.DownloadAddress(text), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("library", "example.com", "https://library.example.com")]
    [InlineData(" Library ", " Example.COM. ", "https://library.example.com")]
    [InlineData("tv", "my-site.co.uk", "https://tv.my-site.co.uk")]
    [InlineData("", "example.com", null)]
    [InlineData("library", "localhost", null)]
    [InlineData("library", "192.168.1.5", null)]
    [InlineData("lib rary", "example.com", null)]
    [InlineData("library", "-bad.com", null)]
    public void TheAddress_IsBuiltFromItsTwoParts_OrRefusedWhenItCannotBeAWebAddress(string front, string domain, string? expected) =>
        Assert.Equal(expected, OwnDomainAddress.Build(front, domain));

    [Fact]
    public void TheHelpLinks_AreBuiltFromTheDocsSiteAddress_WithOneAnchorPerFindingKey()
    {
        Assert.StartsWith(ProjectLinks.DocsSite, RemoteAccessGuideLinks.DomainNames, StringComparison.Ordinal);
        foreach (var key in new[] { RemoteCheckFindingKeys.ProxyNotTrusted, RemoteCheckFindingKeys.CertificateInvalid, RemoteCheckFindingKeys.CertificateExpiring,
                     RemoteCheckFindingKeys.NameNotPointingHere, RemoteCheckFindingKeys.PublicAddressUnreachable })
        {
            Assert.EndsWith("/testing-and-troubleshooting/#" + key, RemoteAccessGuideLinks.Troubleshooting(key), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheInvitationPage_OffersTheAppFirst_WithTheAddressAndCode_AndTheStoreForThePhone_ButNeverALinkWhenTheStoreIsUnpublished()
    {
        var links = new AppStoreLinks(AppStore: "https://apps.apple.com/app/example", GooglePlay: string.Empty);

        var iphone = InviteAppHandoff.Render("https://library.example.com/", "ABCDE-FGHIJ", "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0)", links);
        var android = InviteAppHandoff.Render("https://library.example.com", "ABCDE-FGHIJ", "Mozilla/5.0 (Linux; Android 14)", links);
        var desktop = InviteAppHandoff.Render("https://library.example.com", "ABCDE-FGHIJ", "Mozilla/5.0 (Windows NT 10.0)", links);

        Assert.Contains("tuvima://invite?address=https%3A%2F%2Flibrary.example.com&amp;code=ABCDE-FGHIJ", iphone, StringComparison.Ordinal);
        Assert.Contains("apps.apple.com", iphone, StringComparison.Ordinal);
        Assert.DoesNotContain("data-invite-store", android, StringComparison.Ordinal);
        Assert.DoesNotContain("data-invite-store", desktop, StringComparison.Ordinal);
        Assert.Contains("data-invite-open-app", desktop, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInvitationLink_IsTheAwayAddressPlusTheInvitationPageAndTheOneTimeCode()
    {
        Assert.Equal("https://library.example.com/auth/invite?code=ABCDE-FGHIJ", FriendInviteLink.Build("https://library.example.com/", "ABCDE-FGHIJ"));
    }
}
