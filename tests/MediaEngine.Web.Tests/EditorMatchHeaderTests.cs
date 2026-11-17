using Bunit;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.Editor;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The editor's header and the match (universal editor v7 D1; v8 D1): the header is the same on every tab, the Match tab included; there is no
/// "Matched" tag and no state dot; (v8 D1) at the top right a small badge, "Retail" and "Canonical" each with a green check or a red cross, replaces
/// the links and the words "Not matched" and is hidden for an item that never uses a retail provider. The Engine is a hand-written stub.
/// </summary>
public sealed class EditorMatchHeaderTests : AsyncBunitContext
{
    private static readonly Guid SeriesId = Guid.Parse("6e000000-0000-0000-0000-000000000001");
    private static readonly Guid BookId = Guid.Parse("6e000000-0000-0000-0000-0000000000b1");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private IReadOnlyList<EditorMatchLinkDto> _links = [];
    private bool _kindWithoutMatchState;

    public EditorMatchHeaderTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(_device);
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetEditorNavLevelAsync), _ => Task.FromResult<EditorHeaderLevelDto?>(Level()));
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ => Task.FromResult<MediaEditorContextDto?>(Context()));
        }));
        Render<AppPopoverHost>();
    }

    private async Task<(EditSessionFlow Flow, ItemFieldEditSession Fields)> StartAsync()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var api = Services.GetRequiredService<IEngineApiClient>();
        var fields = new ItemFieldEditSession(api);
        var mode = new EditingModeContext(_device, fields) { HasPermission = true };
        mode.Loader = () => fields.LoadAsync(BookId, "Books", "book", null);
        var session = new EditSessionState(new PendingEditList());
        var flow = new EditSessionFlow(session, mode, new EditSessionSaver(api), () => [new EditTrailItem(BookId, "Dune", "Book")], () => Task.FromResult(false));
        Assert.True(await mode.EnterAsync());
        return (flow, fields);
    }

    private IRenderedComponent<EditorItemHeader> RenderHeader(EditSessionFlow flow, ItemFieldEditSession fields, bool unmatched, EditorMatchReadState? read = null) =>
        Render<EditorItemHeader>(parameters => parameters
            .Add(header => header.Flow, flow)
            .Add(header => header.Fields, fields)
            .Add(header => header.Family, new EditorFamilyState(Services.GetRequiredService<IEngineApiClient>(), NullLogger.Instance))
            .Add(header => header.Unmatched, unmatched)
            .Add(header => header.MatchRead, read));

    [Fact]
    public async Task TheHeader_IsTheFullHeaderOnEveryTab_WithNoOneLineMatchForm()
    {
        var (flow, fields) = await StartAsync();

        var cut = RenderHeader(flow, fields, unmatched: true, read: new EditorMatchReadState(BookId, false, null));

        Assert.Empty(cut.FindAll("[data-testid=match-header], .tl-editor-header--compact, .tl-hnav--compact, .tl-editor-header__minicover"));
        Assert.NotNull(cut.Find(".tl-editor-header__label"));
        Assert.NotNull(cut.Find(".tl-editor-header__cover"));
        Assert.Equal("Dune", cut.Find(".tl-editor-header__title").TextContent.Trim());
        Assert.NotEmpty(cut.FindAll(".tl-hnav__home"));
        Assert.NotEmpty(cut.FindAll("#tl-hnav-item"));
    }

    [Fact]
    public async Task AMatchedItem_ShowsNoMatchedTagAndNoStateDot()
    {
        _links = [Link(EditorMatchLinkSources.Provider, "TMDB", "https://www.themoviedb.org/movie/1")];
        var (flow, fields) = await StartAsync();

        var cut = RenderHeader(flow, fields, unmatched: false, read: new EditorMatchReadState(BookId, true, "TMDB"));

        Assert.Empty(cut.FindAll(".tl-editor-header__chips, .tl-editor-header__state, .tl-editor-header__dot, .tl-editor-header__dot--ok"));
        Assert.DoesNotContain("Matched", cut.Find(".tl-editor-header").TextContent.Replace("Matched to", string.Empty), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMatchedItem_ShowsTheBadgeWithTwoGreenChecks_AndNoLinksOrWords()
    {
        _links =
        [
            Link(EditorMatchLinkSources.Provider, "Apple Books", "https://books.apple.com/book/603"),
            Link(EditorMatchLinkSources.Wikidata, "Wikidata", "https://www.wikidata.org/wiki/Q83495"),
        ];
        var (flow, fields) = await StartAsync();

        var cut = RenderHeader(flow, fields, unmatched: false, read: new EditorMatchReadState(BookId, true, "Apple Books"));

        AssertBadge(cut, retail: true, canonical: true);
        Assert.Equal("Retail", cut.Find("[data-testid=header-badge-retail] .tl-hbadge__label").TextContent.Trim());
        Assert.Equal("Canonical", cut.Find("[data-testid=header-badge-canonical] .tl-hbadge__label").TextContent.Trim());
        // The links to the source pages are on the Match tab now: nothing at the top right is a link or the words "Not matched".
        Assert.Empty(cut.FindAll("[data-testid=header-match-links], [data-testid=header-not-matched], .tl-editor-header__links, .tl-editor-header a"));
        // The badge sits in its own slot, away from the window's close button (drawn by the window around the header).
        Assert.NotNull(cut.Find(".tl-editor-header > .tl-editor-header__badge [data-testid=header-match-badge]"));
    }

    [Fact]
    public async Task TheBadge_NamesTheProviderInItsTooltip_AndSaysItInWordsForAScreenReader()
    {
        _links =
        [
            Link(EditorMatchLinkSources.Provider, "Apple Books", "https://books.apple.com/book/603"),
            Link(EditorMatchLinkSources.Wikidata, "Wikidata", "https://www.wikidata.org/wiki/Q83495"),
        ];
        var (flow, fields) = await StartAsync();

        var cut = RenderHeader(flow, fields, unmatched: false, read: new EditorMatchReadState(BookId, true, "Apple Books"));

        Assert.Equal("Matched to Apple Books", cut.Find("[data-testid=header-badge-retail]").ParentElement!.GetAttribute("data-playback-tooltip"));
        Assert.Equal("Matched to Wikidata", cut.Find("[data-testid=header-badge-canonical]").ParentElement!.GetAttribute("data-playback-tooltip"));
        Assert.Equal("Retail: Matched to Apple Books", cut.Find("[data-testid=header-badge-retail] .sr-only").TextContent.Trim());
        Assert.Equal("Canonical: Matched to Wikidata", cut.Find("[data-testid=header-badge-canonical] .sr-only").TextContent.Trim());
        Assert.Equal("Match status", cut.Find("[data-testid=header-match-badge]").GetAttribute("aria-label"));
    }

    [Fact]
    public async Task AnItemMatchedAtTheProviderButNotAtWikidata_ShowsACheckAndACross()
    {
        _links = [Link(EditorMatchLinkSources.Provider, "Apple Books", "https://books.apple.com/book/603")];
        var (flow, fields) = await StartAsync();

        var cut = RenderHeader(flow, fields, unmatched: false, read: new EditorMatchReadState(BookId, true, "Apple Books"));

        AssertBadge(cut, retail: true, canonical: false);
        Assert.Equal("Not matched to Wikidata", cut.Find("[data-testid=header-badge-canonical]").ParentElement!.GetAttribute("data-playback-tooltip"));
        Assert.Equal("Canonical: Not matched to Wikidata", cut.Find("[data-testid=header-badge-canonical] .sr-only").TextContent.Trim());
    }

    [Fact]
    public async Task AnItemWithNoMatchAtAll_ShowsTwoRedCrosses_NotTheWordsNotMatched()
    {
        _links = [];
        var (flow, fields) = await StartAsync();

        var cut = RenderHeader(flow, fields, unmatched: true);

        AssertBadge(cut, retail: false, canonical: false);
        Assert.Equal("Not matched to a retail provider", cut.Find("[data-testid=header-badge-retail]").ParentElement!.GetAttribute("data-playback-tooltip"));
        Assert.Empty(cut.FindAll("[data-testid=header-not-matched], [data-testid=header-match-links]"));
    }

    [Fact]
    public async Task TheEnginesReadOfAnUnmatchedItem_WinsOverAPageFlagThatSaysMatched()
    {
        _links = [Link(EditorMatchLinkSources.Provider, "TMDB", "https://www.themoviedb.org/movie/603")];
        var (flow, fields) = await StartAsync();

        var cut = RenderHeader(flow, fields, unmatched: false, read: new EditorMatchReadState(BookId, false, null));

        Assert.Equal("false", cut.Find("[data-testid=header-badge-retail]").GetAttribute("data-matched"));
    }

    [Fact]
    public async Task AStagedRemoveMatch_ReadsAsACrossOnRetail_AndLeavesTheWikidataCheck()
    {
        _links =
        [
            Link(EditorMatchLinkSources.Provider, "TMDB", "https://www.themoviedb.org/movie/603"),
            Link(EditorMatchLinkSources.Wikidata, "Wikidata", "https://www.wikidata.org/wiki/Q83495"),
        ];
        var (flow, fields) = await StartAsync();

        // The Match tab reads a staged Remove match as not matched; removing a match does not touch the Wikidata match.
        var cut = RenderHeader(flow, fields, unmatched: false, read: new EditorMatchReadState(BookId, false, null));

        AssertBadge(cut, retail: false, canonical: true);
    }

    [Fact]
    public async Task AReadAboutAnotherItem_IsIgnored_SoThePageFlagDecidesUntilTheTabHasReadThisOne()
    {
        _links = [Link(EditorMatchLinkSources.Provider, "TMDB", "https://www.themoviedb.org/movie/603")];
        var (flow, fields) = await StartAsync();

        var cut = RenderHeader(flow, fields, unmatched: false, read: new EditorMatchReadState(SeriesId, false, null));

        Assert.Equal("true", cut.Find("[data-testid=header-badge-retail]").GetAttribute("data-matched"));
    }

    [Fact]
    public async Task AKindThatNeverUsesARetailProvider_ShowsNoBadge()
    {
        _kindWithoutMatchState = true;
        var (flow, fields) = await StartAsync();

        var cut = RenderHeader(flow, fields, unmatched: true);

        Assert.Empty(cut.FindAll("[data-testid=header-match-badge], .tl-editor-header__badge, .tl-editor-header--badge"));
        Assert.NotNull(cut.Find(".tl-editor-header__title"));
    }

    private static void AssertBadge(IRenderedComponent<EditorItemHeader> cut, bool retail, bool canonical)
    {
        var retailItem = cut.Find("[data-testid=header-badge-retail]");
        var canonicalItem = cut.Find("[data-testid=header-badge-canonical]");
        Assert.Equal(retail ? "true" : "false", retailItem.GetAttribute("data-matched"));
        Assert.Equal(canonical ? "true" : "false", canonicalItem.GetAttribute("data-matched"));
        Assert.Contains(retail ? "tl-hbadge__mark--yes" : "tl-hbadge__mark--no", retailItem.QuerySelector(".tl-hbadge__mark")!.ClassList);
        Assert.Contains(canonical ? "tl-hbadge__mark--yes" : "tl-hbadge__mark--no", canonicalItem.QuerySelector(".tl-hbadge__mark")!.ClassList);
    }

    private static EditorMatchLinkDto Link(string source, string name, string url) => new() { Source = source, Name = name, Url = url };

    private static EditorHeaderLevelDto Level() => new()
    {
        LevelId = SeriesId,
        Total = 1,
        PageSize = 50,
        Items = [new EditorNavItemDto { Id = BookId, Kind = "book", Number = 1, Title = "Dune" }],
        SupportingFiles = new EditorHeaderSupportingFilesDto(),
    };

    private EditorHeaderDto Header() => new()
    {
        Kind = EditorKindNames.Book,
        Root = new EditorHeaderRootDto { Id = SeriesId, Title = "Dune Novels", MediaType = "Books" },
        CurrentId = BookId,
        Label = "Dune Novels",
        ShowHome = true,
        HomeId = SeriesId,
        ShowItemDropdown = true,
        Level = Level(),
        MatchLinks = _links,
        Config = BookConfig(),
    };

    private EditorHeaderConfigDto BookConfig()
    {
        var config = EditorHeaderTestData.Config(EditorKindNames.Book, "details", "artwork", "match", "history");
        return _kindWithoutMatchState
            ? new EditorHeaderConfigDto { Kind = config.Kind, InlineSupportingFiles = config.InlineSupportingFiles, ItemListPageSize = config.ItemListPageSize, Tabs = config.Tabs, FactsLine = ["year"], QuickFacts = config.QuickFacts }
            : config;
    }

    private MediaEditorContextDto Context() => new()
    {
        LaunchEntityId = BookId,
        MediaType = "Books",
        InitialScope = "book",
        AvailableTabs = ["details", "artwork", "links", "history"],
        Header = Header(),
        Scopes =
        [
            new MediaEditorScopeDto
            {
                ScopeId = "book",
                Label = "book",
                FieldEntityId = BookId,
                DisplayTitle = "Dune",
                CanEditFields = true,
                CanEditArtwork = true,
                ArtworkSlots = ["CoverArt"],
                FieldSnapshot = new MediaEditorScopeFieldSnapshotDto
                {
                    CanonicalFields = [new MediaEditorScopedFieldValueDto { Key = "title", Value = "Dune", ProviderName = "openlibrary" }],
                    DisplayOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    AllowedOverrideKeys = ["title", "description"],
                },
            },
        ],
    };
}
