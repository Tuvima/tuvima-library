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
    public void PageHeader_WithoutATitle_ShowsOnlyTheButton()
    {
        var cut = Render<AppListPageHeader>(p => p.Add(c => c.Actions, b => b.AddMarkupContent(0, "<button id=\"main\">Upload plugin</button>")));

        Assert.Empty(cut.FindAll(".app-list-page-header__copy"));
        Assert.Empty(cut.FindAll(".app-list-page-header__title"));
        Assert.Contains("app-list-page-header--actions-only", cut.Find("header").ClassList);
        Assert.Equal("Upload plugin", cut.Find("#main").TextContent);
    }

    [Fact]
    public void ListCard_PutsStatusKindWarningAndBadgesInOneRowAndKeepsTheNameOnOneLine()
    {
        var cut = Render<AppListCard>(p => p
            .Add(c => c.Title, "Commercial Skip")
            .Add(c => c.Kind, "Media processing")
            .Add(c => c.Status, b => b.AddMarkupContent(0, "<span class=\"app-status-badge\" data-testid=\"status\">Working</span>"))
            .Add(c => c.Warning, b => b.AddMarkupContent(0, "<button class=\"app-warning-strip\">Requires Comskip</button>")));

        var row = cut.Find(".app-list-card__badges");
        Assert.Equal("Working", row.FirstElementChild!.TextContent);
        Assert.Contains("Media processing", row.TextContent);
        Assert.Empty(cut.FindAll(".app-list-card__aside"));
        Assert.NotNull(row.QuerySelector(".app-list-card__warning .app-warning-strip"));
        Assert.Equal("Commercial Skip", cut.Find(".app-list-card__title").GetAttribute("title"));
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
    public void SummaryStrip_CarriesTheColourOfEachCountsMeaning()
    {
        var cut = Render<AppSummaryStrip>(p => p.Add(c => c.Items, new[]
        {
            new AppSummaryItem("installed", "17", Tone: AppSummaryTone.Accent),
            new AppSummaryItem("switched on", "15", Tone: AppSummaryTone.Ok),
            new AppSummaryItem("need attention", "2", Tone: AppSummaryTone.Warn),
            new AppSummaryItem("broken", "0", Tone: AppSummaryTone.Mute),
        }));

        var items = cut.FindAll(".app-summary-strip__item");
        Assert.Contains("app-summary-strip__item--accent", items[0].ClassList);
        Assert.Contains("app-summary-strip__item--ok", items[1].ClassList);
        Assert.Contains("app-summary-strip__item--warn", items[2].ClassList);
        Assert.Contains("app-summary-strip__item--mute", items[3].ClassList);
    }

    [Theory]
    [InlineData("books", "Books")]
    [InlineData("tv", "TV")]
    [InlineData("audiobooks", "Audiobooks")]
    public void MediaPill_UsesTheStatusPillShapeInTheMediaColour(string media, string label)
    {
        var cut = Render<AppMediaPill>(p => p.Add(c => c.Media, media));

        var pill = cut.Find(".app-status-badge");
        Assert.Equal(label, pill.TextContent.Trim());
        Assert.Contains("app-media-pill", pill.ClassList);
        Assert.Contains($"app-media-pill--{media}", pill.ClassList);
    }

    [Fact]
    public void NoPageStylesItsOwnPill()
    {
        // One pill set (spec plugins-and-integrations-screens-v3 D23): cards use AppStatusBadge / AppMediaPill, not hand-styled spans or AppChip.
        var web = FindWebSource();
        var offenders = Directory.EnumerateFiles(web, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".razor", StringComparison.Ordinal) || path.EndsWith(".css", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(path => File.ReadAllText(path).Contains("app-list-card__kind", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(offenders);

        var card = File.ReadAllText(Path.Combine(web, "Components", "Shared", "AppListCard.razor"));
        Assert.DoesNotContain("<AppChip", card, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"app-list-card__kind\"", card, StringComparison.Ordinal);
    }

    private static string FindWebSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "MediaEngine.Web")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "MediaEngine.Web");
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
        Assert.Equal("4", buttons[1].QuerySelector(".tl-tab__count")!.TextContent);
        Assert.Equal("-1", buttons[0].GetAttribute("tabindex"));
    }

    [Fact]
    public void CountTabs_UseTheSameTabClassAsAppTabs()
    {
        var counts = Render<AppCountTabs>(p => p.Add(c => c.Tabs, new[] { new AppCountTab("all", "All", 3) }).Add(c => c.Selected, "all"));
        var tabs = Render<AppTabs>(p => p.AddChildContent<AppTabPanel>(panel => panel.Add(x => x.Text, "All")));

        Assert.NotNull(counts.Find("[role=tablist].tl-tabs-toolbar"));
        Assert.NotNull(tabs.Find("[role=tablist].tl-tabs-toolbar"));
        Assert.Contains("tl-tab-active", counts.Find("[role=tab]").ClassList);
        Assert.Contains("tl-tab", tabs.Find("[role=tab]").ClassList);
    }

    [Fact]
    public void Tabs_WithAnAddress_RenderAsLinks()
    {
        var cut = Render<AppTabs>(p => p
            .Add(c => c.ActivePanelIndex, 1)
            .AddChildContent<AppTabPanel>(panel => panel.Add(x => x.Text, "Plugins").Add(x => x.Href, "/settings/plugins"))
            .AddChildContent<AppTabPanel>(panel => panel.Add(x => x.Text, "Integrations").Add(x => x.Href, "/settings/integrations")));

        var links = cut.FindAll("a[role=tab]");
        Assert.Equal(2, links.Count);
        Assert.Equal("/settings/integrations", links[1].GetAttribute("href"));
        Assert.Equal("page", links[1].GetAttribute("aria-current"));
        Assert.Null(links[0].GetAttribute("aria-current"));
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
        Assert.Equal("Metadata", cut.Find(".app-list-card__badges .app-status-badge").TextContent.Trim());
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
    public void Pager_UnderOnePage_DrawsNothing()
    {
        var cut = Render<AppPager>(p => p.Add(c => c.Total, 24).Add(c => c.PageSize, 24).Add(c => c.Noun, "plugins"));

        Assert.Empty(cut.FindAll(".app-pager"));
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
