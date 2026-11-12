using Bunit;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Tests.Support;

namespace MediaEngine.Web.Tests;

/// <summary>Look v3.1 D1: the shared tab row draws an icon in every tab and marks the open tab (its 3 px underline is the open tab's class).</summary>
public sealed class TabRowRenderTests : AsyncBunitContext
{
    [Fact]
    public void AppTabs_DrawsAnIconInEveryTab_AndMarksTheOpenTabActive()
    {
        var cut = Render<AppTabs>(p => p
            .Add(c => c.ActivePanelIndex, 1)
            .AddChildContent<AppTabPanel>(panel => panel.Add(x => x.Text, "Overview").Add(x => x.Icon, AppMaterialIcons.Outlined.SpaceDashboard))
            .AddChildContent<AppTabPanel>(panel => panel.Add(x => x.Text, "Activity").Add(x => x.Icon, AppMaterialIcons.Outlined.History))
            .AddChildContent<AppTabPanel>(panel => panel.Add(x => x.Text, "Taste").Add(x => x.Icon, AppMaterialIcons.Outlined.Category)));

        var tabs = cut.FindAll("[role=tab]");
        Assert.Equal(3, tabs.Count);
        Assert.All(tabs, tab => Assert.NotNull(tab.QuerySelector("svg.tl-icon-root")));

        var active = Assert.Single(cut.FindAll(".tl-tab-active"));
        Assert.Equal("Activity", active.TextContent.Trim());
        Assert.Equal("true", active.GetAttribute("aria-selected"));
        Assert.All(tabs.Where(tab => tab != active), tab => Assert.DoesNotContain("tl-tab-active", tab.ClassList));
    }

    [Fact]
    public void AppCountTabs_DrawsAnIconInEveryTab_AndKeepsTheAdvancedTabAmberAndLast()
    {
        var tabs = new[]
        {
            new AppCountTab("all", "All", 3, AppMaterialIcons.Outlined.AllInclusive),
            new AppCountTab("setup", "Set up", null, AppMaterialIcons.Outlined.Tune),
            new AppCountTab("advanced", "Advanced", null, AppMaterialIcons.Outlined.Settings, Advanced: true),
        };

        var cut = Render<AppCountTabs>(p => p.Add(c => c.Tabs, tabs).Add(c => c.Selected, "setup"));

        var rendered = cut.FindAll("[role=tab]");
        Assert.Equal(3, rendered.Count);
        Assert.All(rendered, tab => Assert.NotNull(tab.QuerySelector("svg.tl-icon-root")));
        Assert.Equal("Set up", Assert.Single(cut.FindAll(".tl-tab-active")).TextContent.Trim());
        Assert.Contains("tl-tab--advanced", rendered[^1].ClassList);
    }
}
