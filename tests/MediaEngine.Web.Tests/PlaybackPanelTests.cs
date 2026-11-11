using Bunit;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Playback;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>Unified player v2 D11 and acceptance A5: one panel system, one panel open at a time, a fixed header and footer, and a phone sheet.</summary>
public sealed class PlaybackPanelTests : AsyncBunitContext
{
    private readonly PlaybackTransientToolCoordinator _tools = new();

    public PlaybackPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        Services.AddSingleton(_tools);
        Render<AppPopoverHost>();
    }

    private IRenderedComponent<PlaybackPanel> RenderPanel(string title, string? toolId = null, Action<ComponentParameterCollectionBuilder<PlaybackPanel>>? configure = null) =>
        Render<PlaybackPanel>(parameters =>
        {
            parameters.Add(p => p.Title, title).Add(p => p.TriggerContent, (RenderFragment)(builder => builder.AddContent(0, title[..1])))
                .AddChildContent($"{title} contents");
            if (toolId is not null)
            {
                parameters.Add(p => p.ToolId, toolId);
            }

            configure?.Invoke(parameters);
        });

    [Fact]
    public async Task OpeningASecondPanel_ClosesTheFirst_SoOnlyOneTopLevelPanelIsOpen()
    {
        var queue = RenderPanel("Queue", "queue");
        var lyrics = RenderPanel("Lyrics", "lyrics");

        await queue.InvokeAsync(() => queue.Instance.OpenAsync(true));
        Assert.Contains("Queue contents", queue.Markup);
        await lyrics.InvokeAsync(() => lyrics.Instance.OpenAsync(true));

        queue.WaitForAssertion(() => Assert.DoesNotContain("Queue contents", queue.Markup));
        Assert.Contains("Lyrics contents", lyrics.Markup);
        Assert.Equal("lyrics", _tools.OpenToolId);
        Assert.Equal("false", queue.Find("button.playback-popover-trigger").GetAttribute("aria-expanded"));
        Assert.Equal("true", lyrics.Find("button.playback-popover-trigger").GetAttribute("aria-expanded"));
    }

    [Fact]
    public async Task ThePanel_IsAnOwnedTopLayerPopoverWithAFixedHeaderBodyAndFooter()
    {
        var cut = RenderPanel("Playback settings", "settings", p => p.Add(x => x.Footer, (RenderFragment)(b => b.AddMarkupContent(0, "<span id='reset'>Reset playback preferences</span>"))));
        await cut.InvokeAsync(() => cut.Instance.OpenAsync(true));

        var panel = cut.WaitForElement("[data-playback-popover-panel]");
        Assert.Equal("manual", panel.GetAttribute("popover"));
        var frame = panel.QuerySelector("section[role='dialog']")!;
        var parts = frame.Children.Select(child => child.LocalName + "." + child.ClassName!.Split(' ')[0]).ToArray();
        Assert.Equal(["header.playback-panel__header", "div.playback-panel__body", "footer.playback-panel__footer"], parts);
        Assert.Equal("Playback settings", frame.QuerySelector("header h2")!.TextContent);
        Assert.NotNull(frame.QuerySelector("header button[aria-label='Close Playback settings']"));
        Assert.Contains("Playback settings contents", frame.QuerySelector(".playback-panel__body")!.TextContent);
        Assert.NotNull(frame.QuerySelector("footer #reset"));
        Assert.Null(frame.QuerySelector(".playback-panel__body #reset"));
    }

    [Theory]
    [InlineData(null, "ordinary", "playback-panel--panel")]
    [InlineData(null, "menu", "playback-panel--menu")]
    [InlineData("wide", "ordinary", "playback-panel--wide")]
    public async Task ThePanel_ChoosesMenuPanelOrWideSize(string? size, string kind, string expectedClass)
    {
        var cut = RenderPanel("Sizes", "sizes", p => p.Add(x => x.Size, size).Add(x => x.Kind, kind));
        await cut.InvokeAsync(() => cut.Instance.OpenAsync(true));

        Assert.Contains(expectedClass, cut.WaitForElement("[data-playback-popover-panel]").ClassList);
    }

    [Fact]
    public async Task AMenu_HasNoHeaderBarButOneOptionalHeaderLine()
    {
        var cut = RenderPanel("More song actions", "menu", p => p.Add(x => x.Kind, "menu").Add(x => x.HeaderLine, "Stay a Little Longer"));
        await cut.InvokeAsync(() => cut.Instance.OpenAsync(true));

        var panel = cut.WaitForElement("[data-playback-popover-panel]");
        Assert.Null(panel.QuerySelector("header"));
        Assert.Equal("Stay a Little Longer", panel.QuerySelector(".playback-panel__header-line")!.TextContent);
    }

    [Fact]
    public async Task TheCloseButton_ClosesThePanelAndAsksTheBrowserToReturnFocusToTheTrigger()
    {
        var module = JSInterop.SetupModule("./js/playback-popover.js");
        module.Mode = JSRuntimeMode.Loose;
        var cut = RenderPanel("Queue", "queue");
        await cut.InvokeAsync(() => cut.Instance.OpenAsync(true));
        cut.WaitForElement("[data-playback-popover-panel]");
        var release = module.SetupVoid("update", call => call.Arguments.Count == 4 && Equals(call.Arguments[2], false));

        var closing = cut.Find("button[aria-label='Close Queue']").ClickAsync();
        cut.WaitForAssertion(() => Assert.Single(release.Invocations));
        Assert.Equal(true, release.Invocations.Single().Arguments[3]);
        release.SetVoidResult();
        await closing;

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-playback-popover-panel]")));
        Assert.Null(_tools.OpenToolId);
    }

    [Theory]
    [InlineData(false, "an outside click leaves focus on the thing that was clicked")]
    [InlineData(true, "Escape returns focus to the trigger")]
    public async Task ABrowserClose_CarriesWhetherFocusReturnsToTheTrigger(bool restoreFocus, string because)
    {
        var module = JSInterop.SetupModule("./js/playback-popover.js");
        module.Mode = JSRuntimeMode.Loose;
        var cut = RenderPanel("Queue", "queue");
        await cut.InvokeAsync(() => cut.Instance.OpenAsync(true));
        cut.WaitForElement("[data-playback-popover-panel]");
        var release = module.SetupVoid("update", call => call.Arguments.Count == 4 && Equals(call.Arguments[2], false));

        var closing = cut.InvokeAsync(() => cut.Instance.CloseFromBrowserAsync(restoreFocus));
        cut.WaitForAssertion(() => Assert.Single(release.Invocations));
        Assert.True(restoreFocus == (bool)release.Invocations.Single().Arguments[3]!, because);
        release.SetVoidResult();
        await closing;
        Assert.Null(_tools.OpenToolId);
    }

    [Fact]
    public async Task OpeningAPanel_NeverStartsOrStopsPlayback()
    {
        var playback = new PlaybackSessionController(null!, null!);
        using var tools = new PlaybackTransientToolCoordinator(playback);
        var before = (playback.IsPlaying, playback.CurrentTimeSeconds, playback.PlaybackRate);
        var cut = Render<PlaybackPanel>(p => p.Add(x => x.Title, "Speed").Add(x => x.ToolId, "speed-playback").AddChildContent("Speed contents"));

        await cut.InvokeAsync(() => cut.Instance.OpenAsync(true));
        await cut.InvokeAsync(() => cut.Instance.CloseAsync());

        Assert.Equal(before, (playback.IsPlaying, playback.CurrentTimeSeconds, playback.PlaybackRate));
    }

    [Fact]
    public async Task ANestedView_ShowsABackArrowAndTheBackArrowReturnsToTheParentView()
    {
        PlaybackPanelNavigator? navigator = null;
        var cut = Render<PlaybackPanel>(p => p.Add(x => x.Title, "Audio & subtitles").Add(x => x.ToolId, "audio-subtitles")
            .AddChildContent<NavigatorProbe>(probe => probe.Add(x => x.Capture, n => navigator = n)));
        await cut.InvokeAsync(() => cut.Instance.OpenAsync(true));
        cut.WaitForElement("[data-playback-popover-panel]");
        Assert.Null(cut.FindAll("button[aria-label='Back']").FirstOrDefault());

        await cut.InvokeAsync(() => navigator!.Push("Subtitle appearance", b => b.AddContent(0, "Style choices")));

        Assert.Equal("Subtitle appearance", cut.Find("header h2").TextContent);
        Assert.Contains("Style choices", cut.Find(".playback-panel__body").TextContent);
        Assert.DoesNotContain("Parent view", cut.Markup);
        await cut.Find("button[aria-label='Back']").ClickAsync();
        Assert.Equal("Audio & subtitles", cut.Find("header h2").TextContent);
        Assert.Contains("Parent view", cut.Find(".playback-panel__body").TextContent);
        Assert.Empty(cut.FindAll("button[aria-label='Back']"));
    }

    [Fact]
    public async Task ClosingAPanel_ForgetsItsNestedViewsSoItReopensOnTheMainView()
    {
        PlaybackPanelNavigator? navigator = null;
        var cut = Render<PlaybackPanel>(p => p.Add(x => x.Title, "Settings").Add(x => x.ToolId, "settings-nested")
            .AddChildContent<NavigatorProbe>(probe => probe.Add(x => x.Capture, n => navigator = n)));
        await cut.InvokeAsync(() => cut.Instance.OpenAsync(true));
        cut.WaitForElement("[data-playback-popover-panel]");
        await cut.InvokeAsync(() => navigator!.Push("Playback information", b => b.AddContent(0, "Details")));

        await cut.InvokeAsync(() => cut.Instance.CloseAsync());
        cut.WaitForAssertion(() => Assert.Equal(0, cut.Instance.Navigator.Depth));
        await cut.InvokeAsync(() => cut.Instance.OpenAsync(true));

        Assert.Equal("Settings", cut.WaitForElement("header h2").TextContent);
    }

    [Fact]
    public async Task OnAPhone_EveryPanelIsABottomSheetWithTheSameHeaderBodyAndFooter()
    {
        var cut = RenderPanel("Chapters", "chapters", p => p.Add(x => x.Footer, (RenderFragment)(b => b.AddMarkupContent(0, "<span id='foot'>Footer action</span>"))));
        await cut.InvokeAsync(() => cut.Instance.SetViewport(true));
        await cut.InvokeAsync(() => cut.Instance.OpenAsync(true));

        var panel = cut.WaitForElement("[data-playback-popover-panel]");
        Assert.Contains("playback-panel--sheet", panel.ClassList);
        Assert.NotNull(panel.QuerySelector("[data-playback-popover-backdrop]"));
        var sheet = panel.QuerySelector("section[role='dialog'][aria-modal='true']")!;
        Assert.Contains("playback-tool-sheet--framed", sheet.ClassList);
        Assert.NotNull(sheet.QuerySelector(".playback-tool-sheet__handle"));
        Assert.NotNull(sheet.QuerySelector("button[aria-label='Close Chapters']"));
        Assert.Contains("Chapters contents", sheet.QuerySelector(".playback-tool-sheet__content")!.TextContent);
        Assert.NotNull(sheet.QuerySelector(".playback-tool-sheet__footer #foot"));
    }

    [Fact]
    public async Task OnAPhone_AMenuIsAlsoABottomSheet()
    {
        var cut = RenderPanel("More song actions", "phone-menu", p => p.Add(x => x.Kind, "menu"));
        await cut.InvokeAsync(() => cut.Instance.SetViewport(true));
        await cut.InvokeAsync(() => cut.Instance.OpenAsync(true));

        Assert.Contains("playback-panel--sheet", cut.WaitForElement("[data-playback-popover-panel]").ClassList);
    }

    [Fact]
    public async Task APhoneSheet_ShowsABackArrowWhileANestedViewIsOpen()
    {
        var cut = RenderPanel("Audio & subtitles", "phone-nested");
        await cut.InvokeAsync(() => cut.Instance.SetViewport(true));
        await cut.InvokeAsync(() => cut.Instance.OpenAsync(true));
        cut.WaitForElement("[data-playback-popover-panel]");
        await cut.InvokeAsync(() => cut.Instance.Navigator.Push("Subtitle appearance", b => b.AddContent(0, "Style choices")));

        await cut.Find("button[aria-label='Back']").ClickAsync();

        Assert.Contains("Audio &amp; subtitles contents", cut.Find(".playback-tool-sheet__content").InnerHtml);
    }

    [Fact]
    public async Task TheCoordinator_ReportsWhetherAnyPanelIsOpen_ForTheVideoChromeToReadAndHoldItsControls()
    {
        var cut = RenderPanel("Queue", "queue-chrome");
        Assert.False(_tools.IsPanelOpen);

        await cut.InvokeAsync(() => cut.Instance.OpenAsync(true));
        Assert.True(_tools.IsPanelOpen);

        await cut.InvokeAsync(() => cut.Instance.CloseAsync());
        Assert.False(_tools.IsPanelOpen);
    }

    [Fact]
    public void ThePanelSystem_HasNoHoverOpenNoArrowAndNoSpeechBubbleStyling()
    {
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");
        var sources = new[]
        {
            File.ReadAllText(Path.Combine(web, "Components", "Shared", "PlaybackPanel.razor")),
            File.ReadAllText(Path.Combine(web, "wwwroot", "js", "playback-popover.js")),
        };
        foreach (var source in sources)
        {
            Assert.DoesNotContain("HoverPreview", source, StringComparison.Ordinal);
            Assert.DoesNotContain("pointerenter", source, StringComparison.Ordinal);
            Assert.DoesNotContain("pointerleave", source, StringComparison.Ordinal);
            Assert.DoesNotContain("hoverTimer", source, StringComparison.Ordinal);
        }

        var styles = Directory.EnumerateFiles(web, "*.css", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText);
        foreach (var css in styles)
        {
            Assert.DoesNotContain("playback-popover-arrow", css, StringComparison.Ordinal);
            Assert.DoesNotContain("playback-caret-left", css, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheOldPopoverPopoutShellAndDesktopToolSheetUsesAreGone()
    {
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web", "Components");
        Assert.False(File.Exists(Path.Combine(web, "Shared", "PlaybackPopover.razor")));
        Assert.False(File.Exists(Path.Combine(web, "Shared", "PlaybackPopoutShell.razor")));
        var panel = File.ReadAllText(Path.Combine(web, "Shared", "PlaybackPanel.razor"));
        Assert.Contains("PlaybackToolSheet", panel, StringComparison.Ordinal); // The phone sheet reuses it, inside the `_mobile` branch only.
        var desktop = panel[panel.IndexOf("else", panel.IndexOf("@if (_mobile)", StringComparison.Ordinal), StringComparison.Ordinal)..];
        Assert.DoesNotContain("<PlaybackToolSheet", desktop, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "MediaEngine.slnx")))
        {
            root = Path.GetDirectoryName(root);
        }

        return root ?? throw new InvalidOperationException("Repository root not found.");
    }

    /// <summary>Stands in for a panel's content: shows a parent view and hands the panel's navigator out so a test can push a nested view.</summary>
    private sealed class NavigatorProbe : ComponentBase
    {
        [CascadingParameter] public PlaybackPanelNavigator? Navigator { get; set; }

        [Parameter] public Action<PlaybackPanelNavigator> Capture { get; set; } = _ => { };

        protected override void OnParametersSet()
        {
            if (Navigator is not null)
            {
                Capture(Navigator);
            }
        }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder) => builder.AddContent(0, "Parent view");
    }
}
