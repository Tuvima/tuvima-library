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
        TileImageUrl = "/art/lighthouse.jpg",
        HoverImageUrl = "/art/lighthouse-wide.jpg",
        Description = "A quiet coastal discovery.",
        DetailsNavigationUrl = "/details/work/lighthouse",
        NavigationUrl = "/details/work/lighthouse",
    };

    private Task Init(string formFactor, bool touch) =>
        _device.InitialiseAsync(new DeviceContextSnapshot(formFactor == "phone" ? "mobile" : "web", formFactor, touch));

    [Fact]
    public async Task MouseDesktopRendersNoHoverPanelAndRegistersNoHoverScript()
    {
        await Init("desktop", touch: false);

        var cut = Render<MediaTile>(p => p.Add(c => c.Item, CinematicMovie()));

        // No tile grows on hover any more (Home, detail and tiles spec D1), so there is no panel and no hover script.
        Assert.Empty(cut.FindAll(".media-tile-hover-panel"));
        Assert.DoesNotContain(JSInterop.Invocations, call => call.Identifier == "registerMediaTileHover");
    }

    private static MediaTileViewModel KeptShow() => new()
    {
        Id = Guid.NewGuid(),
        WorkId = Guid.NewGuid(),
        Title = "Lost Show",
        SortYear = 2024,
        MediaKind = "TV",
        IsCollection = true,
        Presentation = MediaTilePresentation.TvSeries,
        IsKept = true,
        Shape = MediaTileShape.Portrait,
        SurfaceKind = MediaTileSurfaceKind.CoverPortrait,
        HoverLayout = MediaTileHoverLayout.BannerPopover,
        TileImageUrl = "/art/lost.jpg",
        HoverImageUrl = "/art/lost-wide.jpg",
        DetailsNavigationUrl = "/details/tvshow/lost",
        NavigationUrl = "/details/tvshow/lost",
    };

    [Fact]
    public async Task ACardThatIsNotKept_ShowsNoKeptLabel()
    {
        await Init("phone", touch: true);

        var cut = Render<MediaTile>(p => p.Add(c => c.Item, CinematicMovie()));

        Assert.DoesNotContain("Kept", cut.Markup);
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
