using Bunit;
using MediaEngine.Web.Components.Editor;
using MediaEngine.Web.Services.Editing;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The file's path at the foot of the Quick Facts panel (Editor v6 D14): out of the facts list, on one line, shortened in the middle by two
/// parts (a start that gives way and an end that never does), the whole path in a tooltip a focus or a press can reach, and a copy button.
/// </summary>
public sealed class EditorQuickFactsPathTests : AsyncBunitContext
{
    private const string Path = "/media/Movies/Dune (2021)/Dune.Part.One.2021.2160p.UHD.BluRay.REMUX.HDR.mkv";

    public EditorQuickFactsPathTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
    }

    private static QuickFactsModel Model(string? path) => new(
        QuickFactsKind.Film,
        [new QuickFact("size", "Storage", "58.2 GB", "2 files"), new QuickFact("quality", "HighQuality", "4K HDR", "Best quality you own")],
        [],
        FilePath: path);

    private IRenderedComponent<EditorQuickFacts> RenderFacts(QuickFactsModel model) =>
        Render<EditorQuickFacts>(parameters => parameters.Add(component => component.Model, model));

    [Fact]
    public void ThePath_IsNotInTheFactsList_ItSitsOnceAtTheFootOfThePanel()
    {
        var cut = RenderFacts(Model(Path));

        Assert.DoesNotContain(cut.FindAll("[data-fact]"), fact => fact.TextContent.Contains("/media/", StringComparison.Ordinal));
        var panel = cut.Find("aside.tl-quick-facts");
        var foot = Assert.Single(cut.FindAll("[data-quick-facts-path]"));
        Assert.Same(foot, panel.LastElementChild);
    }

    [Fact]
    public void TheFootLine_HoldsAStartPartAndAnEndPart_ThatJoinBackToTheWholePath()
    {
        var cut = RenderFacts(Model(Path));

        var start = cut.Find(".tl-quick-facts__path-start").TextContent;
        var end = cut.Find(".tl-quick-facts__path-end").TextContent;

        Assert.Equal(Path, start + end);
        Assert.Equal("/media/Movies/Dune (2021)/", start);
        Assert.Equal("Dune.Part.One.2021.2160p.UHD.BluRay.REMUX.HDR.mkv", end);
    }

    [Fact]
    public void AWindowsPath_IsSplitAtItsLastBackslash()
    {
        var cut = RenderFacts(Model(@"Z:\Books\Rick Riordan\Book 2.epub"));

        Assert.Equal(@"Z:\Books\Rick Riordan\", cut.Find(".tl-quick-facts__path-start").TextContent);
        Assert.Equal("Book 2.epub", cut.Find(".tl-quick-facts__path-end").TextContent);
    }

    [Fact]
    public void TheEndPart_ReadsRightToLeftWithItsNameIsolated_SoAnOverlongNameLosesItsFrontAndKeepsItsExtension()
    {
        var cut = RenderFacts(Model(Path));

        var end = cut.Find(".tl-quick-facts__path-end");

        Assert.Equal("rtl", end.GetAttribute("dir"));
        Assert.Equal("ltr", end.QuerySelector("bdi")!.GetAttribute("dir"));
    }

    [Fact]
    public void TheWholePath_IsInTheStyledTooltip_ReachableByFocus_AndHeardByAScreenReader()
    {
        var cut = RenderFacts(Model(Path));

        var text = cut.Find(".tl-quick-facts__path-text");

        Assert.Equal(Path, text.Closest("[data-playback-tooltip]")!.GetAttribute("data-playback-tooltip"));
        Assert.Equal("0", text.GetAttribute("tabindex"));
        Assert.Equal(Path, text.QuerySelector(".sr-only")!.TextContent);
        Assert.Equal("true", text.QuerySelector(".tl-quick-facts__path-start")!.GetAttribute("aria-hidden"));
        Assert.Null(cut.Find("[data-quick-facts-path]").QuerySelector("[title]"));
    }

    [Fact]
    public void TheCopyButton_HasAnAccessibleName_AndPutsTheWholePathOnTheClipboard()
    {
        var cut = RenderFacts(Model(Path));

        var copy = cut.Find("button[data-quick-facts-copy]");
        Assert.Equal("Copy file path", copy.GetAttribute("aria-label"));
        copy.Click();

        var call = Assert.Single(JSInterop.Invocations, invocation => invocation.Identifier == "navigator.clipboard.writeText");
        Assert.Equal(Path, call.Arguments[0]);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".tl-quick-facts__path-error")));
    }

    [Fact]
    public void AnItemWithoutAFilePath_HasNoFootLine()
    {
        var cut = RenderFacts(Model(null));

        Assert.Empty(cut.FindAll("[data-quick-facts-path]"));
        Assert.NotEmpty(cut.FindAll("[data-fact]"));
    }

    [Fact]
    public void QuickFacts_NoLongerDrawAReadOnlyOrWriteBackPill()
    {
        var cut = RenderFacts(Model(Path));

        Assert.Empty(cut.FindAll("[data-note]"));
        Assert.DoesNotContain("Read-only", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("write-back", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }
}
