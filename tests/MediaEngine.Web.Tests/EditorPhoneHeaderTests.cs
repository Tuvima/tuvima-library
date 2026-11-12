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
/// The editor's header on a phone (universal editor v6 section 4a, D20 to D22): always the one line (a small cover, the title, the state), the
/// whole line opens the navigation as a bottom sheet with Home, the Season choice, the episodes with their pictures, the supporting files and the
/// folder button, and the "..." at its right holds Delete. The Engine's header is test data; the sheet uses the same rows and handlers as the dropdowns.
/// </summary>
public sealed class EditorPhoneHeaderTests : AsyncBunitContext
{
    private static readonly Guid EpisodeId = EditorHeaderTestData.EpisodeThreeId;

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private EditorHeaderDto _header = EditorHeaderTestData.TvEpisode();

    public EditorPhoneHeaderTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        Services.AddSingleton(_device);
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetEditorNavLevelAsync), _ => Task.FromResult<EditorHeaderLevelDto?>(_header.Level));
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ => Task.FromResult<MediaEditorContextDto?>(Context()));
        }));
        Render<AppPopoverHost>();
        Render<AppDialogHost>();
    }

    private async Task<(EditSessionFlow Flow, ItemFieldEditSession Fields)> StartAsync()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var api = Services.GetRequiredService<IEngineApiClient>();
        var fields = new ItemFieldEditSession(api);
        var mode = new EditingModeContext(_device, fields) { HasPermission = true };
        mode.Loader = () => fields.LoadAsync(EpisodeId, "TV", "episode", null);
        var session = new EditSessionState(new PendingEditList());
        var flow = new EditSessionFlow(session, mode, new EditSessionSaver(api), () => [new EditTrailItem(EpisodeId, "Echoes in the Dark", "Episode")], () => Task.FromResult(false));
        Assert.True(await mode.EnterAsync());
        return (flow, fields);
    }

    private IRenderedComponent<EditorItemHeader> RenderHeader(EditSessionFlow flow, ItemFieldEditSession fields, bool phone, bool unmatched = false,
        EditorMatchReadState? read = null, bool compact = false, IReadOnlyList<AppRowMenuItem>? menu = null) =>
        Render<EditorItemHeader>(parameters => parameters
            .Add(header => header.Flow, flow)
            .Add(header => header.Fields, fields)
            .Add(header => header.Family, new EditorFamilyState(Services.GetRequiredService<IEngineApiClient>(), NullLogger.Instance))
            .Add(header => header.Phone, phone)
            .Add(header => header.PhoneMenuItems, menu)
            .Add(header => header.Compact, compact)
            .Add(header => header.Unmatched, unmatched)
            .Add(header => header.MatchRead, read));

    [Fact]
    public async Task OnAPhone_TheHeaderIsAlwaysOneLine_WithASmallCoverTheTitleAndTheState()
    {
        var (flow, fields) = await StartAsync();

        var cut = RenderHeader(flow, fields, phone: true, unmatched: true);

        var header = cut.Find("[data-testid=phone-header]");
        Assert.Equal("Echoes in the Dark", cut.Find("[data-testid=phone-header-title]").TextContent.Trim());
        Assert.Equal("tl-editor-panel-title", cut.Find("[data-testid=phone-header-title]").Id);
        Assert.NotNull(header.QuerySelector(".tl-editor-header__minicover"));
        var state = cut.Find("[data-testid=phone-header-state]");
        Assert.Equal("Unmatched", state.TextContent.Trim());
        Assert.Equal("unmatched", state.GetAttribute("data-state"));
        // No big cover, label, facts line, provider links or row of dropdowns: that is the desktop header.
        Assert.Empty(cut.FindAll(".tl-editor-header__cover, .tl-editor-header__label, .tl-editor-header__facts, .tl-editor-header__links, .tl-hnav"));
    }

    [Fact]
    public async Task OnAPhone_TheStateSaysMatchedToTheProvider_OnceTheEngineHasReadIt()
    {
        var (flow, fields) = await StartAsync();

        var cut = RenderHeader(flow, fields, phone: true, unmatched: true, read: new EditorMatchReadState(EpisodeId, true, "TheTVDB"));

        var state = cut.Find("[data-testid=phone-header-state]");
        Assert.Equal("Matched to TheTVDB", state.TextContent.Trim());
        Assert.Equal("matched", state.GetAttribute("data-state"));
        Assert.NotNull(state.QuerySelector(".tl-editor-header__dot--ok"));
    }

    [Fact]
    public async Task OnAPhoneOnTheMatchTab_TheLineStaysOneLine_AndNamesTheMatch()
    {
        var (flow, fields) = await StartAsync();

        var cut = RenderHeader(flow, fields, phone: true, compact: true, unmatched: true);

        Assert.Equal("Match episode", cut.Find("[data-testid=phone-header-title] > span:first-child").TextContent.Trim());
        Assert.Single(cut.FindAll("[data-testid=phone-header]"));
        Assert.Empty(cut.FindAll("[data-testid=match-header]"));
    }

    [Fact]
    public async Task OnADesktop_TheFullHeaderIsUnchanged_WithNoPhoneLineAndNoNavigationButton()
    {
        var (flow, fields) = await StartAsync();

        var cut = RenderHeader(flow, fields, phone: false, unmatched: true);

        Assert.Empty(cut.FindAll("[data-testid=phone-header], #tl-hnav-phone"));
        Assert.NotNull(cut.Find(".tl-editor-header__cover"));
        Assert.NotNull(cut.Find(".tl-hnav"));
        Assert.Empty(cut.FindAll(".app-row-menu__trigger, .app-overflow-menu__trigger"));
    }

    [Fact]
    public async Task OnAPhone_TappingTheLineOpensTheNavigationSheet_WithHomeSeasonEpisodesSupportingFilesAndTheFolderButton()
    {
        var (flow, fields) = await StartAsync();
        var cut = RenderHeader(flow, fields, phone: true);
        Assert.Empty(cut.FindAll("[data-testid=phone-nav-sheet]"));

        var tap = cut.Find("#tl-hnav-phone");
        Assert.Equal("false", tap.GetAttribute("aria-expanded"));
        Assert.Contains("Echoes in the Dark", tap.GetAttribute("aria-label"), StringComparison.Ordinal);
        tap.Click();

        var sheet = cut.Find("[data-testid=phone-nav-sheet]");
        Assert.Equal("true", cut.Find("#tl-hnav-phone").GetAttribute("aria-expanded"));
        Assert.Equal($"Go to {EditorHeaderTestData.ShowTitle}", sheet.QuerySelector("[data-testid=phone-nav-home]")!.GetAttribute("aria-label"));
        Assert.Contains("Season 2", sheet.QuerySelector("[data-testid=phone-nav-second]")!.TextContent, StringComparison.Ordinal);
        var episodes = sheet.QuerySelectorAll(".tl-hnav__option[data-state]");
        Assert.Equal(3, episodes.Length);
        Assert.All(episodes, episode => Assert.NotNull(episode.QuerySelector(".tl-hnav__still")));
        Assert.Equal(["Episode guide", "Script, S02E03"], sheet.QuerySelectorAll("[data-file-kind] .tl-hnav__text > b").Select(file => file.TextContent.Trim()).ToArray());
        Assert.NotNull(sheet.QuerySelector("#tl-hnav-all-files"));
        Assert.Contains("The Last Signal / Season 02", sheet.QuerySelector("#tl-hnav-folder")!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnAPhone_TheSeasonChoiceOpensItsListInsideTheSheet_NotInAPopoverOfItsOwn()
    {
        var (flow, fields) = await StartAsync();
        var cut = RenderHeader(flow, fields, phone: true);
        cut.Find("#tl-hnav-phone").Click();
        Assert.Empty(cut.FindAll("[role=listbox][aria-label=Season]"));

        cut.Find("[data-testid=phone-nav-second]").Click();

        var seasons = cut.Find("[data-testid=phone-nav-sheet] [role=listbox][aria-label=Season]");
        Assert.Equal(["Season 1", "Season 2"], seasons.QuerySelectorAll(".tl-hnav__option .tl-hnav__text > b").Select(entry => entry.TextContent.Trim()).ToArray());
        Assert.Empty(cut.FindAll(".tl-popover.tl-hnav__surface--second"));
    }

    [Fact]
    public async Task OnAPhone_TheSheetClosesWhenTheBackdropOrEscapeCancelsIt_AndTheLineIsTheOnlyWayBackIn()
    {
        var (flow, fields) = await StartAsync();
        var cut = RenderHeader(flow, fields, phone: true);
        cut.Find("#tl-hnav-phone").Click();

        cut.Find("#tl-hnav-phone").Click();

        Assert.Empty(cut.FindAll("[data-testid=phone-nav-sheet]"));
        Assert.Equal("false", cut.Find("#tl-hnav-phone").GetAttribute("aria-expanded"));
    }

    [Fact]
    public async Task OnAPhone_AFilmWithNothingToMoveAroundIn_HasNoNavigationButton()
    {
        _header = EditorHeaderTestData.Movie();
        var (flow, fields) = await StartAsync();

        var cut = RenderHeader(flow, fields, phone: true);

        Assert.NotNull(cut.Find("[data-testid=phone-header]"));
        Assert.Empty(cut.FindAll("#tl-hnav-phone"));
    }

    [Fact]
    public async Task OnAPhone_TheDeleteMenuItemsShowAsAMoreActionsButtonAtTheRightOfTheLine()
    {
        var (flow, fields) = await StartAsync();
        var deleted = 0;
        var menu = new[]
        {
            new AppRowMenuItem
            {
                Label = "Delete",
                Destructive = true,
                OnClick = Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, () => deleted++),
            },
        };

        var cut = RenderHeader(flow, fields, phone: true, menu: menu);

        var more = cut.Find("[data-testid=phone-header] .app-overflow-menu__trigger, [data-testid=phone-header] .app-row-menu__trigger");
        Assert.Equal("More actions", more.GetAttribute("aria-label"));
        more.Click();
        var item = cut.WaitForElement(".app-menu-item");
        Assert.Equal("Delete", item.TextContent.Trim());
        item.Click();
        cut.WaitForAssertion(() => Assert.Equal(1, deleted));
    }

    [Fact]
    public async Task OnAPhone_ArrowDownInTheSheetMovesExactlyOneRow_AndRunsOnceNotOncePerHandler()
    {
        var (flow, fields) = await StartAsync();
        var cut = RenderHeader(flow, fields, phone: true);
        cut.Find("#tl-hnav-phone").Click();
        cut.Find("#tl-hnav-home").TriggerEvent("onfocusin", new Microsoft.AspNetCore.Components.Web.FocusEventArgs());

        cut.Find("#tl-hnav-home").KeyDown(Key.Down);

        // Home, then the Season row: not the first episode, which is where a key heard by the row's panel and by the sheet both would land.
        Assert.Equal("tl-hnav-second", FocusedId());
    }

    [Fact]
    public async Task OnAPhone_ArrowDownInAnEpisodeListMovesExactlyOneEpisode()
    {
        var (flow, fields) = await StartAsync();
        var cut = RenderHeader(flow, fields, phone: true);
        cut.Find("#tl-hnav-phone").Click();
        var episodes = cut.FindAll(".tl-hnav__option[data-state]");
        episodes[0].TriggerEvent("onfocusin", new Microsoft.AspNetCore.Components.Web.FocusEventArgs());

        episodes[0].KeyDown(Key.Down);

        Assert.Equal(episodes[1].Id, FocusedId());
    }

    [Fact]
    public async Task OnAPhone_ArrowDownFromTheSeasonRowReachesTheSeasonRowsOnceItIsOpen()
    {
        var (flow, fields) = await StartAsync();
        var cut = RenderHeader(flow, fields, phone: true);
        cut.Find("#tl-hnav-phone").Click();
        cut.Find("[data-testid=phone-nav-second]").Click();
        cut.Find("#tl-hnav-second").TriggerEvent("onfocusin", new Microsoft.AspNetCore.Components.Web.FocusEventArgs());
        var seasons = cut.FindAll("[role=listbox][aria-label=Season] .tl-hnav__option");

        cut.Find("#tl-hnav-second").KeyDown(Key.Down);
        Assert.Equal(seasons[0].Id, FocusedId());
        seasons[0].KeyDown(Key.Down);

        Assert.Equal(seasons[1].Id, FocusedId());
    }

    [Fact]
    public async Task OnAPhone_TheSheetReadsTheTitleInFullAtTheTop_WhateverTheLineCutsShort()
    {
        var (flow, fields) = await StartAsync();
        var cut = RenderHeader(flow, fields, phone: true);

        cut.Find("#tl-hnav-phone").Click();

        Assert.Equal("Echoes in the Dark", cut.Find("[data-testid=phone-nav-title]").TextContent.Trim());
    }

    [Fact]
    public async Task WhenTheWindowSaysPhoneByCascade_TheHeaderIsTheOneLineWithoutAPhoneParameter()
    {
        var (flow, fields) = await StartAsync();
        RenderTree.Add<Microsoft.AspNetCore.Components.CascadingValue<EditorWindowLayout>>(parameters => parameters.Add(host => host.Value, EditorWindowLayout.PhoneSheet));

        var cut = RenderHeader(flow, fields, phone: false);

        Assert.NotNull(cut.Find("[data-testid=phone-header]"));
        Assert.NotNull(cut.Find("#tl-hnav-phone"));
    }

    [Fact]
    public async Task OnAPhone_AUniverseGetsTheSameOneLineHeader_WithItsKindsInTheSheetInsteadOfASecondRow()
    {
        _withHeader = false;
        var (flow, fields) = await StartAsync();
        var cut = Render<EditorItemHeader>(parameters => parameters
            .Add(header => header.Flow, flow)
            .Add(header => header.Fields, fields)
            .Add(header => header.Family, new EditorFamilyState(Services.GetRequiredService<IEngineApiClient>(), NullLogger.Instance))
            .Add(header => header.Phone, true)
            .Add(header => header.HasFamily, true)
            .Add(header => header.Selects, "<nav class=\"kinds-under-test\">Characters</nav>"));

        Assert.NotNull(cut.Find("[data-testid=phone-header]"));
        Assert.Empty(cut.FindAll(".tl-editor-header__selects, .kinds-under-test"));
        Assert.Empty(cut.FindAll("[data-testid=phone-universe-sheet]"));

        cut.Find("#tl-universe-phone").Click();

        var sheet = cut.Find("[data-testid=phone-universe-sheet]");
        Assert.NotNull(sheet.QuerySelector(".kinds-under-test"));
        Assert.Equal("Echoes in the Dark", sheet.QuerySelector(".tl-hnav__sheettitle")!.TextContent.Trim());
    }

    // The id the arrow keys last asked the browser to focus.
    private string FocusedId() => (string)JSInterop.Invocations["tuvimaFocusById"].Last().Arguments[0]!;

    private bool _withHeader = true;

    private MediaEditorContextDto Context() => new()
    {
        LaunchEntityId = EpisodeId,
        MediaType = "TV",
        InitialScope = "episode",
        AvailableTabs = ["details", "artwork", "links", "history"],
        Header = _withHeader ? _header : null,
        Scopes =
        [
            new MediaEditorScopeDto
            {
                ScopeId = "episode",
                Label = "episode",
                FieldEntityId = EpisodeId,
                DisplayTitle = "Echoes in the Dark",
                CanEditFields = true,
                CanEditArtwork = true,
                ArtworkSlots = ["EpisodeStill"],
                FieldSnapshot = new MediaEditorScopeFieldSnapshotDto
                {
                    CanonicalFields = [new MediaEditorScopedFieldValueDto { Key = "title", Value = "Echoes in the Dark", ProviderName = "tvdb" }],
                    DisplayOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    AllowedOverrideKeys = ["title", "description"],
                },
            },
        ],
    };
}
