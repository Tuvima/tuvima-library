using Bunit;
using MediaEngine.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace MediaEngine.Web.Tests;

public sealed class NativeStructureTests : AsyncBunitContext
{
    public NativeStructureTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    [Fact]
    public async Task RemovableChipInvokesItsCloseCallbackAndRespectsDisabledState()
    {
        var removed = 0;
        var cut = Render<AppChip>(parameters => parameters
            .Add(component => component.Text, "Draft tag")
            .Add(component => component.OnClose, () => removed++));
        await cut.Find("button[aria-label='Remove tag']").ClickAsync(new MouseEventArgs());
        Assert.Equal(1, removed);
        cut.Render(parameters => parameters.Add(component => component.Disabled, true));
        Assert.True(cut.Find("button").HasAttribute("disabled"));
        await cut.Find("button").ClickAsync(new MouseEventArgs());
        Assert.Equal(1, removed);
    }

    [Fact]
    public void HiddenFieldLabelsRetainAccessibleNames()
    {
        var text = Render<AppTextField>(parameters => parameters
            .Add(component => component.Label, "Series title")
            .Add(component => component.HideLabel, true));
        Assert.Empty(text.FindAll("label"));
        Assert.Equal("Series title", text.Find("input").GetAttribute("aria-label"));
        var select = Render<AppSelect>(parameters => parameters
            .Add(component => component.Label, "Canonical year")
            .Add(component => component.HideLabel, true)
            .Add(component => component.Native, true));
        Assert.Empty(select.FindAll("label"));
        Assert.Equal("Canonical year", select.Find("select").GetAttribute("aria-label"));
    }

    [Theory]
    [InlineData(AppSkeletonShape.Rectangle, "rect")]
    [InlineData(AppSkeletonShape.Text, "text")]
    [InlineData(AppSkeletonShape.Circle, "circle")]
    public void SkeletonSupportsSizedShapesAndAccessibleLoadingLabels(AppSkeletonShape shape, string className)
    {
        var cut = Render<AppSkeleton>(parameters => parameters
            .AddUnmatched("Width", "120px")
            .AddUnmatched("Height", "16px")
            .AddUnmatched("Shape", shape)
            .AddUnmatched("aria-label", "Loading account"));
        var root = cut.Find(".app-skeleton");
        Assert.Equal("width:120px;height:16px", root.GetAttribute("style"));
        Assert.Contains($"app-skeleton--{className}", root.ClassName);
        Assert.Equal("status", root.GetAttribute("role"));
        Assert.Equal("Loading account", root.GetAttribute("aria-label"));
        Assert.False(root.HasAttribute("aria-hidden"));
    }

    [Fact]
    public void UnlabelledSizeBasedSkeletonRetainsItsExistingGeometryContract()
    {
        var cut = Render<AppSkeleton>(parameters => parameters.Add(component => component.Size, AppControlSize.Compact));
        var root = cut.Find(".app-skeleton");
        Assert.Equal("true", root.GetAttribute("aria-hidden"));
        Assert.False(root.HasAttribute("role"));
        Assert.False(root.HasAttribute("style"));
        Assert.DoesNotContain("app-skeleton--sized", root.ClassName);
        Assert.Contains("app-control--compact", root.ClassName);
    }

    [Fact]
    public async Task TabsSkipDisabledSectionsAndExposeTheSelectedPanelToKeyboardUsers()
    {
        var selected = -1;
        var cut = Render<AppTabs>(parameters => parameters
            .Add(component => component.ActivePanelIndexChanged, (int value) => selected = value)
            .AddChildContent(TabPanels));

        var tabs = cut.FindAll("[role='tab']");
        Assert.Equal("true", tabs[0].GetAttribute("aria-selected"));
        Assert.Equal("0", tabs[0].GetAttribute("tabindex"));
        Assert.True(tabs[1].HasAttribute("disabled"));
        await tabs[0].KeyDownAsync(new KeyboardEventArgs { Key = "ArrowRight" });

        Assert.Equal(2, selected);
        tabs = cut.FindAll("[role='tab']");
        Assert.Equal("true", tabs[2].GetAttribute("aria-selected"));
        Assert.Equal("0", tabs[2].GetAttribute("tabindex"));
        var activePanel = cut.Find($"#{tabs[2].GetAttribute("aria-controls")}");
        Assert.False(activePanel.HasAttribute("hidden"));
        Assert.Equal(tabs[2].Id, activePanel.GetAttribute("aria-labelledby"));
        Assert.Contains("Third content", activePanel.TextContent);
        Assert.DoesNotContain("First content", cut.Markup);

        await tabs[2].KeyDownAsync(new KeyboardEventArgs { Key = "Home" });
        Assert.Equal(0, selected);
        await cut.FindAll("[role='tab']")[0].KeyDownAsync(new KeyboardEventArgs { Key = "End" });
        Assert.Equal(2, selected);
    }

