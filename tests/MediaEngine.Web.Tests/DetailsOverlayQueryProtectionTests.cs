using Bunit;
using MediaEngine.Web.Services.Navigation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// While the details overlay is open the address bar names the detail item, but the page under it must keep its own
/// query values. These tests record what the framework's query binding does with that address, and pin that
/// <see cref="RoutedPageQuery"/> reads values the way the binding did.
/// </summary>
public sealed class DetailsOverlayQueryProtectionTests : BunitContext
{
    [Fact]
    public void FrameworkQueryBinding_FollowsTheAddressBarEvenWhileAnOverlayAddressIsOpen_SoRoutedPagesCannotUseIt()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/listen?q=dune&tab=songs");
        var underlay = Render<BoundProbe>();
        Assert.Equal("dune", underlay.Instance.Q);
        Assert.Equal("songs", underlay.Instance.Tab);

        // A title opens over the page: the address bar becomes a detail address with its own query.
        nav.NavigateTo("/details/book/6b0b8b0a-8c8e-4a5e-9c79-0c2d6d4b4f11?tab=cast");

        underlay.WaitForAssertion(() =>
        {
            Assert.Equal("cast", underlay.Instance.Tab);
            Assert.Null(underlay.Instance.Q);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("?q=dune")]
    [InlineData("?q=a&q=b")]
    [InlineData("?q=a%20b%2Bc")]
    [InlineData("?q=a+b")]
    [InlineData("?q=")]
    [InlineData("?q")]
    [InlineData("?Q=case")]
    [InlineData("?q=dune#fragment")]
    [InlineData("?media=book&media=audiobook&media=")]
    [InlineData("?media=book")]
    [InlineData("?year=1999&id=6b0b8b0a-8c8e-4a5e-9c79-0c2d6d4b4f11&flag=true")]
    [InlineData("?year=&id=")]
    [InlineData("?year=-5&flag=true")]
    [InlineData("?flag=True")]
    [InlineData("?flag=FALSE")]
    [InlineData("?year=%20 12")]
    public void RoutedPageQuery_ReadsEveryValueTheFrameworkBindingDid(string query)
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/somewhere" + query);
        var probe = Render<BoundProbe>().Instance;
        var read = RoutedPageQuery.From(nav.Uri);

        Assert.Equal(probe.Q, read.String("q"));
        Assert.Equal(probe.Media, read.Strings("media"));
        Assert.Equal(probe.Year, read.Int("year"));
        Assert.Equal(probe.Id, read.Guid("id"));
        Assert.Equal(probe.Flag, read.Bool("flag"));
    }

    private sealed class BoundProbe : ComponentBase
    {
        [SupplyParameterFromQuery(Name = "q")] public string? Q { get; set; }
        [SupplyParameterFromQuery(Name = "tab")] public string? Tab { get; set; }
        [SupplyParameterFromQuery(Name = "media")] public string[]? Media { get; set; }
        [SupplyParameterFromQuery(Name = "year")] public int? Year { get; set; }
        [SupplyParameterFromQuery(Name = "id")] public Guid? Id { get; set; }
        [SupplyParameterFromQuery(Name = "flag")] public bool Flag { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
        }
    }
}
