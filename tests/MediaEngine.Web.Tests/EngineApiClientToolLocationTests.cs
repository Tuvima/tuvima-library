using System.Net;
using System.Text;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>Saving a tool's location passes the Engine's plain refusal on to the person, and never throws.</summary>
public sealed class EngineApiClientToolLocationTests
{
    private static readonly ToolLocationRequest Request = new("shared", "ffmpeg/bin", null);

    [Fact]
    public async Task ASavedLocation_SendsTheFolderAsSnakeCaseJson_AndReturnsTheFreshToolsCheck()
    {
        HttpRequestMessage? seen = null;
        string? body = null;
        using var http = CreateClient(message =>
        {
            seen = message;
            body = message.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(HttpStatusCode.OK, """{"install_kind":"source","checked_at":"2026-10-18T10:00:00Z","needs_attention":false,"tools":[]}""");
        });
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var result = await client.SetToolLocationAsync("ffmpeg", Request);

        Assert.True(result.Success);
        Assert.NotNull(result.Tools);
        Assert.Equal(HttpMethod.Put, seen!.Method);
        Assert.Equal("/settings/tools/ffmpeg/location", seen.RequestUri!.AbsolutePath);
        Assert.Contains("\"storage_location_id\":\"shared\"", body, StringComparison.Ordinal);
        Assert.Contains("\"relative_path\":\"ffmpeg/bin\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedFolder_ReturnsTheEnginesPlainSentenceWithoutStatusCodesOrTraceIds()
    {
        using var http = CreateClient(_ => Json(
            HttpStatusCode.BadRequest,
            """{"title":"Invalid request.","detail":"That folder doesn't contain FFmpeg.","status":400,"traceId":"00-abc"}"""));
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var result = await client.SetToolLocationAsync("ffmpeg", Request);

        Assert.False(result.Success);
        Assert.Null(result.Tools);
        Assert.Equal("That folder doesn't contain FFmpeg.", result.ErrorMessage);
    }

    [Fact]
    public async Task AnEngineThatCannotBeReached_ReturnsAPlainMessageInsteadOfThrowing()
    {
        using var http = new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("http://localhost:61495/") };
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var result = await client.SetToolLocationAsync("tesseract", Request);

        Assert.False(result.Success);
        Assert.Equal("The Library could not save that location. Check that the server is online and try again.", result.ErrorMessage);
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
