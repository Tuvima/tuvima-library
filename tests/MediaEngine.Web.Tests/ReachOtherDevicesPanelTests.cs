using System.Security.Claims;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Configuration;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Home network spec D8, the Settings side and the first-run question. "Reach from other devices" is on by default and says so plainly; two
/// pills tell what is still open ("Add a password" until the signed-in administrator has one, and whether outside access is on); on Windows
/// Settings says whether the firewall lets the home network in and offers the one Private-only rule; the first run asks once and
/// remembers the answer. Nothing here uses a toast.
/// </summary>
public sealed class ReachOtherDevicesPanelTests : AsyncBunitContext
{
    private readonly HomeNetworkFirewallTests.SpyRunner _runner = new() { IsWindows = true };
    private readonly SwitchableUser _user = new();
    private readonly DashboardSessionAccessor _session = TestSessions.For(kids: false, administrator: true);
    private readonly List<NetworkSettingsDto> _saved = [];
    private NetworkSettingsDto _stored = new();
    private bool _saveFails;

    public ReachOtherDevicesPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddLogging();
        Services.AddNativeUiServices();
        Services.AddSingleton<AuthenticationStateProvider>(_user);
        Services.AddSingleton(_session);
        Services.AddSingleton<IFirewallCommandRunner>(_runner);
        Services.AddSingleton<IHomeNetworkFirewall, HomeNetworkFirewall>();
        // The rule is there once the add command has run.
        _runner.OnRun = (arguments, _) =>
        {
            if (arguments.Contains(" add rule ", StringComparison.Ordinal))
            {
                _runner.RuleExists = true;
                return 0;
            }

            return _runner.RuleExists ? 0 : 1;
        };
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetNetworkSettingsAsync), _ => Task.FromResult<NetworkSettingsDto?>(_stored));
            // The answer is remembered through its own action, so no second "Confirm it's you" is asked for.
            stub.SetHandler(nameof(IEngineApiClient.MarkFirewallPromptAnsweredAsync), _ =>
            {
                var answered = new NetworkSettingsDto();
                answered.Local.FirewallPromptAnswered = !_saveFails;
                _saved.Add(answered);
                return Task.FromResult(!_saveFails);
            });
            stub.SetHandler(nameof(IEngineApiClient.UpdateNetworkSettingsAsync), args =>
            {
                var settings = (NetworkSettingsDto)args![0]!;
                _saved.Add(settings);
                return Task.FromResult<NetworkSettingsDto?>(_saveFails ? null : settings);
            });
        }));
        Render<AppPopoverHost>();
    }

    private IRenderedComponent<ReachOtherDevicesPanel> RenderPanel(NetworkSettingsDto? settings = null, bool container = false, Func<Task>? onSave = null) =>
        Render<ReachOtherDevicesPanel>(p => p
            .Add(x => x.Settings, settings ?? new NetworkSettingsDto())
            .Add(x => x.InContainer, container)
            .Add(x => x.SaveRequested, EventCallback.Factory.Create(this, onSave ?? (() => Task.CompletedTask))));

    // ---- The panel ----

    [Fact]
    public void TheSwitchIsOnByDefaultWithItsSubtitle_AndNoPasswordPillWhenTheAdministratorHasOne()
    {
        var cut = RenderPanel();

        cut.WaitForAssertion(() => Assert.Contains("Reach from other devices", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("Phones, TVs and tablets on your home network", cut.Markup, StringComparison.Ordinal);
        Assert.True(cut.Find("input[role='switch']").HasAttribute("checked"));
        Assert.Empty(cut.FindAll("[data-testid='reach-add-password']"));
        Assert.Contains("Outside access: Off", cut.Find("[data-testid='reach-outside']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAdministratorWithoutAPassword_SeesAnAddAPasswordPillThatOpensSecurity()
    {
        _user.ThisComputerOnly = true;
        var cut = RenderPanel();

        var pill = cut.WaitForElement("[data-testid='reach-add-password']");
        Assert.Equal("/settings/security", pill.GetAttribute("href"));
        Assert.Contains("Add a password", pill.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void OutsideAccessReadsOnlyWhenAWayInFromTheInternetHasBeenChosen()
    {
        var settings = new NetworkSettingsDto();
        settings.Remote.ConnectionMode = "tailscale";

        var cut = RenderPanel(settings);

        Assert.Contains("Outside access: On", cut.Find("[data-testid='reach-outside']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TurningTheSwitchOff_SavesIt_AndSaysPlainlyThatItTakesEffectOnRestart()
    {
        var settings = new NetworkSettingsDto();
        var saves = 0;
        var cut = RenderPanel(settings, onSave: () => { saves++; return Task.CompletedTask; });
        Assert.Empty(cut.FindAll("[data-testid='reach-restart-note']"));

        cut.Find("input[role='switch']").Change(false);

        Assert.False(settings.Local.ReachOtherDevices);
        Assert.Equal(1, saves);
        Assert.Contains("next time Library starts", cut.Find("[data-testid='reach-restart-note']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void OnAContainerInstall_NoSwitchIsOffered()
    {
        var cut = RenderPanel(container: true);

        Assert.Empty(cut.FindAll("input[role='switch']"));
        Assert.NotEmpty(cut.FindAll("[data-testid='reach-container-note']"));
        Assert.Empty(cut.FindAll("[data-testid='reach-firewall']"));
    }

    [Fact]
    public void OnWindowsWithoutTheRule_AnAdministratorOnThisComputerCanAllowIt_AndThenSeesItIsOpen()
    {
        _user.Ingress = ClientIngressValues.ThisComputer;
        var cut = RenderPanel();

        cut.WaitForElement("[data-testid='reach-firewall-blocked']");
        Assert.Contains("public Wi-Fi stays blocked", cut.Find("[data-testid='reach-firewall-blocked']").TextContent, StringComparison.Ordinal);
        Assert.Equal(HomeNetworkFirewallCommands.ManualCommand(5016), cut.Find("[data-testid='reach-firewall-command']").TextContent);

        cut.Find("[data-testid='reach-firewall-allow']").Click();

        cut.WaitForElement("[data-testid='reach-firewall-open']");
        var add = Assert.Single(_runner.Runs, run => run.Arguments.Contains(" add rule ", StringComparison.Ordinal));
        Assert.Contains("profile=private", add.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void OnWindowsWithoutTheRule_APhoneOrAnotherDeviceSeesWhatToAllowButNoButton()
    {
        _user.Ingress = ClientIngressValues.HomeNetwork;
        var cut = RenderPanel();

        cut.WaitForElement("[data-testid='reach-firewall-blocked']");
        Assert.Empty(cut.FindAll("[data-testid='reach-firewall-allow']"));
        Assert.NotEmpty(cut.FindAll("[data-testid='reach-firewall-elsewhere']"));
        Assert.Contains("profile=private", cut.Find("[data-testid='reach-firewall-command']").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Runs, run => run.Arguments.Contains(" add rule ", StringComparison.Ordinal));
    }

    [Fact]
    public void WhenWindowsDeclines_ThePanelSaysSoAndKeepsTheManualCommand()
    {
        _user.Ingress = ClientIngressValues.ThisComputer;
        _runner.OnRun = (_, _) => null;
        var cut = RenderPanel();

        cut.WaitForElement("[data-testid='reach-firewall-allow']").Click();

        cut.WaitForElement("[data-testid='reach-firewall-refused']");
        Assert.NotEmpty(cut.FindAll("[data-testid='reach-firewall-command']"));
    }

    [Fact]
    public void WhenTheRuleIsAlreadyThere_ThePanelSaysWindowsAllowsTheHomeNetwork()
    {
        _runner.RuleExists = true;
        var cut = RenderPanel();

        cut.WaitForElement("[data-testid='reach-firewall-open']");
        Assert.Empty(cut.FindAll("[data-testid='reach-firewall-command']"));
    }

    [Fact]
    public void OffWindows_NoFirewallLineIsDrawn()
    {
        _runner.IsWindows = false;
        var cut = RenderPanel();

        cut.WaitForAssertion(() => Assert.Contains("Reach from other devices", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-testid='reach-firewall']"));
        Assert.Empty(_runner.Runs);
    }

    [Fact]
    public void WithTheSwitchOff_NoFirewallLineIsDrawn()
    {
        var settings = new NetworkSettingsDto();
        settings.Local.ReachOtherDevices = false;
        var cut = RenderPanel(settings);

        cut.WaitForAssertion(() => Assert.Contains("Reach from other devices", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-testid='reach-firewall']"));
    }

    // ---- The first-run question ----

    private IRenderedComponent<HomeNetworkFirewallPrompt> RenderPrompt(bool container = false) =>
        Render<HomeNetworkFirewallPrompt>(p => p.Add(x => x.InContainer, container));

    [Fact]
    public void FirstRun_AsksOnceWithTheApprovedWords_OnWindows_ForAnAdministratorOnThisComputer()
    {
        _user.Ingress = ClientIngressValues.ThisComputer;
        var cut = RenderPrompt();

        var body = cut.WaitForElement("[data-testid='firewall-prompt']");
        Assert.Contains("Allow Library on your home network?", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(
            "So your phone and TV can open it. Windows will ask once. Only home (Private) networks are opened. Public Wi-Fi stays blocked.",
            body.TextContent, StringComparison.Ordinal);
        Assert.Contains("Not now", cut.Find("[data-testid='firewall-not-now']").TextContent, StringComparison.Ordinal);
        Assert.Contains("Allow", cut.Find("[data-testid='firewall-allow']").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Runs, run => run.Arguments.Contains(" add rule ", StringComparison.Ordinal));
    }

    [Fact]
    public void NotNow_RemembersTheAnswerAndLeavesTheFirewallAlone()
    {
        _user.Ingress = ClientIngressValues.ThisComputer;
        var cut = RenderPrompt();

        cut.WaitForElement("[data-testid='firewall-not-now']").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid='firewall-prompt']")));
        Assert.True(Assert.Single(_saved).Local.FirewallPromptAnswered);
        Assert.DoesNotContain(_runner.Runs, run => run.Arguments.Contains(" add rule ", StringComparison.Ordinal));
    }

    [Fact]
    public void Allow_AddsThePrivateOnlyRule_RemembersTheAnswerAndCloses()
    {
        _user.Ingress = ClientIngressValues.ThisComputer;
        var cut = RenderPrompt();

        cut.WaitForElement("[data-testid='firewall-allow']").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid='firewall-prompt']")));
        var add = Assert.Single(_runner.Runs, run => run.Arguments.Contains(" add rule ", StringComparison.Ordinal));
        Assert.Contains("profile=private", add.Arguments, StringComparison.Ordinal);
        Assert.Contains("localport=5016", add.Arguments, StringComparison.Ordinal);
        Assert.True(Assert.Single(_saved).Local.FirewallPromptAnswered);
    }

    [Fact]
    public void Allow_WhenWindowsDeclines_StaysOpenWithAPlainLine_AndStillRemembersItAsked()
    {
        _user.Ingress = ClientIngressValues.ThisComputer;
        _runner.OnRun = (arguments, _) => arguments.Contains(" show rule ", StringComparison.Ordinal) ? 1 : null;
        var cut = RenderPrompt();

        cut.WaitForElement("[data-testid='firewall-allow']").Click();

        var failure = cut.WaitForElement("[data-testid='firewall-prompt-failure']");
        Assert.Contains("Settings", failure.TextContent, StringComparison.Ordinal);
        Assert.True(Assert.Single(_saved).Local.FirewallPromptAnswered);
        Assert.Empty(cut.FindAll("[data-testid='firewall-allow']"));
    }

    [Fact]
    public void WhenTheAnswerCannotBeSaved_ItIsTold_AndTheQuestionStaysForNextTime()
    {
        _user.Ingress = ClientIngressValues.ThisComputer;
        _saveFails = true;
        var cut = RenderPrompt();

        cut.WaitForElement("[data-testid='firewall-not-now']").Click();

        cut.WaitForElement("[data-testid='firewall-prompt-failure']");
        Assert.False(Assert.Single(_saved).Local.FirewallPromptAnswered);
    }

    [Fact]
    public void TheQuestionIsNeverAsked_ToSomeoneOnAnotherDevice()
    {
        _user.Ingress = ClientIngressValues.HomeNetwork;
        AssertNeverAsked(RenderPrompt());
    }

    [Fact]
    public void TheQuestionIsNeverAsked_ToSomeoneWhoIsNotAnAdministrator()
    {
        var plain = TestSessions.For(kids: false, administrator: false);
        _session.Set("token", plain.AccountId, plain.ActiveProfileId, plain.SessionId, plain.Authority);
        _user.Ingress = ClientIngressValues.ThisComputer;
        AssertNeverAsked(RenderPrompt());
    }

    [Fact]
    public void TheQuestionIsNeverAsked_OnceItHasBeenAnswered()
    {
        _stored.Local.FirewallPromptAnswered = true;
        _user.Ingress = ClientIngressValues.ThisComputer;
        AssertNeverAsked(RenderPrompt());
    }

    [Fact]
    public void TheQuestionIsNeverAsked_WithTheSwitchOff()
    {
        _stored.Local.ReachOtherDevices = false;
        _user.Ingress = ClientIngressValues.ThisComputer;
        AssertNeverAsked(RenderPrompt());
    }

    [Fact]
    public void TheQuestionIsNeverAsked_OnAContainerInstall()
    {
        _user.Ingress = ClientIngressValues.ThisComputer;
        AssertNeverAsked(RenderPrompt(container: true));
    }

    [Fact]
    public void TheQuestionIsNeverAsked_WhenTheRuleIsAlreadyThere()
    {
        _runner.RuleExists = true;
        _user.Ingress = ClientIngressValues.ThisComputer;
        AssertNeverAsked(RenderPrompt());
    }

    [Fact]
    public void TheQuestionIsNeverAsked_OffWindows()
    {
        _runner.IsWindows = false;
        _user.Ingress = ClientIngressValues.ThisComputer;
        AssertNeverAsked(RenderPrompt());
        Assert.Empty(_runner.Runs);
    }

    private void AssertNeverAsked(IRenderedComponent<HomeNetworkFirewallPrompt> cut)
    {
        // Give the after-render work its turn, then check that nothing was drawn or changed.
        cut.WaitForState(() => true);
        Task.Delay(100).GetAwaiter().GetResult();
        Assert.Empty(cut.FindAll("[data-testid='firewall-prompt']"));
        Assert.Empty(_saved);
        Assert.DoesNotContain(_runner.Runs, run => run.Arguments.Contains(" add rule ", StringComparison.Ordinal));
    }

    private sealed class SwitchableUser : AuthenticationStateProvider
    {
        public bool ThisComputerOnly { get; set; }
        public string? Ingress { get; set; }

        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, "Owner"),
                new("tuvima:authentication_method", ThisComputerOnly ? ThisComputerRequests.AuthenticationMethod : "Password"),
            };
            if (Ingress is not null)
            {
                claims.Add(new Claim(DashboardPrincipalFactory.ClientIngressClaim, Ingress));
            }

            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))));
        }
    }
}
