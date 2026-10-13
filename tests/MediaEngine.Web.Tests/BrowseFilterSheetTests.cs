using Bunit;
using MediaEngine.Web.Components.Browse;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class BrowseFilterSheetTests : AsyncBunitContext
{
    private static readonly string[] FilterNames = ["genre", "year", "rating", "sort", "tile-size", "layout"];
    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());

    public BrowseFilterSheetTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_device);
    }

    private Task UseAsync(string formFactor) =>
        _device.InitialiseAsync(new DeviceContextSnapshot(formFactor == "phone" ? "mobile" : "web", formFactor, formFactor != "desktop"));

    private static RenderFragment<bool> Filters => inSheet => builder =>
    {
        foreach (var name in FilterNames)
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "test-filter");
            builder.AddAttribute(2, "data-filter", name);
            builder.AddAttribute(3, "data-in-sheet", inSheet ? "true" : "false");
            builder.CloseElement();
        }
    };

    private IRenderedComponent<BrowseFilterSheet> RenderSheet(int active = 0, string? results = null) =>
        Render<BrowseFilterSheet>(parameters => parameters
            .Add(component => component.ChildContent, Filters)
            .Add(component => component.ActiveCount, active)
            .Add(component => component.ResultLabel, results));

    [Fact]
    public async Task PhoneShowsOneFiltersButtonAndNoInlineControls()
    {
        await UseAsync("phone");
        var sheet = RenderSheet(active: 2);

        var trigger = sheet.Find("button.browse-filter-sheet__trigger");
        Assert.Contains("Filters (2)", trigger.TextContent);
        Assert.Equal("dialog", trigger.GetAttribute("aria-haspopup"));
        Assert.Empty(sheet.FindAll(".test-filter"));
        Assert.Empty(sheet.FindAll("dialog"));
    }

    [Fact]
    public async Task DesktopRendersTheInlinePanelAndNoButton()
    {
        await UseAsync("desktop");
        var sheet = RenderSheet(active: 2);

        Assert.Empty(sheet.FindAll("button.browse-filter-sheet__trigger"));
        Assert.Equal(FilterNames, sheet.FindAll(".test-filter").Select(element => element.GetAttribute("data-filter")));
        Assert.All(sheet.FindAll(".test-filter"), element => Assert.Equal("false", element.GetAttribute("data-in-sheet")));
    }

    [Fact]
    public async Task SheetListsEveryFilterTheInlinePanelHas()
    {
        await UseAsync("desktop");
        var inline = RenderSheet().FindAll(".test-filter").Select(element => element.GetAttribute("data-filter")).ToList();

        await UseAsync("phone");
        var sheet = RenderSheet(active: 1, results: "24 items");
        sheet.Find("button.browse-filter-sheet__trigger").Click();

        Assert.Contains("app-native-dialog--sheet", sheet.Find("dialog").ClassName);
        var inSheet = sheet.FindAll("dialog .test-filter").Select(element => element.GetAttribute("data-filter")).ToList();
        Assert.Equal(inline, inSheet);
        Assert.All(sheet.FindAll("dialog .test-filter"), element => Assert.Equal("true", element.GetAttribute("data-in-sheet")));
        Assert.Contains("Show 24 items", sheet.Find("button.browse-filter-sheet__apply").TextContent);
    }

    [Fact]
    public async Task ShowButtonClosesTheSheetAndClearRunsTheClearCallback()
    {
        await UseAsync("phone");
        var cleared = 0;
        var sheet = Render<BrowseFilterSheet>(parameters => parameters
            .Add(component => component.ChildContent, Filters)
            .Add(component => component.ActiveCount, 3)
            .Add(component => component.OnClear, EventCallback.Factory.Create(this, () => cleared++)));
        sheet.Find("button.browse-filter-sheet__trigger").Click();

        sheet.Find("button.browse-filter-sheet__clear").Click();
        Assert.Equal(1, cleared);
        Assert.Single(sheet.FindAll("dialog"));

        sheet.Find("button.browse-filter-sheet__apply").Click();
        sheet.WaitForAssertion(() => Assert.Empty(sheet.FindAll("dialog")));
    }

    [Fact]
    public async Task LeavingThePhoneFormFactorClosesTheSheetAndShowsTheInlinePanelOnce()
    {
        await UseAsync("phone");
        var sheet = RenderSheet(active: 1);
        sheet.Find("button.browse-filter-sheet__trigger").Click();
        Assert.Single(sheet.FindAll("dialog"));

        await UseAsync("desktop");

        sheet.WaitForAssertion(() =>
        {
            Assert.Empty(sheet.FindAll("dialog"));
            Assert.Empty(sheet.FindAll("button.browse-filter-sheet__trigger"));
            Assert.Equal(FilterNames.Length, sheet.FindAll(".test-filter").Count);
        });
    }

    [Fact]
    public async Task ModeSelectorAttachesTheEdgeHintAndBringsTheActiveModeIntoView()
    {
        var module = JSInterop.SetupModule("./js/scroll-edge-hint.js");
        module.Mode = JSRuntimeMode.Loose;
        await UseAsync("phone");

        Render<AppBrowseModeSelector>(parameters => parameters
            .Add(component => component.Options, new[]
            {
                new BrowseGroupingOption("all", "All", "apps"),
                new BrowseGroupingOption("series", "Series", "list"),
            })
            .Add(component => component.Value, "series"));

        Assert.Single(module.Invocations["attach"]);
        Assert.Single(module.Invocations["showActive"]);
    }
}
