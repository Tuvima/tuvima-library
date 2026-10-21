using MediaEngine.Web.Services.MediaTiles;

namespace MediaEngine.Web.Tests;

public sealed class ArtworkImageDeliveryTests
{
    [Theory]
    [InlineData("/stream/artwork/11111111-1111-1111-1111-111111111111?size=l", "/stream/artwork/11111111-1111-1111-1111-111111111111?size=s")]
    [InlineData("/api/v1/display/artwork/assets/11111111-1111-1111-1111-111111111111/content?size=l", "/api/v1/display/artwork/assets/11111111-1111-1111-1111-111111111111/content?size=s")]
    [InlineData("/stream/entity/tvshow/11111111-1111-1111-1111-111111111111/cover?size=l", "/stream/entity/tvshow/11111111-1111-1111-1111-111111111111/cover?size=s")]
    [InlineData("/persons/11111111-1111-1111-1111-111111111111/headshot?size=l", "/persons/11111111-1111-1111-1111-111111111111/headshot?size=s")]
    public void Sized_UsesTheRequestedRenditionForSupportedArtworkRoutes(string source, string expected)
    {
        Assert.Equal(expected, MediaTileArtworkUrl.Sized(source, "s"));
    }

    [Fact]
    public void Sized_PreservesNonSizeQueryParametersAndFragments()
    {
        var result = MediaTileArtworkUrl.Sized(
            "/stream/entity/work/11111111-1111-1111-1111-111111111111/cover?token=abc&size=l#preview",
            "m");

        Assert.Equal(
            "/stream/entity/work/11111111-1111-1111-1111-111111111111/cover?token=abc&size=m#preview",
            result);
    }

    [Fact]
    public void Sized_RejectsRoutesWithoutAnAcceptedRenditionContract()
    {
        Assert.Null(MediaTileArtworkUrl.Sized("https://images.example.test/original.jpg", "s"));
        Assert.Null(MediaTileArtworkUrl.Sized("/collections/11111111-1111-1111-1111-111111111111/artwork/poster", "s"));
    }
    [Theory]
    [InlineData(36, "t")]
    [InlineData(96, "t")]
    [InlineData(97, "s")]
    [InlineData(320, "s")]
    [InlineData(640, "m")]
    [InlineData(1920, "l")]
    public void ArtworkSize_PicksTheSmallestCopyAtLeastTheDrawnSize(int pixels, string expected)
    {
        Assert.Equal(expected, ArtworkSize.ForPixels(pixels));
    }

    [Fact]
    public void ForDrawnSize_AsksForTheTinyCopyForAFace()
    {
        Assert.Equal(
            "/engine-image/persons/11111111-1111-1111-1111-111111111111/headshot?v=abc&size=t",
            MediaTileArtworkUrl.ForDrawnSize("/engine-image/persons/11111111-1111-1111-1111-111111111111/headshot?v=abc", 48));
    }

    [Fact]
    public void ForDrawnSize_KeepsAddressesWithoutCopies()
    {
        Assert.Equal("https://images.example.test/original.jpg", MediaTileArtworkUrl.ForDrawnSize("https://images.example.test/original.jpg", 48));
        Assert.Null(MediaTileArtworkUrl.ForDrawnSize(null, 48));
    }

    [Fact]
    public void SrcSetForDrawnSize_GivesSharpScreensTheNextCopyUp()
    {
        Assert.Equal(
            "/stream/artwork/11111111-1111-1111-1111-111111111111?size=t 1x, /stream/artwork/11111111-1111-1111-1111-111111111111?size=s 2x, /stream/artwork/11111111-1111-1111-1111-111111111111?size=s 3x",
            MediaTileArtworkUrl.SrcSetForDrawnSize("/stream/artwork/11111111-1111-1111-1111-111111111111?size=l", 64));
        Assert.Null(MediaTileArtworkUrl.SrcSetForDrawnSize("/stream/artwork/11111111-1111-1111-1111-111111111111", 20));
        Assert.Null(MediaTileArtworkUrl.SrcSetForDrawnSize("https://images.example.test/original.jpg", 64));
    }

    [Theory]
    [InlineData("/engine-image/stream/11111111-1111-1111-1111-111111111111/cover")]
    [InlineData("/engine-image/stream/11111111-1111-1111-1111-111111111111/background")]
    [InlineData("/engine-image/library/portraits/11111111-1111-1111-1111-111111111111")]
    public void Sized_AcceptsTheAssetAndPortraitRoutesThatNowTakeASize(string source)
    {
        Assert.Equal($"{source}?size=t", MediaTileArtworkUrl.Sized(source, "t"));
    }

    [Theory]
    [InlineData("/engine-image/stream/11111111-1111-1111-1111-111111111111/logo")]
    [InlineData("/stream/11111111-1111-1111-1111-111111111111")]
    public void Sized_LeavesRoutesWithoutCopiesAlone(string source)
    {
        Assert.Null(MediaTileArtworkUrl.Sized(source, "t"));
    }
}
