using Bunit;
using MediaEngine.Web.Components.Artwork;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Playback;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>Right-sized pictures (WP4b): Listen, Settings, editor and playback pictures ask the Engine for the copy that fits how big they are drawn.</summary>
public sealed class RightSizedPicturesListenTests : BunitContext
{
    private static readonly Guid WorkId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly string Cover = $"/engine-image/stream/entity/work/{WorkId:D}/cover?size=s";

    public RightSizedPicturesListenTests()
    {
        Services.AddNativeUiServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void PlaybackContextRow_AsksForTheTinyCopyForA56PixelThumbnailAndOffersSharperOnes()
    {
        var cut = Render<PlaybackContextRow>(parameters => parameters
            .Add(component => component.Title, "Track")
            .Add(component => component.ArtworkUrl, Cover));

        var img = cut.Find("img");
        Assert.EndsWith("?size=t", img.GetAttribute("src"), StringComparison.Ordinal);
        Assert.Contains("size=s 2x", img.GetAttribute("srcset"), StringComparison.Ordinal);
        Assert.Equal("lazy", img.GetAttribute("loading"));
    }

    [Fact]
    public void PlaybackContextRow_ReplacesALargerRequestedCopyWithTheOneThatFitsTheThumbnail()
    {
        var cut = Render<PlaybackContextRow>(parameters => parameters
            .Add(component => component.Title, "Track")
            .Add(component => component.ArtworkUrl, Cover.Replace("size=s", "size=m", StringComparison.Ordinal)));

        Assert.EndsWith("?size=t", cut.Find("img").GetAttribute("src"), StringComparison.Ordinal);
    }

    [Fact]
    public void PlaybackPositionRow_AsksForTheTinyCopyForAThumbnailDrawnAt48Pixels()
    {
        var cut = Render<PlaybackPositionRow>(parameters => parameters
            .Add(component => component.Title, "Chapter")
            .Add(component => component.ArtworkUrl, Cover));

        var img = cut.Find("img");
        Assert.EndsWith("?size=t", img.GetAttribute("src"), StringComparison.Ordinal);
        Assert.Contains("2x", img.GetAttribute("srcset"), StringComparison.Ordinal);
    }

    [Fact]
    public void ArtworkJustifiedGallery_SizesEachCardToHowWideItIsDrawn()
    {
        var items = new[]
        {
            new GalleryItem("a", $"/engine-image/stream/artwork/{Guid.NewGuid():D}?size=l", 1000, 1500),
            new GalleryItem("b", $"/engine-image/stream/artwork/{Guid.NewGuid():D}?size=l", 4000, 1000),
        };

        var cut = Render<ArtworkJustifiedGallery<GalleryItem>>(parameters => parameters
            .Add(component => component.Items, items)
            .Add(component => component.ItemKey, item => item.Key)
            .Add(component => component.ImageUrl, item => item.Url)
            .Add(component => component.Width, item => item.Width)
            .Add(component => component.Height, item => item.Height)
            .Add(component => component.Title, item => item.Key));

        var images = cut.FindAll("img");
        Assert.Equal(2, images.Count);
        Assert.EndsWith("?size=s", images[0].GetAttribute("src"), StringComparison.Ordinal);
        Assert.EndsWith("?size=m", images[1].GetAttribute("src"), StringComparison.Ordinal);
        Assert.All(images, image => Assert.Null(image.GetAttribute("sizes")));
    }

    [Fact]
    public void PlaybackArtworkUrl_ForDrawnSize_PicksTheSmallestCopyThatCoversTheDrawnSize()
    {
        var item = new ListenQueueItem { WorkId = WorkId, MediaType = "Music" };

        Assert.EndsWith("?size=t", PlaybackArtworkUrl.ForDrawnSize(item, 64), StringComparison.Ordinal);
        Assert.EndsWith("?size=s", PlaybackArtworkUrl.ForDrawnSize(item, 200), StringComparison.Ordinal);
        Assert.EndsWith("?size=m", PlaybackArtworkUrl.ForDrawnSize(item, 520), StringComparison.Ordinal);
        Assert.Null(PlaybackArtworkUrl.ForDrawnSize(null, 64));
    }

    [Fact]
    public void PlaybackArtworkUrl_SrcSetForDrawnSize_OffersTheNextCopyUpToSharperScreens()
    {
        var item = new ListenQueueItem { WorkId = WorkId, MediaType = "Music" };

        var srcSet = PlaybackArtworkUrl.SrcSetForDrawnSize(item, 64);

        Assert.NotNull(srcSet);
        Assert.Contains("size=t 1x", srcSet, StringComparison.Ordinal);
        Assert.Contains("size=s 2x", srcSet, StringComparison.Ordinal);
        Assert.Null(PlaybackArtworkUrl.SrcSetForDrawnSize(item, 2000));
        Assert.Null(PlaybackArtworkUrl.SrcSetForDrawnSize(null, 64));
    }

    private sealed record GalleryItem(string Key, string Url, int Width, int Height);
}
