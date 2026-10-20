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

        var page = await client.GetDisplayGridAsync("watch", "recently-added", null, "movie", " ", null, "in-progress", 48, 48);

        Assert.Equal("Recently added", page!.Title);
        Assert.Equal("/api/v1/display/grids/watch/recently-added", captured!.RequestUri!.AbsolutePath);
        Assert.Equal("?type=movie&status=in-progress&offset=48&limit=48", captured.RequestUri.Query);
    }

    [Fact]
    public async Task GetDisplayGridAsync_RecordsA404SoThePageCanTellAMissingGridFromAFailedRead()
    {
        var client = ClientReplying(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        Assert.Null(await client.GetDisplayGridAsync("watch", "nope", null, null, null, null, null, 0, 48));

        Assert.Equal(404, client.LastStatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, 400)]
    [InlineData(HttpStatusCode.InternalServerError, 500)]
    public async Task GetDisplayGridAsync_RecordsOtherStatusesAsFailuresThatAreNotA404(HttpStatusCode status, int expected)
    {
        var client = ClientReplying(_ => new HttpResponseMessage(status));

        Assert.Null(await client.GetDisplayGridAsync("watch", "recently-added", null, null, null, null, null, 0, 48));

        Assert.Equal(expected, client.LastStatusCode);
    }

    [Fact]
    public async Task GetDisplayGridAsync_LeavesNoStatusWhenTheEngineCannotBeReached()
    {
        var client = ClientReplying(_ => throw new HttpRequestException("connection refused"));

        Assert.Null(await client.GetDisplayGridAsync("watch", "recently-added", null, null, null, null, null, 0, 48));

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
