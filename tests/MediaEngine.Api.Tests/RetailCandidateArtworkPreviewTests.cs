using System.Net;
using MediaEngine.Api.Services.Canonical;
using Microsoft.Extensions.Caching.Memory;
using SkiaSharp;

namespace MediaEngine.Api.Tests;

public sealed class RetailCandidateArtworkPreviewTests
{
    [Fact]
    public async Task TvdbPosterIsResizedForAnEditorResult()
    {
        using var source = new SKBitmap(600, 900);
        source.Erase(SKColors.Purple);
        using var image = SKImage.FromBitmap(source);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        var handler = new PosterHandler(encoded.ToArray());
        var factory = new PosterClientFactory(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());

        var preview = await RetailCandidateArtworkPreview.LoadTvdbAsync(
            "https://artworks.thetvdb.com/banners/poster.jpg", factory, cache, CancellationToken.None);

        Assert.NotNull(preview);
        Assert.StartsWith("data:image/jpeg;base64,", preview);
        using var resized = SKBitmap.Decode(Convert.FromBase64String(preview.Split(',')[1]));
        Assert.Equal(180, resized.Width);
        Assert.Equal(270, resized.Height);
        Assert.Equal(1, handler.RequestCount);

        Assert.Equal(preview, await RetailCandidateArtworkPreview.LoadTvdbAsync(
            "https://artworks.thetvdb.com/banners/poster.jpg", factory, cache, CancellationToken.None));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task NonTvdbImageIsNotFetched()
    {
        var handler = new PosterHandler([]);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        Assert.Null(await RetailCandidateArtworkPreview.LoadTvdbAsync(
            "https://other.example/poster.jpg", new PosterClientFactory(handler), cache, CancellationToken.None));
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData("apple_api", "https://is1-ssl.mzstatic.com/image/thumb/cover.jpg")]
    [InlineData("musicbrainz", "https://coverartarchive.org/release/id/front-500")]
    [InlineData("comicvine", "https://comicvine.gamespot.com/a/uploads/cover.jpg")]
    [InlineData("tmdb", "https://image.tmdb.org/t/p/w500/poster.jpg")]
    public async Task SupportedProviderArtworkIsResizedForAnEditorResult(string provider, string sourceUrl)
    {
        using var source = new SKBitmap(600, 900);
        source.Erase(SKColors.Purple);
        using var image = SKImage.FromBitmap(source);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        var handler = new PosterHandler(encoded.ToArray());
        using var cache = new MemoryCache(new MemoryCacheOptions());

        var preview = await RetailCandidateArtworkPreview.LoadAsync(
            provider, sourceUrl, new PosterClientFactory(handler), cache, CancellationToken.None);

        Assert.NotNull(preview);
        Assert.StartsWith("data:image/jpeg;base64,", preview);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task UnapprovedProviderArtworkIsNotFetched()
    {
        var handler = new PosterHandler([]);
        using var cache = new MemoryCache(new MemoryCacheOptions());

        var preview = await RetailCandidateArtworkPreview.LoadAsync(
            "apple_api", "https://other.example/poster.jpg",
            new PosterClientFactory(handler), cache, CancellationToken.None);

        Assert.Null(preview);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData("apple_api", "https://is1-ssl.mzstatic.com/image/thumb/a/9999x9999bb.jpg", "https://is1-ssl.mzstatic.com/image/thumb/a/180x180bb.jpg")]
    [InlineData("tmdb", "https://image.tmdb.org/t/p/w500/a.jpg", "https://image.tmdb.org/t/p/w185/a.jpg")]
    [InlineData("musicbrainz", "https://coverartarchive.org/release/id/front-500", "https://coverartarchive.org/release/id/front-250")]
    [InlineData("comicvine", "https://comicvine.gamespot.com/a/uploads/original/1/2/cover.jpg", "https://comicvine.gamespot.com/a/uploads/scale_small/1/2/cover.jpg")]
    public void PickerPreviewUsesACompactProviderRendition(string provider, string original, string expected)
    {
        Assert.Equal(expected, RetailCandidateArtworkPreview.NormalizePreviewSource(provider, original));
    }

    private sealed class PosterClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class PosterHandler(byte[] bytes) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
            });
        }
    }
}
