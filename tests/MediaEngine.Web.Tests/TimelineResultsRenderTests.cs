using Bunit;
using MediaEngine.Contracts.Display;
using MediaEngine.Web.Components.Browse;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class TimelineResultsRenderTests : AsyncBunitContext
{
    public TimelineResultsRenderTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddNativeUiServices();
        JSInterop.SetupModule("./js/catalogue-timeline.js");
    }

    [Fact]
    public void Timeline_RendersVerticalYearRowsAndWorkingQuickNavigation()
    {
        var items = new List<MediaTileViewModel>
        {
            CreateItem("Starlight Dawn", 1977, MediaTileShape.Portrait),
            CreateItem("Void Creature", 1980, MediaTileShape.Square),
            CreateItem("Empire of Ash", 1980, MediaTileShape.Portrait),
            CreateItem("Unplaced Story", 0, MediaTileShape.Landscape),
        };

        var cut = Render<AppTimelineResults>(parameters => parameters
            .Add(component => component.Items, items)
            .Add(component => component.YearSemantic, "Original release year"));

        Assert.Equal(3, cut.FindAll(".app-timeline__year").Count);
        Assert.Single(cut.FindAll(".app-timeline-grouping"));
        Assert.Contains("Original release year", cut.Find(".app-timeline__header").TextContent, StringComparison.Ordinal);
        Assert.Equal("1980", cut.Find(".app-timeline__year.is-selected h3").TextContent.Trim());
        Assert.Equal(3, cut.FindAll(".view-timeline-scrubber__year").Count);
        Assert.Equal("2 items", cut.Find("[data-timeline-key='1980'] .app-timeline__year-heading span").TextContent.Trim());
        Assert.Equal(2, cut.FindAll("[data-timeline-key='1980'] .app-timeline__item").Count);
        Assert.Single(cut.FindAll(".app-timeline__artwork.is-square"));
        Assert.Single(cut.FindAll(".app-timeline__artwork.is-landscape"));
        Assert.Contains("app-timeline--art-92", cut.Find(".app-timeline").ClassList);

        cut.Find("button[aria-label='Jump to 1980, 2 items']").Click();

        Assert.Equal("1980", cut.Find(".app-timeline__year.is-selected h3").TextContent.Trim());
        Assert.Equal("date", cut.Find("button[aria-label='Jump to 1980, 2 items']").GetAttribute("aria-current"));
    }

    [Fact]
    public async Task Decades_GroupOnceExpandOnlyActiveYearsAndJumpBeyondLoadedPage()
    {
        int? jumped = null;
        var cut = Render<AppTimelineResults>(p => p
            .Add(c => c.Items, new[] { CreateItem("Recent", 2024, MediaTileShape.Portrait), CreateItem("Older", 2021, MediaTileShape.Portrait), CreateItem("Past", 1998, MediaTileShape.Portrait) })
            .Add(c => c.ShowGroupingControl, false)
            .Add(c => c.Grouping, TimelineGrouping.Decade)
            .Add(c => c.Index, new DisplayTimelinePeriodDto[] { new(2024, 1, 0), new(2021, 1, 1), new(1998, 1, 2), new(1985, 80, 3) })
            .Add(c => c.OnJumpYear, year => jumped = year));
        Assert.Equal(new[] { "2020s", "1990s" }, cut.FindAll(".app-timeline__decade-heading").Select(e => e.TextContent.Trim()));
        Assert.Equal("Years in 2020s", cut.Find(".view-timeline-scrubber__months").GetAttribute("aria-label"));
        Assert.Equal(2, cut.FindAll(".view-timeline-scrubber__months button").Count);
        cut.Find("button[aria-label='Jump to 1980s, 80 items']").Click();
        Assert.Equal(1985, jumped);
        await cut.InvokeAsync(() => cut.Instance.SetActivePeriod(1998));
        Assert.Equal("Years in 1990s", cut.Find(".view-timeline-scrubber__months").GetAttribute("aria-label"));
        Assert.Single(cut.FindAll(".view-timeline-scrubber__months button"));
        Assert.Empty(cut.FindAll(".app-timeline-grouping"));
    }

    [Fact]
    public void TimelineControlPlacementIsExplicitAtDesktopTabletAndMobileWidths()
    {
        var css = ReadSource("src/MediaEngine.Web/Components/Browse/MediaBrowseShell.razor.css");
        Assert.Contains(".browse-shell__timeline-grouping {\n    grid-column: 3;\n    grid-row: 1 / 3;", css.Replace("\r", ""));
        Assert.Contains(".browse-shell__timeline-grouping {\n        grid-column: 1;\n        grid-row: 5 / 7;", css.Replace("\r", ""));
        var source = ReadSource("src/MediaEngine.Web/Components/Browse/MediaBrowseShell.razor");
        Assert.Contains("SupportsLayoutToggle && !IsTimelineGrouping", source);
        var grouping = ReadSource("src/MediaEngine.Web/Components/Shared/AppTimelineGroupingControl.razor.css");
        Assert.Contains("grid-template-rows:auto 48px", grouping);
        var rail = ReadSource("src/MediaEngine.Web/Components/Shared/AppTimelineNavigator.razor.css");
        Assert.Contains(".view-timeline-scrubber__mobile-picker{display:block;flex:none}", rail);
        Assert.Contains("!element.querySelector('.app-timeline')", ReadSource("src/MediaEngine.Web/wwwroot/app.js"));
    }

    [Fact]
    public void Timeline_UsesVerticalLayoutWithoutTheRetiredHorizontalRail()
    {
        var source = ReadSource("src/MediaEngine.Web/Components/Browse/AppTimelineResults.razor");
        var styles = ReadSource("src/MediaEngine.Web/Components/Browse/AppTimelineResults.razor.css");

        Assert.Contains("<AppTimelineNavigator", source, StringComparison.Ordinal);
        Assert.Contains("OnJump=\"JumpPeriodAsync\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Group by", source, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: minmax(0, 1fr) 4.3rem", styles, StringComparison.Ordinal);
        Assert.Contains("padding-bottom: calc(6rem + env(safe-area-inset-bottom))", styles, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: minmax(132px, 170px) minmax(0, 1fr)", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("grid-auto-flow: column", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("scroll-snap-type: x", styles, StringComparison.Ordinal);
    }

    private static MediaTileViewModel CreateItem(string title, int year, MediaTileShape shape) => new()
    {
        Id = Guid.NewGuid(),
        WorkId = Guid.NewGuid(),
        Title = title,
        SortYear = year,
        Shape = shape,
        TileImageUrl = $"/artwork/{Uri.EscapeDataString(title)}.jpg",
        NavigationUrl = $"/details/{Uri.EscapeDataString(title)}",
    };

    private static string ReadSource(string relativePath) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
