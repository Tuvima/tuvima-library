using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Four window kinds, one ladder (Look pass v3 D14, WP11): Window S 480 / M 640 / L 900 with 12 px corners, Sheet under 600 px
/// with no opt-out, Pop-out Menu 272 / Panel 400, and the full-screen Viewer with its own 44 px close X. Hand-built
/// <c>role="dialog"</c> and raw <c>&lt;dialog&gt;</c> markup is allowed only in the shared parts' files, listed here with the reason.
/// </summary>
public sealed class FloatingSurfaceKindsTests : AsyncBunitContext
{
    // The written allowlist: the only Razor files that may carry role="dialog" or a raw <dialog> element.
    private static readonly IReadOnlyDictionary<string, string> Allowlist = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Components/Shared/AppDialogFrame.razor"] = "Window and Sheet: the one native dialog element every Window sits in.",
        ["Components/Shared/AppViewer.razor"] = "Viewer: the full-screen surface; draws the native dialog for the details overlay.",
        ["Components/Shared/AppDrawer.razor"] = "Side drawer implementation (Chronicle and Users panels): a native dialog driven by native-structure.js; it is not one of the four kinds and goes when those panels become Windows.",
        ["Components/Layout/ReconnectModal.razor"] = "The Blazor reconnect dialog: the framework toggles this exact element (components-reconnect-modal) from static HTML while no circuit exists, so it cannot be an interactive component.",
    };

    private static readonly Regex HandBuilt = new(@"role=""dialog""|<dialog\b", RegexOptions.Compiled);

    public FloatingSurfaceKindsTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddNativeUiServices();
    }

    [Fact]
    public void RoleDialogAndRawDialogMarkupAppearOnlyInTheSharedPartsFiles()
    {
        var offenders = RazorFiles()
            .Where(file => HandBuilt.IsMatch(file.Source) && !Allowlist.ContainsKey(file.Relative))
            .Select(file => file.Relative)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Build this with Window (AppDialogFrame), Sheet, Pop-out (AppPopover or AppPopout) or Viewer (AppViewer), or list it with a reason: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheAllowlistHasNoStaleEntries()
    {
        var drawing = RazorFiles().Where(file => HandBuilt.IsMatch(file.Source)).Select(file => file.Relative).ToHashSet(StringComparer.Ordinal);
        var stale = Allowlist.Keys.Where(relative => !drawing.Contains(relative)).ToList();

        Assert.True(stale.Count == 0, "No longer hand-builds a dialog, remove from the allowlist: " + string.Join(", ", stale));
    }

    [Fact]
    public void TheWindowLadderIs480640And900PixelsWithTwelvePixelCorners()
    {
        var tokens = ReadWeb("wwwroot/tuvima.tokens.css");
        var frame = ReadWeb("Components/Shared/AppDialogFrame.razor.css");

        Assert.Contains("--tl-window-s: 480px;", tokens);
        Assert.Contains("--tl-window-m: 640px;", tokens);
        Assert.Contains("--tl-window-l: 900px;", tokens);
        Assert.Contains("--tl-window-xl: 1200px;", tokens);
        Assert.Contains("--tl-window-radius: 12px;", tokens);
        Assert.Contains(".app-native-dialog--win-s{max-width:var(--tl-window-s,480px)}", frame);
        Assert.Contains(".app-native-dialog--win-m{max-width:var(--tl-window-m,640px)}", frame);
        Assert.Contains(".app-native-dialog--win-l{max-width:var(--tl-window-l,900px)}", frame);
        Assert.Contains(".app-native-dialog--win-xl{max-width:var(--tl-window-xl,1200px)}", frame);
        Assert.Contains("border-radius:var(--tl-window-radius,12px)", frame);
    }

    [Theory]
    [InlineData(AppWindowSize.Small, "app-native-dialog--win-s")]
    [InlineData(AppWindowSize.Medium, "app-native-dialog--win-m")]
    [InlineData(AppWindowSize.Large, "app-native-dialog--win-l")]
    public void AWindowTakesTheClassOfItsSize(AppWindowSize size, string expected)
    {
        var frame = Render<AppDialogFrame>(parameters => parameters
            .Add(p => p.Title, "Pick one")
            .Add(p => p.DialogOptions, new AppDialogOptions { Size = size })
            .AddChildContent("<p>body</p>"));

        Assert.Contains(expected, frame.Find("dialog").ClassName);
    }

    [Theory]
    [InlineData(AppMaxWidth.ExtraSmall, null, AppWindowSize.Small)]
    [InlineData(AppMaxWidth.Small, "Small", AppWindowSize.Small)]
    [InlineData(AppMaxWidth.Small, "Medium", AppWindowSize.Medium)]
    [InlineData(AppMaxWidth.Small, "Large", AppWindowSize.Medium)]
    [InlineData(AppMaxWidth.Medium, "Large", AppWindowSize.Large)]
    [InlineData(AppMaxWidth.Large, null, AppWindowSize.Large)]
    [InlineData(AppMaxWidth.ExtraExtraLarge, null, AppWindowSize.Large)]
    public void OlderWidthHintsSnapOntoTheLadder(AppMaxWidth width, string? shellSize, AppWindowSize expected)
    {
        Assert.Equal(expected, AppWindowSizing.Resolve(new AppDialogOptions { MaxWidth = width }, shellSize));
    }

    [Fact]
    public void AnExplicitSizeBeatsTheOlderHints()
    {
        Assert.Equal(AppWindowSize.Large, AppWindowSizing.Resolve(new AppDialogOptions { MaxWidth = AppMaxWidth.Small, Size = AppWindowSize.Large }, "Small"));
    }

    [Fact]
    public void EveryWindowBecomesASheetUnder600PixelsWithNoOptOut()
    {
        var frame = ReadWeb("Components/Shared/AppDialogFrame.razor.css");
        var markup = ReadWeb("Components/Shared/AppDialogFrame.razor");

        Assert.Contains("@media(max-width:600px)", frame);
        Assert.Contains(".app-native-dialog:not(.app-native-dialog--fullscreen){margin:auto 0 0;width:100%;max-width:none", frame);
        Assert.Contains("border-radius:var(--tl-sheet-radius,16px) var(--tl-sheet-radius,16px) 0 0", frame);
        // The only thing a window can ask for is the full-screen phone menu; no option turns the Sheet off.
        Assert.Contains("Options.FullScreen != true && Options.Sheet switch", markup);
        Assert.Contains("_ => _device?.IsPhone == true", markup);
    }

    [Theory]
    [InlineData(AppDialogSheetMode.Never)]
    [InlineData(AppDialogSheetMode.Phone)]
    [InlineData(AppDialogSheetMode.PhoneAndTablet)]
    public async Task OnAPhoneEveryWindowIsASheetWhateverItsOptions(AppDialogSheetMode mode)
    {
        var device = new DeviceContextService(EngineApiClientStub.CreateDefault());
        await device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true));
        Services.AddSingleton(device);

        var frame = Render<AppDialogFrame>(parameters => parameters
            .Add(p => p.DialogOptions, new AppDialogOptions { Sheet = mode, Size = AppWindowSize.Large }));

        Assert.Contains("app-native-dialog--sheet", frame.Find("dialog").ClassName);
        Assert.Single(frame.FindAll("[data-app-dialog-grip]"));
    }

    [Fact]
    public void TheWindowAnatomyIsATwentyPixelTitleAndA44PixelCloseButtonTopRight()
    {
        var shell = ReadWeb("Components/Shared/AppDialogShell.razor.css");
        var markup = ReadWeb("Components/Shared/AppDialogShell.razor");

        Assert.Contains("font-size: var(--tl-dialog-title-size);", shell);
        Assert.Contains("font-weight: 600;", shell);
        Assert.Contains("Size=\"AppControlSize.Normal\"", markup);
        Assert.Contains("justify-content: flex-end;", shell);
        Assert.Contains("padding: 16px 24px;", shell);
    }

    [Fact]
    public void PopOutsAreMenu240To360AndPanel400WithEightPixelCornersOnTheRaisedSurface()
    {
        var tokens = ReadWeb("wwwroot/tuvima.tokens.css");
        var css = ReadWeb("Components/Shared/AppPopover.razor.css");

        Assert.Contains("--tl-popout-menu: 272px;", tokens);
        Assert.Contains("--tl-popout-panel: 400px;", tokens);
        Assert.Contains("--tl-popout-row: 44px;", tokens);
        Assert.Contains("--tl-popout-radius: 8px;", tokens);
        // A menu grows with its longest label between 240 and 360 px (Look v3.1 D2).
        Assert.Contains(".app-popover.app-popout--menu{width:max-content;min-width:var(--tl-menu-min-width,240px);max-width:min(var(--tl-menu-max-width,360px),calc(100vw - 16px))}", css);
        Assert.Contains(".app-popover.app-popout--panel{width:var(--tl-popout-panel,400px)}", css);
        Assert.Contains("background:var(--tl-bg-surface-elevated)", css);
        Assert.Contains("border-radius:var(--tl-popout-radius,8px)", css);
        // Menu rows are 48 px (Look v3.1 D2); a dropdown's option rows stay at the 44 px pop-out row.
        Assert.Contains("--tl-menu-min-width: 240px;", tokens);
        Assert.Contains("--tl-menu-max-width: 360px;", tokens);
        Assert.Contains("--tl-menu-row-height: 48px;", tokens);
    }

    [Fact]
    public void APopOutBecomesASheetOnPhonesThroughTheStylesheetAndTheAnchoringScript()
    {
        var css = ReadWeb("Components/Shared/AppPopover.razor.css");
        var script = ReadWeb("wwwroot/js/app-popover.js");

        Assert.Contains(".app-popover.app-popout--sheet{", css);
        Assert.Contains("border-radius:var(--tl-sheet-radius,16px) var(--tl-sheet-radius,16px) 0 0", css);
        Assert.Contains("matchMedia('(max-width: 600px)')", script);
    }

    [Theory]
    [InlineData(AppPopoutSize.Menu, "app-popout--menu")]
    [InlineData(AppPopoutSize.Panel, "app-popout--panel")]
    public void ASizedPopoverCarriesItsPopOutSize(AppPopoutSize size, string expected)
    {
        Render<AppPopoverHost>();
        var popover = Render<AppPopover>(parameters => parameters
            .Add(p => p.Open, true)
            .Add(p => p.Size, size)
            .Add(p => p.Role, "dialog")
            .Add(p => p.AriaLabel, "Tools")
            .AddChildContent("<p>tool</p>"));

        var surface = popover.Find(".app-popover");
        Assert.Contains(expected, surface.ClassName);
        Assert.Equal(size.ToString().ToLowerInvariant(), surface.GetAttribute("data-popout-size"));
        Assert.Equal("dialog", surface.GetAttribute("role"));
        Assert.Equal("Tools", surface.GetAttribute("aria-label"));
    }

    [Fact]
    public void AnUnsizedPopoverKeepsItsCallersWidth()
    {
        Render<AppPopoverHost>();
        var popover = Render<AppPopover>(parameters => parameters.Add(p => p.Open, true).AddChildContent("<p>list</p>"));

        Assert.DoesNotContain("app-popout", popover.Find(".app-popover").ClassName);
        Assert.Null(popover.Find(".app-popover").GetAttribute("data-popout-size"));
    }

    [Fact]
    public void TheViewerIsFullScreenWithItsOwn44PixelCloseButton()
    {
        var tokens = ReadWeb("wwwroot/tuvima.tokens.css");
        var css = ReadWeb("wwwroot/app.css");

        Assert.Contains("--tl-viewer-close: 44px;", tokens);
        Assert.Matches(@":where\(\.app-viewer\) \{\s*position: fixed;\s*inset: 0;", css);
        Assert.Matches(@":where\(\.app-viewer__close\) \{[^}]*width: var\(--tl-viewer-close\);[^}]*height: var\(--tl-viewer-close\);", css);
    }

    [Fact]
    public void AViewerOverAPageIsAModalDialogWithACloseButtonThatCloses()
    {
        var closed = 0;
        var viewer = Render<AppViewer>(parameters => parameters
            .Add(p => p.Label, "Photo")
            .Add(p => p.OnClose, Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, () => closed++))
            .AddChildContent("<p>picture</p>"));

        var root = viewer.Find(".app-viewer");
        Assert.Equal("dialog", root.GetAttribute("role"));
        Assert.Equal("true", root.GetAttribute("aria-modal"));
        Assert.Equal("Photo", root.GetAttribute("aria-label"));
        Assert.Contains("app-viewer--black", root.ClassName);

        viewer.Find("button.app-viewer__close").Click();
        Assert.Equal(1, closed);
    }

    [Fact]
    public void AViewerWithoutACloseHandlerDrawsNoCloseButtonAndThePageSurfaceIsAnOption()
    {
        var viewer = Render<AppViewer>(parameters => parameters
            .Add(p => p.Surface, AppViewerSurface.Page)
            .Add(p => p.Role, string.Empty)
            .AddChildContent("<p>the reader</p>"));

        Assert.Empty(viewer.FindAll(".app-viewer__close"));
        var root = viewer.Find(".app-viewer");
        Assert.Contains("app-viewer--page", root.ClassName);
        Assert.Null(root.GetAttribute("role"));
        Assert.Null(root.GetAttribute("aria-modal"));
    }

    [Fact]
    public void ANativeViewerDrawsTheModalDialogElementForTheDetailsOverlay()
    {
        var viewer = Render<AppViewer>(parameters => parameters
            .Add(p => p.Native, true)
            .Add(p => p.LabelledBy, "title")
            .AddChildContent("<h2 id=\"title\">Details</h2>"));

        var dialog = viewer.Find("dialog.app-viewer");
        Assert.Equal("title", dialog.GetAttribute("aria-labelledby"));
        Assert.Equal("true", dialog.GetAttribute("aria-modal"));
    }

    [Theory]
    [InlineData("preview-window-s", "app-native-dialog--win-s")]
    [InlineData("preview-window-m", "app-native-dialog--win-m")]
    [InlineData("preview-window-l", "app-native-dialog--win-l")]
    public void ThePreviewPageOpensAWindowAtEachSize(string button, string expected)
    {
        Render<AppPopoverHost>();
        var page = Render<MediaEngine.Web.Components.Pages.DesignSystemPreview>();

        page.Find($"[data-testid='{button}']").Click();

        Assert.Contains(page.FindAll("dialog"), dialog => dialog.ClassName!.Contains(expected, StringComparison.Ordinal) && dialog.GetAttribute("data-app-dialog") is not null);
    }

    [Fact]
    public void ThePreviewPageShowsBothPopOutSizesAndTheViewer()
    {
        Render<AppPopoverHost>();
        var page = Render<MediaEngine.Web.Components.Pages.DesignSystemPreview>();

        page.Find("[data-testid='preview-popout-menu']").Click();
        Assert.NotEmpty(page.FindAll(".app-popover.app-popout--menu"));
        page.Find("[data-testid='preview-popout-panel']").Click();
        Assert.NotEmpty(page.FindAll(".app-popover.app-popout--panel"));
        page.Find("[data-testid='preview-viewer']").Click();
        Assert.NotEmpty(page.FindAll("[data-testid='preview-viewer-surface'] .app-viewer__close, .app-viewer .app-viewer__close"));
    }

    private static IEnumerable<(string Relative, string Source)> RazorFiles()
    {
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");
        return Directory.EnumerateFiles(Path.Combine(web, "Components"), "*.razor", SearchOption.AllDirectories)
            .Select(path => (Relative: Path.GetRelativePath(web, path).Replace('\\', '/'), Source: File.ReadAllText(path)));
    }

    private static string ReadWeb(string relative) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web", relative)).Replace("\r\n", "\n");

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
