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
