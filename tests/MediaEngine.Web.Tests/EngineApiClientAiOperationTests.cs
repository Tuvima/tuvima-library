using System.Net;
using System.Text;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class EngineApiClientAiOperationTests
{
    [Fact]
    public async Task AiConfig_ExposesResourceProfileWithoutRawModelCatalogue()
    {
        using var http = CreateHttpClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "minimum_free_disk_mb": 4096,
                  "resource_profile": "standard",
                  "audio_pack_enabled": false,
                  "features": {},
                  "vibe_vocabulary": {},
                  "scheduling": {}
                }
                """, Encoding.UTF8, "application/json"),
        });
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var config = await client.GetAiConfigAsync();

        Assert.NotNull(config);
        Assert.Equal(4096, config.MinimumFreeDiskMB);
        Assert.Equal("standard", config.ResourceProfile);
        Assert.False(config.AudioPackEnabled);
    }

    [Fact]
    public async Task ResourceSnapshot_PreservesFractionalCpuPressure()
    {
        using var http = CreateHttpClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "total_ram_mb": 32000,
                  "free_ram_mb": 16000,
                  "engine_ram_mb": 2048,
                  "cpu_pressure": 0.73,
                  "transcoding_active": false
                }
                """, Encoding.UTF8, "application/json"),
        });
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var snapshot = await client.GetResourceSnapshotAsync();

        Assert.NotNull(snapshot);
        Assert.Equal(0.73, snapshot.CpuPressure);
    }

    [Fact]
    public async Task ModelCommand_ParsesSafeProblemDetailsAndBlockingReasons()
    {
        using var http = CreateHttpClient(_ => Problem(HttpStatusCode.UnprocessableEntity, """
            {
              "type": "https://tuvima.local/problems/ai/runtime-not-ready",
              "title": "AI runtime is unavailable",
              "status": 422,
              "detail": "Install the compatible local runtime before loading this model.",
              "blockingReasons": ["Runtime adapter is not registered.", "Validation has not passed."]
            }
            """));
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var result = await client.LoadAiModelAsync("text_fast");

        Assert.False(result.Succeeded);
        Assert.Equal(422, result.Problem!.Status);
        Assert.Equal("AI runtime is unavailable", result.Problem.Title);
        Assert.Equal(2, result.Problem.BlockingReasons.Count);
        Assert.Contains("Runtime adapter is not registered", result.Problem.ToUserMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelCommand_DoesNotExposeTransportExceptionOrUnreadableResponseBody()
    {
        using var throwingHttp = new HttpClient(new ThrowingHandler("SECRET connection internals"))
        {
            BaseAddress = new Uri("http://localhost:61495/"),
        };
        var throwingClient = new EngineApiClient(throwingHttp, NullLogger<EngineApiClient>.Instance);

        var transportResult = await throwingClient.StartAiModelDownloadAsync("text_fast");

        Assert.False(transportResult.Succeeded);
        Assert.DoesNotContain("SECRET", transportResult.Problem!.ToUserMessage(), StringComparison.Ordinal);

        using var unreadableHttp = CreateHttpClient(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("SECRET raw server exception", Encoding.UTF8, "text/plain"),
        });
        var unreadableClient = new EngineApiClient(unreadableHttp, NullLogger<EngineApiClient>.Instance);

        var unreadableResult = await unreadableClient.UnloadAiModelAsync("text_fast");

        Assert.DoesNotContain("SECRET", unreadableResult.Problem!.ToUserMessage(), StringComparison.Ordinal);
        Assert.Contains("without readable problem details", unreadableResult.Problem.Detail, StringComparison.Ordinal);

        using var untrustedProblemHttp = CreateHttpClient(_ => Problem(HttpStatusCode.InternalServerError, """
            { "type": "about:blank", "title": "System.InvalidOperationException", "detail": "SECRET stack trace" }
            """));
        var untrustedProblemClient = new EngineApiClient(untrustedProblemHttp, NullLogger<EngineApiClient>.Instance);
        var untrustedResult = await untrustedProblemClient.LoadAiModelAsync("text_fast");

        Assert.DoesNotContain("SECRET", untrustedResult.Problem!.ToUserMessage(), StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", untrustedResult.Problem.ToUserMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HardwareBenchmark_ReturnsTypedProblemInsteadOfNull()
    {
        using var http = CreateHttpClient(_ => Problem(HttpStatusCode.ServiceUnavailable, """
            { "type": "https://tuvima.local/problems/ai/benchmark-unavailable", "title": "Benchmark unavailable", "status": 503, "detail": "The Engine is under resource pressure." }
            """));
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var result = await client.RunBenchmarkAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("Benchmark unavailable", result.Problem!.Title);
        Assert.Equal(503, result.Problem.Status);
    }

    private static HttpResponseMessage Problem(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/problem+json"),
    };

    private static HttpClient CreateHttpClient(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new StubHttpMessageHandler(responder)) { BaseAddress = new Uri("http://localhost:61495/") };

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class ThrowingHandler(string message) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException(message);
    }
}
