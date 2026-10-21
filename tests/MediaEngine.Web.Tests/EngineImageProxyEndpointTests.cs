using System.Net;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.MediaTiles;
using Microsoft.AspNetCore.Http;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Dashboard's picture relay passes the browser's "has it changed?" headers to the Engine and the
/// Engine's keep rules straight back, so an unchanged picture is answered with no body and a
/// versioned one stays kept for a year.
/// </summary>
public sealed class EngineImageProxyEndpointTests
{
    private const string AssetId = "11111111-1111-1111-1111-111111111111";
    private const string Immutable = "private, max-age=31536000, immutable";

    [Fact]
    public async Task Relay_ForwardsTheBrowsersChangeHeadersToTheEngine()
    {
        var handler = new StubEngine(_ => new HttpResponseMessage(HttpStatusCode.NotModified));
        var context = NewContext($"?size=s&v=abc");
        context.Request.Headers["If-None-Match"] = "\"abc\"";
        context.Request.Headers["If-Modified-Since"] = "Wed, 21 Oct 2026 07:28:00 GMT";

        await EngineImageProxyEndpoint.HandleAsync($"stream/artwork/{AssetId}", context, StubClient(handler), CancellationToken.None);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal($"/stream/artwork/{AssetId}?size=s&v=abc", sent.RequestUri!.PathAndQuery);
        Assert.Equal("\"abc\"", Assert.Single(sent.Headers.GetValues("If-None-Match")));
        Assert.True(sent.Headers.Contains("If-Modified-Since"));
    }

    [Fact]
    public async Task Relay_PassesANotModifiedAnswerStraightThroughWithTheEnginesCacheControlAndNoBody()
    {
        var handler = new StubEngine(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.NotModified);
            response.Headers.TryAddWithoutValidation("Cache-Control", "private, no-cache");
            response.Headers.TryAddWithoutValidation("ETag", "\"abc\"");
            return response;
        });
        var context = NewContext("?size=s");
        context.Request.Headers["If-None-Match"] = "\"abc\"";

        await EngineImageProxyEndpoint.HandleAsync($"stream/artwork/{AssetId}", context, StubClient(handler), CancellationToken.None);

        Assert.Equal(StatusCodes.Status304NotModified, context.Response.StatusCode);
        Assert.Equal("private, no-cache", context.Response.Headers.CacheControl.ToString());
        Assert.Equal("\"abc\"", context.Response.Headers.ETag.ToString());
        Assert.Equal(0, context.Response.Body.Length);
    }

    [Fact]
    public async Task Relay_KeepsAVersionedPicturesLongKeepUnchanged()
    {
        var handler = new StubEngine(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            response.Content.Headers.ContentType = new("image/jpeg");
            response.Headers.TryAddWithoutValidation("Cache-Control", Immutable);
            response.Headers.TryAddWithoutValidation("ETag", "\"v1\"");
            return response;
        });
        var context = NewContext("?size=s&v=abc");

        await EngineImageProxyEndpoint.HandleAsync($"stream/artwork/{AssetId}", context, StubClient(handler), CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(Immutable, context.Response.Headers.CacheControl.ToString());
        Assert.Equal("\"v1\"", context.Response.Headers.ETag.ToString());
        Assert.Equal([1, 2, 3], ((MemoryStream)context.Response.Body).ToArray());
    }

    [Fact]
    public async Task Relay_DoesNotCallTheEngineForAnAddressThatIsNotAPicture()
    {
        var handler = new StubEngine(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var context = NewContext(string.Empty);

        await EngineImageProxyEndpoint.HandleAsync("auth/sessions", context, StubClient(handler), CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void SizedPictureAddress_KeepsTheChangeMarker()
    {
        var sized = MediaTileArtworkUrl.Sized($"/stream/artwork/{AssetId}?size=s&v=abc", "m");

        Assert.Equal($"/stream/artwork/{AssetId}?v=abc&size=m", sized);
    }

    [Fact]
    public void BrowserPictureAddress_KeepsTheChangeMarker()
    {
        var url = EngineImageProxyPath.ToBrowserUrl($"/stream/artwork/{AssetId}?size=s&v=abc", new Uri("http://localhost:61495"));

        Assert.Equal($"/engine-image/stream/artwork/{AssetId}?size=s&v=abc", url);
    }

    private static DefaultHttpContext NewContext(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.QueryString = new QueryString(query);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class StubEngine(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpClient StubClient(HttpMessageHandler handler) => new(handler, disposeHandler: false)
    {
        BaseAddress = new Uri("http://localhost:61495"),
    };
}
