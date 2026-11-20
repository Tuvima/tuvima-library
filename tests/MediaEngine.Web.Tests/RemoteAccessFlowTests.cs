using AngleSharp.Dom;
using Bunit;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Settings;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Settings &gt; Network &gt; Remote access, the guided "away from home" flow (spec remote-access, WP2 and WP4): the start page, the
/// five steps from Check to Test against a spy Engine client, the order of the ways in, the password gate and the live Tailscale
/// ticks. Nothing here starts an Engine; the spy answers the check and the remote test.
/// </summary>
public sealed class RemoteAccessFlowTests : AsyncBunitContext
{
    private readonly NetworkSettingsDto _settings = new();
    private readonly List<string> _savedModes = [];
    private readonly Queue<RemoteCheckResultDto?> _checks = new();
    private RemoteCheckResultDto? _lastCheck;
    private readonly List<string> _calls = [];
    private NetworkTestResultDto? _remoteTest = new()
    {
        Kind = "remote",
        Status = "passed",
        Headline = "Away-from-home access works",
        Detail = "Library was reached from the internet just now.",
        Checks = [new NetworkTestCheckDto { Key = "reach", Label = "Library answered from outside", Status = "passed", Detail = "Reached in 120 ms." }],
    };
    private TaskCompletionSource<RemoteCheckResultDto?>? _pending;
    private IReadOnlyList<OutsideSignInDto>? _signIns = [];
    private string? _awayAddress;
    private RemoteOffResultDto? _remoteOff = new() { WasOn = true };

