using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

// The Folders pop-up (Setup and Libraries fixes D4, #857 #858): one line per folder with a status icon, the path, a handling icon and Copy.
public sealed class LibraryFoldersPopoverTests : AsyncBunitContext
{
    public LibraryFoldersPopoverTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(EngineApiClientStub.Create(_ => { }));
        Services.AddScoped<DeviceContextService>();
    }

    private static string Text(AngleSharp.Dom.IElement element) => Regex.Replace(element.TextContent, "\\s+", " ").Trim();

    private IRenderedComponent<LibraryFoldersPopover> OpenWith(params FolderListItem[] items)
    {
        var cut = Render<LibraryFoldersPopover>(parameters => parameters
            .Add(x => x.Items, items)
            .Add(x => x.LibraryName, "Movies"));
        cut.Find("[data-folder-count]").Click();
        return cut;
    }

    [Fact]
    public void AReachableFolderShowsTheGreenCheckWithItsSpokenName()
    {
        var cut = OpenWith(new FolderListItem("a", "E:\\Library\\Movies", Managed: true));

        var icon = cut.Find("li [data-folder-status]");
        Assert.Equal("reachable", icon.GetAttribute("data-folder-status"));
        Assert.Contains("lib-folders__icon--ok", icon.ClassName, StringComparison.Ordinal);
        Assert.Equal("img", icon.GetAttribute("role"));
        Assert.Equal("Library can reach this folder", icon.GetAttribute("aria-label"));
        Assert.Empty(cut.FindAll("[data-folder-reason]"));
        Assert.DoesNotContain("Online", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreachableFolderShowsTheAmberTriangleAndItsReasonAsText()
    {
        var cut = OpenWith(new FolderListItem("a", "Z:\\Archive\\Films", Issue: "Folder unavailable", Managed: false));

        var icon = cut.Find("li [data-folder-status]");
        Assert.Equal("unreachable", icon.GetAttribute("data-folder-status"));
        Assert.Contains("lib-folders__icon--warn", icon.ClassName, StringComparison.Ordinal);
        Assert.Equal("Library can't reach this folder", icon.GetAttribute("aria-label"));
        Assert.Equal("Folder not found. Is the drive connected?", Text(cut.Find("li [data-folder-reason]")));
        Assert.Equal("offline", cut.Find("li").GetAttribute("data-state"));
    }

    [Fact]
    public void AReadOnlyFolderShowsTheLockAndAManagedFolderShowsThePencilEachWithAName()
    {
        var cut = OpenWith(
            new FolderListItem("a", "E:\\Library\\Movies", Managed: true),
            new FolderListItem("b", "F:\\Movies", Managed: false));

        var items = cut.FindAll("li");
        var managed = items[0].QuerySelector("[data-handling]")!;
        var readOnly = items[1].QuerySelector("[data-handling]")!;
        Assert.Equal("managed", managed.GetAttribute("data-handling"));
        Assert.Contains("lib-folders__icon--managed", managed.ClassName, StringComparison.Ordinal);
        Assert.StartsWith("Managed:", managed.GetAttribute("aria-label"), StringComparison.Ordinal);
        Assert.Equal("read-only", readOnly.GetAttribute("data-handling"));
        Assert.StartsWith("Read-only:", readOnly.GetAttribute("aria-label"), StringComparison.Ordinal);
        // The words are no longer printed on the line; the icons carry them.
        Assert.DoesNotContain("lib-folders__tag", cut.Markup, StringComparison.Ordinal);
        Assert.Equal("img", managed.GetAttribute("role"));
        Assert.Equal("img", readOnly.GetAttribute("role"));
    }

    [Fact]
    public void EveryFolderLineHasACopyPathButtonWithAnAccessibleName()
    {
        var cut = OpenWith(new FolderListItem("a", "E:\\Library\\Movies", Managed: true));

        var copy = cut.Find("li button[aria-label^='Copy']");
        Assert.Equal("Copy E:\\Library\\Movies", copy.GetAttribute("aria-label"));
    }

    [Fact]
    public void EachFolderIsOneLineOfIconsPathAndCopyWithNoWrappingTagMarkup()
    {
        var cut = OpenWith(
            new FolderListItem("a", "E:\\Library\\Movies", Name: "4K shelf", Managed: true),
            new FolderListItem("b", "F:\\Movies", Name: "Old", Managed: false));

        var items = cut.FindAll("li");
        Assert.Equal(2, items.Count);
        foreach (var item in items)
        {
            Assert.Single(item.QuerySelectorAll("[data-folder-status]"));
            Assert.Single(item.QuerySelectorAll("[data-handling]"));
            Assert.Single(item.QuerySelectorAll(".lib-folders__path"));
            Assert.Empty(item.QuerySelectorAll(".lib-folders__tag, .lib-folders__state"));
        }
    }

    [Fact]
    public void ThePathCarriesItsFullTextAsTheStyledTooltipNotABrowserTitle()
    {
        const string path = "\\\\nas-01\\media\\video\\movies\\by-year\\2024";
        var cut = OpenWith(new FolderListItem("a", path, Managed: false));

        var owner = cut.Find("li .playback-tooltip-owner:has(.lib-folders__path)");
        Assert.Equal(path, owner.GetAttribute("data-playback-tooltip"));
        Assert.Empty(cut.FindAll("li [title]"));
    }

    [Fact]
    public void ALongPathKeepsItsLastSegmentsInASeparateUncutEnd()
    {
        const string path = "\\\\nas-01\\media\\video\\movies\\by-year\\2024";
        var cut = OpenWith(new FolderListItem("a", path, Managed: false));

        var start = Text(cut.Find(".lib-folders__path-start"));
        var end = Text(cut.Find(".lib-folders__path-end"));
        Assert.Equal(path, start + end);
        Assert.EndsWith("by-year\\2024", end, StringComparison.Ordinal);
    }

    [Fact]
    public void AShortPathIsNotSplit()
    {
        var cut = OpenWith(new FolderListItem("a", "F:\\Movies", Managed: false));

        Assert.Equal("F:\\Movies", Text(cut.Find(".lib-folders__path")));
        Assert.Empty(cut.FindAll(".lib-folders__path-end"));
    }
}
