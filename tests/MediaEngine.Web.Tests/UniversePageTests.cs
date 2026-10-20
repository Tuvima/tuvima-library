using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Contracts.Universe;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Components.Universe;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Universe page (<c>/universe/{qid}</c>): what it shows at each width, for each kind of person, and in each state.
/// The Engine is a hand-written stub that answers the one overview read.
/// </summary>
public sealed class UniversePageTests : AsyncBunitContext
{
    private const string Qid = "Q4242";

    private readonly IEngineApiClient _api;
    private readonly DeviceContextService _device;
    private readonly DashboardSessionAccessor _session = new();
    private readonly AdministratorAccessSpy _access = new();
    private readonly List<string> _calls = [];
    private Func<UniversePageLoadResult> _overview = () => UniversePageLoadResult.Loaded(Overview());
    private TaskCompletionSource<UniversePageLoadResult>? _gate;
    private SharedEntityEditorContextDto? _editContext;
    private IReadOnlyList<PluginSummaryResponse> _plugins = [];

    public UniversePageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddLogging();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        _api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetUniversePageAsync), _ =>
            {
                _calls.Add("overview");
                return _gate is { } gate ? gate.Task : Task.FromResult(_overview());
            });
            stub.SetHandler(nameof(IEngineApiClient.GetSharedEntityEditorContextAsync), _ =>
                Task.FromResult(_editContext));
            stub.SetHandler(nameof(IEngineApiClient.GetPluginsAsync), _ =>
            {
                _calls.Add("plugins");
                return Task.FromResult(_plugins);
            });
        });
        _device = new DeviceContextService(_api);
        Services.AddSingleton(_api);
        Services.AddSingleton(_device);
        Services.AddSingleton(_session);
        // The page confirms administrator status with the Engine when the session does not already say so; this Engine
        // stand-in answers that check with "not signed in", so the session's own authority decides.
        Services.AddSingleton<IHttpClientFactory>(new SignedOutEngineClients());
        Services.AddScoped<DashboardIdentityClient>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddSingleton<IAdministratorSurfaceAccessService>(_access);
        // What the editor shell inside the drawer needs, as the drawer's own tests provide it.
        Services.AddSingleton(serviceProvider => new UIOrchestratorService(
            _api,
            new UniverseStateContainer(),
            new ActiveProfileSessionService(serviceProvider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>(), _api),
            new ConfigurationBuilder().Build(),
            serviceProvider.GetRequiredService<ILogger<UIOrchestratorService>>()));
        Services.AddSingleton(_ => new ProviderCatalogueService(_api, new MemoryCache(new MemoryCacheOptions())));
    }

    // The hero and its totals ---------------------------------------------------------------------------------------

    [Fact]
    public async Task TheHero_NamesTheUniverse_AndCountsItsSeriesWorksAndFormats()
    {
        _overview = () => UniversePageLoadResult.Loaded(Overview(seriesCount: 3, itemCount: 14, mediaTypes: ["Book", "Movie"]));
        await UseDeviceAsync("web", "desktop");

        var cut = RenderPage();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Dune", cut.Find(".universe-hero__title").TextContent);
            Assert.Equal("3 Series · 14 Works · 2 formats", cut.Find(".universe-hero__totals").TextContent);
        });
    }

    [Theory]
    [InlineData(1, 1, 1, "1 Series · 1 Work · 1 format")]
    [InlineData(2, 7, 1, "2 Series · 7 Works · 1 format")]
    public void TheTotalsLine_UsesSingularAndPluralWording(int series, int works, int formats, string expected)
    {
        var detail = Overview(seriesCount: series, itemCount: works, mediaTypes: Enumerable.Range(0, formats).Select(i => $"Kind{i}").ToList()).Detail;

        Assert.Equal(expected, MediaEngine.Web.Components.Universe.UniversePageText.TotalsLine(detail));
    }

    [Fact]
    public void TheTotalsLine_SaysShows_OnlyWhenEveryLineIsATvShow()
    {
        var shows = Overview(seriesCount: 2, itemCount: 20, mediaTypes: ["TV"], shelves: [AsShow(Shelf("A", "TV", "One")), AsShow(Shelf("B", "TV", "Two"))]).Detail;
        var mixed = Overview(seriesCount: 2, itemCount: 20, mediaTypes: ["TV", "Book"], shelves: [AsShow(Shelf("A", "TV", "One")), Shelf("B", "Book", "Two")]).Detail;

        Assert.Equal("2 shows · 20 Works · 1 format", MediaEngine.Web.Components.Universe.UniversePageText.TotalsLine(shows));
        Assert.StartsWith("2 Series", MediaEngine.Web.Components.Universe.UniversePageText.TotalsLine(mixed), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EachSeries_IsItsOwnShelf_InTheOrderTheEngineGave()
    {
        _overview = () => UniversePageLoadResult.Loaded(Overview(shelves:
        [
            Shelf("The Novels", "Book", "Dune", "Messiah"),
            Shelf("The Films", "Movie", "Part One"),
        ]));
        await UseDeviceAsync("web", "desktop");

        var cut = RenderPage();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(["The Novels", "The Films"], cut.FindAll(".universe-series__title").Select(title => title.TextContent).ToArray());
            Assert.Contains("Messiah", cut.Markup);
        });
    }

    [Fact]
    public async Task TheStandaloneWorks_AreOneShelfCalledStandalone()
    {
        var page = Overview(shelves: [Shelf("The Novels", "Book", "Dune")], standalone: new DisplayShelfDto("standalone", "Standalone", "1 item", [Card("A Lone Film", "Movie")], null));
        _overview = () => UniversePageLoadResult.Loaded(page);
        await UseDeviceAsync("web", "desktop");

        var cut = RenderPage();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("A Lone Film", cut.Markup);
            Assert.Contains("Standalone", cut.FindAll("h2").Select(heading => heading.TextContent));
        });
    }

    // Connections ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("running")]
    [InlineData("not_started")]
    public async Task WithNothingRecordedYetAndEnrichmentNotFinished_ConnectionsSaysItIsStillLearning(string state)
    {
        _overview = () => UniversePageLoadResult.Loaded(Overview(enrichment: new UniverseEnrichmentStateDto { State = state, EnrichedWorkCount = 6, WorkCount = 14 }));
        await UseDeviceAsync("web", "desktop");

        var cut = RenderPage();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Still learning about this Universe", cut.Markup);
            Assert.Contains("Details found for 6 of 14 works", cut.Markup);
            Assert.DoesNotContain("No cast or adaptations recorded", cut.Markup);
            Assert.Empty(cut.FindAll(".universe-cast"));
        });
    }

    [Fact]
    public async Task WithNothingRecordedAndEnrichmentFinished_ConnectionsSaysSoPlainly()
    {
        _overview = () => UniversePageLoadResult.Loaded(Overview(enrichment: new UniverseEnrichmentStateDto { State = "ready", EnrichedWorkCount = 14, WorkCount = 14 }));
        await UseDeviceAsync("web", "desktop");

        var cut = RenderPage();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("No cast or adaptations recorded for this Universe yet.", cut.Markup);
            Assert.DoesNotContain("Still learning", cut.Markup);
        });
    }

    [Fact]
    public async Task ACastRow_OpensAlsoAppearsIn_ForThatPersonAndTheirCharacters()
    {
        var person = Guid.NewGuid();
        _overview = () => UniversePageLoadResult.Loaded(Overview(
            cast: [Cast(person, "Timothee C", ("Q1", "Paul"))]));
        await UseDeviceAsync("web", "desktop");
        var dialogs = Services.GetRequiredService<IAppDialogService>();

        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button.universe-cast__row")));
        cut.Find("button.universe-cast__row").Click();

        var dialog = Assert.Single(((AppDialogService)dialogs).Dialogs);
        Assert.Equal(typeof(AlsoAppearsInPanel), dialog.ComponentType);
    }

    [Fact]
    public async Task Connections_ListsTheCastAsCharacters_AndAdaptationsWithACapitalisedRelationship()
    {
        var person = Guid.NewGuid();
        _overview = () => UniversePageLoadResult.Loaded(Overview(
            cast: [Cast(person, "Timothee C", ("Q1", "Paul"), ("Q2", "Muad'Dib"))],
            adaptations: [new UniverseClientAdaptationDto { Qid = "based on:Q9", Label = "Dune: Part One", Relationship = "based on", SourceQid = "Q9", SourceLabel = "Dune" }]));
        await UseDeviceAsync("web", "desktop");

        var cut = RenderPage();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Timothee C", cut.Find(".universe-cast__name").TextContent);
            Assert.Equal("as Paul, Muad'Dib", cut.Find(".universe-cast__characters").TextContent);
            Assert.Equal("Dune: Part One — Based on Dune", cut.Find(".universe-adaptations__line").TextContent);
            Assert.DoesNotContain("Still learning", cut.Markup);
        });
    }

    [Theory]
    [InlineData("web", "desktop", true)]
    [InlineData("web", "tablet", false)]
    [InlineData("mobile", "phone", false)]
    [InlineData("television", "tv", false)]
    public async Task TheExplorerLink_IsOnlyOnDesktop(string deviceClass, string formFactor, bool expected)
    {
        _overview = () => UniversePageLoadResult.Loaded(Overview(cast: [Cast(Guid.NewGuid(), "Ann", ("Q1", "Hero"))]));
        await UseDeviceAsync(deviceClass, formFactor);

        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".universe-connections")));
        var links = cut.FindAll("a.universe-connections__explorer");
        Assert.Equal(expected, links.Count == 1);
        if (expected)
        {
            Assert.Equal($"/universe/{Qid}/explore", links[0].GetAttribute("href"));
            Assert.Equal("Open the explorer", links[0].TextContent);
        }
    }

    // Editing -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("web", "desktop", true, true, true)]
    [InlineData("web", "desktop", false, true, false)]
    [InlineData("web", "desktop", true, false, false)]
    [InlineData("web", "tablet", true, true, false)]
    [InlineData("mobile", "phone", true, true, false)]
    [InlineData("television", "tv", true, true, false)]
    public async Task TheEditButton_IsOnlyForDesktopAdministratorsWhoMayEditThisUniverse(
        string deviceClass, string formFactor, bool administrator, bool editable, bool expected)
    {
        await UseDeviceAsync(deviceClass, formFactor);
        SignIn(administrator);
        _editContext = EditContext(editable);

        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".universe-hero")));
        cut.WaitForAssertion(() => Assert.Equal(expected, cut.FindAll("button.universe-hero__edit").Count == 1));
    }

    [Fact]
    public async Task TheEditButton_Appears_WhenTheDeviceIsOnlyKnownAfterThePageHasLoaded()
    {
        // A fresh load can render before the browser has reported its device; unknown means "can't edit" until it does.
        SignIn(administrator: true);
        _editContext = EditContext(editable: true);

        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".universe-hero")));
        Assert.Empty(cut.FindAll("button.universe-hero__edit"));

        await cut.InvokeAsync(() => UseDeviceAsync("web", "desktop"));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("button.universe-hero__edit")));
    }

    [Fact]
    public async Task ChoosingEdit_AsksForTheAdministratorUnlockFirst_AndDoesNothingElseWhenItIsRefused()
    {
        await UseDeviceAsync("web", "desktop");
        SignIn(administrator: true);
        _editContext = EditContext(editable: true);
        _access.Unlocked = false;

        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("button.universe-hero__edit")));
        cut.Find("button.universe-hero__edit").Click();

        cut.WaitForAssertion(() => Assert.Equal(1, _access.Requests));
        Assert.Empty(cut.FindAll("aside.tl-editing-drawer"));
    }

    [Fact]
    public async Task ChoosingEdit_AfterTheUnlock_OpensTheSideDrawerBesideThePage_NotAModal()
    {
        await UseDeviceAsync("web", "desktop");
        SignIn(administrator: true);
        _editContext = EditContext(editable: true);
        _access.Unlocked = true;

        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("button.universe-hero__edit")));
        cut.Find("button.universe-hero__edit").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(1, _access.Requests);
            Assert.Single(cut.FindAll("aside.tl-editing-drawer"));
            Assert.Contains("universe-page--editing-drawer", cut.Find(".universe-page").ClassName);
            Assert.Contains("Editing Dune", cut.Find(".tl-editing-drawer__heading h2").TextContent);
        });
        // The page behind it is unchanged.
        Assert.NotEmpty(cut.FindAll(".universe-hero"));
        Assert.Empty(cut.FindAll(".tl-dialog"));
    }

    // The lore plugin -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public async Task TheFandomLine_IsOnlyForAdministrators_WhenThePluginIsOff(bool administrator, bool pluginEnabled, bool expected)
    {
        await UseDeviceAsync("web", "desktop");
        SignIn(administrator);
        _plugins =
        [
            new PluginSummaryResponse { Id = "tuvima.fandom-lore", Name = "Fandom lore", Enabled = pluginEnabled },
            new PluginSummaryResponse { Id = "tuvima.other", Name = "Other", Enabled = true },
        ];

        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".universe-connections")));
        cut.WaitForAssertion(() =>
        {
            var links = cut.FindAll(".universe-connections__fandom a");
            Assert.Equal(expected, links.Count == 1);
            if (expected)
            {
                Assert.Equal("/settings/plugins", links[0].GetAttribute("href"));
                Assert.Equal("Add Fandom details", links[0].TextContent);
            }
        });
        // The plugin list is an administrator's action: other people never trigger the call.
        Assert.Equal(administrator, _calls.Contains("plugins"));
    }

    [Fact]
    public async Task AnUnreadablePluginList_HidesTheFandomLine()
    {
        await UseDeviceAsync("web", "desktop");
        SignIn(administrator: true);
        _plugins = [];

        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".universe-connections")));
        Assert.Empty(cut.FindAll(".universe-connections__fandom"));
    }

    // States --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WhileTheEngineAnswers_ThePageShowsALoadingSkeleton()
    {
        var gate = _gate = new TaskCompletionSource<UniversePageLoadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await UseDeviceAsync("web", "desktop");

        var cut = RenderPage();

        Assert.NotEmpty(cut.FindAll(".universe-page__loading .app-skeleton"));
        Assert.Empty(cut.FindAll(".universe-hero"));
        await cut.InvokeAsync(() => gate.SetResult(UniversePageLoadResult.Loaded(Overview())));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".universe-hero")));
    }

    [Fact]
    public async Task WhenTheEngineCannotBeReached_ThePageSaysSo_AndRetryLoadsIt()
    {
        var answers = new Queue<UniversePageLoadResult>([UniversePageLoadResult.Unavailable, UniversePageLoadResult.Loaded(Overview())]);
        _overview = () => answers.Dequeue();
        await UseDeviceAsync("web", "desktop");

        var cut = RenderPage();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Can't reach the Engine", cut.Markup);
            Assert.Empty(cut.FindAll(".universe-hero"));
        });
        cut.Find("button.app-error-state__retry").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.NotEmpty(cut.FindAll(".universe-hero"));
            Assert.DoesNotContain("Can't reach the Engine", cut.Markup);
        });
    }

    [Fact]
    public async Task AUniverseThatIsNotAvailable_SaysSo_WithoutBlamingTheEngine()
    {
        _overview = () => UniversePageLoadResult.NotFound;
        await UseDeviceAsync("web", "desktop");

        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.Contains("This Universe isn't available.", cut.Markup));
        Assert.DoesNotContain("Can't reach the Engine", cut.Markup);
        Assert.Empty(cut.FindAll(".universe-hero"));
    }

    [Fact]
    public async Task WhileEnrichmentIsRunning_ThePageShowsTheRunningIndicator()
    {
        _overview = () => UniversePageLoadResult.Loaded(Overview(enrichment: new UniverseEnrichmentStateDto { State = "running", EnrichedWorkCount = 1, WorkCount = 4 }));
        await UseDeviceAsync("web", "desktop");

        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.Equal("Enrichment running", cut.Find(".universe-hero__working").TextContent.Trim()));
    }

    [Fact]
    public async Task AUniverseWithOnlyStandaloneWorks_ShowsThePage_NotNotFound()
    {
        var page = Overview(
            seriesCount: 0,
            itemCount: 1,
            mediaTypes: ["Movie"],
            shelves: [],
            standalone: new DisplayShelfDto("standalone", "Standalone", "1 item", [Card("Only Film", "Movie")], null));
        _overview = () => UniversePageLoadResult.Loaded(page);
        await UseDeviceAsync("web", "desktop");

        var cut = RenderPage();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("1 Work \u00B7 1 format", cut.Find(".universe-hero__totals").TextContent);
            Assert.Contains("Only Film", cut.Markup);
            Assert.Empty(cut.FindAll(".universe-series"));
            Assert.DoesNotContain("This Universe isn't available.", cut.Markup);
            // The standalone work's cover is what the hero stack is made of.
            Assert.NotEmpty(cut.FindAll(".universe-hero .artwork-stack__item"));
        });
    }

    [Fact]
    public async Task AnEnrichmentEventFromTheEngineThread_IsHandledOnTheComponentsOwnContext()
    {
        await UseDeviceAsync("web", "desktop");
        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".universe-hero")));
        var container = Services.GetRequiredService<UniverseStateContainer>();

        // SignalR raises these on a pool thread; handling one off the renderer must neither throw nor read twice.
        await Task.Run(() => container.PushUniverseEnrichmentProgress(new UniverseEnrichmentProgressEvent("Q1", "A work", 1, 5, "Reading")));
        await Task.Delay(700);

        Assert.Equal(1, _calls.Count(call => call == "overview"));
        Assert.NotEmpty(cut.FindAll(".universe-hero"));
    }

    [Fact]
    public async Task AnEnrichmentEvent_DoesNotReadTheOverviewAgainStraightAfterAnotherRead()
    {
        await UseDeviceAsync("web", "desktop");
        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".universe-hero")));
        var container = Services.GetRequiredService<UniverseStateContainer>();

        // The event names no Universe, so the page asks its own question, but never more than once per ten seconds.
        await cut.InvokeAsync(() => container.PushUniverseEnrichmentProgress(new UniverseEnrichmentProgressEvent("Q1", "A work", 1, 5, "Reading")));
        await Task.Delay(700);

        Assert.Equal(1, _calls.Count(call => call == "overview"));
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("not_started")]
    public async Task OtherwiseThereIsNoRunningIndicator(string state)
    {
        _overview = () => UniversePageLoadResult.Loaded(Overview(enrichment: new UniverseEnrichmentStateDto { State = state, WorkCount = 4 }));
        await UseDeviceAsync("web", "desktop");

        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".universe-hero")));
        Assert.Empty(cut.FindAll(".universe-hero__working"));
    }

    // Helpers -------------------------------------------------------------------------------------------------------

    private IRenderedComponent<UniversePage> RenderPage() => Render<UniversePage>(parameters => parameters.Add(page => page.Qid, Qid));

    private async Task UseDeviceAsync(string deviceClass, string formFactor) =>
        await _device.InitialiseAsync(new DeviceContextSnapshot(
            deviceClass,
            formFactor,
            Touch: formFactor != "desktop",
            FinePointer: formFactor == "desktop",
            Hover: formFactor == "desktop"));

    private void SignIn(bool administrator) =>
        _session.Set(
            "token",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            new DashboardAuthorityResponse(
                Guid.NewGuid(), Guid.NewGuid(), true, true, 1, 1, administrator, administrator, null, 1, [], [], []));

    private static SharedEntityEditorContextDto EditContext(bool editable) => new(
        new SharedEntityEditorTargetDto(SharedEntityEditorTargetKinds.Universe, Qid, null, Qid),
        "Dune",
        "Universe",
        [new SharedEntityEditorCapabilityDto("details", true, editable)],
        "ready");

    private static UniverseClientCastMemberDto Cast(Guid person, string name, params (string Qid, string Label)[] characters) => new()
    {
        PersonId = person,
        Name = name,
        Characters = characters.Select(character => new UniverseClientCastCharacterDto { Qid = character.Qid, Label = character.Label }).ToList(),
        WorkCount = 1,
    };

    private static UniversePageDto Overview(
        int seriesCount = 1,
        int itemCount = 3,
        IReadOnlyList<string>? mediaTypes = null,
        IReadOnlyList<UniverseMemberShelfDto>? shelves = null,
        DisplayShelfDto? standalone = null,
        IReadOnlyList<UniverseClientCastMemberDto>? cast = null,
        IReadOnlyList<UniverseClientAdaptationDto>? adaptations = null,
        UniverseEnrichmentStateDto? enrichment = null) => new()
        {
            Detail = new UniverseClientDetailDto
            {
                Qid = Qid,
                Name = "Dune",
                ItemCount = itemCount,
                SeriesCount = seriesCount,
                MediaTypes = mediaTypes ?? ["Book"],
                Shelves = shelves ?? [Shelf("The Novels", "Book", "Dune")],
                Cast = cast ?? [],
                Adaptations = adaptations ?? [],
            },
            Standalone = standalone,
            Enrichment = enrichment ?? new UniverseEnrichmentStateDto { State = "not_started", WorkCount = 3 },
            GraphEntityCount = cast is { Count: > 0 } ? 4 : 0,
        };

    private static UniverseMemberShelfDto AsShow(UniverseMemberShelfDto shelf) => new()
    {
        CollectionId = shelf.CollectionId,
        GroupType = "show",
        ItemCount = shelf.ItemCount,
        Shelf = shelf.Shelf,
    };

    private static UniverseMemberShelfDto Shelf(string title, string mediaType, params string[] cardTitles) => new()
    {
        CollectionId = Guid.NewGuid(),
        GroupType = "series",
        ItemCount = cardTitles.Length,
        Shelf = new DisplayShelfDto(Guid.NewGuid().ToString("D"), title, null, cardTitles.Select(cardTitle => Card(cardTitle, mediaType)).ToList(), null),
    };

    internal static DisplayCardDto Card(string title, string mediaType)
    {
        var asset = Guid.NewGuid();
        var fields = typeof(DisplayArtworkDto).GetProperties()
            .Where(property => property.Name.EndsWith("Url", StringComparison.Ordinal))
            .ToDictionary(property => property.Name, property => $"/stream/artwork/{asset:D}?variant={property.Name}");
        var artwork = System.Text.Json.JsonSerializer.Deserialize<DisplayArtworkDto>(System.Text.Json.JsonSerializer.Serialize(fields))!;
        return new DisplayCardDto(asset, asset, asset, null, mediaType, "work", title, null, [], artwork, "portrait", "default", "caption", "bottom", null, [], new(false, true, false, false), DateTimeOffset.UtcNow);
    }

    private sealed class AdministratorAccessSpy : IAdministratorSurfaceAccessService
    {
        public bool Unlocked { get; set; } = true;

        public int Requests { get; private set; }

        public Task<bool> EnsureUnlockedAsync(CancellationToken ct = default)
        {
            Requests++;
            return Task.FromResult(Unlocked);
        }
    }

    private sealed class SignedOutEngineClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new SignedOutHandler(), true) { BaseAddress = new Uri("http://engine.test") };
    }

    private sealed class SignedOutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized));
    }
}
