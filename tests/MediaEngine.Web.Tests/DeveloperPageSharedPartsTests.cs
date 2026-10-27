using Bunit;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The shared parts the Plugins and Integrations pages are built from (Plugins and Integrations screens spec WP1):
/// header, summary strip, search and filter row, tabs with counts, card, warning strip and pager.
/// </summary>
public sealed class DeveloperPageSharedPartsTests : AsyncBunitContext
{
    public DeveloperPageSharedPartsTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
    }

    [Fact]
    public void PageHeader_ShowsTitleSubtitleAndTheMainButton()
    {
        var cut = Render<AppListPageHeader>(p => p
            .Add(c => c.Title, "Plugins")
            .Add(c => c.Subtitle, "Manage extensions and integrations")
            .Add(c => c.Actions, b => b.AddMarkupContent(0, "<button id=\"main\">Upload plugin</button>")));

        Assert.Equal("Plugins", cut.Find(".app-list-page-header__title").TextContent);
        Assert.Contains("Manage extensions", cut.Find(".app-list-page-header__subtitle").TextContent);
        Assert.Equal("Upload plugin", cut.Find("#main").TextContent);
    }

    [Fact]
    public void PageHeader_WithoutSubtitleOrActions_LeavesThemOut()
    {
        var cut = Render<AppListPageHeader>(p => p.Add(c => c.Title, "Integrations"));

        Assert.Empty(cut.FindAll(".app-list-page-header__subtitle"));
        Assert.Empty(cut.FindAll(".app-list-page-header__actions"));
    }

    [Fact]
    public void SummaryStrip_ListsEveryCountWithItsWords()
    {
        var cut = Render<AppSummaryStrip>(p => p.Add(c => c.Items, new[]
        {
            new AppSummaryItem("installed", "11"),
            new AppSummaryItem("switched on", "8"),
            new AppSummaryItem("need attention", "1"),
        }));

        var items = cut.FindAll(".app-summary-strip__item");
        Assert.Equal(3, items.Count);
        Assert.Contains("need attention", items[2].TextContent);
        Assert.Equal("1", items[2].QuerySelector(".app-summary-strip__value")!.TextContent);
    }

    [Fact]
    public void SummaryStrip_WhileLoading_ShowsPlaceholdersNotNumbers()
    {
        var cut = Render<AppSummaryStrip>(p => p
            .Add(c => c.Loading, true)
            .Add(c => c.Items, new[] { new AppSummaryItem("installed", "0") }));

        Assert.Empty(cut.FindAll(".app-summary-strip__value"));
    }

    [Fact]
    public void SearchFilterRow_ShowsClearOnlyWhileNarrowed()
    {
        var cleared = 0;
        var cut = Render<AppSearchFilterRow>(p => p
            .Add(c => c.Placeholder, "Search plugins by name or capability")
            .Add(c => c.OnClear, () => cleared++));
        Assert.DoesNotContain("Clear filters", cut.Markup);

        cut.Render(p => p
            .Add(c => c.Placeholder, "Search plugins by name or capability")
            .Add(c => c.Narrowed, true)
            .Add(c => c.OnClear, () => cleared++));
        cut.Find("button").Click();

        Assert.Equal(1, cleared);
    }

    [Fact]
    public void CountTabs_ShowCountsAndMarkTheSelectedTab()
    {
        var tabs = new[] { new AppCountTab("all", "All", 11), new AppCountTab("metadata", "Metadata", 4) };
        var cut = Render<AppCountTabs>(p => p.Add(c => c.Tabs, tabs).Add(c => c.Selected, "metadata"));

        var buttons = cut.FindAll("[role=tab]");
        Assert.Equal("false", buttons[0].GetAttribute("aria-selected"));
        Assert.Equal("true", buttons[1].GetAttribute("aria-selected"));
        Assert.Equal("4", buttons[1].QuerySelector(".app-count-tabs__count")!.TextContent);
        Assert.Equal("-1", buttons[0].GetAttribute("tabindex"));
    }

    [Fact]
    public void CountTabs_ClickAndArrowKeysChangeTheSelection()
    {
        string? chosen = null;
        var tabs = new[] { new AppCountTab("all", "All", 3), new AppCountTab("a", "A", 1), new AppCountTab("b", "B", 2) };
        var cut = Render<AppCountTabs>(p => p
            .Add(c => c.Tabs, tabs)
            .Add(c => c.Selected, "all")
            .Add(c => c.SelectedChanged, key => chosen = key));

        cut.FindAll("[role=tab]")[2].Click();
        Assert.Equal("b", chosen);

        cut.FindAll("[role=tab]")[0].KeyDown(new KeyboardEventArgs { Key = "ArrowLeft" });
        Assert.Equal("b", chosen);

        cut.FindAll("[role=tab]")[0].KeyDown(new KeyboardEventArgs { Key = "ArrowRight" });
        Assert.Equal("a", chosen);
    }

    [Fact]
    public void ListCard_ShowsNameKindDescriptionAndFooter()
    {
        var cut = Render<AppListCard>(p => p
            .Add(c => c.Title, "MusicBrainz")
            .Add(c => c.Kind, "Metadata")
            .Add(c => c.Description, "Looks up albums")
            .Add(c => c.Footer, b => b.AddMarkupContent(0, "<span id=\"foot\">Configure</span>")));

        Assert.Equal("MusicBrainz", cut.Find(".app-list-card__title").TextContent);
        Assert.Equal("Metadata", cut.Find(".app-list-card__kind").TextContent);
        Assert.Equal("Configure", cut.Find("#foot").TextContent);
        Assert.Empty(cut.FindAll(".app-list-card__warning"));
    }

    [Fact]
    public void ListCard_WithAVeryLongName_StillRendersTheWholeName()
    {
        var name = new string('x', 200);
        var cut = Render<AppListCard>(p => p.Add(c => c.Title, name));

        Assert.Equal(name, cut.Find(".app-list-card__title").TextContent);
    }

    [Fact]
    public void WarningStrip_WithAHandler_IsAButtonThatOpensTheFix()
    {
        var opened = false;
        var cut = Render<AppWarningStrip>(p => p
            .Add(c => c.Text, "Requires FFmpeg.")
            .Add(c => c.OnClick, () => opened = true));

        cut.Find("button").Click();

        Assert.True(opened);
        Assert.Contains("Requires FFmpeg.", cut.Markup);
    }

    [Fact]
    public void Pager_UnderOnePage_ShowsTheCountAndNoPageButtons()
    {
        var cut = Render<AppPager>(p => p.Add(c => c.Total, 11).Add(c => c.PageSize, 12).Add(c => c.Noun, "plugins"));

        Assert.Contains("Showing 11 of 11 plugins", cut.Markup);
        Assert.Empty(cut.FindAll(".app-pager__pages"));
    }

    [Fact]
    public void Pager_BeyondOnePage_PagesThroughTheList()
    {
        var page = 1;
        var cut = Render<AppPager>(p => p
            .Add(c => c.Total, 25)
            .Add(c => c.PageSize, 12)
            .Add(c => c.Page, 1)
            .Add(c => c.PageChanged, value => page = value));

        Assert.Contains("Showing 12 of 25", cut.Markup);
        Assert.Contains("Page 1 of 3", cut.Markup);
        cut.FindAll(".app-pager__pages button")[1].Click();

        Assert.Equal(2, page);
    }

    [Fact]
    public void Pager_LastPage_ShowsOnlyTheRemainder()
    {
        var cut = Render<AppPager>(p => p.Add(c => c.Total, 25).Add(c => c.PageSize, 12).Add(c => c.Page, 3));

        Assert.Contains("Showing 1 of 25", cut.Markup);
    }
}
