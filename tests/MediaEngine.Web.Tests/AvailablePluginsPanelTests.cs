using Bunit;
using MediaEngine.Contracts.Approvals;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Contracts.System;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Plugins > Available plugins (Developer tools spec D-F, D-H): trust labels and the not-verified warning in the Engine's
/// words, the last-checked time, Check now, the off switch, the "new plugins" note, and an Install button that exists only
/// when the Engine says installing is supported and then opens the approval step.
/// </summary>
public sealed class AvailablePluginsPanelTests : AsyncBunitContext
{
    private const string Warning = "Tuvima hasn't checked this plugin. Plugins run inside the Library with its full access.";

    private static readonly Guid AccountId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid ProfileId = Guid.Parse("00000000-0000-0000-0000-0000000000b1");

    private readonly DashboardSessionAccessor _session = new();
    private readonly IEngineApiClient _api;
    private readonly List<string> _calls = [];
    private readonly List<bool> _switchSaves = [];
    private PluginDirectoryResponse _directory = Directory();
    private PluginDirectoryResponse? _afterCheck;
    private PluginDirectoryResponse? _afterSwitch;
    private bool _switchSaveWorks = true;

    public AvailablePluginsPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        _api = EngineApiClientStub.Create(stub =>
        {
            // Only the full Plugins page reads these; the panel on its own does not.
            stub.SetHandler(nameof(IEngineApiClient.GetPluginsAsync), _ =>
                Task.FromResult<IReadOnlyList<PluginSummaryResponse>>([]));
            stub.SetHandler(nameof(IEngineApiClient.GetScheduledTasksAsync), _ =>
                Task.FromResult<ScheduledTaskListResponse?>(new ScheduledTaskListResponse { Tasks = [] }));
            stub.SetHandler(nameof(IEngineApiClient.GetPluginDirectoryAsync), _ =>
            {
                _calls.Add("get");
                return Task.FromResult<PluginDirectoryResponse?>(_directory);
            });
            stub.SetHandler(nameof(IEngineApiClient.CheckPluginDirectoryAsync), _ =>
            {
                _calls.Add("check");
                return Task.FromResult<PluginDirectoryResponse?>(_afterCheck ?? _directory);
            });
            stub.SetHandler(nameof(IEngineApiClient.MarkPluginDirectorySeenAsync), _ =>
            {
                _calls.Add("seen");
                return Task.FromResult<PluginDirectoryResponse?>(_directory);
            });
            stub.SetHandler(nameof(IEngineApiClient.SavePluginDirectoryEnabledAsync), args =>
            {
                _calls.Add("switch");
                _switchSaves.Add((bool)args![0]!);
                if (_switchSaveWorks && _afterSwitch is not null)
                {
                    _directory = _afterSwitch;
                }

                return Task.FromResult(_switchSaveWorks);
            });
            stub.SetHandler(nameof(IEngineApiClient.PreviewPluginDirectoryInstallAsync), args =>
            {
                _calls.Add("preview");
                var id = (string)args![0]!;
                return Task.FromResult<ApprovalPreviewResponse?>(new ApprovalPreviewResponse
                {
                    Kind = ApprovalKinds.Plugin,
                    ItemId = id,
                    Title = "Sketchy",
                    KindLabel = "Plugin",
                    CanDo = ["Connects to the internet"],
                    SendsDataOut = true,
                    SendsDataTo = "the internet",
                    Notes = [Warning],
                    StatementHash = "directory-hash",
                });
            });
        });
        Services.AddSingleton(_api);
        Services.AddSingleton(_session);
        Services.AddSingleton(new DeviceContextService(_api));
        Services.AddSingleton(serviceProvider => new UIOrchestratorService(
            _api,
            new UniverseStateContainer(),
            new ActiveProfileSessionService(
                serviceProvider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>(), _api,
                authenticationStateProvider: new SessionOnlyAuthentication(), dashboardSession: _session),
            new ConfigurationBuilder().Build(),
            NullLogger<UIOrchestratorService>.Instance,
            dashboardSession: _session));
        Services.AddSingleton<SettingsAttentionState>();
        SignInAdministrator();
    }

    [Fact]
    public void EachListedPlugin_ShowsItsTrustLabel_AndOnlyTheUncheckedOnesShowTheWarning()
    {
        _directory = Directory(
            Entry("acme.official", "Official one", trust: "official"),
            Entry("acme.verified", "Verified one", trust: "verified"),
            Entry("acme.sketchy", "Sketchy one", trust: "community", warning: Warning));

        var cut = Render<AvailablePluginsPanel>();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid=available-plugin-entry]").Count));
        Assert.Equal("Official", Label(cut, "acme.official"));
        Assert.Equal("Verified", Label(cut, "acme.verified"));
        Assert.Equal("Not verified", Label(cut, "acme.sketchy"));
        Assert.Empty(Entry(cut, "acme.official").QuerySelectorAll("[data-testid=available-plugin-warning]"));
        Assert.Empty(Entry(cut, "acme.verified").QuerySelectorAll("[data-testid=available-plugin-warning]"));
        Assert.Contains(Warning, Entry(cut, "acme.sketchy").QuerySelector("[data-testid=available-plugin-warning]")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("Connects to the internet", Entry(cut, "acme.sketchy").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Approved Catalog", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInstalledPlugin_IsLabelledInstalled()
    {
        _directory = Directory(Entry("acme.one", "One", installed: true), Entry("acme.two", "Two"));

        var cut = Render<AvailablePluginsPanel>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=available-plugin-entry]").Count));
        Assert.Contains("Installed", Entry(cut, "acme.one").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Installed", Entry(cut, "acme.two").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePage_ShowsWhenTheDirectoryWasLastChecked()
    {
        _directory = Directory(Entry("acme.one", "One"));
        _directory.LastCheckedAt = new DateTimeOffset(2026, 10, 20, 14, 5, 0, TimeSpan.Zero);
        _directory.LastSuccessAt = _directory.LastCheckedAt;

        var cut = Render<AvailablePluginsPanel>();

        cut.WaitForAssertion(() =>
            Assert.Contains($"Last checked {_directory.LastCheckedAt.Value.ToLocalTime().ToString("g")}", cut.Find("[data-testid=available-plugins-checked]").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void WhenTheEngineCouldNotReachGitHub_ThePageShowsTheSavedCopyAndItsDate()
    {
        _directory = Directory(Entry("acme.one", "One"));
        _directory.Status = "saved_copy";
        _directory.Message = "Couldn't reach the plugin directory; showing the copy from 17 October 2026.";
        _directory.LastSuccessAt = new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);
        _directory.LastCheckedAt = new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);

        var cut = Render<AvailablePluginsPanel>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=available-plugin-entry]")));
        Assert.Contains("showing the copy from 17 October 2026", cut.Find("[data-testid=available-plugins-message]").TextContent, StringComparison.Ordinal);
        Assert.Contains($"Saved copy from {_directory.LastSuccessAt.Value.ToLocalTime().ToString("g")}", cut.Find("[data-testid=available-plugins-copy]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void WithTheSwitchOff_ThePageSaysSoAndStillShowsTheSavedCopy_AndCheckNowIsUnavailable()
    {
        _directory = Directory(Entry("acme.one", "One"));
        _directory.Enabled = false;
        _directory.Status = "disabled";
        _directory.Message = "Plugin directory is switched off.";
        _directory.LastSuccessAt = new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);

        var cut = Render<AvailablePluginsPanel>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=available-plugin-entry]")));
        Assert.Contains("Plugin directory is switched off.", cut.Find("[data-testid=available-plugins-message]").TextContent, StringComparison.Ordinal);
        Assert.Contains(_directory.LastSuccessAt.Value.ToLocalTime().ToString("g"), cut.Find("[data-testid=available-plugins-copy]").TextContent, StringComparison.Ordinal);
        Assert.False(cut.Find("input[role=switch]").HasAttribute("checked"));
        Assert.True(cut.Find("[data-testid=available-plugins-check]").HasAttribute("disabled"));
    }

    [Fact]
    public void TheSwitch_SavesTheChoice_ThenReadsTheDirectoryAgain()
    {
        _directory = Directory(Entry("acme.one", "One"));
        _afterSwitch = Directory(Entry("acme.one", "One"));
        _afterSwitch.Enabled = false;
        _afterSwitch.Status = "disabled";
        _afterSwitch.Message = "Plugin directory is switched off.";
        var cut = Render<AvailablePluginsPanel>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=available-plugin-entry]")));

        cut.Find("input[role=switch]").Change(false);

        cut.WaitForAssertion(() => Assert.Contains("Plugin directory is switched off.", cut.Markup, StringComparison.Ordinal));
        Assert.Equal([false], _switchSaves);
        Assert.Equal(["get", "switch", "get"], _calls.Where(call => call is "get" or "switch"));
    }

    [Fact]
    public void WhenTheSwitchCannotBeSaved_TheSwitchGoesBackAndNothingElseHappens()
    {
        _directory = Directory(Entry("acme.one", "One"));
        _switchSaveWorks = false;
        var cut = Render<AvailablePluginsPanel>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=available-plugin-entry]")));

        cut.Find("input[role=switch]").Change(false);

        cut.WaitForAssertion(() => Assert.Contains("switch", _calls));
        Assert.Single(_calls, call => call == "get");
        Assert.True(cut.Find("input[role=switch]").HasAttribute("checked"));
    }

    [Fact]
    public void CheckNow_AsksTheEngine_AndShowsTheNewAnswer()
    {
        _directory = Directory(Entry("acme.one", "One"));
        _afterCheck = Directory(Entry("acme.one", "One"), Entry("acme.two", "Two"));
        var cut = Render<AvailablePluginsPanel>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=available-plugin-entry]")));

        cut.Find("[data-testid=available-plugins-check]").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=available-plugin-entry]").Count));
        Assert.Contains("check", _calls);
    }

    [Fact]
    public void NewPlugins_GetAQuietNote_AndOpeningThePageMarksThemSeen()
    {
        _directory = Directory(Entry("acme.one", "One", isNew: true), Entry("acme.two", "Two", isNew: true), Entry("acme.three", "Three"));
        _directory.NewCount = 2;

        var cut = Render<AvailablePluginsPanel>();

        cut.WaitForAssertion(() => Assert.Equal("2 new plugins available", cut.Find("[data-testid=available-plugins-new]").TextContent.Trim()));
        cut.WaitForAssertion(() => Assert.Contains("seen", _calls));
        Assert.DoesNotContain("badge", cut.Find("[data-testid=available-plugins-new]").OuterHtml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithNothingNew_ThereIsNoNote_AndNothingIsMarkedSeen()
    {
        _directory = Directory(Entry("acme.one", "One"));

        var cut = Render<AvailablePluginsPanel>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=available-plugin-entry]")));
        Assert.Empty(cut.FindAll("[data-testid=available-plugins-new]"));
        Assert.DoesNotContain("seen", _calls);
    }

    [Fact]
    public void WhileInstallingIsNotSupported_ThereIsNoInstallButton_NotEvenAGreyedOne()
    {
        _directory = Directory(Entry("acme.sketchy", "Sketchy", warning: Warning));
        _directory.InstallSupported = false;

        var cut = Render<AvailablePluginsPanel>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=available-plugin-entry]")));
        Assert.Empty(cut.FindAll("[data-testid=available-plugin-install]"));
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Contains("Install", StringComparison.Ordinal));
    }

    [Fact]
    public void WhenInstallingIsSupported_InstallOpensTheApprovalStepWithTheWarning_AndCancelChangesNothing()
    {
        _directory = Directory(Entry("acme.sketchy", "Sketchy", warning: Warning), Entry("acme.here", "Here", installed: true));
        _directory.InstallSupported = true;
        var dialogs = Render<AppDialogHost>();
        var cut = Render<AvailablePluginsPanel>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=available-plugin-entry]").Count));
        Assert.Single(cut.FindAll("[data-testid=available-plugin-install]"));
        Assert.Empty(Entry(cut, "acme.here").QuerySelectorAll("[data-testid=available-plugin-install]"));

        Entry(cut, "acme.sketchy").QuerySelector("[data-testid=available-plugin-install]")!.Click();

        dialogs.WaitForAssertion(() => Assert.Contains("Install Sketchy?", dialogs.Markup));
        Assert.Contains(Warning, dialogs.Find("[data-testid=approval-notes]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Connects to the internet", dialogs.Find("[data-testid=approval-can-do]").TextContent, StringComparison.Ordinal);
        Assert.Contains(dialogs.FindAll("button"), button => button.TextContent.Trim() == "Install");
        dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Cancel").Click();

        dialogs.WaitForAssertion(() => Assert.DoesNotContain("Install Sketchy?", dialogs.Markup));
        Assert.Equal(1, _calls.Count(call => call == "preview"));
    }

    [Fact]
    public void ThePluginsPage_ShowsAvailablePlugins_AndNoLongerTheApprovedCatalogOrThePlannedNote()
    {
        _directory = Directory(Entry("acme.one", "One"));

        var cut = Render<PluginSettingsTab>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=available-plugin-entry]")));
        Assert.Contains("Available plugins", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Approved Catalog", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("marketplace flows are planned", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    private static string Label(IRenderedComponent<AvailablePluginsPanel> cut, string id) =>
        Entry(cut, id).QuerySelector("[data-testid=available-plugin-trust]")!.TextContent.Trim();

    private static AngleSharp.Dom.IElement Entry(IRenderedComponent<AvailablePluginsPanel> cut, string id) =>
        cut.FindAll("[data-testid=available-plugin-entry]").Single(entry => entry.GetAttribute("data-plugin-id") == id);

    private static PluginDirectoryResponse Directory(params PluginDirectoryEntryDto[] entries) => new()
    {
        Status = "fresh",
        Enabled = true,
        SourceUrl = "https://raw.githubusercontent.com/Tuvima/tuvima-library/main/docs/reference/approved-plugins.json",
        Plugins = entries.ToList(),
    };

    private static PluginDirectoryEntryDto Entry(
        string id, string name, string trust = "verified", string? warning = null, bool installed = false, bool isNew = false)
    {
        return new PluginDirectoryEntryDto
        {
            Id = id,
            Name = name,
            Author = "Acme",
            Description = "Does a thing.",
            Version = "1.0.0",
            Kind = "service",
            TrustLevel = trust,
            TrustWarning = warning,
            AllowedTo = ["Connects to the internet"],
            IsInstalled = installed,
            IsNew = isNew,
        };
    }

    private void SignInAdministrator()
    {
        AccountProfileGrantDto[] grants =
        [
            new(AccountId, ProfileId, "Test User", null, true, true, true,
                new GrantAdminProtectionDto(false, "None", null, 1, false, null), 1, DateTimeOffset.UtcNow),
        ];
        _session.Set("token", AccountId, ProfileId, Guid.NewGuid(),
            new DashboardAuthorityResponse(AccountId, ProfileId, true, true, 1, 1, true, true, null, 1, grants, ["settings.administration"], []));
    }

    /// <summary>The active person comes from the Dashboard session; the cookie principal adds nothing.</summary>
    private sealed class SessionOnlyAuthentication : Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider
    {
        public override Task<Microsoft.AspNetCore.Components.Authorization.AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(
                new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity())));
    }
}
