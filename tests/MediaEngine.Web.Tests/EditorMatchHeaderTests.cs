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
/// The editor's header on the Match tab (universal editor v6 D2): one line with a small cover, "Match book", a state dot and words, and the same
/// navigation at the right. The state is the Engine's read of the match as soon as the Match tab has it, else the page's answer. On every other
/// tab the full header is unchanged. The Engine is a hand-written stub.
/// </summary>
public sealed class EditorMatchHeaderTests : AsyncBunitContext
{
    private static readonly Guid SeriesId = Guid.Parse("6e000000-0000-0000-0000-000000000001");
    private static readonly Guid BookId = Guid.Parse("6e000000-0000-0000-0000-0000000000b1");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());

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

    private IRenderedComponent<EditorItemHeader> RenderHeader(EditSessionFlow flow, ItemFieldEditSession fields, bool compact, bool unmatched, EditorMatchReadState? read = null) =>
        Render<EditorItemHeader>(parameters => parameters
            .Add(header => header.Flow, flow)
            .Add(header => header.Fields, fields)
            .Add(header => header.Family, new EditorFamilyState(Services.GetRequiredService<IEngineApiClient>(), NullLogger.Instance))
            .Add(header => header.Compact, compact)
            .Add(header => header.Unmatched, unmatched)
            .Add(header => header.MatchRead, read));

    [Fact]
    public async Task OnTheMatchTab_TheHeaderIsOneLine_WithAMatchKindTitle_AStateDot_AndTheSameNavigation()
    {
        var (flow, fields) = await StartAsync();

        var cut = RenderHeader(flow, fields, compact: true, unmatched: true);

        Assert.Equal("Match book", cut.Find("[data-testid=match-header-heading]").TextContent.Trim());
        // The window is named by this heading, so it keeps the item's name for a screen reader (hidden to the eye).
        Assert.Equal("Match book: Dune", cut.Find("[data-testid=match-header-title]").TextContent.Trim());
        Assert.Contains("sr-only", cut.Find("[data-testid=match-header-item]").ClassList);
        Assert.Equal("tl-editor-panel-title", cut.Find("[data-testid=match-header-title]").Id);
        var state = cut.Find("[data-testid=match-header-state]");
        Assert.Equal("Unmatched", state.TextContent.Trim());
        Assert.Equal("unmatched", state.GetAttribute("data-state"));
        Assert.Empty(state.QuerySelectorAll(".tl-editor-header__dot--ok"));
        Assert.NotNull(state.QuerySelector(".tl-editor-header__dot"));
        // One line: no label above the title, no facts line, no big cover, but the home button and the item dropdown are there.
        Assert.Empty(cut.FindAll(".tl-editor-header__label, .tl-editor-header__facts, .tl-editor-header__cover"));
        Assert.NotNull(cut.Find(".tl-editor-header__minicover"));
        Assert.NotNull(cut.Find(".tl-hnav--compact"));
        Assert.NotEmpty(cut.FindAll(".tl-hnav__home"));
        Assert.NotEmpty(cut.FindAll("#tl-hnav-item"));
    }

    [Fact]
    public async Task OnEveryOtherTab_TheFullHeaderIsUnchanged()
    {
        var (flow, fields) = await StartAsync();

        var cut = RenderHeader(flow, fields, compact: false, unmatched: true);

        Assert.Empty(cut.FindAll("[data-testid=match-header]"));
        Assert.NotNull(cut.Find(".tl-editor-header__label"));
        Assert.NotNull(cut.Find(".tl-editor-header__cover"));
        Assert.Equal("Dune", cut.Find(".tl-editor-header__title").TextContent.Trim());
        Assert.Empty(cut.FindAll(".tl-hnav--compact"));
    }

    [Fact]
    public async Task TheEnginesReadOfAMatchedItem_SaysMatchedToTheProvider_WithAGreenDot_WhateverThePageFlagSays()
    {
        var (flow, fields) = await StartAsync();

        var cut = RenderHeader(flow, fields, compact: true, unmatched: true, new EditorMatchReadState(BookId, true, "Apple Books"));

        var state = cut.Find("[data-testid=match-header-state]");
        Assert.Equal("Matched to Apple Books", state.TextContent.Trim());
        Assert.Equal("matched", state.GetAttribute("data-state"));
        Assert.NotNull(state.QuerySelector(".tl-editor-header__dot--ok"));
    }

    [Fact]
    public async Task TheEnginesReadOfAnUnmatchedItem_WinsOverAPageFlagThatSaysMatched()
    {
        var (flow, fields) = await StartAsync();

        var cut = RenderHeader(flow, fields, compact: true, unmatched: false, new EditorMatchReadState(BookId, false, null));

        Assert.Equal("Unmatched", cut.Find("[data-testid=match-header-state]").TextContent.Trim());
    }

    [Fact]
    public async Task AReadAboutAnotherItem_IsIgnored_SoThePageFlagDecidesUntilTheTabHasReadThisOne()
    {
        var (flow, fields) = await StartAsync();

        var cut = RenderHeader(flow, fields, compact: true, unmatched: false, new EditorMatchReadState(SeriesId, false, null));

        Assert.Equal("Matched", cut.Find("[data-testid=match-header-state]").TextContent.Trim());
    }

    [Fact]
    public async Task OnEveryOtherTab_TheFullHeadersBadgeFollowsTheEnginesRead_NotThePageFlag()
    {
        var (flow, fields) = await StartAsync();

        var matched = RenderHeader(flow, fields, compact: false, unmatched: true, new EditorMatchReadState(BookId, true, "TMDB"));
        var unmatched = RenderHeader(flow, fields, compact: false, unmatched: false, new EditorMatchReadState(BookId, false, null));
        var noRead = RenderHeader(flow, fields, compact: false, unmatched: true);

        Assert.Contains("matched", matched.Find(".tl-editor-header__chips").TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not matched", matched.Find(".tl-editor-header__chips").TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not matched", unmatched.Find(".tl-editor-header__chips").TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not matched", noRead.Find(".tl-editor-header__chips").TextContent, StringComparison.OrdinalIgnoreCase);
    }

    private static EditorHeaderLevelDto Level() => new()
    {
        LevelId = SeriesId,
        Total = 1,
        PageSize = 50,
        Items = [new EditorNavItemDto { Id = BookId, Kind = "book", Number = 1, Title = "Dune" }],
        SupportingFiles = new EditorHeaderSupportingFilesDto(),
    };

    private static EditorHeaderDto Header() => new()
    {
        Kind = EditorKindNames.Book,
        Root = new EditorHeaderRootDto { Id = SeriesId, Title = "Dune Novels", MediaType = "Books" },
        CurrentId = BookId,
        Label = "Dune Novels",
        ShowHome = true,
        HomeId = SeriesId,
        ShowItemDropdown = true,
        Level = Level(),
        Config = EditorHeaderTestData.Config(EditorKindNames.Book, "details", "artwork", "match", "history"),
    };

    private static MediaEditorContextDto Context() => new()
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
