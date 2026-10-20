using System.Net;
using System.Net.Http;
using System.Text;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Universe page read tells the page why it failed, so "this Universe isn't available" and "can't reach the Engine"
/// are never confused with each other.
/// </summary>
public sealed class EngineApiClientUniversePageTests
{
    [Fact]
    public async Task GetUniversePageAsync_ReturnsThePageWithAbsoluteArtworkAddresses()
    {
        const string json = """
            {
              "detail": {
                "qid": "Q4242", "name": "Dune", "group_type": "universe", "item_count": 1, "series_count": 1, "media_types": ["Book"],
                "shelves": [], "cast": [], "adaptations": []
              },
              "connections": {
                "characters": [ { "qid": "Q1", "label": "Paul", "work_count": 2, "no_plot_details": false, "sources": [ { "kind": "wikidata", "label": "Wikidata" } ] } ],
                "character_count": 5,
                "cast": [ { "member": { "person_id": "11111111-1111-1111-1111-111111111111", "name": "Ann", "headshot_url": "/stream/head", "characters": [], "work_count": 1 }, "sources": [ { "kind": "wikidata", "label": "Wikidata" } ] } ],
                "adaptations": []
              },
              "standalone": null,
              "missing": [],
              "enrichment": { "state": "running", "enriched_work_count": 1, "work_count": 3 },
              "graph_entity_count": 4,
              "graph_relationship_count": 2
            }
            """;
        var client = ClientReplying(request =>
        {
            Assert.Equal("/universe/Q4242/overview", request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        });

        var result = await client.GetUniversePageAsync("Q4242");

        Assert.Equal(UniversePageLoadStatus.Loaded, result.Status);
        Assert.Equal("Dune", result.Page!.Detail.Name);
        Assert.Equal("http://localhost:61495/stream/head", result.Page.Connections.Cast.Single().Member.HeadshotUrl);
        Assert.Equal("Wikidata", result.Page.Connections.Cast.Single().Sources.Single().Label);
        Assert.Equal("Paul", result.Page.Connections.Characters.Single().Label);
        Assert.Equal(5, result.Page.Connections.CharacterCount);
        Assert.Equal("running", result.Page.Enrichment.State);
        Assert.Equal(4, result.Page.GraphEntityCount);
    }

    [Fact]
    public async Task GetUniversePageAsync_ReportsNotFoundWhenTheEngineHasNoSuchUniverseForThisProfile()
    {
        var client = ClientReplying(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await client.GetUniversePageAsync("Q4242");

        Assert.Null(result.Page);
        Assert.Equal(UniversePageLoadStatus.NotFound, result.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task GetUniversePageAsync_ReportsUnavailableForAnyOtherAnswer_AndRecordsWhy(HttpStatusCode status)
    {
        var client = ClientReplying(_ => new HttpResponseMessage(status));

        var result = await client.GetUniversePageAsync("Q4242");

        Assert.Null(result.Page);
        Assert.Equal(UniversePageLoadStatus.Unavailable, result.Status);
        Assert.NotNull(client.LastError);
    }

    [Fact]
    public async Task GetUniversePageAsync_ReportsUnavailableWhenTheConnectionFails()
    {
        var client = ClientReplying(_ => throw new HttpRequestException("connection refused"));

        var result = await client.GetUniversePageAsync("Q4242");

        Assert.Null(result.Page);
        Assert.Equal(UniversePageLoadStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task GetUniversePageAsync_ReportsUnavailableWhenTheRequestTimesOutOrIsCancelled()
    {
        var timedOut = ClientReplying(_ => throw new TaskCanceledException("timed out"));
        Assert.Equal(UniversePageLoadStatus.Unavailable, (await timedOut.GetUniversePageAsync("Q4242")).Status);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var cancelled = ClientReplying(_ => new HttpResponseMessage(HttpStatusCode.OK));
        Assert.Equal(UniversePageLoadStatus.Unavailable, (await cancelled.GetUniversePageAsync("Q4242", cts.Token)).Status);
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
