using Bunit;
using MediaEngine.Web.Components.Shared;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Packet S2: one tooltip controller serves the whole document (wwwroot/js/playback-tooltip.js, loaded once per page), so a tooltip
/// is markup only: no script import, no attach call, no teardown, however many a page draws.
/// </summary>
public sealed class AppTooltipControllerTests : AsyncBunitContext
{
    [Fact]
    public void RenderingATooltipMakesNoScriptCallAndKeepsTheMarkupTheControllerReads()
    {
        // Strict mode (the default) fails the test on any interop call that was not set up, and none are set up.
        var cut = Render<AppTooltip>(parameters => parameters
            .Add(component => component.Text, "Open folder")
            .Add(component => component.Placement, AppPlacement.Bottom)
            .AddChildContent("<button>Go</button>"));

        var owner = cut.Find(".playback-tooltip-owner");
        Assert.Equal("Open folder", owner.GetAttribute("data-playback-tooltip"));
        Assert.Equal("bottom", owner.GetAttribute("data-playback-tooltip-placement"));
        Assert.Equal("false", owner.GetAttribute("data-playback-tooltip-suppressed"));
        Assert.Empty(JSInterop.Invocations);
    }

    [Fact]
    public void ASuppressedTooltipSaysSoInItsMarkupAndStillMakesNoScriptCall()
    {
        var cut = Render<AppTooltip>(parameters => parameters
            .Add(component => component.Text, "Why it is off")
            .Add(component => component.Suppressed, true));

        Assert.Equal("true", cut.Find(".playback-tooltip-owner").GetAttribute("data-playback-tooltip-suppressed"));
        Assert.Empty(JSInterop.Invocations);
    }

    [Fact]
    public void AnIconButtonWithATooltipMakesNoScriptCall()
    {
        var cut = Render<AppIconButton>(parameters => parameters
            .Add(component => component.Icon, AppMaterialIcons.Outlined.Add)
            .Add(component => component.Tooltip, "Add to list"));

        Assert.Equal("Add to list", cut.Find(".playback-tooltip-owner").GetAttribute("data-playback-tooltip"));
        Assert.Equal("Add to list", cut.Find("button").GetAttribute("aria-label"));
        Assert.Empty(JSInterop.Invocations);
    }

    [Fact]
    public void ManyTooltipsAndARedrawStillMakeNoScriptCall()
    {
        var cut = Render<AppIconButton>(parameters => parameters
            .Add(component => component.Icon, AppMaterialIcons.Outlined.Add)
            .Add(component => component.Tooltip, "First"));
        for (var i = 0; i < 100; i++)
        {
            var text = $"Tile {i}";
            Render<AppTooltip>(parameters => parameters.Add(component => component.Text, text).AddChildContent("<span>x</span>"));
        }

        cut.Render(parameters => parameters.Add(component => component.Tooltip, "Changed"));

        Assert.Equal("Changed", cut.Find(".playback-tooltip-owner").GetAttribute("data-playback-tooltip"));
        Assert.Empty(JSInterop.Invocations);
    }

    [Fact]
    public void TheTooltipComponentHasNoScriptModuleOfItsOwn()
    {
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");
        var razor = File.ReadAllText(Path.Combine(web, "Components", "Shared", "AppTooltip.razor"));
        Assert.DoesNotContain("IJSRuntime", razor);
        Assert.DoesNotContain("IAsyncDisposable", razor);
        Assert.DoesNotContain("OnAfterRenderAsync", razor);

        // The controller is installed by the module the page already loads once (App.razor), through its import of the tooltip module.
        var overflow = File.ReadAllText(Path.Combine(web, "wwwroot", "js", "app-overflow-tooltip.js"));
        Assert.Contains("from './playback-tooltip.js'", overflow);
        var controller = File.ReadAllText(Path.Combine(web, "wwwroot", "js", "playback-tooltip.js"));
        Assert.Contains("const OWNER = '.playback-tooltip-owner'", controller);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }
}
