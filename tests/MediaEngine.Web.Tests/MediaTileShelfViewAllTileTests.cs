using Bunit;
using MediaEngine.Web.Components.MediaTiles;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>The View all tile that ends every shelf with a grid page, beside the unchanged header link.</summary>
public sealed class MediaTileShelfViewAllTileTests : AsyncBunitContext
{
    private readonly DeviceContextService _device;

    public MediaTileShelfViewAllTileTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        var api = EngineApiClientStub.CreateDefault();
        Services.AddSingleton(api);
        _device = new DeviceContextService(api);
        Services.AddSingleton(_device);
    }

    private Task UseAsync(string formFactor) =>
        _device.InitialiseAsync(new DeviceContextSnapshot(formFactor == "phone" ? "mobile" : "web", formFactor, formFactor != "desktop"));

    private static MediaTileViewModel Item(string title) => new()
    {
        Id = Guid.NewGuid(),
        WorkId = Guid.NewGuid(),
        Title = title,
        MediaKind = "Movie",
        Shape = MediaTileShape.Portrait,
        TileImageUrl = "/art.jpg",
        DetailsNavigationUrl = "/details/work/x",
        HoverMode = MediaTileHoverMode.GlowOnly,
    };

    private IRenderedComponent<MediaTileShelf> RenderShelf(string? seeAllRoute) =>
        Render<MediaTileShelf>(p => p.Add(c => c.Shelf, new MediaTileShelfViewModel
        {
            Title = "My List",
            SeeAllRoute = seeAllRoute,
            Items = [Item("One"), Item("Two")],
        }));

    [Fact]
    public async Task ShelfWithAGridRoute_EndsWithAViewAllTileThatLinksToItAndNamesTheShelf()
    {
        await UseAsync("desktop");

        var cut = RenderShelf("/all/personal/my-list");

        var tile = cut.Find("a.media-tile-shelf-view-all");
        Assert.Equal("/all/personal/my-list", tile.GetAttribute("href"));
        Assert.Equal("View all My List", tile.GetAttribute("aria-label"));
        Assert.Equal("View all", cut.Find(".media-tile-shelf-view-all-text").TextContent.Trim());
        Assert.NotEmpty(tile.QuerySelectorAll("svg"));
        // It is the last item of the row, after every tile.
        var row = cut.Find(".media-tile-shelf-scroll");
        Assert.Equal(tile.OuterHtml, row.LastElementChild!.OuterHtml);
    }

    [Fact]
    public async Task ShelfWithoutAGridRoute_HasNoViewAllTileAndNoHeaderLink()
    {
        await UseAsync("desktop");

        var cut = RenderShelf(null);

        Assert.Empty(cut.FindAll("a.media-tile-shelf-view-all"));
        Assert.Empty(cut.FindAll("a.media-tile-shelf-link"));
    }

    [Theory]
    [InlineData("/all/watch/recent")]
    [InlineData("/watch/series")]
    [InlineData("/listen/music/playlists")]
    public async Task ShelfWhoseRouteIsItsOwnPageGetsTheSameEndTile(string route)
    {
        await UseAsync("desktop");

        var cut = RenderShelf(route);

        Assert.Equal(route, cut.Find("a.media-tile-shelf-view-all").GetAttribute("href"));
        Assert.Equal(route, cut.Find("a.media-tile-shelf-link").GetAttribute("href"));
    }

    [Theory]
    [InlineData("phone", "See All")]
    [InlineData("desktop", "View all")]
    public async Task HeaderLinkKeepsItsWordingNextToTheEndTile(string formFactor, string wording)
    {
        await UseAsync(formFactor);

        var cut = RenderShelf("/all/personal/liked");

        var header = cut.Find("a.media-tile-shelf-link");
        Assert.Equal(wording, header.TextContent.Trim());
        Assert.Equal("/all/personal/liked", header.GetAttribute("href"));
        Assert.Single(cut.FindAll("a.media-tile-shelf-view-all"));
    }

    [Fact]
    public void EndTileIsASquareCentredOnTheRowWithAFocusRingAndAtLeast48pxEachWay()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var css = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "MediaTiles", "MediaTileShelf.razor.css"));
        css = System.Text.RegularExpressions.Regex.Replace(css, @"\s+", string.Empty);
        var rule = css[css.IndexOf(".media-tile-shelf-view-all{", StringComparison.Ordinal)..];
        rule = rule[..rule.IndexOf('}')];

        Assert.Contains("align-self:center", rule);
        Assert.Contains("aspect-ratio:1/1", rule);
        Assert.Contains("width:max(48px,var(--media-tile-shelf-view-all-width))", rule);
        Assert.Contains("min-height:48px", rule);
        Assert.Contains("min-width:48px", rule);
        Assert.Contains(".media-tile-shelf-view-all:focus-visible{outline:3pxsolid", css);
    }
}
