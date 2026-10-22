using System.Net;
using System.Text;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>The file write schedule calls: the right addresses and bodies, and Write now refusals come back as plain sentences, never as errors.</summary>
public sealed class EngineApiClientFileWriteTests
{
    [Fact]
    public async Task SavingTheInterval_PutsTheMinutesAsSnakeCaseJsonAndReturnsTheFreshStatus()
    {
        HttpRequestMessage? seen = null;
        string? body = null;
        using var http = CreateClient(message =>
        {
            seen = message;
            body = message.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(HttpStatusCode.OK, """{"enabled":true,"interval_minutes":30,"waiting_count":4}""");
        });
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var saved = await client.UpdateFileWriteScheduleAsync(30);

        Assert.Equal(30, saved!.IntervalMinutes);
        Assert.Equal(HttpMethod.Put, seen!.Method);
        Assert.Equal("/settings/writeback/schedule", seen.RequestUri!.AbsolutePath);
        Assert.Contains("\"interval_minutes\":30", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteNow_WhenTheEngineStartsARun_ReturnsTheStatus()
    {
        HttpRequestMessage? seen = null;
        using var http = CreateClient(message =>
        {
            seen = message;
            return Json(HttpStatusCode.Accepted, """{"enabled":true,"interval_minutes":60,"is_running":true,"run_total":12,"run_done":0}""");
        });
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var result = await client.StartFileWriteRunAsync();

        Assert.True(result.Started);
        Assert.True(result.Schedule!.IsRunning);
        Assert.Equal(HttpMethod.Post, seen!.Method);
        Assert.Equal("/settings/writeback/run", seen.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task WriteNow_WhenARunIsAlreadyGoing_ReturnsTheEnginesPlainSentence()
    {
        using var http = CreateClient(_ => Json(
            HttpStatusCode.Conflict,
            """{"title":"Conflict.","detail":"A write run is already going.","status":409,"traceId":"00-abc"}"""));
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var result = await client.StartFileWriteRunAsync();

        Assert.False(result.Started);
        Assert.Equal("A write run is already going.", result.Message);
    }

    [Fact]
    public async Task WriteNow_WhenThePersonIsNotAnAdministrator_SaysSoInPlainWords()
    {
        using var http = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var result = await client.StartFileWriteRunAsync();

        Assert.False(result.Started);
        Assert.Equal("Only an administrator can write changes into files now.", result.Message);
    }

    [Fact]
    public async Task WriteNow_WhenTheEngineCannotBeReached_ReturnsAPlainMessageInsteadOfThrowing()
    {
        using var http = new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("http://localhost:61495/") };
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var result = await client.StartFileWriteRunAsync();

        Assert.False(result.Started);
        Assert.Equal("The Library could not start writing. Check that the server is online and try again.", result.Message);
    }

    [Fact]
    public async Task TheFailedFilesAndTheFileStatus_ReadFromTheirOwnAddresses()
    {
        var seen = new List<string>();
        using var http = CreateClient(message =>
        {
            seen.Add(message.RequestUri!.AbsolutePath);
            return message.RequestUri.AbsolutePath.EndsWith("/failed", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, """[{"target_kind":"media_asset","title":"Dune","path":"E:\\Dune.epub","reason":"Read-only.","attempts":3,"updated_at":"2026-10-22T10:00:00Z"}]""")
                : Json(HttpStatusCode.OK, """{"waiting":true,"next_run_at":"2026-10-22T11:00:00Z"}""");
        });
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);
        var assetId = Guid.NewGuid();

        var failed = await client.GetFailedFileWritesAsync();
        var status = await client.GetAssetFileWriteStatusAsync(assetId);

        Assert.Equal("Read-only.", Assert.Single(failed).Reason);
        Assert.True(status!.Waiting);
        Assert.Equal(["/settings/writeback/failed", $"/metadata/assets/{assetId}/file-write"], seen);
    }

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new StubHandler(responder)) { BaseAddress = new Uri("http://localhost:61495/") };

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }
}
