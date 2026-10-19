using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Configuration;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>Personal &gt; Overview &gt; Apps (D24): store links once published, the server address for everyone, and a QR code of that address only.</summary>
public sealed class OverviewAppsPageTests : AsyncBunitContext
{
    private const string Home = "http://192.168.1.20:5016";
    private const string Away = "https://tuvima.example.com";

    private NetworkAppAddressesDto? _addresses = new() { AtHome = Home };
    private AppStoreLinks _links = new();
    private bool _serverAdministrator;

    public OverviewAppsPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddNativeUiServices();
    }

    private IRenderedComponent<OverviewAppsPage> RenderPage()
    {
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetNetworkAppAddressesAsync), _ => Task.FromResult(_addresses))));
        Services.AddSingleton(_links);
        var id = Guid.NewGuid();
        string[] navigation = _serverAdministrator ? ["settings.administration"] : [];
        var session = new DashboardSessionAccessor();
        session.Set("test-session-token", id, id, Guid.NewGuid(),
            new DashboardAuthorityResponse(id, id, true, true, 1, 1, _serverAdministrator, _serverAdministrator, null, 1, [], navigation, []));
        Services.AddScoped(_ => session);

        var cut = Render<OverviewAppsPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-settings-overview-apps]")));
        cut.WaitForAssertion(() => Assert.True(
            cut.FindAll("[data-apps-address]").Count > 0 || cut.Markup.Contains("The server address is unavailable", StringComparison.Ordinal)));
        return cut;
    }

    [Fact]
    public void AMember_SeesTheAddressAtHome_WithCopy()
    {
        var cut = RenderPage();

        var home = cut.Find("[data-apps-address='at-home']");
        Assert.Contains(Home, home.TextContent, StringComparison.Ordinal);
        Assert.Single(home.QuerySelectorAll("button"), button => button.TextContent.Contains("Copy", StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-apps-address='away-from-home']"));
    }

    [Fact]
    public void GetTheApp_OffersInstallingTheLibraryOnThisDevice()
    {
        var cut = RenderPage();

        Assert.Contains("Install on this device", cut.Find("[data-settings-overview-apps]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenAnOutsideAddressIsSet_BothAddressesShow_EachWithCopy()
    {
        _addresses = new() { AtHome = Home, AwayFromHome = Away };

        var cut = RenderPage();

        Assert.Contains(Home, cut.Find("[data-apps-address='at-home']").TextContent, StringComparison.Ordinal);
        var away = cut.Find("[data-apps-address='away-from-home']");
        Assert.Contains(Away, away.TextContent, StringComparison.Ordinal);
        Assert.Contains("Away from home", away.TextContent, StringComparison.Ordinal);
        Assert.Single(away.QuerySelectorAll("button"), button => button.TextContent.Contains("Copy", StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-apps-no-outside-address]"));
    }

    [Fact]
    public void Copy_PutsTheAddressOnTheClipboard()
    {
        _addresses = new() { AtHome = Home, AwayFromHome = Away };
        var cut = RenderPage();

        cut.Find("[data-apps-address='away-from-home'] button").Click();

        var call = Assert.Single(JSInterop.Invocations, invocation => invocation.Identifier == "navigator.clipboard.writeText");
        Assert.Equal(Away, call.Arguments[0]);
    }

    [Fact]
    public void QrCode_HoldsOnlyTheAddressAtHome_AndIsExactlyThatAddressDrawn()
    {
        var cut = RenderPage();

        var qr = cut.Find(".overview-apps__qr");
        Assert.Equal(Home, qr.GetAttribute("data-qr-payload"));
        Assert.Single(cut.FindAll("[data-qr-payload]"));
        var svg = Assert.Single(qr.QuerySelectorAll("svg"));
        Assert.Equal("QR code", svg.GetAttribute("aria-label"));
        var expected = Drawing(QrCodeSvg.Render(Home));
        Assert.NotEmpty(expected);
        Assert.Equal(expected, Drawing(qr.InnerHtml));
        Assert.Contains("Scan to use the address at home", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void QrCode_HoldsTheOutsideAddress_AndSaysSo_WhenOneIsSet()
    {
        _addresses = new() { AtHome = Home, AwayFromHome = Away };

        var cut = RenderPage();

        Assert.Equal(Away, cut.Find("[data-qr-payload]").GetAttribute("data-qr-payload"));
        Assert.Contains("Scan to use the outside address", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void QrCode_NeverCarriesAKeyOrASignIn()
    {
        _addresses = new() { AtHome = Home, AwayFromHome = Away };

        var cut = RenderPage();

        var payload = cut.Find("[data-qr-payload]").GetAttribute("data-qr-payload")!;
        Assert.True(Uri.TryCreate(payload, UriKind.Absolute, out var uri));
        Assert.Equal(string.Empty, uri!.Query);
        Assert.Equal(string.Empty, uri.UserInfo);
        Assert.True(uri.AbsolutePath is "" or "/");
        Assert.DoesNotContain("token", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("key", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HelpText_ExplainsTheScanAndStillSigningIn()
    {
        var cut = RenderPage();

        Assert.Contains("Scan with the Tuvima app on your phone to fill in the server, then sign in as usual.", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoAddressDetected_SaysSo_AndDrawsNoQrCode()
    {
        _addresses = new() { AtHome = null };

        var cut = RenderPage();

        Assert.Contains("Not detected yet", cut.Find("[data-apps-address='at-home']").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-qr-payload]"));
        Assert.Empty(cut.FindAll("[data-apps-address='at-home'] button"));
    }

    [Fact]
    public void WhenTheEngineCannotBeAsked_ShowsAnErrorInsteadOfAnAddress()
    {
        _addresses = null;

        var cut = RenderPage();

        Assert.Empty(cut.FindAll("[data-apps-address]"));
        Assert.Empty(cut.FindAll("[data-qr-payload]"));
        Assert.Contains("The server address is unavailable", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAnOutsideAddress_AMemberIsToldToAskTheAdministrator_WithNoLinkTheyCannotOpen()
    {
        var cut = RenderPage();

        var note = cut.Find("[data-apps-no-outside-address]");
        Assert.Empty(note.QuerySelectorAll("a"));
        Assert.Contains("ask the server administrator", note.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAnOutsideAddress_AServerAdministratorGetsTheRemoteAccessLink()
    {
        _serverAdministrator = true;

        var cut = RenderPage();

        Assert.Equal("/settings/network/remote", cut.Find("[data-apps-no-outside-address] a").GetAttribute("href"));
    }

    [Fact]
    public void StoreSlots_AreHiddenWhileNoLinkIsConfigured()
    {
        var cut = RenderPage();

        Assert.Empty(cut.FindAll("[data-apps-stores]"));
        Assert.NotEmpty(cut.FindAll("[data-apps-stores-empty]"));
    }

    [Fact]
    public void StoreSlots_ShowOnlyTheStoresThatHaveALink_AndIgnoreAnythingThatIsNotAWebAddress()
    {
        _links = new AppStoreLinks(AppStore: "https://apps.apple.com/app/tuvima/id1", GooglePlay: "   ", Roku: "javascript:alert(1)");

        var cut = RenderPage();

        var stores = cut.FindAll("[data-apps-store]");
        var store = Assert.Single(stores);
        Assert.Equal("app-store", store.GetAttribute("data-apps-store"));
        Assert.Equal("https://apps.apple.com/app/tuvima/id1", store.GetAttribute("href"));
        Assert.Contains("App Store", store.TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-apps-stores-empty]"));
    }

    [Fact]
    public void EveryStoreShowsWhenAllAreConfigured()
    {
        _links = new AppStoreLinks(
            "https://apps.apple.com/app/tuvima/id1",
            "https://play.google.com/store/apps/details?id=app.tuvima",
            "https://channelstore.roku.com/details/tuvima");

        var cut = RenderPage();

        Assert.Equal(["app-store", "google-play", "roku"], cut.FindAll("[data-apps-store]").Select(store => store.GetAttribute("data-apps-store")!).ToArray());
    }

    [Fact]
    public void TheShippedConfiguration_HasAnAppStoreLinksKey_WithEveryStoreBlank()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var reader = new MediaEngine.Web.Services.Configuration.DashboardConfigurationReader(Path.Combine(root, "config"));

        var links = reader.LoadAppStoreLinks();

        Assert.Equal(string.Empty, links.AppStore);
        Assert.Equal(string.Empty, links.GooglePlay);
        Assert.Equal(string.Empty, links.Roku);
        Assert.Contains("\"app_store_links\"", File.ReadAllText(Path.Combine(root, "config", "ui", "global.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void TheReader_ReadsConfiguredStoreLinksFromTheUiGlobalFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tuvima-apps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "ui"));
        try
        {
            File.WriteAllText(Path.Combine(directory, "ui", "global.json"),
                "{ \"dark_mode\": true, \"app_store_links\": { \"google_play\": \"https://play.google.com/store/apps/details?id=app.tuvima\" } }");

            var links = new MediaEngine.Web.Services.Configuration.DashboardConfigurationReader(directory).LoadAppStoreLinks();

            Assert.Equal("https://play.google.com/store/apps/details?id=app.tuvima", links.GooglePlay);
            Assert.Equal(string.Empty, links.AppStore);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // The drawing is the path data and the view box; AngleSharp re-serialises the tags around them.
    private static string[] Drawing(string svg) =>
        [.. System.Text.RegularExpressions.Regex.Matches(svg, @"(?:\sd|viewBox)=""([^""]+)""")
            .Cast<System.Text.RegularExpressions.Match>()
            .Select(match => match.Groups[1].Value)];
}