    public RemoteAccessFlowTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
    }

    private IRenderedComponent<RemoteAccessSettingsPanel> RenderPanel(string mode = "local-only", string? deviceClass = null, string? formFactor = null)
    {
        _settings.Remote.ConnectionMode = mode;
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetRemoteCheckAsync), _ =>
            {
                _calls.Add(nameof(IEngineApiClient.GetRemoteCheckAsync));
                if (_pending is not null)
                {
                    return _pending.Task;
                }

                _lastCheck = _checks.Count > 0 ? _checks.Dequeue() : _lastCheck;
                return Task.FromResult(_lastCheck);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetOutsideSignInsAsync), _ =>
            {
                _calls.Add(nameof(IEngineApiClient.GetOutsideSignInsAsync));
                return Task.FromResult(_signIns);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetNetworkAppAddressesAsync), _ =>
                Task.FromResult<NetworkAppAddressesDto?>(new NetworkAppAddressesDto { AwayFromHome = _awayAddress }));
            stub.SetHandler(nameof(IEngineApiClient.TurnRemoteOffAsync), _ =>
            {
                _calls.Add(nameof(IEngineApiClient.TurnRemoteOffAsync));
                return Task.FromResult(_remoteOff);
            });
            stub.SetHandler(nameof(IEngineApiClient.TestRemoteNetworkAsync), _ =>
            {
                _calls.Add(nameof(IEngineApiClient.TestRemoteNetworkAsync));
                return Task.FromResult(_remoteTest);
            });
        });
        Services.AddSingleton(api);
        Services.AddScoped<RemoteAccessFlowState>();
        Services.AddSingleton<IFriendInviteService>(new FriendInviteServiceSpy());
        if (deviceClass is not null)
        {
            var device = new DeviceContextService(api);
            device.InitialiseAsync(new DeviceContextSnapshot(deviceClass, formFactor ?? deviceClass, true)).GetAwaiter().GetResult();
            Services.AddSingleton(device);
        }

        return Render<RemoteAccessSettingsPanel>(parameters => parameters
            .Add(p => p.Settings, _settings)
            .Add(p => p.SaveRequested, EventCallback.Factory.Create(this, () => _savedModes.Add(_settings.Remote.ConnectionMode))));
    }

    private static RemoteCheckFindingDto Finding(string key, string status, string title, string detail = "", string? nextStep = null) =>
        new() { Key = key, Status = status, Title = title, Detail = detail, NextStep = nextStep };

    private static RemoteCheckResultDto Check(string suggestion, params RemoteCheckFindingDto[] findings) => new()
    {
        Suggestion = suggestion,
        SuggestionReason = "It is already installed and needs no router changes.",
        Findings = [.. findings],
    };

    private static RemoteCheckFindingDto TailscaleReady() =>
        Finding(RemoteCheckFindingKeys.TailscaleReady, RemoteCheckStatuses.Ok, "Tailscale is ready", "Your private address is tuvima.tail1234.ts.net.");

    private static void Click(IRenderedComponent<RemoteAccessSettingsPanel> cut, string action) =>
        cut.Find($"[data-remote-action='{action}']").Click();

    private static string Screen(IRenderedComponent<RemoteAccessSettingsPanel> cut) =>
        cut.Find("[data-remote-flow]").GetAttribute("data-remote-screen-name")!;

    private static string CurrentStep(IRenderedComponent<RemoteAccessSettingsPanel> cut) =>
        cut.Find("[data-remote-stepper] [aria-current='step']").GetAttribute("data-remote-step")!;

    // The own-address way opens on the wizard's Address screen; the three ways in are listed on its second screen, Point it here.
    private static void ToThePointItHereScreen(IRenderedComponent<RemoteAccessSettingsPanel> cut)
    {
        Click(cut, "use-my-domain");
        cut.Find("input[placeholder='yourname.com']").Input("example.com");
        Click(cut, "continue");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-remote-screen='domain-point']")));
    }

    [Fact]
    public void AFreshInstall_ShowsTheStartPage_WithTwoStatusCards_OneSetUpButtonAndTheProxyLink()
    {
        var cut = RenderPanel();

        Assert.Equal("start", Screen(cut));
        Assert.Contains("At home: working, no password needed", cut.Find("[data-remote-card='home']").TextContent, StringComparison.Ordinal);
        var away = cut.Find("[data-remote-card='away']");
        Assert.Contains("Away from home: not set up", away.TextContent, StringComparison.Ordinal);
        Assert.Equal("Set up away-from-home access", cut.Find("[data-remote-action='set-up']").TextContent.Trim());
        Assert.Equal("I already have a proxy or tunnel", cut.Find("[data-remote-action='own-address']").TextContent.Trim());
        Assert.Contains("Outside access always needs a password", cut.Find("[data-remote-card='safety']").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-remote-stepper]"));
        Assert.Empty(cut.FindAll("input[type='radio']"));
        Assert.DoesNotContain("Remote access is off", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void SetUp_RunsTheCheck_AndSaysWhatItFoundInPlainSentences_WithoutTheTwoPasswordFindings()
    {
        _checks.Enqueue(Check(RemoteCheckSuggestions.Tailscale,
            Finding(RemoteCheckFindingKeys.TailscaleReady, RemoteCheckStatuses.Ok, "Tailscale is installed and signed in", "Your private address would be tuvima.tail1234.ts.net."),
            Finding(RemoteCheckFindingKeys.SharedAddress, RemoteCheckStatuses.Warn, "Your internet provider shares one address between many homes",
                "Opening a door in your router will not work.", "Use Tailscale, or a tunnel or proxy that does not need your router."),
            Finding(RemoteCheckFindingKeys.ProfilesWithoutPassword, RemoteCheckStatuses.Warn, "One person can only sign in at home")));
        var cut = RenderPanel();

        Click(cut, "set-up");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-remote-card='findings']")));
        Assert.Equal("check", CurrentStep(cut));
        var rows = cut.FindAll("[data-remote-card='findings'] [data-remote-row]");
        Assert.Equal([RemoteCheckFindingKeys.TailscaleReady, RemoteCheckFindingKeys.SharedAddress], rows.Select(row => row.GetAttribute("data-remote-row")!).ToArray());
        Assert.Equal("working", rows[0].GetAttribute("data-remote-status"));
        Assert.Equal("attention", rows[1].GetAttribute("data-remote-status"));
        Assert.Contains("Use Tailscale, or a tunnel or proxy that does not need your router.", rows[1].TextContent, StringComparison.Ordinal);
        Assert.Contains("Our suggestion: Tailscale", cut.Find("[data-remote-card='suggestion']").TextContent, StringComparison.Ordinal);
        Assert.Contains("It is already installed", cut.Find("[data-remote-card='suggestion']").TextContent, StringComparison.Ordinal);
        Assert.Contains("Check again", cut.Find("[data-remote-action='check-again']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void WhileTheCheckRuns_TheLookingLinesShow_AndCancelReturnsToTheStartPage()
    {
        _pending = new TaskCompletionSource<RemoteCheckResultDto?>();
        var cut = RenderPanel();

        Click(cut, "set-up");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-remote-card='checking']")));
        Assert.Equal(4, cut.FindAll("[data-remote-card='checking'] [data-remote-row]").Count);
        Assert.Contains("Looking for Tailscale", cut.Find("[data-remote-card='checking']").TextContent, StringComparison.Ordinal);

        Click(cut, "cancel");
        _pending.SetResult(Check(RemoteCheckSuggestions.Tailscale));

        Assert.Equal("start", Screen(cut));
        Assert.Empty(cut.FindAll("[data-remote-card='findings']"));
    }

    [Fact]
    public void WhenTheEngineCannotBeAsked_TheCheckSaysSo_AndCheckAgainTriesAgain()
    {
        _checks.Enqueue(null);
        var cut = RenderPanel();

        Click(cut, "set-up");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-remote-card='unavailable']")));
        _checks.Enqueue(Check(RemoteCheckSuggestions.Tailscale, TailscaleReady()));
        Click(cut, "check-again");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-remote-card='findings']")));
        Assert.Equal(2, _calls.Count(call => call == nameof(IEngineApiClient.GetRemoteCheckAsync)));
    }

    [Fact]
    public void TheStepper_ReachesTest_OnASpyProvider_AndSavesTheWayOnlyOncePasswordsAllowIt()
    {
        _checks.Enqueue(Check(RemoteCheckSuggestions.Tailscale, TailscaleReady()));
        var cut = RenderPanel();

        Click(cut, "set-up");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-remote-card='findings']")));
        Click(cut, "see-options");
        Assert.Equal("choose", CurrentStep(cut));
        Assert.Equal(["check"], cut.FindAll("[data-remote-stepper] .is-done").Select(pill => pill.GetAttribute("data-remote-step")!).ToArray());

        Click(cut, "choose-tailscale");
        Assert.Equal("passwords", CurrentStep(cut));
        Assert.Empty(_savedModes);

        Click(cut, "continue");
        cut.WaitForAssertion(() => Assert.Equal("connect", CurrentStep(cut)));
        Assert.Equal(["tailscale"], _savedModes);

        Click(cut, "continue");
        Assert.Equal("test", CurrentStep(cut));
        Assert.Equal(5, cut.FindAll("[data-remote-stepper] li").Count);
        Click(cut, "run-test");

        cut.WaitForAssertion(() => Assert.Contains("Away-from-home access works", cut.Find("[data-remote-card='result']").TextContent, StringComparison.Ordinal));
        Assert.Contains(nameof(IEngineApiClient.TestRemoteNetworkAsync), _calls);
        Click(cut, "done");
        Assert.Equal("start", Screen(cut));
    }

    [Fact]
    public void WhenTheRemoteTestFails_TheStepSaysThePlainReasonAndTheNextStep()
    {
        _remoteTest = new NetworkTestResultDto
        {
            Kind = "remote",
            Status = "failed",
            Headline = "Library could not be reached from outside",
            Detail = "Nothing answered at your address.",
            Checks = [new NetworkTestCheckDto { Key = "reach", Label = "Library answered", Status = "failed", Detail = "Your provider shares your address; use Tailscale or a tunnel." }],
        };
        _checks.Enqueue(Check(RemoteCheckSuggestions.Tailscale, TailscaleReady()));
        var cut = RenderPanel(mode: "tailscale");
        Click(cut, "test");

        Click(cut, "run-test");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-remote-card='result']")));
        Assert.Contains("Nothing answered at your address.", cut.Find("[data-remote-card='result']").TextContent, StringComparison.Ordinal);
        Assert.Contains("use Tailscale or a tunnel", cut.Find("[data-remote-card='next-step']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Choose_SuggestsTheWayTheCheckNamed_AndNotNowSavesHomeOnlyAndLeavesTheFlow()
    {
        _checks.Enqueue(Check(RemoteCheckSuggestions.OwnAddress, Finding(RemoteCheckFindingKeys.TailscaleNotInstalled, RemoteCheckStatuses.Info, "Tailscale is not installed")));
        var cut = RenderPanel(mode: "custom");
        Click(cut, "change");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-remote-card='findings']")));
        Click(cut, "see-options");

        Assert.Empty(cut.Find("[data-remote-method='tailscale']").QuerySelectorAll(".ra-choose__badge"));
        Assert.Contains("Suggested for you", cut.Find("[data-remote-method='own-address']").TextContent, StringComparison.Ordinal);
        Assert.Equal(["Tailscale", "My own address", "Not now"],
            cut.FindAll("[data-remote-method] h4").Select(heading => heading.TextContent.Trim()).ToArray());

        Click(cut, "choose-not-now");

        cut.WaitForAssertion(() => Assert.Equal("start", Screen(cut)));
        Assert.Equal(["local-only"], _savedModes);
        Assert.Contains("Away from home: not set up", cut.Find("[data-remote-card='away']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void WithASharedAddress_TheOwnAddressCardWarnsFirst_AndTheRouterDoorComesLastWithAWarning()
    {
        _checks.Enqueue(Check(RemoteCheckSuggestions.Tailscale,
            Finding(RemoteCheckFindingKeys.SharedAddress, RemoteCheckStatuses.Warn, "Your internet provider shares one address between many homes")));
        var cut = RenderPanel();
        Click(cut, "set-up");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-remote-card='findings']")));
        Click(cut, "see-options");

        Assert.Contains("opening a door in your router will probably not work", cut.Find("[data-remote-shared-address]").TextContent, StringComparison.Ordinal);

        Click(cut, "choose-own-address");
        Click(cut, "continue");
        cut.WaitForAssertion(() => Assert.Equal("connect", CurrentStep(cut)));
        ToThePointItHereScreen(cut);

        var routes = cut.FindAll("[data-remote-card='routes'] [data-remote-row]");
        Assert.Equal(["route-tunnel", "route-proxy", "route-router"], routes.Select(row => row.GetAttribute("data-remote-row")!).ToArray());
        Assert.Equal("attention", routes[^1].GetAttribute("data-remote-status"));
        Assert.Contains("probably not work", routes[^1].TextContent, StringComparison.Ordinal);
        Assert.All(_savedModes, mode => Assert.Equal("custom", mode));
    }

    [Fact]
    public void WithoutASharedAddress_TheRouterDoorStillComesLast_WithNoWarning()
    {
        _checks.Enqueue(Check(RemoteCheckSuggestions.OwnAddress));
        var cut = RenderPanel();
        Click(cut, "set-up");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-remote-card='suggestion']")));
        Click(cut, "see-options");
        Assert.Empty(cut.FindAll("[data-remote-shared-address]"));
        Click(cut, "choose-own-address");
        Click(cut, "continue");
        cut.WaitForAssertion(() => Assert.Equal("connect", CurrentStep(cut)));
        ToThePointItHereScreen(cut);

        var routes = cut.FindAll("[data-remote-card='routes'] [data-remote-row]");

        Assert.Equal("route-router", routes[^1].GetAttribute("data-remote-row"));
        Assert.Equal("pending", routes[^1].GetAttribute("data-remote-status"));
    }

    [Fact]
    public void Passwords_ListsWhoIsHomeOnly_AndBlocksContinueUntilTheAdministratorHasAPassword()
    {
        _checks.Enqueue(Check(RemoteCheckSuggestions.Tailscale,
            Finding(RemoteCheckFindingKeys.AdminWithoutPassword, RemoteCheckStatuses.Fail, "The server administrator has no password", "Nobody who looks after Library has one yet."),
            Finding(RemoteCheckFindingKeys.ProfilesWithoutPassword, RemoteCheckStatuses.Warn, "One person can only sign in at home", "Alex has no password or passkey.")));
        var cut = RenderPanel();
        Click(cut, "set-up");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-remote-card='findings']")));
        Click(cut, "see-options");
        Click(cut, "choose-tailscale");

        Assert.True(cut.Find("[data-remote-action='continue']").HasAttribute("disabled"));
        Assert.NotEmpty(cut.FindAll("[data-remote-blocked]"));
        var people = cut.Find("[data-remote-row='profiles_without_password']");
        Assert.Contains("Home only", people.TextContent, StringComparison.Ordinal);
        Assert.Contains("Alex has no password or passkey.", people.TextContent, StringComparison.Ordinal);
        Assert.All(cut.FindAll("[data-remote-action='set-password']"), link =>
            Assert.Equal(SettingsNav.RouteFor(SettingsSection.Access), link.GetAttribute("href")));

        // The administrator sets a password; Check again reads it and Continue opens.
        _checks.Enqueue(Check(RemoteCheckSuggestions.Tailscale,
            Finding(RemoteCheckFindingKeys.ProfilesWithoutPassword, RemoteCheckStatuses.Warn, "One person can only sign in at home", "Alex has no password or passkey.")));
        Click(cut, "check-again");

        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-remote-action='continue']").HasAttribute("disabled")));
        Assert.Empty(cut.FindAll("[data-remote-blocked]"));
        Assert.Empty(_savedModes);
        Click(cut, "continue");
        cut.WaitForAssertion(() => Assert.Equal("connect", CurrentStep(cut)));
    }

    [Fact]
    public void Passwords_WithEveryoneCovered_SaysSoAndContinuesFreely()
    {
        _checks.Enqueue(Check(RemoteCheckSuggestions.Tailscale, TailscaleReady()));
        var cut = RenderPanel();
        Click(cut, "set-up");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-remote-card='findings']")));
        Click(cut, "see-options");
        Click(cut, "choose-tailscale");

        Assert.NotEmpty(cut.FindAll("[data-remote-row='everyone']"));
        Assert.False(cut.Find("[data-remote-action='continue']").HasAttribute("disabled"));
    }

    [Fact]
    public void TheTailscaleStep_TicksItselfAsTheChecksAdvance_AndCarriesCopyableCommandsTaggedForTheServer()
    {
        _checks.Enqueue(Check(RemoteCheckSuggestions.Tailscale, Finding(RemoteCheckFindingKeys.TailscaleNotInstalled, RemoteCheckStatuses.Info, "Tailscale is not installed")));
        var cut = RenderPanel();
        var flow = Services.GetRequiredService<RemoteAccessFlowState>();
        flow.PollInterval = TimeSpan.FromMilliseconds(40);
        Click(cut, "set-up");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-remote-card='findings']")));
        Click(cut, "see-options");
        Click(cut, "choose-tailscale");
        Click(cut, "continue");
        cut.WaitForAssertion(() => Assert.Equal("connect", CurrentStep(cut)));

        // Nothing is done yet: the install line has its download link, and no later line shows a tick.
        Assert.Equal("notinstalled", cut.Find("[data-tailscale-progress]").GetAttribute("data-tailscale-progress"));
        Assert.Equal("pending", cut.Find("[data-remote-row='install']").GetAttribute("data-remote-status"));
        Assert.Equal("https://tailscale.com/download", cut.Find("[data-remote-link='download']").GetAttribute("href"));
        Assert.True(cut.Find("[data-remote-action='continue']").HasAttribute("disabled"));

        // The person installs it and signs in; the page looks again on its own and the first two lines tick.
        _checks.Enqueue(Check(RemoteCheckSuggestions.Tailscale, Finding(RemoteCheckFindingKeys.TailscaleHttpsOff, RemoteCheckStatuses.Warn, "Tailscale needs its secure address")));
        cut.WaitForAssertion(() => Assert.Equal("httpsoff", cut.Find("[data-tailscale-progress]").GetAttribute("data-tailscale-progress")));
        Assert.Equal("working", cut.Find("[data-remote-row='install']").GetAttribute("data-remote-status"));
        Assert.Equal("working", cut.Find("[data-remote-row='sign-in']").GetAttribute("data-remote-status"));
        var secure = cut.Find("[data-remote-row='secure-address']");
        Assert.Equal("pending", secure.GetAttribute("data-remote-status"));
        Assert.Contains("tailscale serve --bg 5017", secure.TextContent, StringComparison.Ordinal);
        Assert.Contains("On the server computer", secure.TextContent, StringComparison.Ordinal);
        Assert.Contains(secure.QuerySelectorAll("button"), button => button.TextContent.Contains("Copy", StringComparison.Ordinal));

        // The secure address is switched on: every line ticks and Continue opens.
        _checks.Enqueue(Check(RemoteCheckSuggestions.Tailscale, TailscaleReady()));
        cut.WaitForAssertion(() => Assert.Equal("ready", cut.Find("[data-tailscale-progress]").GetAttribute("data-tailscale-progress")));
        Assert.Equal("working", cut.Find("[data-remote-row='secure-address']").GetAttribute("data-remote-status"));
        Assert.False(cut.Find("[data-remote-action='continue']").HasAttribute("disabled"));
        Assert.NotEmpty(cut.Find("[data-remote-qr='phone']").QuerySelectorAll("svg"));
    }

    [Fact]
    public void TheTailscaleStep_UsesTheDashboardProxyPortInItsServeCommand()
    {
        _settings.Remote.ProxyPort = 6123;
        _checks.Enqueue(Check(RemoteCheckSuggestions.Tailscale, Finding(RemoteCheckFindingKeys.TailscaleHttpsOff, RemoteCheckStatuses.Warn, "Needs its secure address")));
        var cut = RenderPanel();
        Services.GetRequiredService<RemoteAccessFlowState>().PollInterval = TimeSpan.Zero;
        Click(cut, "set-up");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-remote-card='findings']")));
        Click(cut, "see-options");
        Click(cut, "choose-tailscale");
        Click(cut, "continue");

        cut.WaitForAssertion(() => Assert.Contains("tailscale serve --bg 6123", cut.Find("[data-remote-row='secure-address']").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void IAlreadyHaveAProxy_OpensTheExistingAddressFields_AndSaveAndVerifySavesTheOwnAddressWay()
    {
        var cut = RenderPanel();

        Click(cut, "own-address");

        Assert.Equal("ownaddress", Screen(cut));
        Assert.Empty(cut.FindAll("[data-remote-stepper]"));
        Assert.Empty(cut.FindAll("[data-remote-card='routes']"));
        var field = cut.Find("input[placeholder='https://tuvima.example.com']");
        field.Input("https://library.example.com");
        Click(cut, "save-address");

        cut.WaitForAssertion(() => Assert.Equal(["custom"], _savedModes));
        Assert.Equal("https://library.example.com", _settings.Remote.PublicHostname);
        Click(cut, "test-remote");
        cut.WaitForAssertion(() => Assert.Contains(nameof(IEngineApiClient.TestRemoteNetworkAsync), _calls));
        Click(cut, "back");
        Assert.Equal("start", Screen(cut));
    }

    [Fact]
    public void WhenAWayInIsOn_TheStartPageSaysWhichOne_AndShowsTheOnPanelWithTheOneOffSwitch()
    {
        _awayAddress = "https://tuvima.tail1234.ts.net";
        _signIns =
        [
            new OutsideSignInDto { At = DateTimeOffset.Now, ProfileName = "Shy", Succeeded = true, Method = OutsideSignInMethods.Password },
        ];
        var cut = RenderPanel(mode: "tailscale");

        var away = cut.Find("[data-remote-card='away']");
        Assert.Contains("Away from home: on", away.TextContent, StringComparison.Ordinal);
        Assert.Contains("Tailscale is the way in", away.TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-remote-action='set-up']"));
        Assert.Empty(cut.FindAll("[data-remote-action='turn-off']"));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-remote-row='sign-in']")));
        Assert.NotEmpty(cut.FindAll("[data-remote-qr='address']"));
        Assert.Equal("https://tuvima.tail1234.ts.net", cut.Find("[data-remote-address]").TextContent.Trim());
        Assert.Single(cut.FindAll("[data-remote-switch='allow-outside']"));
    }

    [Fact]
    public void TheOffSwitch_AsksOnce_AndKeepingItOnChangesNothing()
    {
        var cut = RenderPanel(mode: "tailscale");
        var host = Render<AppDialogHost>();

        cut.Find("[data-remote-switch='allow-outside'] input[type='checkbox']").Change(false);

        host.WaitForElement(".app-confirm");
        Assert.Contains("Turn off away-from-home access?", host.Markup, StringComparison.Ordinal);
        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Keep it on").Click();

        cut.WaitForAssertion(() => Assert.Empty(host.FindAll(".app-confirm")));
        Assert.DoesNotContain(nameof(IEngineApiClient.TurnRemoteOffAsync), _calls);
        Assert.Equal("tailscale", _settings.Remote.ConnectionMode);
        Assert.Equal("start", Screen(cut));
    }

    [Fact]
    public void TheOffSwitch_OnceConfirmed_TurnsOutsideAccessOffAtOnce_AndTheStartPageGoesBackToHomeOnly()
    {
        var cut = RenderPanel(mode: "tailscale");
        var host = Render<AppDialogHost>();

        cut.Find("[data-remote-switch='allow-outside'] input[type='checkbox']").Change(false);
        host.WaitForElement(".app-confirm");
        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Turn off").Click();

        cut.WaitForAssertion(() => Assert.Contains(nameof(IEngineApiClient.TurnRemoteOffAsync), _calls));
        cut.WaitForAssertion(() => Assert.Contains("Away from home: not set up", cut.Find("[data-remote-card='away']").TextContent, StringComparison.Ordinal));
        Assert.Equal("local-only", _settings.Remote.ConnectionMode);
        // The Engine did the saving; the page does not save the mode a second time.
        Assert.Empty(_savedModes);
        Assert.Empty(cut.FindAll("[data-remote-on]"));
    }

    [Fact]
    public void WhenTheEngineCannotTurnItOff_TheSwitchSaysSoAndOutsideAccessStaysOn()
    {
        _remoteOff = null;
        var cut = RenderPanel(mode: "tailscale");
        var host = Render<AppDialogHost>();

        cut.Find("[data-remote-switch='allow-outside'] input[type='checkbox']").Change(false);
        host.WaitForElement(".app-confirm");
        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Turn off").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-remote-off-failure]")));
        Assert.Equal("tailscale", _settings.Remote.ConnectionMode);
        Assert.Equal("start", Screen(cut));
    }

    [Fact]
    public void TheTestStep_ShowsAQrForThePhone_TheRecentOutsideSignInsWithRefusalsMarked_AndTheOffSwitch()
    {
        _awayAddress = "https://tuvima.tail1234.ts.net";
        _signIns =
        [
            new OutsideSignInDto { At = DateTimeOffset.Now, ProfileName = "Shy", Succeeded = true, Method = OutsideSignInMethods.Passkey },
            new OutsideSignInDto
            {
                At = DateTimeOffset.Now.AddDays(-1), ProfileName = "Alex", Succeeded = false, Method = OutsideSignInMethods.NoPassword,
                RefusalReason = "Alex has no password or passkey, so Library only lets them in at home.",
            },
            new OutsideSignInDto { At = DateTimeOffset.Now.AddDays(-5), ProfileName = null, Succeeded = false, Method = OutsideSignInMethods.Password },
        ];
        var cut = RenderPanel(mode: "tailscale");

        Click(cut, "test");

        Assert.Equal("test", CurrentStep(cut));
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-remote-card='sign-ins'] [data-remote-row]").Count));
        Assert.Equal("https://tuvima.tail1234.ts.net", cut.Find("[data-remote-address]").TextContent.Trim());
        Assert.NotEmpty(cut.FindAll("[data-remote-qr='address'] svg"));
        Assert.NotEmpty(cut.FindAll("[data-remote-action='copy-address']"));

        var rows = cut.FindAll("[data-remote-card='sign-ins'] [data-remote-row]");
        Assert.Equal("sign-in", rows[0].GetAttribute("data-remote-row"));
        Assert.Contains("Shy · Passkey", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Today", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Equal("sign-in-refused", rows[1].GetAttribute("data-remote-row"));
        Assert.Equal("broken", rows[1].GetAttribute("data-remote-status"));
        Assert.Contains("Alex · No password", rows[1].TextContent, StringComparison.Ordinal);
        Assert.Contains("Yesterday", rows[1].TextContent, StringComparison.Ordinal);
        Assert.Contains("Refused. Alex has no password or passkey", rows[1].TextContent, StringComparison.Ordinal);
        Assert.Contains("A person who is no longer in Library · Password", rows[2].TextContent, StringComparison.Ordinal);
        Assert.Single(cut.FindAll("[data-remote-switch='allow-outside']"));
    }

    [Fact]
    public void TheTestStep_WithNoOutsideSignInsYet_SaysSo_AndWithoutAnAddressDrawsNoQr()
    {
        var cut = RenderPanel(mode: "tailscale");

        Click(cut, "test");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-remote-sign-ins-empty]")));
        Assert.NotEmpty(cut.FindAll("[data-remote-no-address]"));
        Assert.Empty(cut.FindAll("[data-remote-qr]"));
    }

    [Fact]
    public void TheTestStep_WhenTheSignInsCannotBeRead_SaysSo_AndCheckAgainReadsThemAgain()
    {
        _signIns = null;
        var cut = RenderPanel(mode: "tailscale");
        Click(cut, "test");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-remote-sign-ins-unavailable]")));

        _signIns = [new OutsideSignInDto { At = DateTimeOffset.Now, ProfileName = "Shy", Succeeded = true }];
        Click(cut, "refresh-sign-ins");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-remote-row='sign-in']")));
    }

    [Fact]
    public void RunningTheTest_ReadsTheRecentSignInsAgain_SoAPhoneAttemptShowsUp()
    {
        var cut = RenderPanel(mode: "tailscale");
        Click(cut, "test");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-remote-sign-ins-empty]")));
        _signIns = [new OutsideSignInDto { At = DateTimeOffset.Now, ProfileName = "Shy", Succeeded = true }];

        Click(cut, "run-test");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-remote-row='sign-in']")));
    }

    [Fact]
    public void Passwords_WhenTheCheckCouldNotBeRead_ContinueStaysOff_AndCheckAgainIsOffered()
    {
        var cut = RenderStandalonePasswords(null);

        Assert.True(cut.Find("[data-remote-action='continue']").HasAttribute("disabled"));
        Assert.Contains("could not read who has a password", cut.Find("[data-remote-blocked]").TextContent, StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll("[data-remote-action='check-again']"));
    }

    [Fact]
    public void Passwords_SaysTheSingleNextStepTheEngineNamedUnderEachFinding_AndOffersNoFixButton()
    {
        var cut = RenderStandalonePasswords(Check(RemoteCheckSuggestions.Tailscale,
            new RemoteCheckFindingDto
            {
                Key = RemoteCheckFindingKeys.AdminWithoutPassword, Status = RemoteCheckStatuses.Fail, Title = "The server administrator has no password",
                Detail = "Nobody who looks after Library has one yet.", NextStep = "Add a password or passkey to the server administrator's sign-in.",
            }));

        Assert.Contains("Add a password or passkey to the server administrator's sign-in.", cut.Find("[data-remote-next-step]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-remote-action='fix']"));
        Assert.True(cut.Find("[data-remote-action='continue']").HasAttribute("disabled"));
    }

    private IRenderedComponent<RemoteAccessPasswordsStep> RenderStandalonePasswords(RemoteCheckResultDto? result) =>
        Render<RemoteAccessPasswordsStep>(parameters => parameters.Add(p => p.Result, result));

    [Fact]
    public void ChoosingTailscale_AlsoSavesTheDashboardProxyPort_AndTheConnectStepSaysToRestartOnce()
    {
        _checks.Enqueue(Check(RemoteCheckSuggestions.Tailscale, TailscaleReady()));
        var cut = RenderPanel();
        Services.GetRequiredService<RemoteAccessFlowState>().PollInterval = TimeSpan.Zero;
        Click(cut, "set-up");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-remote-card='findings']")));
        Click(cut, "see-options");
        Click(cut, "choose-tailscale");
        Assert.Null(_settings.Remote.ProxyPort);

        Click(cut, "continue");

        cut.WaitForAssertion(() => Assert.Equal("connect", CurrentStep(cut)));
        Assert.Equal(5017, _settings.Remote.ProxyPort);
        Assert.Equal(["tailscale"], _savedModes);
        var note = cut.Find("[data-remote-proxy-port-note]").TextContent;
        Assert.Contains("port 5017", note, StringComparison.Ordinal);
        Assert.Contains("restart Library", note, StringComparison.Ordinal);
    }

    [Fact]
    public void ChoosingTailscale_KeepsAProxyPortThatWasAlreadySet_AndNeverUsesTheMainPort()
    {
        _checks.Enqueue(Check(RemoteCheckSuggestions.Tailscale, TailscaleReady()));
        _settings.Remote.ProxyPort = 5099;
        var cut = RenderPanel();
        Services.GetRequiredService<RemoteAccessFlowState>().PollInterval = TimeSpan.Zero;
        Click(cut, "set-up");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-remote-card='findings']")));
        Click(cut, "see-options");
        Click(cut, "choose-tailscale");
        Click(cut, "continue");
        cut.WaitForAssertion(() => Assert.Equal("connect", CurrentStep(cut)));
        Assert.Equal(5099, _settings.Remote.ProxyPort);
    }

    [Fact]
    public void ChoosingTheOwnAddress_DoesNotTouchTheProxyPort()
    {
        _checks.Enqueue(Check(RemoteCheckSuggestions.OwnAddress, Finding(RemoteCheckFindingKeys.TailscaleNotInstalled, RemoteCheckStatuses.Info, "Tailscale is not installed")));
        var cut = RenderPanel();
        Click(cut, "set-up");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-remote-card='findings']")));
        Click(cut, "see-options");
        Click(cut, "choose-own-address");
        Click(cut, "continue");
        cut.WaitForAssertion(() => Assert.Equal("connect", CurrentStep(cut)));
        Assert.Null(_settings.Remote.ProxyPort);
    }

    [Fact]
    public void OnAPhone_EveryStepOpensOnItsOwn_ShowsTheSameContentAsDesktop_AndKeepsTheServerStepsTagged()
    {
        _awayAddress = "https://tuvima.tail1234.ts.net";
        _checks.Enqueue(Check(RemoteCheckSuggestions.Tailscale, TailscaleReady()));
        var cut = RenderPanel(deviceClass: "mobile", formFactor: "phone");
        Services.GetRequiredService<RemoteAccessFlowState>().PollInterval = TimeSpan.Zero;
        var seen = new List<string>();

        Click(cut, "set-up");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-remote-card='findings']")));
        seen.Add(Screen(cut));
        Assert.Single(cut.FindAll("[data-remote-screen]"));
        Click(cut, "see-options");
        seen.Add(Screen(cut));
        Assert.Single(cut.FindAll("[data-remote-screen]"));
        Assert.Equal(3, cut.FindAll("[data-remote-method]").Count);
        Click(cut, "choose-tailscale");
        seen.Add(Screen(cut));
        Assert.Single(cut.FindAll("[data-remote-screen]"));
        Click(cut, "continue");
        cut.WaitForAssertion(() => Assert.Equal("connect", CurrentStep(cut)));
        seen.Add(Screen(cut));
        Assert.Single(cut.FindAll("[data-remote-screen]"));
        // The phone step keeps its QR for another device and gains a tappable link, so nothing is left out.
        Assert.NotEmpty(cut.FindAll("[data-remote-qr='phone']"));
        Assert.NotEmpty(cut.FindAll("[data-remote-link='phone-download']"));
        Assert.Single(cut.FindAll("[data-remote-action='send-steps']"));
        Click(cut, "continue");
        seen.Add(Screen(cut));
        Assert.Single(cut.FindAll("[data-remote-screen]"));
        // The Test step on a phone has the QR, the sign-ins, the off switch and the actions, all stacked in one column.
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-remote-qr='address']")));
        Assert.NotEmpty(cut.FindAll("[data-remote-action='run-test']"));
        Assert.NotEmpty(cut.FindAll("[data-remote-card='sign-ins']"));
        Assert.Single(cut.FindAll("[data-remote-switch='allow-outside']"));
        Assert.Equal(["check", "choose", "passwords", "connect", "test"], seen);
    }

    [Fact]
    public void WhenAnOwnAddressIsOn_TheStartPageNamesIt()
    {
        _settings.Remote.PublicHostname = "https://library.example.com";

        var cut = RenderPanel(mode: "custom");

        Assert.Contains("https://library.example.com", cut.Find("[data-remote-card='away']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void OnATv_TheFlowIsNotShown()
    {
        var cut = RenderPanel(deviceClass: "television", formFactor: "tv");

        Assert.Empty(cut.FindAll("[data-remote-flow]"));
    }

    [Fact]
    public void OnAPhone_TheStepsStillOpenOneAtATime_AndTheTailscaleStepOffersToSendTheSteps()
    {
        _checks.Enqueue(Check(RemoteCheckSuggestions.Tailscale, Finding(RemoteCheckFindingKeys.TailscaleNotSignedIn, RemoteCheckStatuses.Warn, "Sign in to Tailscale")));
        var cut = RenderPanel(deviceClass: "mobile", formFactor: "phone");
        Services.GetRequiredService<RemoteAccessFlowState>().PollInterval = TimeSpan.Zero;
        Click(cut, "set-up");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-remote-card='findings']")));
        Assert.Single(cut.FindAll("[data-remote-screen='check']"));
        Click(cut, "see-options");
        Click(cut, "choose-tailscale");
        Click(cut, "continue");
        cut.WaitForAssertion(() => Assert.Equal("connect", CurrentStep(cut)));

        Assert.Single(cut.FindAll("[data-remote-screen]"));
        Assert.Contains(cut.FindAll("button"), button => button.TextContent.Contains("Send me these steps", StringComparison.Ordinal));
        Assert.Equal(3, cut.FindAll(".ra-row__tag").Count(tag => tag.TextContent.Contains("On the server computer", StringComparison.Ordinal)));
    }

    [Fact]
    public void NoOtherDeviceSeesTheSendStepsButton()
    {
        _checks.Enqueue(Check(RemoteCheckSuggestions.Tailscale, TailscaleReady()));
        var cut = RenderPanel();
        Services.GetRequiredService<RemoteAccessFlowState>().PollInterval = TimeSpan.Zero;
        Click(cut, "set-up");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-remote-card='findings']")));
        Click(cut, "see-options");
        Click(cut, "choose-tailscale");
        Click(cut, "continue");
        cut.WaitForAssertion(() => Assert.Equal("connect", CurrentStep(cut)));

        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Contains("Send me these steps", StringComparison.Ordinal));
    }
}
