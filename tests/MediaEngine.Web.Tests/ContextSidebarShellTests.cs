using Bunit;
using MediaEngine.Web.Components.Shared;
using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Tests;

public sealed class ContextSidebarShellTests : BunitContext
{
    public ContextSidebarShellTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    [Fact]
    public void OpeningSidebarKeepsMainContentAndAddsNonmodalLayoutSpace()
    {
        var cut = Render<ContextSidebarShell>(parameters => parameters
            .Add(component => component.IsOpen, true)
            .Add(component => component.Width, 480)
            .Add(component => component.AriaLabel, "Ingestion details")
            .Add(component => component.MainContent, Text("Library content"))
            .Add(component => component.SidebarContent, Text("Item details")));

        Assert.Contains("is-open", cut.Find(".context-sidebar-shell").ClassList);
        Assert.Contains("480px", cut.Find(".context-sidebar-shell").GetAttribute("style"));
        Assert.Contains("Library content", cut.Find(".context-sidebar-shell__main").TextContent);
        Assert.Contains("Item details", cut.Find(".context-sidebar-shell__aside").TextContent);
        Assert.Empty(cut.FindAll("[aria-modal='true'], .context-sidebar-shell__backdrop"));
    }

    [Fact]
    public void ClosingSidebarRestoresSingleColumnWithoutDiscardingMainContent()
    {
        var cut = Render<ContextSidebarShell>(parameters => parameters
            .Add(component => component.IsOpen, true)
            .Add(component => component.MainContent, Text("Library content")));

        cut.Render(parameters => parameters
            .Add(component => component.IsOpen, false)
            .Add(component => component.MainContent, Text("Library content")));

        Assert.DoesNotContain("is-open", cut.Find(".context-sidebar-shell").ClassList);
        Assert.Empty(cut.FindAll(".context-sidebar-shell__aside"));
        Assert.Contains("Library content", cut.Find(".context-sidebar-shell__main").TextContent);
    }

    private static RenderFragment Text(string value) => builder => builder.AddContent(0, value);
}
