using Bunit;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The skip link points at the main region, which is inert while a details window is open (the page behind the window is). While a window is open
/// the link leaves the tab order and the accessibility tree instead of pointing at something that cannot take focus.
/// </summary>
public sealed partial class UiShellRenderTests
{
    [Fact]
    public void MainLayout_SkipLinkIsInTheTabOrder_WhenNoDetailsWindowIsOpen()
    {
        var cut = Render<MainLayout>(parameters => parameters
            .Add(layout => layout.Body, builder => builder.AddMarkupContent(0, "<section>Body</section>")));

        var link = cut.Find("a.skip-to-content");
        Assert.Equal("#main-content", link.GetAttribute("href"));
        Assert.Null(link.GetAttribute("tabindex"));
        Assert.Null(link.GetAttribute("aria-hidden"));
    }

    [Fact]
    public async Task MainLayout_SkipLinkLeavesTheTabOrderAndTheAccessibilityTree_WhileADetailsWindowIsOpen()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        var overlay = new DetailsOverlayState(navigation, new ClosedHistory());
        var cut = Render<MainLayout>(parameters => parameters
            .AddCascadingValue(overlay)
            .Add(layout => layout.Body, builder => builder.AddMarkupContent(0, "<section>Body</section>")));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".tl-appbar")));

        await cut.InvokeAsync(() => navigation.NavigateTo($"/details/book/{Guid.NewGuid()}"));

        cut.WaitForAssertion(() =>
        {
            Assert.True(overlay.IsOpen);
            var link = cut.Find("a.skip-to-content");
            Assert.Equal("-1", link.GetAttribute("tabindex"));
            Assert.Equal("true", link.GetAttribute("aria-hidden"));
        });

        // Closing the window puts the link back in the tab order.
        await cut.InvokeAsync(() => navigation.NavigateTo("/"));
        cut.WaitForAssertion(() =>
        {
            Assert.False(overlay.IsOpen);
            Assert.Null(cut.Find("a.skip-to-content").GetAttribute("tabindex"));
        });
    }

    private sealed class ClosedHistory : IDetailsOverlayHistory
    {
        public ValueTask<bool> GoBackAsync(int steps) => ValueTask.FromResult(true);
    }
}
