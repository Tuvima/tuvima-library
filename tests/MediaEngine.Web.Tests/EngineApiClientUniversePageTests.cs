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
    public async Task GetUniversePageAsync_KeepsTheContents_AndMakesTheirPicturesAbsolute()
    {
        const string json = """
            {
              "detail": { "qid": "Q4242", "name": "Dune", "group_type": "universe", "item_count": 1, "series_count": 1, "media_types": [], "shelves": [] },
              "connections": { "characters": [], "character_count": 0, "cast": [], "adaptations": [] },
              "missing": [],
              "enrichment": { "state": "ready", "enriched_work_count": 1, "work_count": 1 },
              "contents": {
                "sections": [ { "key": "factions", "label": "Factions", "count": 1, "tiles": [ { "id": "11111111-1111-1111-1111-111111111111", "label": "House Test", "picture_url": "/stream/artwork/a?size=m", "logo_url": "/stream/artwork/b?size=m" } ] } ],
                "timeline": { "default_order": "release", "stops": [ { "id": "22222222-2222-2222-2222-222222222222", "label": "The Battle", "kind": "events", "picture_url": "/stream/artwork/c?size=m" } ] },
                "hidden_count": 2
              }
            }
            """;
        var client = ClientReplying(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

        var contents = (await client.GetUniversePageAsync("Q4242")).Page!.Contents!;

        var tile = contents.Sections.Single().Tiles.Single();
        Assert.Equal("http://localhost:61495/stream/artwork/a?size=m", tile.PictureUrl);
        Assert.Equal("http://localhost:61495/stream/artwork/b?size=m", tile.LogoUrl);
        Assert.Equal("http://localhost:61495/stream/artwork/c?size=m", contents.Timeline!.Stops.Single().PictureUrl);
        Assert.Equal(2, contents.HiddenCount);
    }

    [Fact]
    public async Task GetUniverseThingAsync_ReadsOneThing_ReportsNotFound_AndAnythingElseAsUnavailable()
    {
        const string json = """
            { "id": "11111111-1111-1111-1111-111111111111", "universe_qid": "Q4242", "label": "House Test", "kind_key": "factions", "kind_label": "Faction",
              "picture_url": "/stream/artwork/a?size=m", "appears_in": [], "related": [] }
            """;
        var found = ClientReplying(request =>
        {
            Assert.Equal("/universe/Q4242/things/11111111-1111-1111-1111-111111111111", request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        });

        var result = await found.GetUniverseThingAsync("Q4242", Guid.Parse("11111111-1111-1111-1111-111111111111"));

        Assert.Equal(UniverseThingLoadStatus.Loaded, result.Status);
        Assert.Equal("http://localhost:61495/stream/artwork/a?size=m", result.Thing!.PictureUrl);
        var missing = ClientReplying(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Equal(UniverseThingLoadStatus.NotFound, (await missing.GetUniverseThingAsync("Q4242", Guid.NewGuid())).Status);
        var broken = ClientReplying(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        Assert.Equal(UniverseThingLoadStatus.Unavailable, (await broken.GetUniverseThingAsync("Q4242", Guid.NewGuid())).Status);
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