    [Fact]
    public async Task KeepPanelsAlivePreservesInactiveContentBehindHiddenPanels()
    {
        var cut = Render<AppTabs>(parameters => parameters
            .Add(component => component.KeepPanelsAlive, true)
            .AddChildContent(TabPanels));
        await cut.FindAll("[role='tab']")[2].ClickAsync();
        Assert.Contains("First content", cut.Markup);
        var first = cut.FindAll("[role='tabpanel']")[0];
        Assert.True(first.HasAttribute("hidden"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpansionPanelsRespectSingleAndMultipleExpansion(bool multiple)
    {
        var cut = Render<AppExpansionPanels>(parameters => parameters
            .Add(component => component.MultiExpansion, multiple)
            .AddChildContent(ExpansionPanels));
        await cut.FindAll("button")[0].ClickAsync();
        await cut.FindAll("button")[1].ClickAsync();
        var headers = cut.FindAll("button");
        Assert.Equal(multiple ? "true" : "false", headers[0].GetAttribute("aria-expanded"));
        Assert.Equal("true", headers[1].GetAttribute("aria-expanded"));
        var controlled = cut.Find($"#{headers[0].GetAttribute("aria-controls")}");
        Assert.Equal(!multiple, controlled.HasAttribute("hidden"));
    }

    [Fact]
    public void CoverageDonutRetainsOwnedAndMissingOrderAndAccessibleCounts()
    {
        var cut = Render<AppChart<double>>(parameters => parameters
            .Add(component => component.ChartLabels, ["Owned", "Missing"])
            .Add(component => component.ChartSeries,
                [new AppChartSeries<double> { Data = new AppChartData<double>([2, 3]) }])
            .Add(component => component.ChartOptions,
                new AppDonutChartOptions { ChartPalette = ["#3F94F6", "#5E43D6"] }));
        Assert.Equal("Owned: 2, Missing: 3", cut.Find("svg").GetAttribute("aria-label"));
        Assert.Equal("img", cut.Find("svg").GetAttribute("role"));
        Assert.Equal(new[] { "#3F94F6", "#5E43D6" }, cut.FindAll("circle").Select(circle => circle.GetAttribute("stroke")));
    }

    [Fact]
    public async Task AlertKeepsErrorSemanticsAndItsDismissAction()
    {
        var dismissed = 0;
        var cut = Render<AppAlert>(parameters => parameters
            .Add(component => component.Severity, AppSeverity.Error)
            .Add(component => component.ShowCloseIcon, true)
            .Add(component => component.CloseIconClicked, () => dismissed++)
            .AddChildContent("The request failed."));
        Assert.Equal("alert", cut.Find("div[role]").GetAttribute("role"));
        Assert.Contains("app-tone--error", cut.Find("div[role]").ClassName);
        await cut.Find("button[aria-label='Dismiss notification']").ClickAsync();
        Assert.Equal(1, dismissed);
    }

    [Fact]
    public void AvatarAcceptsExistingAndMigratedSizeContracts()
    {
        var legacy = Render<AppAvatar>(parameters => parameters.Add(component => component.Size, AppSize.Small));
        var current = Render<AppAvatar>(parameters => parameters.Add(component => component.Size, AppControlSize.Compact));
        var literal = Render<AppAvatar>(parameters => parameters.Add(component => component.Size, "AppSize.Small"));
        Assert.Contains("app-control--compact", legacy.Markup);
        Assert.Contains("app-control--compact", current.Markup);
        Assert.Contains("app-control--compact", literal.Markup);
    }

    private static RenderFragment TabPanels => builder =>
    {
        for (var index = 0; index < 3; index++)
        {
            var label = new[] { "First", "Second", "Third" }[index];
            builder.OpenComponent<AppTabPanel>(0);
            builder.AddAttribute(1, nameof(AppTabPanel.Text), label);
            builder.AddAttribute(2, nameof(AppTabPanel.Disabled), index == 1);
            builder.AddAttribute(3, nameof(AppTabPanel.ChildContent), (RenderFragment)(content => content.AddContent(0, $"{label} content")));
            builder.CloseComponent();
        }
    };

    private static RenderFragment ExpansionPanels => builder =>
    {
        for (var index = 0; index < 2; index++)
        {
            builder.OpenComponent<AppExpansionPanel>(0);
            builder.AddAttribute(1, nameof(AppExpansionPanel.Text), $"Section {index}");
            builder.AddAttribute(2, nameof(AppExpansionPanel.ChildContent), (RenderFragment)(content => content.AddContent(0, "Section content")));
            builder.CloseComponent();
        }
    };
}
