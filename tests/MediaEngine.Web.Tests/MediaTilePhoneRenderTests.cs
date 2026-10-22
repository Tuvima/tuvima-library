using Bunit;
using MediaEngine.Web.Components.MediaTiles;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>Touch never shows the desktop hover preview; the whole card stays the link and carries its own title.</summary>
public sealed class MediaTilePhoneRenderTests : AsyncBunitContext
{
    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());

    public MediaTilePhoneRenderTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(_device);
    }

    private static MediaTileViewModel CinematicMovie() => new()
    {
        Id = Guid.NewGuid(),
        WorkId = Guid.NewGuid(),
        Title = "Beyond the Last Lighthouse",
        SortYear = 2024,
        MediaKind = "Movie",
        Shape = MediaTileShape.Portrait,
        SurfaceKind = MediaTileSurfaceKind.CoverPortrait,
        HoverLayout = MediaTileHoverLayout.BannerPopover,
        HoverMode = MediaTileHoverMode.Expanded,
        TileImageUrl = "/art/lighthouse.jpg",
        HoverImageUrl = "/art/lighthouse-wide.jpg",
        Description = "A quiet coastal discovery.",
        DetailsNavigationUrl = "/details/work/lighthouse",
        NavigationUrl = "/details/work/lighthouse",
    };

    private Task Init(string formFactor, bool touch) =>
        _device.InitialiseAsync(new DeviceContextSnapshot(formFactor == "phone" ? "mobile" : "web", formFactor, touch));

    [Theory]
    [InlineData("phone", true)]
    [InlineData("tablet", true)]
    public async Task TouchRendersNoHoverPanelNoHoverScriptAndShowsTheTitleUnderTheArt(string formFactor, bool touch)
    {
        await Init(formFactor, touch);

        var cut = Render<MediaTile>(p => p.Add(c => c.Item, CinematicMovie()).Add(c => c.IsHomeSurface, true));

        Assert.Empty(cut.FindAll(".media-tile-hover-panel"));
        Assert.Equal("Beyond the Last Lighthouse", cut.Find(".media-tile-caption__title").TextContent.Trim());
        Assert.Contains("2024", cut.Find(".media-tile-caption").TextContent);
        Assert.Single(cut.FindAll("a.media-tile-link"));
        Assert.Empty(JSInterop.Invocations["registerMediaTileHover"]);
    }

    [Fact]
    public async Task MouseDesktopKeepsTheHoverPanelAndRegistersTheHoverScript()
    {
        await Init("desktop", touch: false);

        var cut = Render<MediaTile>(p => p.Add(c => c.Item, CinematicMovie()).Add(c => c.IsHomeSurface, true));

        Assert.Single(cut.FindAll(".media-tile-hover-panel"));
        Assert.Empty(cut.FindAll(".media-tile-caption"));
        Assert.Single(JSInterop.Invocations["registerMediaTileHover"]);
    }

    [Fact]
    public async Task SwitchingToPhoneRemovesThePanelAndStopsTheHoverScript()
    {
        await Init("desktop", touch: false);
        var cut = Render<MediaTile>(p => p.Add(c => c.Item, CinematicMovie()).Add(c => c.IsHomeSurface, true));
        Assert.Single(cut.FindAll(".media-tile-hover-panel"));

        await cut.InvokeAsync(() => _device.SwitchDeviceAsync(new DeviceContextSnapshot("mobile", "phone", true)));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".media-tile-hover-panel")));
        Assert.Single(JSInterop.Invocations["unregisterMediaTileHover"]);
    }

    [Fact]
    public void PhoneShelfSizingPeekAndGutterLiveInTheStyleSheets()
    {
        static string Css(string path) => File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src/MediaEngine.Web", path)));

        var tile = Css("Components/MediaTiles/MediaTile.razor.css");
        var scroller = Css("Components/MediaTiles/MediaShelfScroller.razor.css");
        var group = Css("Components/MediaTiles/MediaGroupTile.razor.css");
        var home = Css("Components/Pages/LibraryBrowsePage.razor.css");

        Assert.Contains("calc((100vw - 32px) / 2.6 * 1.5)", tile);
        Assert.Contains("calc((100vw - 32px) / 2.4)", tile);
        Assert.Contains("width: min(86vw, 340px);", tile);
        Assert.Contains("calc(min(86vw, 320px) * 9 / 16)", tile);
        Assert.Contains("--media-group-tile-width: min(86vw, 340px);", group);
        Assert.Contains(":root[data-form-factor=\"phone\"] .media-tile-shelf-track", scroller);
        Assert.Contains("margin-inline: calc(-1 * var(--media-shelf-gutter, 16px));", scroller);
        Assert.Contains("scroll-padding-inline: var(--media-shelf-gutter, 16px);", scroller);
        Assert.Contains("--phone-shelf-height: var(--home-shelf-height);", home);
    }
    [Fact]
    public async Task ATileWithoutAWidthList_StillOffersSharpScreensTheMediumCopy()
    {
        await Init("desktop", false);
        var item = CinematicMovie();
        item = new MediaTileViewModel
        {
            Id = item.Id,
            WorkId = item.WorkId,
            Title = item.Title,
            MediaKind = item.MediaKind,
            Shape = item.Shape,
            SurfaceKind = item.SurfaceKind,
            TileImageUrl = "/engine-image/stream/artwork/6f6b4d6e-0000-4000-8000-000000000003?size=s&v=abc",
            DetailsNavigationUrl = item.DetailsNavigationUrl,
            NavigationUrl = item.NavigationUrl,
        };

        var cut = Render<MediaTile>(p => p.Add(c => c.Item, item));

        var image = cut.Find("img[srcset]");
        Assert.EndsWith("size=s", image.GetAttribute("src"), StringComparison.Ordinal);
        Assert.Contains("size=s 1x", image.GetAttribute("srcset"), StringComparison.Ordinal);
        Assert.Contains("size=m 2x", image.GetAttribute("srcset"), StringComparison.Ordinal);
    }
}
