using Bunit;
using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.Setup;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Configuration;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>Server &gt; General &gt; Name and address (settings-and-setup-simplified D10): the Library's name and the address that follows it, the numeric address with Copy, store links once published, and a QR code of that address only.</summary>
public sealed class GeneralNameAddressPanelTests : AsyncBunitContext
{
    private const string Home = "http://192.168.1.20:5016";
    private const string Away = "https://tuvima.example.com";

    private NetworkAppAddressesDto? _addresses = new() { AtHome = Home };
    private AppStoreLinks _links = new();
    private SetupServerIdentityDto? _identity = new("Shaya's Library", "shayas-library", true);
    private readonly List<SetupServerIdentityDto> _saved = [];

    public GeneralNameAddressPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddNativeUiServices();
    }

    private IRenderedComponent<GeneralNameAddressPanel> RenderPage()
    {
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetNetworkAppAddressesAsync), _ => Task.FromResult(_addresses));
            stub.SetHandler(nameof(IEngineApiClient.GetServerIdentityAsync), _ => Task.FromResult(_identity));
            stub.SetHandler(nameof(IEngineApiClient.UpdateServerIdentityAsync), args =>
            {
                var requested = (SetupServerIdentityDto)args![0]!;
                // What the Engine does: derive the network name while it follows the friendly name.
                var host = requested.HostNameDerived ? NetworkNameRules.ToLabel(requested.ServerName) : NetworkNameRules.ToLabel(requested.HostName);
                var saved = new SetupServerIdentityDto(requested.ServerName.Trim(), host, NetworkNameRules.IsDerived(requested.ServerName, host));
                _saved.Add(saved);
                return Task.FromResult<SetupServerIdentityDto?>(saved);
            });
        }));
        Services.AddSingleton(_links);

        var cut = Render<GeneralNameAddressPanel>();
        cut.WaitForAssertion(() => Assert.True(
            cut.FindAll("[data-testid='general-name-address']").Count > 0 || cut.Markup.Contains("The Library name is unavailable", StringComparison.Ordinal)));
        return cut;
    }

    [Fact]
    public void TheNumericAddress_ShowsWithCopy()
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

        Assert.Contains("Install on this device", cut.Find("[data-testid='general-name-address']").TextContent, StringComparison.Ordinal);
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

        var qr = cut.Find(".general-address__qr");
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

        Assert.Contains("Scan with the Library app on your phone to fill in the server, then sign in as usual.", cut.Markup, StringComparison.Ordinal);
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
        _identity = null;

        var cut = RenderPage();

        Assert.Empty(cut.FindAll("[data-apps-address]"));
        Assert.Empty(cut.FindAll("[data-qr-payload]"));
        Assert.Contains("The Library name is unavailable", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAnOutsideAddress_GeneralPointsToRemoteAccess_BecauseOnlyAnAdministratorSeesThisPage()
    {
        var cut = RenderPage();

        Assert.Equal("/settings/network/remote", cut.Find("[data-apps-no-outside-address] a").GetAttribute("href"));
    }

    [Fact]
    public void TheLibraryName_ShowsWithTheAddressItGives_AndNothingIsOfferedToSaveUntilItChanges()
    {
        var cut = RenderPage();

        Assert.Equal("Shaya's Library", cut.Find("[data-testid='general-library-name'] input, input[data-testid='general-library-name']").GetAttribute("value"));
        Assert.Contains("Phones and TVs on your home network will find it at shayas-library.local", cut.Find("[data-testid='general-network-name-line']").TextContent, StringComparison.Ordinal);
        Assert.True(cut.Find("[data-testid='general-save-name']").HasAttribute("disabled"));
        Assert.Empty(cut.FindAll("[data-testid='general-network-name-field']"));
    }

    [Fact]
    public void ChangingTheName_UpdatesTheDerivedAddressAtOnce_AndSavingSendsItToTheEngine()
    {
        var cut = RenderPage();

        cut.Find("input[data-testid='general-library-name'], [data-testid='general-library-name'] input").Input("The Den Library");

        Assert.Contains("the-den-library.local", cut.Find("[data-testid='general-network-name-line']").TextContent, StringComparison.Ordinal);
        cut.Find("[data-testid='general-save-name']").Click();

        var saved = Assert.Single(_saved);
        Assert.Equal("The Den Library", saved.ServerName);
        Assert.Equal("the-den-library", saved.HostName);
        Assert.Contains("the-den-library.local", cut.Find("[data-testid='general-network-name-line']").TextContent, StringComparison.Ordinal);
        Assert.Contains(Services.GetRequiredService<AppToastService>().Toasts, toast => toast.Message == "The Library name was saved.");
    }

    [Fact]
    public void Change_RevealsTheNetworkNameField_AndAHandSetNameStopsFollowingTheLibraryName()
    {
        var cut = RenderPage();

        cut.Find("[data-testid='general-network-name-change']").Click();
        Assert.Empty(cut.FindAll("[data-testid='general-network-name-change']"));
        cut.Find("input[data-testid='general-network-name-field'], [data-testid='general-network-name-field'] input").Input("Den TV");
        cut.Find("input[data-testid='general-library-name'], [data-testid='general-library-name'] input").Input("Another Name");

        Assert.Contains("den-tv.local", cut.Find("[data-testid='general-network-name-line']").TextContent, StringComparison.Ordinal);
        cut.Find("[data-testid='general-save-name']").Click();
        var saved = Assert.Single(_saved);
        Assert.Equal("Another Name", saved.ServerName);
        Assert.Equal("den-tv", saved.HostName);
        Assert.False(saved.HostNameDerived);
    }

    [Fact]
    public void ABlankName_CannotBeSaved()
    {
        var cut = RenderPage();

        cut.Find("input[data-testid='general-library-name'], [data-testid='general-library-name'] input").Input("   ");

        Assert.True(cut.Find("[data-testid='general-save-name']").HasAttribute("disabled"));
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
