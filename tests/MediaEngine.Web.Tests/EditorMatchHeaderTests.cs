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
/// The editor's header and the match (universal editor v7 D1 to D3): the header is the same on every tab, the Match tab included; there is no
/// "Matched" tag and no state dot; at the top right the Engine's match links show as plain outlined buttons (a name and one small arrow), or the
/// plain words "Not matched" when the item has no match. The Engine is a hand-written stub.
/// </summary>
public sealed class EditorMatchHeaderTests : AsyncBunitContext
{
    private static readonly Guid SeriesId = Guid.Parse("6e000000-0000-0000-0000-000000000001");
    private static readonly Guid BookId = Guid.Parse("6e000000-0000-0000-0000-0000000000b1");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private IReadOnlyList<EditorMatchLinkDto> _links = [];

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
        Assert.DoesNotContain("Matched", cut.Find(".tl-editor-header").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMatchedItem_ShowsTheEnginesLinksAsPlainOutlinedButtons_WithTheProductNameAndOneArrow()
    {
        _links =
        [
            Link(EditorMatchLinkSources.Provider, "TMDB", "https://www.themoviedb.org/movie/603"),
            Link(EditorMatchLinkSources.Wikidata, "Wikidata", "https://www.wikidata.org/wiki/Q83495"),
        ];
        var (flow, fields) = await StartAsync();

        var cut = RenderHeader(flow, fields, unmatched: false, read: new EditorMatchReadState(BookId, true, "TMDB"));

        var links = cut.FindAll("[data-testid=header-match-links] a");
        Assert.Equal(["TMDB", "Wikidata"], links.Select(link => link.TextContent.Trim()).ToArray());
        Assert.Equal(["https://www.themoviedb.org/movie/603", "https://www.wikidata.org/wiki/Q83495"], links.Select(link => link.GetAttribute("href") ?? string.Empty).ToArray());
        Assert.All(links, link =>
        {
            Assert.Equal("_blank", link.GetAttribute("target"));
            Assert.Contains("app-button--outlined", link.ClassList);
            Assert.Contains("tl-editor-header__link", link.ClassList);
            // One small outward arrow, no letter badge or logo.
            Assert.Single(link.QuerySelectorAll("svg"));
            Assert.Empty(link.QuerySelectorAll(".app-provider-logo, .tl-editor-header__link-logo"));
        });
        Assert.Empty(cut.FindAll("[data-testid=header-not-matched]"));
    }

    [Fact]
    public async Task AMatchedItem_WhoseSourceHasNoPage_ShowsNoLinkForThatSource_AndNoWords()
    {
        _links = [Link(EditorMatchLinkSources.Wikidata, "Wikidata", "https://www.wikidata.org/wiki/Q1")];
        var (flow, fields) = await StartAsync();

        var cut = RenderHeader(flow, fields, unmatched: false, read: new EditorMatchReadState(BookId, true, "ISBN"));

        Assert.Equal(["Wikidata"], cut.FindAll("[data-testid=header-match-links] a").Select(link => link.TextContent.Trim()).ToArray());
        Assert.Empty(cut.FindAll("[data-testid=header-not-matched]"));
    }

    [Fact]
    public async Task AMatchedItem_WithNoPageAtAnySource_ShowsNothingAtTheTopRight()
    {
        _links = [];
        var (flow, fields) = await StartAsync();

        var cut = RenderHeader(flow, fields, unmatched: false, read: new EditorMatchReadState(BookId, true, "ISBN"));

        Assert.Empty(cut.FindAll(".tl-editor-header__links, [data-testid=header-not-matched]"));
    }

    [Fact]
    public async Task AnItemWithNoMatch_ShowsThePlainWordsNotMatched_AndNoLinks()
    {
        _links = [Link(EditorMatchLinkSources.Wikidata, "Wikidata", "https://www.wikidata.org/wiki/Q1")];
        var (flow, fields) = await StartAsync();

        var cut = RenderHeader(flow, fields, unmatched: true);

        Assert.Equal("Not matched", cut.Find("[data-testid=header-not-matched]").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid=header-match-links], .tl-editor-header__links a, .tl-editor-header__links button"));
    }

    [Fact]
    public async Task TheEnginesReadOfAnUnmatchedItem_WinsOverAPageFlagThatSaysMatched()
    {
        _links = [Link(EditorMatchLinkSources.Provider, "TMDB", "https://www.themoviedb.org/movie/603")];
        var (flow, fields) = await StartAsync();

        var cut = RenderHeader(flow, fields, unmatched: false, read: new EditorMatchReadState(BookId, false, null));

        Assert.Equal("Not matched", cut.Find("[data-testid=header-not-matched]").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid=header-match-links]"));
    }

    [Fact]
    public async Task AReadAboutAnotherItem_IsIgnored_SoThePageFlagDecidesUntilTheTabHasReadThisOne()
    {
        _links = [Link(EditorMatchLinkSources.Provider, "TMDB", "https://www.themoviedb.org/movie/603")];
        var (flow, fields) = await StartAsync();

        var cut = RenderHeader(flow, fields, unmatched: false, read: new EditorMatchReadState(SeriesId, false, null));

        Assert.NotEmpty(cut.FindAll("[data-testid=header-match-links] a"));
        Assert.Empty(cut.FindAll("[data-testid=header-not-matched]"));
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
        Config = EditorHeaderTestData.Config(EditorKindNames.Book, "details", "artwork", "match", "history"),
    };

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
