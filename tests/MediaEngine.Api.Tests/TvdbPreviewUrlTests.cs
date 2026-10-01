using MediaEngine.Api.Endpoints;
using Microsoft.Extensions.Caching.Memory;

namespace MediaEngine.Api.Tests;

public sealed class TvdbPreviewUrlTests
{
    [Fact]
    public void ProviderRelativeBannerUrlIsNormalizedToTheAllowedArtworkHost()
    {
        var valid = MetadataEndpoints.TryNormalizeTvdbImageUrl(
            "/banners/v4/episode/7979142/screencap/6580af81ebb87.jpg", out var result);

        Assert.True(valid);
        Assert.Equal("https://artworks.thetvdb.com/banners/v4/episode/7979142/screencap/6580af81ebb87.jpg", result.AbsoluteUri);
    }

    [Theory]
    [InlineData("//evil.example/banners/v4/episode/still.jpg")]
    [InlineData("/banners/../private/image.jpg")]
    [InlineData("/banners/%2e%2e/private/image.jpg")]
    [InlineData("/banners/%252e%252e/private/image.jpg")]
    [InlineData("https://evil.example/banners/episode/still.jpg")]
    [InlineData("http://artworks.thetvdb.com/banners/episode/still.jpg")]
    [InlineData("https://artworks.thetvdb.com:444/banners/episode/still.jpg")]
    [InlineData("https://user@artworks.thetvdb.com/banners/episode/still.jpg")]
    [InlineData("ftp://artworks.thetvdb.com/banners/episode/still.jpg")]
    public void UnsafeOrUnsupportedImageUrlsAreRejected(string imageUrl)
    {
        Assert.False(MetadataEndpoints.TryNormalizeTvdbImageUrl(imageUrl, out _));
    }

    [Fact]
    public void PreviewCreationUsesOnlyTheEpisodeImageAndHasNoParentArtworkFallback()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var entityId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();

        var relativePreview = MetadataEndpoints.CreateTvdbPreviewUrl(cache, entityId, ownerId,
            "/banners/v4/episode/7979142/screencap/6580af81ebb87.jpg");
        var missingPreview = MetadataEndpoints.CreateTvdbPreviewUrl(cache, entityId, ownerId, null);

        Assert.Matches($"^/metadata/{entityId:D}/tvdb-match/previews/[0-9a-f]{{32}}$", relativePreview);
        Assert.Null(missingPreview);
    }
}
