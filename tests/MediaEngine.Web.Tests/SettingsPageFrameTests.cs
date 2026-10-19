using Bunit;
using MediaEngine.Web.Components.Settings;
using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Tests;

public sealed class SettingsPageFrameTests : AsyncBunitContext
{
    private static readonly IReadOnlyList<SettingsBreadcrumb> Trail =
    [
        new("Settings", "/settings/profiles"),
        new("Personal", "/settings/profiles"),
        new("Security", null),
    ];

    public SettingsPageFrameTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Frame_RendersBreadcrumbTitleDescriptionAndContent()
    {
        var cut = Render<SettingsPageFrame>(parameters => parameters
            .Add(frame => frame.Breadcrumbs, Trail)
            .Add(frame => frame.Eyebrow, "Personal")
            .Add(frame => frame.Title, "Security")
            .Add(frame => frame.Description, "Password, email and extra protection.")
            .AddChildContent("<p id=\"page-body\">Body</p>"));

        Assert.Equal("Settings", cut.Find("nav.settings-breadcrumbs a").TextContent);
        Assert.Equal("Security", cut.Find("nav.settings-breadcrumbs span[aria-current='page']").TextContent);
        Assert.Equal("Security", cut.Find("h1").TextContent);
        Assert.Contains("Password, email and extra protection.", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Personal", cut.Find(".app-page-header__eyebrow").TextContent, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-settings-frame-slot='content'] #page-body"));
    }

    [Fact]
    public void Frame_WithoutOptionalSlots_DrawsNoNoticeOrTabWrappers()
    {
        var cut = Render<SettingsPageFrame>(parameters => parameters
            .Add(frame => frame.Breadcrumbs, Trail)
            .Add(frame => frame.Title, "Security")
            .AddChildContent("<p>Body</p>"));

        Assert.Empty(cut.FindAll("[data-settings-frame-slot='notices']"));
        Assert.Empty(cut.FindAll("[data-settings-frame-slot='page-notice']"));
        Assert.Empty(cut.FindAll("[data-settings-frame-slot='tabs']"));
        Assert.Single(cut.FindAll("[data-settings-frame-slot='header']"));
        Assert.Single(cut.FindAll("[data-settings-frame-slot='content']"));
    }

    [Fact]
    public void Frame_WithEverySlot_DrawsNoticesThenBreadcrumbThenHeaderThenPageNoticeThenTabsThenContent()
    {
        var cut = Render<SettingsPageFrame>(parameters => parameters
            .Add(frame => frame.Breadcrumbs, Trail)
            .Add(frame => frame.Title, "Security")
            .Add(frame => frame.Notices, (RenderFragment)(builder => builder.AddMarkupContent(0, "<span id=\"n\">notice</span>")))
            .Add(frame => frame.PageNotice, (RenderFragment)(builder => builder.AddMarkupContent(0, "<span id=\"pn\">page notice</span>")))
            .Add(frame => frame.Tabs, (RenderFragment)(builder => builder.AddMarkupContent(0, "<span id=\"t\">tabs</span>")))
            .AddChildContent("<p id=\"c\">Body</p>"));

        var markup = cut.Markup;
        var order = new[]
        {
            markup.IndexOf("id=\"n\"", StringComparison.Ordinal),
            markup.IndexOf("aria-label=\"Breadcrumb\"", StringComparison.Ordinal),
            markup.IndexOf("<h1", StringComparison.Ordinal),
            markup.IndexOf("id=\"pn\"", StringComparison.Ordinal),
            markup.IndexOf("id=\"t\"", StringComparison.Ordinal),
            markup.IndexOf("id=\"c\"", StringComparison.Ordinal),
        };

        Assert.All(order, position => Assert.True(position >= 0));
        Assert.Equal(order.OrderBy(position => position), order);
    }
}
