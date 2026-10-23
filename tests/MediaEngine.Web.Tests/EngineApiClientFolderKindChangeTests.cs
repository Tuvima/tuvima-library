using System.Net;
using System.Net.Http;
using System.Text;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>The Dashboard's calls for "Change what this folder is" (Library setup D40(c)): they tell a refusal the Engine explained from a failure to ask.</summary>
public sealed class EngineApiClientFolderKindChangeTests
{
    [Fact]
    public async Task APreview_PostsTheKindAndLibraryToTheFolder_AndReturnsTheEnginesAnswer()
    {
        HttpRequestMessage? sent = null;
        string? body = null;
        using var http = CreateHttpClient(request =>
        {
            sent = request;
            body = request.Content!.ReadAsStringAsync().Result;
            return Json(HttpStatusCode.OK, """{"source_id":"s","file_count":12,"moves":12,"drops_details":true,"visibility_loss_checked":true,"visibility_loss":[{"account_id":"11111111-1111-1111-1111-111111111111","display_name":"Sam","file_count":12}]}""");
        });
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var result = await client.PreviewFolderKindChangeAsync("source-1", new FolderKindChangeRequest { KindKey = "other_videos", LibraryId = "library-1" });

        Assert.Equal(HttpMethod.Post, sent!.Method);
        Assert.Equal("/settings/libraries/folders/source-1/change-kind/preview", sent.RequestUri!.AbsolutePath);
        Assert.Equal("""{"kind_key":"other_videos","library_id":"library-1"}""", body);
        Assert.True(result.Succeeded);
        Assert.Equal((12, true), (result.Value!.FileCount, result.Value.DropsDetails));
        Assert.Equal("Sam", Assert.Single(result.Value.VisibilityLoss).DisplayName);
    }

    [Fact]
    public async Task AStart_PostsToTheFolder_AndReturnsTheJob()
    {
        HttpRequestMessage? sent = null;
        using var http = CreateHttpClient(request =>
        {
            sent = request;
            return Json(HttpStatusCode.OK, """{"operation_id":"op-1","source_id":"source-1","job_type":"change_folder_kind","state":"queued"}""");
        });
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var result = await client.ChangeFolderKindAsync("source-1", new FolderKindChangeRequest { KindKey = "youtube" });

        Assert.Equal("/settings/libraries/folders/source-1/change-kind", sent!.RequestUri!.AbsolutePath);
        Assert.True(result.Succeeded);
        Assert.Equal(("op-1", "change_folder_kind"), (result.Value!.OperationId, result.Value.JobType));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task ARefusalTheEngineExplained_IsARefusalWithItsSentence_NotAFailure(HttpStatusCode status)
    {
        using var http = CreateHttpClient(_ => Json(status, """{"title":"Bad Request","detail":"This folder already is Movies."}"""));
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var preview = await client.PreviewFolderKindChangeAsync("source-1", new FolderKindChangeRequest { KindKey = "movies" });
        var start = await client.ChangeFolderKindAsync("source-1", new FolderKindChangeRequest { KindKey = "movies" });

        foreach (var result in new[] { preview.Refusal, start.Refusal })
        {
            Assert.Equal("This folder already is Movies.", result);
        }

        Assert.False(preview.Succeeded);
        Assert.Null(preview.Failure);
        Assert.Null(start.Failure);
    }

    [Fact]
    public async Task AnEngineThatCannotBeReachedOrFails_IsAFailureNeverARefusal()
    {
        using var failing = CreateHttpClient(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var unreachable = new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("http://localhost:61495") };

        var serverError = await new EngineApiClient(failing, NullLogger<EngineApiClient>.Instance)
            .PreviewFolderKindChangeAsync("source-1", new FolderKindChangeRequest { KindKey = "movies" });
        var noEngine = await new EngineApiClient(unreachable, NullLogger<EngineApiClient>.Instance)
            .ChangeFolderKindAsync("source-1", new FolderKindChangeRequest { KindKey = "movies" });

        foreach (var outcome in new[] { (serverError.Refusal, serverError.Failure, serverError.Succeeded), (noEngine.Refusal, noEngine.Failure, noEngine.Succeeded) })
        {
            Assert.Null(outcome.Refusal);
            Assert.False(string.IsNullOrWhiteSpace(outcome.Failure));
            Assert.False(outcome.Succeeded);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpClient CreateHttpClient(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new StubHandler(responder)) { BaseAddress = new Uri("http://localhost:61495") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("No connection could be made.");
    }
}
