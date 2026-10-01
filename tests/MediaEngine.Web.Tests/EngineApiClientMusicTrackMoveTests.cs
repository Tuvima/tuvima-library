using System.Net;
using System.Text;
using System.Text.Json;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class EngineApiClientMusicTrackMoveTests
{
    [Fact]
    public async Task PreviewPostsAssetAndExactReleaseAndReadsTrackChoices()
    {
        var routeId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var releaseId = Guid.NewGuid().ToString("D");
        var trackId = Guid.NewGuid().ToString("D");
        var handler = new CaptureHandler(HttpStatusCode.OK, $$"""
            {"reviewToken":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","expiresAt":"2026-10-01T12:00:00Z",
             "assetId":"{{assetId:D}}","releaseId":"{{releaseId}}","album":"Album","artist":"Artist",
             "tracks":[{"releaseTrackId":"{{trackId}}","recordingId":null,"title":"Track","discNumber":1,"trackNumber":2}]}
            """);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") };
        using var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var result = await client.PreviewMusicTrackMoveAsync(routeId, new(assetId, releaseId));

        Assert.NotNull(result);
        Assert.Equal(trackId, Assert.Single(result.Tracks).ReleaseTrackId);
        Assert.Equal($"/metadata/{routeId:D}/music-track-move-preview", handler.Path);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(assetId, body.RootElement.GetProperty("assetId").GetGuid());
        Assert.Equal(releaseId, body.RootElement.GetProperty("releaseId").GetString());
    }

    [Fact]
    public async Task SavePostsReviewChoiceAndReadsCommitReceipt()
    {
        var routeId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var trackId = Guid.NewGuid().ToString("D");
        const string token = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var handler = new CaptureHandler(HttpStatusCode.OK,
            """{"outcome":"committed","rows":[]}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") };
        using var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var result = await client.SaveMusicTrackMoveAsync(routeId, new(token, trackId, operationId));

        Assert.NotNull(result);
        Assert.Equal("committed", result.Outcome);
        Assert.Equal($"/metadata/{routeId:D}/music-track-move", handler.Path);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(token, body.RootElement.GetProperty("reviewToken").GetString());
        Assert.Equal(trackId, body.RootElement.GetProperty("releaseTrackId").GetString());
        Assert.Equal(operationId, body.RootElement.GetProperty("operationId").GetGuid());
    }

    private sealed class CaptureHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") };
        }
    }
}
