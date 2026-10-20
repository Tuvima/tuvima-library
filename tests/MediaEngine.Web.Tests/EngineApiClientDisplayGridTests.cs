using System.Net;
using System.Net.Http;
using System.Text;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The View all read tells the page why it failed (a missing grid is a 404, anything else is a failed read) and sends only the
/// choices that were made; the remembered-sort write reports whether it was saved.
/// </summary>
public sealed class EngineApiClientDisplayGridTests
{
    private const string GridJson = """
        { "lane": "watch", "shelfKey": "recently-added", "title": "Recently added", "items": [], "totalCount": 0,
          "offset": 0, "limit": 48, "hasMore": false, "sort": "added", "defaultSort": "added",
          "sortOptions": [], "filters": [] }
        """;

    [Fact]
    public async Task GetDisplayGridAsync_SendsOnlyTheChoicesThatWereMade()
    {
        HttpRequestMessage? captured = null;
        var client = ClientReplying(request =>
        {
            captured = request;
            return Ok(GridJson);
        });

        var result = await client.GetDisplayGridAsync("watch", "recently-added", null, "movie", " ", null, "in-progress", 48, 48);

        Assert.Equal(DisplayGridLoadFailure.None, result.Failure);
        Assert.Equal("Recently added", result.Page!.Title);
        Assert.Equal("/api/v1/display/grids/watch/recently-added", captured!.RequestUri!.AbsolutePath);
        Assert.Equal("?type=movie&status=in-progress&offset=48&limit=48", captured.RequestUri.Query);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, DisplayGridLoadFailure.NotFound, 404)]
    [InlineData(HttpStatusCode.BadRequest, DisplayGridLoadFailure.BadRequest, 400)]
    [InlineData(HttpStatusCode.InternalServerError, DisplayGridLoadFailure.Failed, 500)]
    public async Task GetDisplayGridAsync_TellsTheCallerWhyThereIsNoPageFromThisCallsOwnAnswer(
        HttpStatusCode status, DisplayGridLoadFailure expected, int recorded)
    {
        var client = ClientReplying(_ => new HttpResponseMessage(status));

        var result = await client.GetDisplayGridAsync("watch", "recently-added", null, null, null, null, null, 0, 48);

        Assert.Null(result.Page);
        Assert.Equal(expected, result.Failure);
        Assert.Equal(recorded, client.LastStatusCode);
    }

    [Fact]
    public async Task GetDisplayGridAsync_AStale404IsNotMistakenForALaterTimeout()
    {
        var calls = 0;
        var client = ClientReplying(_ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : throw new TaskCanceledException("timed out"));

        var missing = await client.GetDisplayGridAsync("watch", "nope", null, null, null, null, null, 0, 48);
        var timedOut = await client.GetDisplayGridAsync("watch", "recently-added", null, null, null, null, null, 0, 48);

        Assert.Equal(DisplayGridLoadFailure.NotFound, missing.Failure);
        Assert.Equal(DisplayGridLoadFailure.Failed, timedOut.Failure);
    }

    [Fact]
    public async Task GetDisplayGridAsync_ReportsAFailureWhenTheEngineCannotBeReached()
    {
        var client = ClientReplying(_ => throw new HttpRequestException("connection refused"));

        var result = await client.GetDisplayGridAsync("watch", "recently-added", null, null, null, null, null, 0, 48);

        Assert.Equal(DisplayGridLoadFailure.Failed, result.Failure);
        Assert.Null(client.LastStatusCode);
    }

    [Fact]
    public async Task SetDisplayGridSortAsync_PutsTheSortOnTheGridKeyAndReportsSuccess()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var client = ClientReplying(request =>
        {
            captured = request;
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        Assert.True(await client.SetDisplayGridSortAsync("my-list", "title"));

        Assert.Equal(HttpMethod.Put, captured!.Method);
        Assert.Equal("/api/v1/display/grid-preferences/my-list", captured.RequestUri!.AbsolutePath);
        Assert.Contains("\"sort\":\"title\"", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetDisplayGridSortAsync_ReportsFalseWhenTheEngineRefuses()
    {
        var client = ClientReplying(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));

        Assert.False(await client.SetDisplayGridSortAsync("watch", "title"));
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static EngineApiClient ClientReplying(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new HttpClient(new StubHandler(responder)) { BaseAddress = new Uri("http://localhost:61495") },
            NullLogger<EngineApiClient>.Instance);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(request));
        }
    }
}
