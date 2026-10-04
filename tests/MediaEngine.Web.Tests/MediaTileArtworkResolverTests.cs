using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.MediaTiles;

namespace MediaEngine.Web.Tests;

public sealed class MediaTileArtworkResolverTests
{
    [Fact]
    public void Resolve_IndividualMovieUsesPortraitCoverAndCinematicHover()
    {
        var surface = MediaTileArtworkResolver.Resolve(
            MediaTileBucket.Movie,
            MediaTilePresentation.Default,
            [
                new MediaTileArtworkVariant(ArtworkRole.Cover, "/art/poster-s.jpg", "/art/poster-m.jpg", "/art/poster-l.jpg", 600, 900),
                new MediaTileArtworkVariant(ArtworkRole.Background, "/art/backdrop-s.jpg", "/art/backdrop-m.jpg", "/art/backdrop-l.jpg", 1920, 1080),
            ]);

        Assert.Equal(MediaTileShape.Portrait, surface.Shape);
        Assert.Equal(MediaTileSurfaceKind.CoverPortrait, surface.SurfaceKind);
        Assert.Equal(MediaTileHoverLayout.BannerPopover, surface.HoverLayout);
        Assert.Equal("/art/poster-s.jpg", surface.TileImageUrl);
        Assert.Equal("/art/backdrop-m.jpg", surface.HoverImageUrl);
    }

    [Fact]
    public void Resolve_SquareMusicArtUsesArtworkOnlySquareFrame()
    {
        var surface = MediaTileArtworkResolver.Resolve(
            MediaTileBucket.Music,
            MediaTilePresentation.Album,
            [new MediaTileArtworkVariant(ArtworkRole.Square, "/art/album-s.jpg", "/art/album-m.jpg", "/art/album-l.jpg", 1000, 1000)]);

        Assert.Equal(MediaTileShape.Square, surface.Shape);
        Assert.Equal(MediaTileSurfaceKind.CoverSquare, surface.SurfaceKind);
        Assert.Equal(MediaTileImageFitMode.Contain, surface.TileImageFitMode);
        Assert.Equal(MediaTileHoverLayout.ArtOnlyPopover, surface.HoverLayout);
    }

    [Fact]
    public void Resolve_AudiobookPreservesMeasuredPortraitCover()
    {
        var surface = MediaTileArtworkResolver.Resolve(
            MediaTileBucket.Audiobook,
            MediaTilePresentation.Default,
            [new MediaTileArtworkVariant(ArtworkRole.Cover, "/art/audio-s.jpg", "/art/audio-m.jpg", "/art/audio-l.jpg", 680, 1080)]);

        Assert.Equal(MediaTileShape.Square, surface.Shape);
        Assert.Equal(MediaTileSurfaceKind.CoverSquare, surface.SurfaceKind);
        Assert.Equal(MediaTileImageFitMode.Contain, surface.TileImageFitMode);
        Assert.Equal(MediaTileShape.Portrait, surface.HoverArtworkShape);
    }

    [Fact]
    public void Resolve_LandscapeGroupRetainsLandscapeRestingCard()
    {
        var surface = MediaTileArtworkResolver.Resolve(
            MediaTileBucket.Book,
            MediaTilePresentation.BookSeries,
            [new MediaTileArtworkVariant(ArtworkRole.Background, "/art/series-s.jpg", "/art/series-m.jpg", "/art/series-l.jpg", 1920, 1080)],
            preferLandscapeTile: true);

        Assert.Equal(MediaTileShape.Landscape, surface.Shape);
        Assert.Equal(MediaTileSurfaceKind.BannerLandscape, surface.SurfaceKind);
        Assert.Equal("/art/series-s.jpg", surface.TileImageUrl);
    }

    [Fact]
    public void Resolve_UltraWideBannerDoesNotBecomeCinematicHover()
    {
        var surface = MediaTileArtworkResolver.Resolve(
            MediaTileBucket.Book,
            MediaTilePresentation.Default,
            [
                new MediaTileArtworkVariant(ArtworkRole.Cover, "/art/book-s.jpg", "/art/book-m.jpg", "/art/book-l.jpg", 600, 900),
                new MediaTileArtworkVariant(ArtworkRole.Banner, "/art/banner-s.jpg", "/art/banner-m.jpg", "/art/banner-l.jpg", 2700, 500),
            ]);

        Assert.Equal(MediaTileHoverLayout.ArtOnlyPopover, surface.HoverLayout);
        Assert.Equal("/art/book-m.jpg", surface.HoverImageUrl);
    }

    [Fact]
    public void Resolve_PosterOnlyMovieUsesContainedCoverLedHover()
    {
        var surface = MediaTileArtworkResolver.Resolve(
            MediaTileBucket.Movie,
            MediaTilePresentation.Default,
            [new MediaTileArtworkVariant(ArtworkRole.Cover, "/art/poster-s.jpg", "/art/poster-m.jpg", "/art/poster-l.jpg", 600, 900)]);

        Assert.Equal(MediaTileHoverLayout.ArtOnlyPopover, surface.HoverLayout);
        Assert.Equal(MediaTileShape.Portrait, surface.HoverArtworkShape);
        Assert.Equal(MediaTileImageFitMode.Contain, surface.HoverImageFitMode);
        Assert.Equal("/art/poster-m.jpg", surface.HoverImageUrl);
    }
}
