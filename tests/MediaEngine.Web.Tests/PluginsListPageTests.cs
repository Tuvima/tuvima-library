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
    private PluginDependenciesResponse? _dependencies;
    private PluginLibraryLimitResponse? _libraryLimit;
    private PluginRemovalPreviewResponse? _removalPreview;
    private PluginActionResult _removeResult = PluginActionResult.Success;
    private object? _removed;
    private IReadOnlyList<string>? _savedLibraries;
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
            stub.SetHandler(nameof(IEngineApiClient.GetPluginDependenciesAsync), _ => Task.FromResult(_dependencies));
            stub.SetHandler(nameof(IEngineApiClient.GetPluginLibrariesAsync), _ => Task.FromResult(_libraryLimit));
            stub.SetHandler(nameof(IEngineApiClient.SetPluginLibrariesAsync), args =>
            {
                _savedLibraries = (IReadOnlyList<string>)args![1]!;
                return Task.FromResult(true);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetPluginRemovalPreviewAsync), _ => Task.FromResult(_removalPreview));
            stub.SetHandler(nameof(IEngineApiClient.RemovePluginAsync), args =>
            {
                _removed = args![1];
                return Task.FromResult(_removeResult);
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
    public void ThePage_ShowsTheHeaderAndTheSummaryStrip()
    {
        _plugins = [Plugin("a", "Alpha", enabled: true), Plugin("b", "Beta", enabled: false), Plugin("c", "Gamma", enabled: true)];

        var cut = Render<PluginSettingsTab>();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-state-card=plugin]").Count));
        // The page name appears once (in the Settings page header), never again inside the list.
        Assert.Empty(cut.FindAll(".app-list-page-header__title"));
        Assert.Contains("Upload plugin", cut.Find(".app-list-page-header__actions").TextContent);
        var strip = Squash(cut.Find(".app-summary-strip"));
        Assert.Contains("3installed", strip);
        Assert.Contains("2switchedon", strip);
        Assert.Contains("0needattention", strip);
        Assert.Empty(cut.FindAll(".app-pager"));
    }

    [Fact]
    public void Configure_OpensThePluginsOwnPage_ByNavigatingToItsAddress()
    {
        _plugins = [Plugin("a", "Alpha", enabled: true), Plugin("b", "Beta", enabled: false)];
        var cut = Render<PluginSettingsTab>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-state-card=plugin]").Count));

        cut.FindAll("[data-state-card=plugin]")[0].QuerySelectorAll("button")
            .First(button => button.TextContent.Trim() == "Configure").Click();

        Assert.EndsWith("/settings/plugins/a", Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri);
    }

    [Fact]
    public void ThePluginsOwnPage_ShowsItsNameAMenuAndTabsThatAreAddresses()
    {
        _plugins = [Plugin("a", "Alpha", enabled: true)];
        var cut = RenderDetail("a", null);

        var page = cut.WaitForElement("[data-testid=plugin-page]");
        Assert.Contains("Alpha", page.QuerySelector("h2")!.TextContent);
        Assert.NotNull(page.QuerySelector("[data-testid=plugin-status-sentence]"));
        var tabs = page.QuerySelectorAll("a[role=tab]").Select(tab => tab.GetAttribute("href")).ToList();
        Assert.Contains("/settings/plugins/a", tabs);
        Assert.Contains("/settings/plugins/a/activity", tabs);
    }

    [Fact]
    public void TheListView_ShowsOneRowPerPlugin_WithTheSamePillSwitchAndConfigure_WhenTheDeviceRemembersIt()
    {
        JSInterop.Setup<string?>("localStorage.getItem", "tuvima.plugins.layout").SetResult("list");
        _plugins = [Plugin("a", "Alpha", enabled: true), Plugin("b", "Beta", enabled: false)];

        var cut = Render<PluginSettingsTab>();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("[data-testid=plugin-rows] tbody tr");
            Assert.Equal(2, rows.Count);
            Assert.Contains("Alpha", rows[0].TextContent);
            Assert.Contains(rows[0].QuerySelectorAll("button"), button => button.TextContent.Trim() == "Configure");
        });
        Assert.Empty(cut.FindAll("[data-state-card=plugin]"));
    }

    [Fact]
    public void ThePluginsOwnPage_SaysSoWhenThePluginIsNotInstalled()
    {
        _plugins = [Plugin("a", "Alpha", enabled: true)];
        var cut = RenderDetail("nope", null);

        cut.WaitForAssertion(() => Assert.Contains("That plugin is not installed", cut.Markup));
    }

    [Fact]
    public void ThePluginsOwnPage_ShowsAccessNeedsActivityUpdateNoteAndTheRemovalPreview()
    {
        _plugins = [Plugin("a", "Alpha", enabled: true)];

        var setup = RenderDetail("a", null).WaitForElement("[data-testid=plugin-setup]");
        Assert.NotNull(setup.QuerySelector("[data-testid=plugin-privacy-card]"));
        Assert.NotNull(setup.QuerySelector("[data-testid=plugin-about-card]"));
        Assert.Contains("Who can use it", setup.TextContent);
        Assert.Contains("Nothing else depends on it.", setup.TextContent);

        var activity = RenderDetail("a", "activity");
        var text = activity.WaitForElement("section[aria-label='Activity']").TextContent;
        Assert.Contains("Nothing has run yet.", text);
        Assert.Contains(activity.FindAll("button"), button => button.TextContent.Trim() == "Check now");

        Assert.NotNull(setup.QuerySelector("section[aria-label='Removing this plugin']"));
    }

    [Fact]
    public void Configure_ShowsWhatTheEngineSaysAboutAccess_AndTheUpdateNote()
    {
        var plugin = Plugin("a", "Alpha", enabled: true);
        plugin.Access = new PluginAccessDto
        {
            Audience = "everyone",
            VaultWords = "Only if the section's online lookup is on",
            DataLeaving = "The song title goes to LRCLIB",
            WeightClass = "heavy",
            Changelog = "Reads chapter names first.",
        };
        _plugins = [plugin];
        var text = RenderDetail("a", null).WaitForElement("[data-testid=plugin-setup]").TextContent;
        Assert.Contains("Everyone who can use this Library", text);
                Assert.Contains("The song title goes to LRCLIB", text);
        Assert.Contains("Heavy: works when the machine is idle", text);
        Assert.Contains("Reads chapter names first.", text);
    }

    [Fact]
    public void Configure_ListsWhatThePluginNeedsAndWhatNeedsIt()
    {
        _plugins = [Plugin("a", "Alpha", enabled: true)];
        _dependencies = new PluginDependenciesResponse
        {
            PluginId = "a",
            Needs = [new PluginNeedDto { Id = "b", Name = "Skip markers", State = "off" }],
            NeededBy = [new PluginNeededByDto { Id = "c", Name = "Gamma" }],
        };
        var cut = RenderDetail("a", null);

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Skip markers - off", cut.Find("[data-testid=plugin-needs]").TextContent);
            Assert.Contains("Gamma", cut.Find("[data-testid=plugin-needed-by]").TextContent);
        });
    }

    [Fact]
    public void Configure_LetsAnAdministratorLimitThePluginToChosenLibraries()
    {
        _plugins = [Plugin("a", "Alpha", enabled: true)];
        _libraryLimit = new PluginLibraryLimitResponse
        {
            PluginId = "a",
            Libraries =
            [
                new PluginLibraryChoiceDto { Id = "tv", Name = "TV" },
                new PluginLibraryChoiceDto { Id = "movies", Name = "Movies" },
            ],
        };
        var cut = RenderDetail("a", null);
        cut.WaitForAssertion(() => Assert.Contains("All libraries", cut.Find("[data-testid=plugin-detail-sections]").TextContent));

        cut.FindAll("[data-testid=plugin-detail-sections] button").First(button => button.TextContent.Trim() == "Change").Click();
        var picker = cut.WaitForElement("[data-testid=plugin-library-picker]");
        picker.QuerySelectorAll("input[type=checkbox]").First().Change(true);
        picker.QuerySelectorAll("button").First(button => button.TextContent.Trim() == "Save").Click();

        cut.WaitForAssertion(() => Assert.Equal(["tv"], _savedLibraries));
    }

    [Fact]
    public void Removing_ShowsThePreview_AndSendsTheChoicesMade()
    {
        _plugins = [Plugin("a", "Alpha", enabled: true)];
        _removalPreview = new PluginRemovalPreviewResponse
        {
            PluginId = "a",
            Name = "Alpha",
            Lines = ["Its files, settings and schedule are removed.", "12 skip markers it saved can go with it or stay."],
            SavedOutput = "skip markers",
            SavedOutputCount = 12,
            NeededBy = ["Gamma"],
        };
        var cut = RenderDetail("a", null);

        cut.WaitForElement("[data-testid=plugin-setup]").QuerySelectorAll("button")
            .First(button => button.TextContent.Trim() == "Delete plugin").Click();

        var preview = cut.WaitForElement("[data-testid=plugin-removal-preview]");
        Assert.Contains("12 skip markers it saved can go with it or stay.", preview.TextContent);
        cut.FindAll("button").First(button => button.TextContent.Trim() == "Remove the skip markers").Click();
        cut.Find("[data-testid=plugin-setup] input[type=checkbox]").Change(true);
        cut.FindAll("button").First(button => button.TextContent.Trim() == "Confirm delete").Click();

        cut.WaitForAssertion(() =>
        {
            var request = Assert.IsType<PluginRemoveRequest>(_removed);
            Assert.Equal("remove", request.Output);
            Assert.True(request.AlsoSwitchOffDependants);
        });
    }

    [Fact]
    public void Removing_ShowsTheEnginesWordsWhenItRefuses()
    {
        _plugins = [Plugin("a", "Alpha", enabled: true)];
        _removalPreview = new PluginRemovalPreviewResponse { PluginId = "a", Name = "Alpha", Lines = ["Its files are removed."] };
        _removeResult = PluginActionResult.Failure("Gamma needs this plugin. Switch it off too, or cancel.");
        var cut = RenderDetail("a", null);
        cut.WaitForElement("[data-testid=plugin-setup]").QuerySelectorAll("button")
            .First(button => button.TextContent.Trim() == "Delete plugin").Click();
        cut.WaitForElement("[data-testid=plugin-removal-preview]");

        cut.FindAll("button").First(button => button.TextContent.Trim() == "Confirm delete").Click();

        cut.WaitForAssertion(() => Assert.Contains("Gamma needs this plugin.", cut.Markup));
    }

    [Fact]
    public void ACardWaitsForWhatItNeeds_BeforeItIsOpened()
    {
        _plugins = [Plugin("a", "Alpha", enabled: true)];
        _dependencies = new PluginDependenciesResponse { PluginId = "a", WaitingFor = "Waiting for Skip markers" };

        var cut = Render<PluginSettingsTab>();

        cut.WaitForAssertion(() => Assert.Contains("Waiting for Skip markers", cut.Markup));
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

    // The Status and Show menus are tick-box menus (Plugins and Integrations screens v4 D41): open one, tick words, several at once.
    private static void Tick(IRenderedComponent<PluginSettingsTab> cut, string menu, string word)
    {
        if (cut.FindAll(".app-tick-menu__list label").Count == 0)
        {
            cut.FindAll(".app-tick-menu button.app-overflow-menu__trigger").First(button => button.TextContent.Trim().StartsWith(menu)).Click();
        }

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".app-tick-menu__list label")));
        cut.FindAll(".app-tick-menu__list label").First(label => label.TextContent.Trim() == word).QuerySelector("input")!.Change(true);
    }

    [Fact]
    public void StatusFilter_On_ListsOnlySwitchedOnPlugins()
    {
        _plugins = [Plugin("a", "Alpha", enabled: true), Plugin("b", "Beta", enabled: false)];
        var cut = Render<PluginSettingsTab>();
        cut.WaitForAssertion(() => Assert.Equal(2, CardCount(cut)));

        Tick(cut, "Status", "On");

        cut.WaitForAssertion(() => Assert.Equal(1, CardCount(cut)));
        Assert.Contains("Alpha", cut.Find("[data-state-card=plugin]").TextContent);
        Assert.Contains("Status: On", cut.Markup);
    }

    [Fact]
    public void StatusFilter_AllowsSeveralTicksAtOnce()
    {
        _plugins = [Plugin("a", "Alpha", enabled: true), Plugin("b", "Beta", enabled: false), Plugin("c", "Gamma", enabled: true)];
        var cut = Render<PluginSettingsTab>();
        cut.WaitForAssertion(() => Assert.Equal(3, CardCount(cut)));

        Tick(cut, "Status", "On");
        Tick(cut, "Status", "Off");

        cut.WaitForAssertion(() => Assert.Equal(3, CardCount(cut)));
        Assert.Contains("Status: On, Off", cut.Markup);
    }

    [Fact]
    public void CategoryTabs_ShowHonestCounts_AndNarrowTheList()
    {
        var intake = Plugin("m", "Meta", enabled: true);
        intake.Category = "intake";
        var after = Plugin("p", "Processor", enabled: true);
        after.Category = "enrichment";
        _plugins = [intake, after, Plugin("u", "Util", enabled: true)];
        var cut = Render<PluginSettingsTab>();
        cut.WaitForAssertion(() => Assert.Equal(3, CardCount(cut)));

        var tabs = cut.FindAll("[role=tab]");
        Assert.Equal(4, tabs.Count);
        Assert.Contains("All3", Squash(tabs[0]));
        Assert.Contains("Gettingitemsin1", Squash(tabs[1]));
        Assert.Contains("Afterimport2", Squash(tabs[2]));
        Assert.Contains("Accounts&lists0", Squash(tabs[3]));

        tabs[1].Click();

        cut.WaitForAssertion(() => Assert.Equal(1, CardCount(cut)));
        Assert.Contains("Meta", cut.Find("[data-state-card=plugin]").TextContent);
    }

    [Fact]
    public void ShowFilter_Official_ListsOnlyOfficialPlugins_AndNothingIsMarkedOfficial()
    {
        _plugins = [Plugin("a", "Alpha", enabled: true, trust: "official"), Plugin("b", "Beta", enabled: true, trust: "private")];
        var cut = Render<PluginSettingsTab>();
        cut.WaitForAssertion(() => Assert.Equal(2, CardCount(cut)));
        Assert.DoesNotContain("Official Tuvima", cut.Markup);
        Assert.Empty(cut.FindAll(".app-official-pill"));

        Tick(cut, "Show", "Official");

        cut.WaitForAssertion(() => Assert.Equal(1, CardCount(cut)));
        Assert.Contains("Alpha", cut.Find("[data-state-card=plugin]").TextContent);
    }

    [Fact]
    public void APrivateUpload_HasADashedCard_AndNothingElseDoes()
    {
        _plugins = [Plugin("a", "Alpha", enabled: true, trust: "official"), Plugin("b", "Beta", enabled: true, trust: "private")];
        var cut = Render<PluginSettingsTab>();
        cut.WaitForAssertion(() => Assert.Equal(2, CardCount(cut)));

        Assert.Single(cut.FindAll(".app-list-card--private"));
    }

    [Fact]
    public void ATile_HasADotAndOneButton_AndNoPills()
    {
        var plugin = Plugin("a", "Alpha", enabled: true, trust: "official");
        plugin.Provides = ["Details", "Artwork"];
        plugin.ShortDescription = "Finds things.";
        plugin.LongDescription = "Finds things and then some, in a much longer paragraph.";
        _plugins = [plugin];
        var cut = Render<PluginSettingsTab>();
        cut.WaitForAssertion(() => Assert.Equal(1, CardCount(cut)));

        var card = cut.Find("[data-state-card=plugin]");
        Assert.NotNull(card.QuerySelector(".app-status-dot"));
        Assert.Empty(card.QuerySelectorAll(".app-status-badge, .tl-status-badge"));
        Assert.Single(card.QuerySelectorAll(".app-list-card__footer button"));
        Assert.Contains("Details", card.QuerySelector("[data-testid=plugin-provides]")!.TextContent);
        Assert.Equal("Finds things and then some, in a much longer paragraph.", card.QuerySelector(".app-list-card__description")!.GetAttribute("title"));
    }

    [Fact]
    public async Task APluginThatRunsOnLocalAi_ShowsTheSparkle_WaitsForLocalAi_AndLocksTheSwitch()
    {
        var plugin = Plugin("ai", "Insights", enabled: false, trust: "official");
        plugin.RequiresLocalAi = true;
        _plugins = [plugin];
        _onTools = () => new ToolRequirementsDto(
            ToolInstallKinds.Source,
            DateTimeOffset.UnixEpoch,
            true,
            [new ToolRequirementDto(ToolRequirementIds.AiRuntime, "Local AI engine", "tool", ToolRequirementLevels.Required, ToolRequirementStates.Missing,
                "Local AI waits.", null, "Download it.", null, 0, false, false, false, null)]);
        SignInAdministrator();
        await Services.GetRequiredService<SettingsAttentionState>().StartAsync();
        var cut = Render<PluginSettingsTab>();
        cut.WaitForAssertion(() => Assert.Equal(1, CardCount(cut)));

        var card = cut.Find("[data-state-card=plugin]");
        cut.WaitForAssertion(() => Assert.Contains("Set up Local AI", card.QuerySelector(".app-list-card__footer")!.TextContent));
        Assert.Contains("runs on Local AI", card.TextContent);
        Assert.NotNull(card.QuerySelector("input[role=switch][disabled]"));
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
    public void ClickingATab_NeverShowsClearFilters_AndClearingKeepsTheTab()
    {
        _plugins = [Plugin("a", "Alpha", enabled: true), Plugin("b", "Beta", enabled: true)];
        var cut = Render<PluginSettingsTab>();
        cut.WaitForAssertion(() => Assert.Equal(2, CardCount(cut)));

        var tabs = cut.FindAll("[role=tab]");
        tabs[^1].Click();
        cut.WaitForAssertion(() => Assert.Equal("true", cut.FindAll("[role=tab]")[^1].GetAttribute("aria-selected")));
        Assert.DoesNotContain("Clear filters", cut.Markup);

        cut.Find(".app-search-filter-row input").Input("zzz");
        cut.WaitForAssertion(() => Assert.Contains("Clear filters", cut.Markup));
        cut.FindAll("button").First(button => button.TextContent.Trim() == "Clear filters").Click();

        cut.WaitForAssertion(() => Assert.DoesNotContain("Clear filters", cut.Markup));
        Assert.Equal("true", cut.FindAll("[role=tab]")[^1].GetAttribute("aria-selected"));
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
    public void MoreThanTwentyFourPlugins_PageThroughTheList()
    {
        _plugins = Enumerable.Range(1, 25).Select(i => Plugin("p" + i, "Plugin" + i.ToString("00"), enabled: true)).ToList();
        var cut = Render<PluginSettingsTab>();
        cut.WaitForAssertion(() => Assert.Equal(24, CardCount(cut)));
        Assert.Contains("Showing 24 of 25 plugins", cut.Markup);

        cut.FindAll(".app-pager__pages button").First(button => button.TextContent.Trim() == "Next").Click();

        cut.WaitForAssertion(() => Assert.Equal(1, CardCount(cut)));
        Assert.Contains("Plugin25", cut.Find("[data-state-card=plugin]").TextContent);
    }

    [Fact]
    public void APluginWithAMissingTool_ShowsOneAmberPill_AndItsPageOffersToolsAndRequirements()
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

        cut.WaitForAssertion(() => Assert.Contains("Requires Ffmpeg", cut.Markup));
        Assert.Empty(cut.FindAll(".app-warning-strip"));
        Assert.Contains("1needattention", Squash(cut.Find(".app-summary-strip")));

        var page = RenderDetail("seg", null);
        var link = page.WaitForElement("[data-testid=plugin-overview-attention] a[href='/settings/tools']");
        Assert.Contains("Open Tools & requirements", link.TextContent);
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

    private IRenderedComponent<PluginSettingsTab> RenderDetail(string pluginId, string? tab) =>
        Render<PluginSettingsTab>(parameters => parameters
            .Add(component => component.DetailPluginId, pluginId)
            .Add(component => component.DetailTab, tab));

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
            Removable = true,
            Description = name + " does something useful.",
            ShortDescription = name + " does something useful.",
            LongDescription = name + " does something useful.",
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
