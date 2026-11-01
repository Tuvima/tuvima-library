using Bunit;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The one Advanced tab convention (settings-and-setup-simplified D1): last in the row, a small gear, amber at rest and open, no
/// pill, and the shared warning line first in the page when it is open.
/// </summary>
public sealed class SettingsAdvancedTabTests : AsyncBunitContext
{
    public SettingsAdvancedTabTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.CreateDefault());
    }

    [Fact]
    public void AdvancedTab_IsDrawnLast_WithTheGearAndTheAmberClass_EvenWhenTheListNamesItEarlier()
    {
        var playback = SettingsNav.MenuGroups.SelectMany(group => group.Nodes).Single(node => node.Label == "Playback");
        var advanced = playback.Tabs.Single(tab => tab.IsAdvanced);
        var quality = playback.Tabs.Single(tab => !tab.IsAdvanced);

        var cut = Render<SettingsPageTabs>(parameters => parameters
            .Add(component => component.Tabs, new[] { advanced, quality })
            .Add(component => component.Active, advanced)
            .Add(component => component.PageLabel, "Playback"));

        var tabs = cut.FindAll("a.tl-tab");
        Assert.Equal(["Quality", "Advanced"], tabs.Select(tab => tab.TextContent.Trim()));

        var last = tabs[^1];
        Assert.Contains("tl-tab--advanced", last.ClassList);
        Assert.Contains("tl-tab-active", last.ClassList);
        Assert.NotNull(last.QuerySelector("svg"));
        Assert.Empty(last.QuerySelectorAll(".tl-tab__count"));
        Assert.DoesNotContain("tl-tab--advanced", tabs[0].ClassList);
    }

    [Fact]
    public void AdvancedTab_AtRest_IsAmberToo_AndOnlyAdvancedTabsAre()
    {
        var access = SettingsNav.MenuGroups.SelectMany(group => group.Nodes).Single(node => node.Label == "Users & Households");

        var cut = Render<SettingsPageTabs>(parameters => parameters
            .Add(component => component.Tabs, access.Tabs)
            .Add(component => component.Active, access.Tabs[0])
            .Add(component => component.PageLabel, access.Label));

        var amber = cut.FindAll("a.tl-tab--advanced");
        Assert.Equal("Advanced", Assert.Single(amber).TextContent.Trim());
        Assert.DoesNotContain("tl-tab-active", amber[0].ClassList);
        Assert.Equal(2, cut.FindAll("a.tl-tab:not(.tl-tab--advanced)").Count);
    }

    [Fact]
    public void NeedsReviewTab_ShowsHowManyItemsWait()
    {
        var libraries = SettingsNav.MenuGroups.SelectMany(group => group.Nodes).Single(node => node.Label == "Libraries");

        var cut = Render<SettingsPageTabs>(parameters => parameters
            .Add(component => component.Tabs, libraries.Tabs)
            .Add(component => component.Active, libraries.Tabs[0])
            .Add(component => component.PageLabel, libraries.Label)
            .Add(component => component.ReviewCount, 4));

        var counts = cut.FindAll(".tl-tab__count");
        Assert.Equal("4", Assert.Single(counts).TextContent.Trim());
        Assert.Contains("Needs Review", counts[0].ParentElement!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Frame_DrawsTheWarningLineFirst_WhenAnAdvancedTabIsOpen_AndNothingOtherwise()
    {
        var open = Render<SettingsPageFrame>(parameters => parameters
            .Add(component => component.Title, "Playback")
            .Add(component => component.AdvancedWarning, true)
            .AddChildContent("<p id=\"page-body\">content</p>"));

        var content = open.Find("[data-settings-frame-slot='content']");
        var warning = content.FirstElementChild!;
        Assert.Equal("settings-advanced-warning", warning.GetAttribute("data-testid"));
        Assert.Contains("These settings are for experienced administrators. Everyday use never needs them.", warning.TextContent, StringComparison.Ordinal);
        Assert.Equal("page-body", content.QuerySelector("#page-body")!.Id);

        var closed = Render<SettingsPageFrame>(parameters => parameters
            .Add(component => component.Title, "Playback")
            .AddChildContent("<p id=\"page-body\">content</p>"));
        Assert.Empty(closed.FindAll("[data-testid='settings-advanced-warning']"));
    }
}
