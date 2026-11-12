using System.Net;
using System.Text;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Lyrics panel asks the Engine whether a lookup of this song is still queued or running (spec music-player-and-listen-fixes D3): an answer is
/// read, "nothing pending" (204) and any failure both come back as null so the panel never invents a lookup.
/// </summary>
public sealed class EngineApiClientPendingTextTrackTests
{
    private static readonly Guid AssetId = Guid.Parse("3c444922-ffb9-4b2d-9bbc-168fab04bf0b");

    [Fact]
    public async Task APendingLookup_IsReadFromTheKindQuery()
    {
        var client = ClientReplying(request =>
        {
            Assert.Equal($"/stream/{AssetId:D}/text-tracks/refresh", request.RequestUri!.AbsolutePath);
            Assert.Equal("?kind=lyrics", request.RequestUri.Query);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"operation_id":"97cc8282-8766-4d4b-95ee-59f45656b0cb","asset_id":"3c444922-ffb9-4b2d-9bbc-168fab04bf0b","enrichment_type":"TimedLyrics","status":"queued"}""", Encoding.UTF8, "application/json"),
            };
        });

        var pending = await client.GetPendingTextTrackRefreshAsync(AssetId, "lyrics");

        Assert.Equal("queued", pending!.status);
        Assert.Equal(Guid.Parse("97cc8282-8766-4d4b-95ee-59f45656b0cb"), pending.operation_id);
    }

    [Fact]
    public async Task NothingPending_IsNull()
    {
        var client = ClientReplying(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        Assert.Null(await client.GetPendingTextTrackRefreshAsync(AssetId, "lyrics"));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task AFailedRead_IsNull(HttpStatusCode status)
    {
        var client = ClientReplying(_ => new HttpResponseMessage(status));

        Assert.Null(await client.GetPendingTextTrackRefreshAsync(AssetId, "lyrics"));
    }

    [Fact]
    public async Task AnUnreachableEngine_AndAnUnreadableAnswer_AreNull()
    {
        Assert.Null(await ClientReplying(_ => throw new HttpRequestException("connection refused")).GetPendingTextTrackRefreshAsync(AssetId, "lyrics"));
        Assert.Null(await ClientReplying(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json") }).GetPendingTextTrackRefreshAsync(AssetId, "lyrics"));
    }

    private static EngineApiClient ClientReplying(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new HttpClient(new StubHandler(responder)) { BaseAddress = new Uri("http://localhost:61495") },
            NullLogger<EngineApiClient>.Instance);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
