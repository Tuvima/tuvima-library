using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Contracts.Universe;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Components.Universe;
using MediaEngine.Web.Services.Editing;
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
    private EditorSubjectDto? _subject = DuneSubject();
    private readonly List<EditSessionRequest> _sessions = [];
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
            stub.SetHandler(nameof(IEngineApiClient.GetUniverseEditorSubjectAsync), _ =>
            {
                _calls.Add("subject");
                return Task.FromResult(_subject);
            });
            stub.SetHandler(nameof(IEngineApiClient.RunEditSessionAsync), args =>
            {
                var request = (EditSessionRequest)args![0]!;
                _sessions.Add(request);
                return Task.FromResult(LibraryBatchEditOutcome<EditSessionResult>.Ok(new EditSessionResult { PreviewOnly = request.PreviewOnly, Valid = true }));
            });
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
        // What the editor shell inside the section slot needs, as the slot's own tests provide it.
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
            Assert.DoesNotContain("recorded for this Universe yet", cut.Markup);
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
            Assert.Contains("No characters, cast or adaptations recorded for this Universe yet.", cut.Markup);
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
            adaptations: [Adaptation("Dune: Part One", "based on", "Dune")]));
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

    [Fact]
    public async Task Connections_ShowTheSourceOnEveryCastAndAdaptationRow()
    {
        _overview = () => UniversePageLoadResult.Loaded(Overview(
            cast: [Cast(Guid.NewGuid(), "Ann", [Wikidata, new UniverseSourceDto { Kind = UniverseSourceDto.LorePlugin, Label = "Dune Wiki" }], ("Q1", "Hero"))],
            adaptations: [Adaptation("Dune: Part One", "based on", "Dune")]));
        await UseDeviceAsync("web", "desktop");

        var cut = RenderPage();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Sources: Wikidata, Dune Wiki", cut.Find(".universe-cast__source").TextContent);
            Assert.Equal("Source: Wikidata", cut.Find(".universe-adaptations__source").TextContent);
        });
    }

    [Fact]
    public async Task Connections_ListCastThenAdaptationsThenCharacters_AndCharactersKeepTheEngineOrderWithTheirSource()
    {
        _overview = () => UniversePageLoadResult.Loaded(Overview(
            cast: [Cast(Guid.NewGuid(), "Ann", ("Q1", "Hero"))],
            adaptations: [Adaptation("Dune: Part One", "based on", "Dune")],
            characters: [Character("Q10", "Paul", 3), Character("Q11", "Chani", 1), Character("Q12", "Jessica", 0)]));
        await UseDeviceAsync("web", "desktop");

        var cut = RenderPage();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(["Cast", "Adaptations", "Characters"], cut.FindAll(".universe-connections__subtitle").Select(h => h.TextContent).ToArray());
            Assert.Equal(["Paul", "Chani", "Jessica"], cut.FindAll(".universe-characters__name").Select(n => n.TextContent).ToArray());
            Assert.Equal(["In 3 of your works", "In 1 of your works"], cut.FindAll(".universe-characters__works").Select(n => n.TextContent).ToArray());
            Assert.All(cut.FindAll(".universe-characters__source"), source => Assert.Equal("Source: Wikidata", source.TextContent));
            Assert.Equal(3, cut.FindAll(".universe-characters__source").Count);
            Assert.DoesNotContain("Still learning", cut.Markup);
        });
    }

    [Fact]
    public async Task ChoosingACharacter_OpensAlsoAppearsIn_ForThatOneCharacterAndNoPerson()
    {
        _overview = () => UniversePageLoadResult.Loaded(Overview(characters: [Character("Q10", "Paul", 3), Character("Q11", "Chani", 1)]));
        await UseDeviceAsync("web", "desktop");
        var dialogs = (AppDialogService)Services.GetRequiredService<IAppDialogService>();

        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("button.universe-characters__row").Count));
        cut.FindAll("button.universe-characters__row")[1].Click();

        var dialog = Assert.Single(dialogs.Dialogs);
        Assert.Equal(typeof(AlsoAppearsInPanel), dialog.ComponentType);
        Assert.Equal(Qid, dialog.Parameters[nameof(AlsoAppearsInPanel.UniverseQid)]);
        var characters = Assert.IsAssignableFrom<IReadOnlyList<(string Qid, string Label)>>(dialog.Parameters[nameof(AlsoAppearsInPanel.Characters)]);
        Assert.Equal(("Q11", "Chani"), Assert.Single(characters));
        Assert.False(dialog.Parameters.ContainsKey(nameof(AlsoAppearsInPanel.PersonId)) && dialog.Parameters[nameof(AlsoAppearsInPanel.PersonId)] is not null);
    }

    [Fact]
    public async Task AUniverseWithOnlyCharacters_IsNotEmptyAndNotStillLearning()
    {
        _overview = () => UniversePageLoadResult.Loaded(Overview(characters: [Character("Q10", "Paul", 3)]));
        await UseDeviceAsync("web", "desktop");

        var cut = RenderPage();

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(".universe-characters__item"));
            Assert.DoesNotContain("Still learning", cut.Markup);
            Assert.DoesNotContain("recorded for this Universe yet", cut.Markup);
            Assert.Empty(cut.FindAll(".universe-cast"));
            Assert.Empty(cut.FindAll(".universe-adaptations"));
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
    [InlineData("web", "desktop", true, true)]
    [InlineData("web", "desktop", false, false)]
    [InlineData("web", "tablet", true, false)]
    [InlineData("mobile", "phone", true, false)]
    [InlineData("television", "tv", true, false)]
    public async Task TheEditButton_IsOnlyForDesktopAdministrators(
        string deviceClass, string formFactor, bool administrator, bool expected)
    {
        await UseDeviceAsync(deviceClass, formFactor);
        SignIn(administrator);

        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".universe-hero")));
        cut.WaitForAssertion(() => Assert.Equal(expected, cut.FindAll("button.universe-hero__edit").Count == 1));
    }

    [Fact]
    public async Task TheEditButton_Appears_WhenTheDeviceIsOnlyKnownAfterThePageHasLoaded()
    {
        // A fresh load can render before the browser has reported its device; unknown means "can't edit" until it does.
        SignIn(administrator: true);

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
        _access.Unlocked = false;

        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("button.universe-hero__edit")));
        cut.Find("button.universe-hero__edit").Click();

        cut.WaitForAssertion(() => Assert.Equal(1, _access.Requests));
        Assert.Empty(cut.FindAll(".tl-editing-bar"));
        Assert.Empty(cut.FindAll(".tl-editor-panel"));
        Assert.DoesNotContain("subject", _calls);
    }

    [Fact]
    public async Task ChoosingEdit_AfterTheUnlock_ShowsTheSharedBarAndPanel_WithDetailsAndArtworkButNoMatch()
    {
        var cut = await StartEditingAsync();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(1, _access.Requests);
            Assert.Single(cut.FindAll(".tl-editing-bar"));
            Assert.Single(cut.FindAll(".tl-editor-panel[data-tool='details']"));
        });
        // The Universe edits through the same bar as every other page: no banner, slot, drawer or dialog.
        Assert.Empty(cut.FindAll(".tl-editing-banner"));
        Assert.Empty(cut.FindAll(".tl-editing-slot"));
        Assert.Empty(cut.FindAll(".tl-dialog"));
        Assert.DoesNotContain("tl-editing-drawer", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(["Details", "Artwork"], cut.FindAll(".tl-editing-bar__tool").Select(button => button.TextContent.Trim()));
        Assert.Equal(["Dune"], cut.FindAll(".tl-editing-bar__trail li").Select(item => item.TextContent.Trim()));
    }

    [Fact]
    public async Task TheDetailsPanel_ListsTheLocalNameAndDescription_AndTheSeriesReadOnly()
    {
        var cut = await StartEditingAsync();

        cut.WaitForElement(".tl-editor-field[data-field-key='title'] input");
        Assert.Single(cut.FindAll(".tl-editor-field[data-field-key='description'] textarea"));
        var series = cut.Find(".tl-editor-details__group[aria-label='Series in this Universe']");
        Assert.Contains("The Novels", series.TextContent);
        Assert.Empty(series.QuerySelectorAll("input, textarea, button"));
    }

    [Fact]
    public async Task ARenamedUniverse_ShowsOnTheHeroAtOnce_AndSavesNothingUntilSaveChanges()
    {
        var cut = await StartEditingAsync();

        cut.WaitForElement(".tl-editor-field[data-field-key='title'] input").Input("Arrakis");

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Arrakis", cut.Find(".universe-hero__title").TextContent.Trim());
            Assert.Contains("Save 1 change", cut.Find("button.tl-editing-bar__save").TextContent);
        });
        Assert.Empty(_sessions);
    }

    [Fact]
    public async Task AFieldWithALocalOverride_OffersResetToTheCanonicalValue()
    {
        _subject = DuneSubject(overridden: true);
        var cut = await StartEditingAsync();

        var title = cut.WaitForElement(".tl-editor-field[data-field-key='title']");

        Assert.NotNull(title.QuerySelector(".tl-editor-field__dot"));
        Assert.Contains(title.QuerySelectorAll("button"), button => button.TextContent.Contains("Reset to canonical value"));
    }

    [Fact]
    public async Task Cancel_EndsEditing_AndTakesTheBarAndThePanelAway()
    {
        var cut = await StartEditingAsync();
        cut.WaitForElement(".tl-editor-panel");

        cut.Find("button.tl-editing-bar__cancel").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(".tl-editing-bar"));
            Assert.Empty(cut.FindAll(".tl-editor-panel"));
        });
        Assert.NotEmpty(cut.FindAll(".universe-hero"));
    }

    [Fact]
    public async Task AUniverseTheEngineWillNotOpen_ShowsTheReasonOnTheBar()
    {
        _subject = null;
        var cut = await StartEditingAsync(expectPanel: false);

        cut.WaitForAssertion(() => Assert.Contains("Editing is not ready", cut.Find(".tl-editing-bar__notice").TextContent));
    }

    private async Task<IRenderedComponent<UniversePage>> StartEditingAsync(bool expectPanel = true)
    {
        await UseDeviceAsync("web", "desktop");
        SignIn(administrator: true);
        _access.Unlocked = true;

        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("button.universe-hero__edit")));
        cut.Find("button.universe-hero__edit").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".tl-editing-bar")));
        if (expectPanel)
        {
            cut.WaitForElement(".tl-editor-panel");
        }

        return cut;
    }

    private static EditorSubjectDto DuneSubject(bool overridden = false) => new()
    {
        Id = Guid.Parse("00000000-0000-0000-0000-0000000000d1"),
        Kind = EditorSubjectKinds.Universe,
        Title = "Dune",
        ArtworkEntityKind = "Collection",
        ArtworkSlots = ["CoverArt", "Background"],
        Fields =
        [
            new EditorSubjectFieldDto { Key = "title", Label = "Name", Value = overridden ? "Arrakis" : "Dune", CanonicalValue = "Dune", IsOverridden = overridden, SourceLabel = "Wikidata" },
            new EditorSubjectFieldDto { Key = "description", Label = "Description", InputKind = EditorSubjectInputKinds.TextArea, Value = "A desert world.", CanonicalValue = "A desert world.", SourceLabel = "Wikidata" },
        ],
    };

    [Fact]
    public async Task TheAdministratorExtras_Appear_WhenTheSignInArrivesAfterThePageHasLoaded()
    {
        // A fresh load can render before the sign-in details reach the screen; when they arrive the page checks again.
        await UseDeviceAsync("web", "desktop");
        _plugins = [new PluginSummaryResponse { Id = "tuvima.fandom-lore", Name = "Fandom lore", Enabled = false }];

        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".universe-connections")));
        Assert.Empty(cut.FindAll("button.universe-hero__edit"));
        Assert.Empty(cut.FindAll(".universe-connections__fandom a"));

        await cut.InvokeAsync(() => SignIn(administrator: true));

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll("button.universe-hero__edit"));
            Assert.Single(cut.FindAll(".universe-connections__fandom a"));
        });
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
                Assert.Equal("/settings/plugins?plugin=tuvima.fandom-lore", links[0].GetAttribute("href"));
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

    private static readonly UniverseSourceDto Wikidata = new() { Kind = UniverseSourceDto.Wikidata, Label = "Wikidata" };

    private static UniverseSourcedCastMemberDto Cast(Guid person, string name, params (string Qid, string Label)[] characters) =>
        Cast(person, name, [Wikidata], characters);

    private static UniverseSourcedCastMemberDto Cast(Guid person, string name, IReadOnlyList<UniverseSourceDto> sources, params (string Qid, string Label)[] characters) => new()
    {
        Member = new UniverseClientCastMemberDto
        {
            PersonId = person,
            Name = name,
            Characters = characters.Select(character => new UniverseClientCastCharacterDto { Qid = character.Qid, Label = character.Label }).ToList(),
            WorkCount = 1,
        },
        Sources = sources,
    };

    private static UniverseSourcedAdaptationDto Adaptation(string label, string relationship, string sourceLabel) => new()
    {
        Adaptation = new UniverseClientAdaptationDto { Qid = $"{relationship}:{label}", Label = label, Relationship = relationship, SourceQid = "Q9", SourceLabel = sourceLabel },
        Sources = [Wikidata],
    };

    private static UniverseCharacterDto Character(string qid, string label, int workCount, bool noPlotDetails = false) => new()
    {
        Qid = qid,
        Label = label,
        WorkCount = workCount,
        NoPlotDetails = noPlotDetails,
        Sources = [Wikidata],
    };

    private static UniversePageDto Overview(
        int seriesCount = 1,
        int itemCount = 3,
        IReadOnlyList<string>? mediaTypes = null,
        IReadOnlyList<UniverseMemberShelfDto>? shelves = null,
        DisplayShelfDto? standalone = null,
        IReadOnlyList<UniverseSourcedCastMemberDto>? cast = null,
        IReadOnlyList<UniverseSourcedAdaptationDto>? adaptations = null,
        IReadOnlyList<UniverseCharacterDto>? characters = null,
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
            },
            Connections = new UniverseConnectionsDto
            {
                Characters = characters ?? [],
                CharacterCount = characters?.Count ?? 0,
                Cast = cast ?? [],
                Adaptations = adaptations ?? [],
            },
            Standalone = standalone,
            Enrichment = enrichment ?? new UniverseEnrichmentStateDto { State = "not_started", WorkCount = 3 },
            GraphEntityCount = cast is { Count: > 0 } || characters is { Count: > 0 } ? 4 : 0,
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
