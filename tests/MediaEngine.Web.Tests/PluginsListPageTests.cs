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
/// The Plugins list page (Plugins and Integrations screens spec WP2): summary strip, search, Status / Media filters, the
/// Official Tuvima switch, category tabs with honest counts, cards, warning strips and paging.
/// </summary>
public sealed class PluginsListPageTests : AsyncBunitContext
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

    public PluginsListPageTests()
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


    [Fact]
    public void ThePage_ShowsTheHeaderAndTheSummaryStrip()
    {
        _plugins = [Plugin("a", "Alpha", enabled: true), Plugin("b", "Beta", enabled: false), Plugin("c", "Gamma", enabled: true)];

        var cut = Render<PluginSettingsTab>();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-state-card=plugin]").Count));
        Assert.Equal("Plugins", cut.Find(".app-list-page-header__title").TextContent);
        Assert.Contains("Manage extensions and integrations", cut.Markup);
        var strip = Squash(cut.Find(".app-summary-strip"));
        Assert.Contains("3installed", strip);
        Assert.Contains("2switchedon", strip);
        Assert.Contains("0needattention", strip);
        Assert.Contains("Showing 3 of 3 plugins", cut.Markup);
    }

    [Fact]
    public void Configure_OpensTheDetailWithABreadcrumb_AMenuAndABackLink_AndBackClosesIt()
    {
        _plugins = [Plugin("a", "Alpha", enabled: true), Plugin("b", "Beta", enabled: false)];
        var cut = Render<PluginSettingsTab>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-state-card=plugin]").Count));

        cut.FindAll("[data-state-card=plugin]")[0].QuerySelectorAll("button")
            .First(button => button.TextContent.Trim() == "Configure").Click();

        var detail = cut.WaitForElement(".settings-plugin-configure-panel");
        Assert.Contains("Configure Alpha", detail.TextContent);
        Assert.NotNull(detail.QuerySelector("nav[aria-label='Breadcrumb'] [aria-current='page']"));
        Assert.NotNull(detail.QuerySelector("button[aria-label='More actions for Alpha']"));

        detail.QuerySelectorAll("button").First(button => button.TextContent.Trim() == "Back to plugins").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".settings-plugin-configure-panel")));
    }

    [Fact]
    public void Configure_ShowsAccess_Needs_Activity_UpdateNote_AndTheRemovalPreview()
    {
        _plugins = [Plugin("a", "Alpha", enabled: true)];
        var cut = Render<PluginSettingsTab>();
        cut.WaitForAssertion(() => Assert.Equal(1, CardCount(cut)));

        cut.Find("[data-state-card=plugin]").QuerySelectorAll("button")
            .First(button => button.TextContent.Trim() == "Configure").Click();

        var sections = cut.WaitForElement("[data-testid=plugin-detail-sections]");
        foreach (var label in new[] { "Access", "Needs and needed by", "Activity", "Update note", "Removing this plugin" })
        {
            Assert.NotNull(sections.QuerySelector($"section[aria-label='{label}']"));
        }

        Assert.Contains("Who can use it", sections.TextContent);
        Assert.Contains("Nothing else depends on it.", sections.TextContent);
        Assert.Contains("Nothing has run yet.", sections.TextContent);
        Assert.Contains(sections.QuerySelectorAll("button"), button => button.TextContent.Trim() == "Check now");
    }

    [Fact]
    public void Search_MatchesNameAndCapabilityText_AndNarrowsTheList()
    {
        var music = Plugin("m", "MusicBrainz", enabled: true);
        music.Capabilities = [new PluginCapabilityDto { Kind = "metadata-source", Name = "Music lookup", Description = "Finds albums" }];
        _plugins = [music, Plugin("o", "Other", enabled: true)];
        var cut = Render<PluginSettingsTab>();
        cut.WaitForAssertion(() => Assert.Equal(2, CardCount(cut)));

        cut.Find(".app-search-filter-row input").Input("music");

        cut.WaitForAssertion(() => Assert.Equal(1, CardCount(cut)));
        Assert.Contains("MusicBrainz", cut.Find("[data-state-card=plugin]").TextContent);
    }

    [Fact]
    public void StatusFilter_Enabled_ListsOnlySwitchedOnPlugins()
    {
        _plugins = [Plugin("a", "Alpha", enabled: true), Plugin("b", "Beta", enabled: false)];
        var cut = Render<PluginSettingsTab>();
        cut.WaitForAssertion(() => Assert.Equal(2, CardCount(cut)));

        cut.FindAll(".app-search-filter-row select")[0].Change("enabled");

        cut.WaitForAssertion(() => Assert.Equal(1, CardCount(cut)));
        Assert.Contains("Alpha", cut.Find("[data-state-card=plugin]").TextContent);
    }

    [Fact]
    public void MediaFilter_KeepsPluginsForThatTypeAndPluginsForEveryType()
    {
        var tv = Plugin("tv", "TvOnly", enabled: true);
        tv.MediaTypes = ["tv"];
        var books = Plugin("bk", "BooksOnly", enabled: true);
        books.MediaTypes = ["books"];
        _plugins = [tv, books, Plugin("all", "Everything", enabled: true)];
        var cut = Render<PluginSettingsTab>();
        cut.WaitForAssertion(() => Assert.Equal(3, CardCount(cut)));

        cut.FindAll(".app-search-filter-row select")[1].Change("tv");

        cut.WaitForAssertion(() => Assert.Equal(2, CardCount(cut)));
        Assert.DoesNotContain("BooksOnly", string.Concat(cut.FindAll("[data-state-card=plugin]").Select(card => card.TextContent)));
    }

    [Fact]
    public void CategoryTabs_ShowHonestCounts_AndNarrowTheList()
    {
        var meta = Plugin("m", "Meta", enabled: true);
        meta.Category = "metadata";
        var media = Plugin("p", "Processor", enabled: true);
        media.Category = "media-processing";
        _plugins = [meta, media, Plugin("u", "Util", enabled: true)];
        var cut = Render<PluginSettingsTab>();
        cut.WaitForAssertion(() => Assert.Equal(3, CardCount(cut)));

        var tabs = cut.FindAll("[role=tab]");
        Assert.Equal(5, tabs.Count);
        Assert.Contains("All3", Squash(tabs[0]));
        Assert.Contains("Metadata1", Squash(tabs[1]));
        Assert.Contains("Mediaprocessing1", Squash(tabs[2]));
        Assert.Contains("Services0", Squash(tabs[3]));
        Assert.Contains("Utilities1", Squash(tabs[4]));

        tabs[1].Click();

        cut.WaitForAssertion(() => Assert.Equal(1, CardCount(cut)));
        Assert.Contains("Meta", cut.Find("[data-state-card=plugin]").TextContent);
    }

    [Fact]
    public void OfficialSwitch_ListsOnlyOfficialPlugins()
    {
        _plugins = [Plugin("a", "Alpha", enabled: true, trust: "official"), Plugin("b", "Beta", enabled: true, trust: "private")];
        var cut = Render<PluginSettingsTab>();
        cut.WaitForAssertion(() => Assert.Equal(2, CardCount(cut)));
        Assert.Contains("Official Tuvima", cut.Markup);

        cut.Find(".app-search-filter-row input[role=switch]").Change(true);

        cut.WaitForAssertion(() => Assert.Equal(1, CardCount(cut)));
        Assert.Contains("Alpha", cut.Find("[data-state-card=plugin]").TextContent);
    }

    [Fact]
    public void NoMatches_SaysSo_AndClearFiltersBringsEverythingBack()
    {
        _plugins = [Plugin("a", "Alpha", enabled: true)];
        var cut = Render<PluginSettingsTab>();
        cut.WaitForAssertion(() => Assert.Equal(1, CardCount(cut)));

        cut.Find(".app-search-filter-row input").Input("zzz");

        cut.WaitForAssertion(() => Assert.Contains("No plugins match", cut.Markup));
        Assert.Contains("All0", Squash(cut.FindAll("[role=tab]")[0]));
        cut.FindAll("button").First(button => button.TextContent.Trim() == "Clear filters").Click();

        cut.WaitForAssertion(() => Assert.Equal(1, CardCount(cut)));
    }

    [Fact]
    public void NoPlugins_ShowsTheEmptyStateAndNoFilters()
    {
        _plugins = [];

        var cut = Render<PluginSettingsTab>();

        cut.WaitForAssertion(() => Assert.Contains("No plugins installed", cut.Markup));
        Assert.Empty(cut.FindAll(".app-search-filter-row"));
    }

    [Fact]
    public void MoreThanTwelvePlugins_PageThroughTheList()
    {
        _plugins = Enumerable.Range(1, 13).Select(i => Plugin("p" + i, "Plugin" + i.ToString("00"), enabled: true)).ToList();
        var cut = Render<PluginSettingsTab>();
        cut.WaitForAssertion(() => Assert.Equal(12, CardCount(cut)));
        Assert.Contains("Showing 12 of 13 plugins", cut.Markup);

        cut.FindAll(".app-pager__pages button").First(button => button.TextContent.Trim() == "Next").Click();

        cut.WaitForAssertion(() => Assert.Equal(1, CardCount(cut)));
        Assert.Contains("Plugin13", cut.Find("[data-state-card=plugin]").TextContent);
    }

    [Fact]
    public void APluginWithAMissingTool_ShowsAWarningStripThatOpensToolsAndRequirements()
    {
        var plugin = Plugin("seg", "Segments", enabled: true);
        plugin.ToolRequirements = [new PluginToolRequirementDto { Id = "ffmpeg", ExecutableName = "ffmpeg" }];
        _plugins = [plugin];
        _onHealth = _ => new PluginHealthResponse
        {
            PluginId = "seg",
            Status = "degraded",
            Checks = [new PluginHealthCheckDto { Status = "degraded", Message = "FFmpeg is missing." }],
        };
        var cut = Render<PluginSettingsTab>();

        cut.WaitForAssertion(() => Assert.Contains("Requires Ffmpeg. Install it for full functionality.", cut.Markup));
        cut.FindAll(".app-warning-strip").First(strip => strip.TextContent.Contains("Requires")).Click();

        Assert.EndsWith("/settings/tools", Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri);
        Assert.Contains("1needattention", Squash(cut.Find(".app-summary-strip")));
    }

    [Fact]
    public async Task OnAPhone_TheListIsReadOnly_WithNoSearchOrFilters()
    {
        await Services.GetRequiredService<DeviceContextService>().InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true));
        _plugins = [Plugin("a", "Alpha", enabled: true)];

        var cut = Render<PluginSettingsTab>();

        cut.WaitForAssertion(() => Assert.Equal(1, CardCount(cut)));
        Assert.Empty(cut.FindAll(".app-search-filter-row"));
        Assert.Empty(cut.FindAll("input[role=switch]"));
    }

    private static int CardCount(IRenderedComponent<PluginSettingsTab> cut) => cut.FindAll("[data-state-card=plugin]").Count;

    private static string Squash(AngleSharp.Dom.IElement element) => System.Text.RegularExpressions.Regex.Replace(element.TextContent, "\\s+", "");

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
