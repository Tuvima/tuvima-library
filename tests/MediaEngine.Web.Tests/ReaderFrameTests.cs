using AngleSharp.Dom;
using Bunit;
using MediaEngine.Web.Components.Reader;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The shared reader frame (spec music-table-and-reader-frame WP7, mockups B1-B3): the one tool order, the Close at the top right,
/// the phone "..." sheet, the panel, the hidden-chrome state, accessible names, focus order and 44 px targets.
/// </summary>
public sealed class ReaderFrameTests : AsyncBunitContext
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private readonly DeviceContextService _device;

    public ReaderFrameTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        var api = EngineApiClientStub.Create(_ => { });
        Services.AddSingleton(api);
        _device = new DeviceContextService(api);
        Services.AddSingleton(_device);
    }

    private Task UsePhoneAsync() => _device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true));

    private IRenderedComponent<ReaderFrame> Frame(
        Action<ComponentParameterCollectionBuilder<ReaderFrame>>? configure = null,
        ReaderFrameState state = ReaderFrameState.Ready,
        string? title = "Annual Report 2026") =>
        Render<ReaderFrame>(parameters =>
        {
            parameters
                .Add(frame => frame.State, state)
                .Add(frame => frame.Title, title)
                .Add(frame => frame.Subtitle, "PDF · 48 pages");
            configure?.Invoke(parameters);
        });

    private static string[] ToolLabels(IRenderedComponent<ReaderFrame> cut) =>
        cut.FindAll(".rf-tools button").Select(button => button.GetAttribute("aria-label") ?? string.Empty).ToArray();

    // ---- Tool order and combinations ----------------------------------------------------------------------------------------

    [Fact]
    public void APdfTopLine_OffersSearchContentsPagesBookmarkThisPageBookmarksAndReadingSettingsThenClose()
    {
        var cut = Frame();

        Assert.Equal(["Search", "Contents", "Pages", "Bookmark this page", "Bookmarks", "Reading settings", "Close"], ToolLabels(cut));
    }

    [Fact]
    public void ABookTopLine_HasNoPagesGridAndKeepsTheSameOrderForTheRest()
    {
        var cut = Frame(parameters => parameters.Add(frame => frame.Tools, ReaderTools.Book));

        Assert.Equal(["Search", "Contents", "Bookmark this page", "Bookmarks", "Reading settings", "Close"], ToolLabels(cut));
    }

    [Fact]
    public void AComicTopLine_HasNoSearchOrContents()
    {
        var cut = Frame(parameters => parameters.Add(frame => frame.Tools, ReaderTools.Comic));

        Assert.Equal(["Pages", "Bookmark this page", "Bookmarks", "Reading settings", "Close"], ToolLabels(cut));
    }

    [Fact]
    public void AReaderWithNoToolsAtAll_StillHasClose()
    {
        var cut = Frame(parameters => parameters.Add(frame => frame.Tools, Array.Empty<ReaderTool>()));

        Assert.Equal(["Close"], ToolLabels(cut));
    }

    [Fact]
    public void ToolsListedOutOfOrder_AreStillDrawnInTheFixedOrder()
    {
        var cut = Frame(parameters => parameters.Add(frame => frame.Tools,
            new[] { ReaderTool.ReadingSettings, ReaderTool.Search, ReaderTool.Bookmarks, ReaderTool.Pages }));

        Assert.Equal(["Search", "Pages", "Bookmarks", "Reading settings", "Close"], ToolLabels(cut));
    }

    [Fact]
    public void TheSearchToolTakesTheNameTheReaderGivesIt()
    {
        var cut = Frame(parameters => parameters.Add(frame => frame.SearchLabel, "Search in this book"));

        Assert.Equal("Search in this book", ToolLabels(cut)[0]);
    }

    [Fact]
    public void ThereIsNoBackArrowAtTheTopLeft()
    {
        var cut = Frame();

        Assert.DoesNotContain(cut.FindAll(".rf-bar--top button"), button => (button.GetAttribute("aria-label") ?? string.Empty).Contains("Back", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(cut.FindAll(".app-viewer__close"));
        Assert.Equal("Annual Report 2026", cut.Find(".rf-title-text").TextContent.Trim());
        Assert.Equal("PDF · 48 pages", cut.Find(".rf-title-sub").TextContent.Trim());
    }

    [Fact]
    public void Close_IsTheLastControlOnTheLineAndRaisesOnClose()
    {
        var closed = 0;
        var cut = Frame(parameters => parameters.Add(frame => frame.OnClose, EventCallback.Factory.Create(this, () => closed++)));

        var last = cut.FindAll(".rf-tools button").Last();
        Assert.Equal("Close", last.GetAttribute("aria-label"));
        Assert.Contains("rf-close", last.ClassName, StringComparison.Ordinal);

        last.Click();

        Assert.Equal(1, closed);
    }

    [Fact]
    public void PressingATool_RaisesOnToolClickWithThatTool()
    {
        var pressed = new List<ReaderTool>();
        var cut = Frame(parameters => parameters.Add(frame => frame.OnToolClick, EventCallback.Factory.Create<ReaderTool>(this, pressed.Add)));

        cut.Find(".rf-tools button[aria-label='Pages']").Click();
        cut.Find(".rf-tools button[aria-label='Bookmarks']").Click();

        Assert.Equal([ReaderTool.Pages, ReaderTool.Bookmarks], pressed);
    }

    [Fact]
    public void TheToolWhosePanelIsOpen_IsMarkedOnAndExpanded()
    {
        var cut = Frame(parameters => parameters.Add(frame => frame.OpenPanel, ReaderTool.Pages));

        var pages = cut.Find(".rf-tools button[aria-label='Pages']");
        Assert.Equal("true", pages.GetAttribute("aria-expanded"));
        Assert.Contains("rf-ib--on", pages.ClassName, StringComparison.Ordinal);
        var search = cut.Find(".rf-tools button[aria-label='Search']");
        Assert.Equal("false", search.GetAttribute("aria-expanded"));
        Assert.DoesNotContain("rf-ib--on", search.ClassName, StringComparison.Ordinal);
    }

    // ---- Bookmark this page ----------------------------------------------------------------------------------------------

    [Fact]
    public void TheBookmarkButton_IsOutlineUntilThePageIsBookmarkedAndThenFilledAndRemovesOnPress()
    {
        var toggled = 0;
        var cut = Frame(parameters => parameters.Add(frame => frame.OnBookmarkToggle, EventCallback.Factory.Create(this, () => toggled++)));

        var outline = cut.Find(".rf-tools button[aria-label='Bookmark this page']");
        Assert.Equal("false", outline.GetAttribute("aria-pressed"));
        var outlineIcon = outline.InnerHtml;

        cut.Render(parameters => parameters.Add(frame => frame.IsPageBookmarked, true));

        var filled = cut.Find(".rf-tools button[aria-label='Remove bookmark']");
        Assert.Equal("true", filled.GetAttribute("aria-pressed"));
        Assert.Contains("rf-ib--on", filled.ClassName, StringComparison.Ordinal);
        Assert.NotEqual(outlineIcon, filled.InnerHtml);

        filled.Click();

        Assert.Equal(1, toggled);
    }

    [Fact]
    public void PressingBookmarkThisPage_DoesNotOpenAPanel()
    {
        var cut = Frame(parameters => parameters.Add(frame => frame.OpenPanel, ReaderTool.BookmarkThisPage));

        Assert.Empty(cut.FindAll(".rf-panel"));
    }

    // ---- Phone ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OnAPhone_OnlyBookmarkThisPageTheMoreButtonAndCloseStayOnTheLine()
    {
        await UsePhoneAsync();

        var cut = Frame();

        Assert.Equal(["Bookmark this page", "More actions", "Close"], ToolLabels(cut));
        Assert.Empty(cut.FindAll("dialog"));
    }

    [Fact]
    public async Task OnAPhone_MoreOpensASheetWithTheOtherToolsInOrderAndChoosingOneRaisesOnToolClick()
    {
        await UsePhoneAsync();
        var pressed = new List<ReaderTool>();
        var cut = Frame(parameters => parameters.Add(frame => frame.OnToolClick, EventCallback.Factory.Create<ReaderTool>(this, pressed.Add)));

        cut.Find(".rf-tools button[aria-label='More actions']").Click();

        var sheet = cut.Find("dialog");
        Assert.Contains("app-native-dialog--sheet", sheet.ClassName, StringComparison.Ordinal);
        Assert.Equal(
            ["Search", "Contents", "Pages", "Bookmarks", "Reading settings"],
            sheet.QuerySelectorAll(".app-menu-item").Select(item => item.TextContent.Trim()).ToArray());

        sheet.QuerySelectorAll(".app-menu-item").Single(item => item.TextContent.Trim() == "Pages").Click();

        cut.WaitForAssertion(() => Assert.Equal([ReaderTool.Pages], pressed));
        Assert.Empty(cut.FindAll("dialog"));
    }

    [Fact]
    public async Task OnAPhone_TheSheetOfAComicLeavesOutSearchAndContents()
    {
        await UsePhoneAsync();
        var cut = Frame(parameters => parameters.Add(frame => frame.Tools, ReaderTools.Comic));

        cut.Find(".rf-tools button[aria-label='More actions']").Click();

        Assert.Equal(
            ["Pages", "Bookmarks", "Reading settings"],
            cut.Find("dialog").QuerySelectorAll(".app-menu-item").Select(item => item.TextContent.Trim()).ToArray());
    }

    [Fact]
    public async Task OnAPhone_AReaderWithNothingForTheSheetHasNoMoreButton()
    {
        await UsePhoneAsync();

        var cut = Frame(parameters => parameters.Add(frame => frame.Tools, new[] { ReaderTool.BookmarkThisPage }));

        Assert.Equal(["Bookmark this page", "Close"], ToolLabels(cut));
    }

    [Fact]
    public async Task OnAPhone_ThePanelIsModalLikeASheet()
    {
        await UsePhoneAsync();

        var cut = Frame(parameters => parameters.Add(frame => frame.OpenPanel, ReaderTool.Bookmarks));

        Assert.Equal("true", cut.Find(".rf-panel").GetAttribute("aria-modal"));
    }

    [Fact]
    public void OnAComputer_ThereIsNoMoreButtonAndNoSheet()
    {
        var cut = Frame();

        Assert.Empty(cut.FindAll(".rf-more"));
        Assert.Empty(cut.FindAll("dialog"));
    }

    [Fact]
    public void OnAComputer_ThePanelIsNotDrawnAsAModalSheet()
    {
        var cut = Frame(parameters => parameters.Add(frame => frame.OpenPanel, ReaderTool.Bookmarks));

        Assert.Null(cut.Find(".rf-panel").GetAttribute("aria-modal"));
    }

    // ---- Panel ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void WithNoPanelOpen_ThereIsNoPanelAndNoDim()
    {
        var cut = Frame();

        Assert.Empty(cut.FindAll(".rf-panel"));
        Assert.Empty(cut.FindAll(".rf-scrim"));
    }

    [Fact]
    public void AnOpenPanel_HasTheToolsNameAsItsTitleACloseWithAnAccessibleNameAndTheReadersContent()
    {
        var cut = Frame(parameters => parameters
            .Add(frame => frame.OpenPanel, ReaderTool.Bookmarks)
            .Add(frame => frame.PanelContent, tool => builder => builder.AddContent(0, $"body for {tool}")));

        var panel = cut.Find(".rf-panel");
        Assert.Equal("dialog", panel.GetAttribute("role"));
        Assert.Equal("Bookmarks", panel.GetAttribute("aria-label"));
        Assert.Equal("Bookmarks", cut.Find(".rf-panel-title").TextContent.Trim());
        Assert.Equal("H2", cut.Find(".rf-panel-title").TagName);
        Assert.Equal("Close Bookmarks", cut.Find(".rf-panel-head button").GetAttribute("aria-label"));
        Assert.Equal("body for Bookmarks", cut.Find(".rf-panel-body").TextContent.Trim());
        Assert.NotEmpty(cut.FindAll(".rf-scrim"));
    }

    [Fact]
    public void ThePanelTitleCanBeOverriddenButTheCloseStaysInTheHeader()
    {
        var cut = Frame(parameters => parameters
            .Add(frame => frame.OpenPanel, ReaderTool.Contents)
            .Add(frame => frame.PanelTitle, "Table of contents"));

        Assert.Equal("Table of contents", cut.Find(".rf-panel-title").TextContent.Trim());
        Assert.Equal("Close Table of contents", cut.Find(".rf-panel-head button").GetAttribute("aria-label"));
    }

    [Fact]
    public void ThePanelClosesFromItsCloseAndFromTheDim()
    {
        var closed = 0;
        var cut = Frame(parameters => parameters
            .Add(frame => frame.OpenPanel, ReaderTool.ReadingSettings)
            .Add(frame => frame.OnPanelClose, EventCallback.Factory.Create(this, () => closed++)));

        cut.Find(".rf-panel-head button").Click();
        cut.Find(".rf-scrim").Click();

        Assert.Equal(2, closed);
    }

    [Fact]
    public void ThePanelIsDrawnAtTheSharedWidthNotAWidthOfItsOwn()
    {
        var css = File.ReadAllText(Path.Combine(Root, "src/MediaEngine.Web/wwwroot/css/reader-frame.css"));

        Assert.Contains("inline-size: min(var(--tl-popout-panel), 100%)", css, StringComparison.Ordinal);
    }

    // ---- Hidden chrome ------------------------------------------------------------------------------------------------------

    [Fact]
    public void WhileTheBarsAreShown_TheShellSaysShownAndTheBarsAreNotHiddenFromReaders()
    {
        var cut = Frame();

        Assert.Equal("shown", cut.Find(".rf-shell").GetAttribute("data-chrome"));
        Assert.All(cut.FindAll(".rf-bar"), bar => Assert.Null(bar.GetAttribute("aria-hidden")));
    }

    [Fact]
    public void WhenTheBarsAutoHide_TheShellSaysHiddenAndBothBarsAreHiddenFromReaders()
    {
        var cut = Frame(parameters => parameters.Add(frame => frame.ChromeHidden, true));

        Assert.Equal("hidden", cut.Find(".rf-shell").GetAttribute("data-chrome"));
        Assert.Equal(2, cut.FindAll(".rf-bar").Count);
        Assert.All(cut.FindAll(".rf-bar"), bar => Assert.Equal("true", bar.GetAttribute("aria-hidden")));
    }

    [Fact]
    public void TheStyleSheetHidesTheBarsFromTheShellsChromeState()
    {
        var css = File.ReadAllText(Path.Combine(Root, "src/MediaEngine.Web/wwwroot/css/reader-frame.css"));

        Assert.Contains(".rf-shell[data-chrome=\"hidden\"] .rf-bar", css, StringComparison.Ordinal);
    }

    // ---- Slots, states and the live region --------------------------------------------------------------------------------

    [Fact]
    public void TheReadersStageBottomLineAndOverlaysFillTheirSlots()
    {
        var cut = Frame(parameters => parameters
            .Add(frame => frame.Class, "br-theme-sepia")
            .Add(frame => frame.Stage, builder => builder.AddMarkupContent(0, "<div id=\"page\">the page</div>"))
            .Add(frame => frame.Bottom, builder => builder.AddMarkupContent(0, "<span id=\"progress\">42%</span>"))
            .Add(frame => frame.Overlays, builder => builder.AddMarkupContent(0, "<div id=\"extra\">card</div>"))
            .AddUnmatched("data-reader", "pdf"));

        Assert.Equal("the page", cut.Find(".rf-stage #page").TextContent);
        Assert.Equal("42%", cut.Find(".rf-bar--bottom #progress").TextContent);
        Assert.NotNull(cut.Find("#extra"));
        var shell = cut.Find(".rf-shell");
        Assert.Contains("br-theme-sepia", shell.ClassName, StringComparison.Ordinal);
        Assert.Equal("pdf", shell.GetAttribute("data-reader"));
        Assert.Equal("ready", shell.GetAttribute("data-state"));
    }

    [Fact]
    public void WhileOpening_TheFrameShowsASpinnerAndOnlyClose()
    {
        var cut = Frame(
            parameters => parameters.Add(frame => frame.Bottom, builder => builder.AddMarkupContent(0, "<span id=\"progress\">42%</span>")),
            ReaderFrameState.Loading);

        Assert.Equal("loading", cut.Find(".rf-shell").GetAttribute("data-state"));
        Assert.Equal("Opening", cut.Find(".rf-spinner").GetAttribute("aria-label"));
        Assert.Equal(["Close"], ToolLabels(cut));
        Assert.Empty(cut.FindAll("#progress"));
    }

    [Fact]
    public void WhenTheFileCannotBeOpened_TheFrameSaysSoWithTryAgainAndClose()
    {
        var retried = 0;
        var closed = 0;
        var cut = Frame(
            parameters => parameters
                .Add(frame => frame.ErrorMessage, "This file could not be opened.")
                .Add(frame => frame.CanRetry, true)
                .Add(frame => frame.OnRetry, EventCallback.Factory.Create(this, () => retried++))
                .Add(frame => frame.OnClose, EventCallback.Factory.Create(this, () => closed++)),
            ReaderFrameState.Error);

        var state = cut.Find(".rf-state");
        Assert.Equal("alert", state.GetAttribute("role"));
        Assert.Contains("This file could not be opened.", state.TextContent, StringComparison.Ordinal);
        var buttons = state.QuerySelectorAll("button").ToArray();
        Assert.Equal(["Try again", "Close"], buttons.Select(button => button.TextContent.Trim()).ToArray());

        buttons[0].Click();
        buttons[1].Click();

        Assert.Equal(1, retried);
        Assert.Equal(1, closed);
    }

    [Fact]
    public void WithoutRetry_TheErrorOffersOnlyClose()
    {
        var cut = Frame(state: ReaderFrameState.Error);

        Assert.Equal(["Close"], cut.Find(".rf-state").QuerySelectorAll("button").Select(button => button.TextContent.Trim()).ToArray());
    }

    [Fact]
    public void TheLiveRegionIsPoliteAndCarriesThePositionAndTheLastAnnouncement()
    {
        var cut = Frame(parameters => parameters
            .Add(frame => frame.LiveText, "Page 12 of 28")
            .Add(frame => frame.Announcement, "Bookmark added on page 12"));

        var live = cut.Find(".rf-live");
        Assert.Equal("status", live.GetAttribute("role"));
        Assert.Equal("polite", live.GetAttribute("aria-live"));
        Assert.Contains("Page 12 of 28", live.TextContent, StringComparison.Ordinal);
        Assert.Contains("Bookmark added on page 12", live.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTopLineIsLabelledByTheTitleOrReaderWhenThereIsNone()
    {
        Assert.Equal("Annual Report 2026", Frame().Find(".rf-bar--top").GetAttribute("aria-label"));
        Assert.Equal("Reader", Frame(title: null).Find(".rf-bar--top").GetAttribute("aria-label"));
        Assert.Equal("Reader tools", Frame().Find(".rf-tools").GetAttribute("aria-label"));
        Assert.Equal("toolbar", Frame().Find(".rf-tools").GetAttribute("role"));
    }

    // ---- Accessibility: names, focus order, 44 px targets ----------------------------------------------------------------

    [Fact]
    public void EveryIconOnlyControlHasAnAccessibleName()
    {
        var cut = Frame(parameters => parameters
            .Add(frame => frame.OpenPanel, ReaderTool.Pages)
            .Add(frame => frame.Bottom, builder => builder.AddMarkupContent(0, "<span>42%</span>")));

        foreach (var button in cut.FindAll(".rf-bar--top button, .rf-panel-head button"))
        {
            Assert.False(string.IsNullOrWhiteSpace(button.GetAttribute("aria-label")), $"A reader frame button has no accessible name: {button.OuterHtml}");
        }
    }

    [Fact]
    public void FocusMovesThroughTheTopLineInToolOrderThenTheBottomLineThenThePanel()
    {
        var cut = Frame(parameters => parameters
            .Add(frame => frame.OpenPanel, ReaderTool.Bookmarks)
            .Add(frame => frame.Bottom, builder => builder.AddMarkupContent(0, "<button aria-label=\"Slider\">s</button>"))
            .Add(frame => frame.PanelContent, _ => builder => builder.AddMarkupContent(0, "<button aria-label=\"Row\">r</button>")));

        var order = cut.FindAll(".rf-shell button")
            .Select(button => button.GetAttribute("aria-label") ?? button.TextContent.Trim())
            .ToArray();

        Assert.Equal(
            ["Search", "Contents", "Pages", "Bookmark this page", "Bookmarks", "Reading settings", "Close", "Slider", "Close Bookmarks", "Row"],
            order);
        Assert.All(cut.FindAll(".rf-shell [tabindex]"), element => Assert.True(int.Parse(element.GetAttribute("tabindex")!) <= 0, "A positive tabindex would reorder focus."));
    }

    [Fact]
    public void EveryControlOnTheTopLineIsA44PixelTarget()
    {
        var cut = Frame(parameters => parameters.Add(frame => frame.OpenPanel, ReaderTool.Search));

        foreach (var button in cut.FindAll(".rf-tools button, .rf-panel-head button"))
        {
            Assert.Contains("app-control--normal", button.ClassName, StringComparison.Ordinal);
            Assert.Contains("rf-ib", button.ClassName, StringComparison.Ordinal);
        }

        var css = File.ReadAllText(Path.Combine(Root, "src/MediaEngine.Web/wwwroot/css/reader-frame.css"));
        Assert.Matches(@"\.rf-ib \{[^}]*width: var\(--tl-tap-target-min\);[^}]*height: var\(--tl-tap-target-min\);", css);
    }

    [Fact]
    public async Task OnAPhone_TheMoreTriggerIsAlsoA44PixelTarget()
    {
        await UsePhoneAsync();

        var cut = Frame();

        Assert.Contains("rf-ib", cut.Find(".rf-tools button[aria-label='More actions']").ClassName, StringComparison.Ordinal);
    }

    // ---- The style sheet --------------------------------------------------------------------------------------------------

    [Fact]
    public void TheStyleSheetReadsExactlyThreeColoursFromVariablesTheReaderSets()
    {
        var css = File.ReadAllText(Path.Combine(Root, "src/MediaEngine.Web/wwwroot/css/reader-frame.css"));

        foreach (var variable in new[] { "--rf-page:", "--rf-text:", "--rf-accent:" })
        {
            Assert.Contains(variable, css, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("!important", css, StringComparison.Ordinal);
        Assert.DoesNotContain("::deep", css, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFrameLinksItsOwnStyleSheetTheWayTheReadersLinkTheirs()
    {
        var frame = File.ReadAllText(Path.Combine(Root, "src/MediaEngine.Web/Components/Reader/ReaderFrame.razor"));

        Assert.Contains("<link href=\"css/reader-frame.css\" rel=\"stylesheet\" />", frame, StringComparison.Ordinal);
    }
}
