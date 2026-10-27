using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.System;
using MediaEngine.Web.Components.Settings;
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
/// Local AI and Plugins show the same state card, with the same four health words, and the Plugins menu row turns red
/// only for a plugin that is switched on and cannot work.
/// </summary>
public sealed class StateCardPagesTests : AsyncBunitContext
{
    private static readonly Guid AccountId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid ProfileId = Guid.Parse("00000000-0000-0000-0000-0000000000b1");

    private readonly DashboardSessionAccessor _session = new();
    private readonly IEngineApiClient _api;
    private List<PluginSummaryResponse> _plugins = [];
    private List<ScheduledTaskDto> _tasks = [];
    private Func<string, PluginHealthResponse?> _onHealth = _ => null;
    private Func<ToolRequirementsDto?> _onTools = () => null;
    private int _pluginListReads;
    private int _taskListReads;

    public StateCardPagesTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        _api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetPluginsAsync), _ =>
            {
                Interlocked.Increment(ref _pluginListReads);
                return Task.FromResult<IReadOnlyList<PluginSummaryResponse>>(_plugins.ToList());
            });
            stub.SetHandler(nameof(IEngineApiClient.GetScheduledTasksAsync), _ =>
            {
                Interlocked.Increment(ref _taskListReads);
                return Task.FromResult<ScheduledTaskListResponse?>(new ScheduledTaskListResponse { Tasks = _tasks });
            });
            stub.SetHandler(nameof(IEngineApiClient.CheckPluginHealthAsync), args =>
                Task.FromResult(_onHealth((string)args![0]!)));
            stub.SetHandler(nameof(IEngineApiClient.GetToolRequirementsAsync), _ => Task.FromResult(_onTools()));
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

    private SettingsAttentionState State => Services.GetRequiredService<SettingsAttentionState>();

    // ── Plugins ──────────────────────────────────────────────────────────────

    [Fact]
    public void PluginsPage_DrawsOneStateCardPerPlugin_WithItsTrustBadgeAndPlainPermissions()
    {
        _plugins =
        [
            Plugin("a.official", "Alpha", enabled: true, trust: "official", permissions: ["media.read", "network.http"],
                allowedTo: ["Reads your media files", "Connects to the internet"], sendsDataTo: "the internet"),
            Plugin("b.community", "Beta", enabled: false, trust: "community"),
        ];

        var cut = Render<PluginSettingsTab>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-state-card=plugin]").Count));
        var alpha = cut.FindAll("[data-state-card=plugin]")[0];
        Assert.Contains("Alpha", alpha.TextContent, StringComparison.Ordinal);
        Assert.Contains("Official", alpha.TextContent, StringComparison.Ordinal);
        Assert.Contains("Reads your media files", alpha.TextContent, StringComparison.Ordinal);
        Assert.Contains("Connects to the internet", alpha.TextContent, StringComparison.Ordinal);
        Assert.Contains("Yes, to the internet", alpha.TextContent, StringComparison.Ordinal);
        Assert.Contains("Community", cut.FindAll("[data-state-card=plugin]")[1].TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("table.settings-plugin-installed-table"));
    }

    [Fact]
    public void PluginsPage_ACardForAPluginWithDestinations_ListsEveryHostAndWhatItSendsThere()
    {
        _plugins =
        [
            Plugin("a.lore", "Lore", enabled: true, trust: "official", permissions: ["network.http"],
                allowedTo:
                [
                    "Contacts www.wikidata.org: Item IDs of Universes already in your Library",
                    "Contacts *.fandom.com: Requests for the page list of a wiki you approved",
                ],
                sendsDataTo: "www.wikidata.org, *.fandom.com"),
        ];

        var cut = Render<PluginSettingsTab>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-state-card=plugin]")));
        var card = cut.Find("[data-state-card=plugin]");
        Assert.Equal("Yes, to www.wikidata.org, *.fandom.com", card.QuerySelector("[data-testid=sends-data]")!.TextContent.Trim());
        Assert.Contains("Contacts www.wikidata.org: Item IDs of Universes already in your Library", card.TextContent, StringComparison.Ordinal);
        Assert.Contains("Contacts *.fandom.com: Requests for the page list of a wiki you approved", card.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void PluginsPage_ACardForAPluginThatSendsNothingOut_SaysNo()
    {
        _plugins = [Plugin("a.local", "Local", enabled: true, permissions: ["media.read"], allowedTo: ["Reads your media files"])];

        var cut = Render<PluginSettingsTab>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-state-card=plugin]")));
        Assert.Equal("No", cut.Find("[data-state-card=plugin] [data-testid=sends-data]").TextContent.Trim());
    }

    [Fact]
    public void PluginsPage_AnInstalledPluginLink_ScrollsToThatCard_RingsIt_AndOpensItsSettings()
    {
        _plugins =
        [
            Plugin("a.official", "Alpha", enabled: true, trust: "official"),
            Plugin("tuvima.fandom-lore", "Fandom Lore", enabled: false, trust: "official"),
        ];

        var cut = Render<PluginSettingsTab>(parameters => parameters.Add(page => page.FocusPluginId, "TUVIMA.fandom-lore"));

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-state-card=plugin]").Count));
        var focused = cut.FindAll("[data-focused=true]");
        var wrapper = Assert.Single(focused);
        Assert.Equal("plugin-card-tuvima-fandom-lore", wrapper.Id);
        Assert.Contains("settings-plugin-card-focus", wrapper.ClassName, StringComparison.Ordinal);
        Assert.Contains("Fandom Lore", wrapper.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Alpha", wrapper.TextContent, StringComparison.Ordinal);
        cut.WaitForAssertion(() => Assert.Contains("Configure Fandom Lore", cut.Markup, StringComparison.Ordinal));
        cut.WaitForAssertion(() =>
        {
            var scroll = Assert.Single(JSInterop.Invocations["tuvimaEditorScrollTo"]);
            Assert.Equal("#plugin-card-tuvima-fandom-lore", scroll.Arguments[0]);
        });
    }

    [Theory]
    [InlineData("no.such.plugin")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void PluginsPage_AnUnknownOrEmptyPluginLink_OpensThePageNormally_WithNoRingNoScrollAndNoError(string? pluginId)
    {
        _plugins = [Plugin("a.official", "Alpha", enabled: true, trust: "official")];

        var cut = Render<PluginSettingsTab>(parameters => parameters.Add(page => page.FocusPluginId, pluginId));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-state-card=plugin]")));
        Assert.Empty(cut.FindAll("[data-focused]"));
        Assert.DoesNotContain("Configure Alpha", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Plugin settings could not be loaded", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(JSInterop.Invocations, invocation => invocation.Identifier == "tuvimaEditorScrollTo");
    }

    [Fact]
    public void PluginsPage_AnEnabledPluginWithALoadError_ShowsBroken_WithRetry_AndTheMenuRowTurnsRed()
    {
        _plugins = [Plugin("a.bad", "Bad plugin", enabled: true, loadError: "The manifest is missing.")];

        var cut = Render<PluginSettingsTab>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-state-card=plugin]")));
        var card = cut.Find("[data-state-card=plugin]");
        Assert.Equal("broken", card.GetAttribute("data-health"));
        Assert.Contains("Broken", card.TextContent, StringComparison.Ordinal);
        Assert.Contains("The manifest is missing.", card.TextContent, StringComparison.Ordinal);
        Assert.Contains(card.QuerySelectorAll("button"), button => button.TextContent.Trim() == "Retry");
        Assert.DoesNotContain(card.QuerySelectorAll("button"), button => button.TextContent.Trim() == "Configure");

        Assert.True(State.PluginsBroken);
        var plugins = PluginsRow();
        Assert.Equal(MediaSectionAttentionLevel.Blocked, plugins.Attention);
        Assert.Equal("Broken", plugins.AttentionLabel);
    }

    [Fact]
    public void PluginsPage_ADisabledPluginWithALoadError_ShowsOff_AndNothingIsShaded()
    {
        _plugins = [Plugin("a.bad", "Bad plugin", enabled: false, loadError: "The manifest is missing.")];

        var cut = Render<PluginSettingsTab>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-state-card=plugin]")));
        var card = cut.Find("[data-state-card=plugin]");
        Assert.Equal("off", card.GetAttribute("data-health"));
        Assert.Contains(card.QuerySelectorAll("button"), button => button.TextContent.Trim() == "Configure");
        Assert.DoesNotContain("The manifest is missing.", card.TextContent, StringComparison.Ordinal);

        Assert.False(State.PluginsBroken);
        var plugins = PluginsRow();
        Assert.Equal(MediaSectionAttentionLevel.None, plugins.EffectiveAttention);
        Assert.Null(plugins.AttentionLabel);
    }

    [Fact]
    public async Task TheAttentionState_KnowsWhetherAnAiFeaturePluginIsSwitchedOn()
    {
        _plugins = [Plugin("tuvima.ai-vibe-tags", "Vibe tags", enabled: false), Plugin("a.other", "Other", enabled: true)];
        await State.StartAsync();
        Assert.False(State.AiFeaturesOn);
        State.Stop();

        _plugins = [Plugin("tuvima.ai-vibe-tags", "Vibe tags", enabled: true)];
        await State.StartAsync();
        Assert.True(State.AiFeaturesOn);
        State.Stop();
    }

    [Fact]
    public async Task TheAttentionState_ShadesPluginsFromTheListItReads_WithoutTheCardsPageOpen()
    {
        _plugins = [Plugin("a.bad", "Bad plugin", enabled: true, loadError: "Could not load.")];

        await State.StartAsync();

        Assert.True(State.PluginsBroken);
        Assert.Equal("Broken", PluginsRow().AttentionLabel);
        State.Stop();
    }

    [Fact]
    public async Task ThePluginsRow_IsNotShadedOnAPhone()
    {
        _plugins = [Plugin("a.bad", "Bad plugin", enabled: true, loadError: "Could not load.")];
        await State.StartAsync();

        var phone = SettingsMenuBuilder.Build(MenuContext() with { IsMobile = true });

        Assert.All(Flatten(phone), row => Assert.Equal(MediaSectionAttentionLevel.None, row.EffectiveAttention));
        State.Stop();
    }

    [Fact]
    public void PluginsPage_ADegradedHealthCheck_ShowsNeedsAttentionWithTheMessage_AndOpenSettings()
    {
        _plugins = [Plugin("a.ok", "Fine plugin", enabled: true)];
        _onHealth = _ => new PluginHealthResponse
        {
            PluginId = "a.ok",
            Status = "degraded",
            Checks = [new PluginHealthCheckDto { Status = "degraded", Message = "FFmpeg is missing." }],
        };

        var cut = Render<PluginSettingsTab>();

        cut.WaitForAssertion(() =>
        {
            var card = cut.Find("[data-state-card=plugin]");
            Assert.Equal("needsattention", card.GetAttribute("data-health"));
            Assert.Contains("FFmpeg is missing.", card.TextContent, StringComparison.Ordinal);
            Assert.Contains(card.QuerySelectorAll("button"), button => button.TextContent.Trim() == "Configure");
        });
    }

    [Fact]
    public void PluginsPage_AFailedLatestRun_ShowsNeedsAttention_AndTheLastActivityComesFromTheRunRecord()
    {
        _plugins = [Plugin("a.ok", "Fine plugin", enabled: true)];
        _tasks =
        [
            new ScheduledTaskDto
            {
                Key = "fine",
                PluginId = "a.ok",
                LastRun = new ScheduledTaskLastRunDto
                {
                    StartedAt = DateTimeOffset.Now.AddHours(-2).AddMinutes(-1),
                    FinishedAt = DateTimeOffset.Now.AddHours(-2),
                    Outcome = "failed",
                    Message = "The data store was busy.",
                },
            },
        ];

        var cut = Render<PluginSettingsTab>();

        cut.WaitForAssertion(() =>
        {
            var card = cut.Find("[data-state-card=plugin]");
            Assert.Equal("needsattention", card.GetAttribute("data-health"));
            Assert.Contains("The data store was busy.", card.TextContent, StringComparison.Ordinal);
            Assert.Contains("2 hours ago", card.TextContent, StringComparison.Ordinal);
        });
        Assert.Equal(1, Volatile.Read(ref _taskListReads));
    }

    [Fact]
    public void PluginsPage_ReadsTheRunRecordsOncePerVisit_EvenWhenRetryReloadsTheList()
    {
        _plugins = [Plugin("a.bad", "Bad plugin", enabled: true, loadError: "Could not load.")];
        var cut = Render<PluginSettingsTab>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-state-card=plugin]")));

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Retry").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, Volatile.Read(ref _pluginListReads)));
        Assert.Equal(1, Volatile.Read(ref _taskListReads));
    }

    [Fact]
    public async Task PluginsPage_OnAPhone_ShowsTheCardsReadOnly()
    {
        await Services.GetRequiredService<DeviceContextService>().InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true));
        _plugins = [Plugin("a.ok", "Fine plugin", enabled: true)];

        var cut = Render<PluginSettingsTab>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-state-card=plugin]")));
        Assert.Empty(cut.FindAll("input[role=switch]"));
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Trim() == "Configure");
    }

    // ── Local AI ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task LocalAiStatus_DrawsOneEngineCard_WithNoFeatureCardsOrSwitchesOfItsOwn()
    {
        _plugins = AiFeaturePlugins();
        await State.StartAsync();

        var cut = Render<LocalAiSettingsTab>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-state-card=localaiengine]")));
        Assert.Empty(cut.FindAll(".local-ai-settings__feature-card"));
        Assert.Empty(cut.FindAll("input[role=switch]"));
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Trim() == "Save features");
        State.Stop();
    }

    [Fact]
    public async Task LocalAiStatus_WhileLocalAiIsBlocked_ShowsTheEngineAsBroken()
    {
        _onTools = () => new ToolRequirementsDto(
            ToolInstallKinds.Source,
            DateTimeOffset.UnixEpoch,
            true,
            [new ToolRequirementDto(ToolRequirementIds.AiRuntime, "Local AI engine", "tool", ToolRequirementLevels.Required, ToolRequirementStates.Missing,
                "Local AI waits.", null, "Download it.", null, 0, false, false, false, null)]);
        _plugins = AiFeaturePlugins();
        _plugins[0] = _plugins[0] with { Enabled = true };
        await State.StartAsync();

        var cut = Render<LocalAiSettingsTab>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-state-card=localaiengine]")));
        Assert.Equal("broken", cut.Find("[data-state-card=localaiengine]").GetAttribute("data-health"));
        State.Stop();
    }

    private static List<PluginSummaryResponse> AiFeaturePlugins() =>
    [
        Plugin("tuvima.ai-description-insights", "Description insights", enabled: false),
        Plugin("tuvima.ai-series-order", "Series order", enabled: false),
        Plugin("tuvima.ai-smart-labelling", "Smart labelling", enabled: false),
        Plugin("tuvima.ai-type-helper", "Type helper", enabled: false),
        Plugin("tuvima.ai-vibe-tags", "Vibe tags", enabled: false),
    ];

    // ── Helpers ──────────────────────────────────────────────────────────────

    private MediaSectionNavigationItem PluginsRow() =>
        Flatten(SettingsMenuBuilder.Build(MenuContext())).Single(row => row.Label == "Plugins");

    private SettingsMenuContext MenuContext() => new(
        AdministrationAllowed: true,
        HouseholdAdministrator: false,
        IsMobile: false,
        ReviewCount: 0,
        SecurityNeedsAttention: false,
        LocalAiBlocked: State.LocalAiBlocked,
        PluginsBroken: State.PluginsBroken);

    private static IEnumerable<MediaSectionNavigationItem> Flatten(IEnumerable<MediaSectionNavigationGroup> groups) =>
        groups.SelectMany(group => Flatten(group.Items));

    private static IEnumerable<MediaSectionNavigationItem> Flatten(IEnumerable<MediaSectionNavigationItem> items) =>
        items.SelectMany(item => new[] { item }.Concat(item.Children is null ? [] : Flatten(item.Children)));

    private static PluginSummaryResponse Plugin(
        string id,
        string name,
        bool enabled,
        string trust = "private",
        string? loadError = null,
        List<string>? permissions = null,
        List<string>? allowedTo = null,
        string? sendsDataTo = null) =>
        new()
        {
            Id = id,
            Name = name,
            Description = name + " does something useful.",
            Enabled = enabled,
            TrustLevel = trust,
            LoadError = loadError,
            Permissions = permissions ?? [],
            AllowedTo = allowedTo ?? [],
            SendsDataTo = sendsDataTo,
        };

    private void SignInAdministrator()
    {
        AccountProfileGrantDto[] grants =
        [
            new(AccountId, ProfileId, "Test User", null, true, true, true,
                new ProfileAdminProtectionResponse(false, "None", null, 1), 1, DateTimeOffset.UtcNow),
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
