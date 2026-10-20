using System.Net;
using System.Text;
using System.Text.Json;
using MediaEngine.Contracts.Library;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The three Engine actions behind moving a title (details overlay spec D12 to D17): the places it can go, the Match step's
/// preview, and the confirmed move. Each refusal is told apart so the Match step can word it: not allowed (403), gone (404),
/// not accepted (400), a stale preview (409 with the fresh one), an expired review (409 without it) and the Engine unreachable.
/// </summary>
public sealed class EngineApiClientLibraryMoveTests
{
    private static readonly Guid ItemId = LibraryMoveTestData.SongId;

    private static (EngineApiClient Api, StubHandler Handler, HttpClient Http) Client(HttpStatusCode status, string body = "{}", string contentType = "application/json")
    {
        var handler = new StubHandler(status, body, contentType);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") };
        return (new EngineApiClient(http, NullLogger<EngineApiClient>.Instance), handler, http);
    }

    private static string PreviewJson(string token) => JsonSerializer.Serialize(LibraryMoveTestData.AlbumPreview(token: token));

    [Fact]
    public async Task Destinations_AreRequestedForTheItemWithTheSearchText()
    {
        var response = JsonSerializer.Serialize(new LibraryMoveDestinationsResponse { Destinations = [LibraryMoveTestData.Album()] });
        var (api, handler, http) = Client(HttpStatusCode.OK, response);
        using var _ = http;
        using var __ = api;

        var outcome = await api.GetLibraryMoveDestinationsAsync(ItemId, "ok comp");

        Assert.True(outcome.IsOk);
        Assert.Equal("OK Computer", Assert.Single(outcome.Value!.Destinations).Label);
        Assert.Equal($"/library/items/{ItemId:D}/move-destinations?query=ok%20comp", handler.LastRequestUri);
        Assert.Equal(HttpMethod.Get, handler.LastMethod);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, LibraryMoveOutcomeStatus.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, LibraryMoveOutcomeStatus.NotFound)]
    [InlineData(HttpStatusCode.BadRequest, LibraryMoveOutcomeStatus.Invalid)]
    [InlineData(HttpStatusCode.InternalServerError, LibraryMoveOutcomeStatus.Unavailable)]
    public async Task Destinations_TellEachRefusalApart(HttpStatusCode status, LibraryMoveOutcomeStatus expected)
    {
        var (api, _, http) = Client(status, """{"detail":"Plain words."}""", "application/problem+json");
        using var _ = http;
        using var __ = api;

        var outcome = await api.GetLibraryMoveDestinationsAsync(ItemId, null);

        Assert.False(outcome.IsOk);
        Assert.Equal(expected, outcome.Status);
    }

    [Fact]
    public async Task Preview_PostsTheItemsAndTheDestinationAndReadsTheMatchStep()
    {
        var (api, handler, http) = Client(HttpStatusCode.OK, PreviewJson("token-9"));
        using var _ = http;
        using var __ = api;

        var outcome = await api.PreviewLibraryMoveAsync(new LibraryMovePreviewRequest { ItemIds = [ItemId], DestinationId = LibraryMoveTestData.AlbumId });

        Assert.True(outcome.IsOk);
        Assert.Equal("token-9", outcome.Value!.PreviewToken);
        Assert.Equal("/library/items/move-preview", handler.LastRequestUri);
        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(LibraryMoveTestData.AlbumId.ToString(), body.RootElement.GetProperty("destination_id").GetString());
        Assert.Equal(ItemId.ToString(), body.RootElement.GetProperty("item_ids")[0].GetString());
    }

    [Fact]
    public async Task Commit_SendsThePreviewTokenAndEachChoice()
    {
        var (api, handler, http) = Client(HttpStatusCode.OK, """{"moved_count":1,"rows":[{"item_id":"aaaaaaaa-0000-0000-0000-000000000001","outcome":"matched"}],"removed_containers":["The Best Of"]}""");
        using var _ = http;
        using var __ = api;

        var outcome = await api.MoveLibraryItemsAsync(new LibraryMoveCommitRequest
        {
            PreviewToken = "token-1",
            Rows = [new LibraryMoveCommitRow { ItemId = ItemId, Mode = LibraryMoveModes.Match, EntryKey = "t2" }],
        });

        Assert.True(outcome.IsOk);
        Assert.Equal(1, outcome.Value!.MovedCount);
        Assert.Equal(["The Best Of"], outcome.Value.RemovedContainers);
        Assert.Equal("/library/items/move", handler.LastRequestUri);
        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("token-1", body.RootElement.GetProperty("preview_token").GetString());
        Assert.Equal("t2", body.RootElement.GetProperty("rows")[0].GetProperty("entry_key").GetString());
        Assert.Equal("match", body.RootElement.GetProperty("rows")[0].GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Commit_AStalePreviewIsAConflictThatCarriesTheFreshOne()
    {
        var conflict = JsonSerializer.Serialize(new LibraryMoveConflictDto
        {
            Message = "Something changed since you opened this.",
            Preview = LibraryMoveTestData.AlbumPreview(token: "token-2"),
        });
        var (api, _, http) = Client(HttpStatusCode.Conflict, conflict);
        using var _ = http;
        using var __ = api;

        var outcome = await api.MoveLibraryItemsAsync(new LibraryMoveCommitRequest { PreviewToken = "token-1" });

        Assert.Equal(LibraryMoveOutcomeStatus.Conflict, outcome.Status);
        Assert.Equal("token-2", outcome.FreshPreview!.PreviewToken);
        Assert.Equal("Something changed since you opened this.", outcome.Message);
    }

    [Fact]
    public async Task Commit_AnExpiredReviewIsAConflictWithoutAFreshPreview()
    {
        var (api, _, http) = Client(HttpStatusCode.Conflict, """{"detail":"This review expired. Review the move again."}""", "application/problem+json");
        using var _ = http;
        using var __ = api;

        var outcome = await api.MoveLibraryItemsAsync(new LibraryMoveCommitRequest { PreviewToken = "token-1" });

        Assert.Equal(LibraryMoveOutcomeStatus.Expired, outcome.Status);
        Assert.Null(outcome.FreshPreview);
        Assert.Equal("This review expired. Review the move again.", outcome.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, LibraryMoveOutcomeStatus.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, LibraryMoveOutcomeStatus.NotFound)]
    [InlineData(HttpStatusCode.BadRequest, LibraryMoveOutcomeStatus.Invalid)]
    [InlineData(HttpStatusCode.BadGateway, LibraryMoveOutcomeStatus.Unavailable)]
    public async Task Commit_TellsEachRefusalApart(HttpStatusCode status, LibraryMoveOutcomeStatus expected)
    {
        var (api, _, http) = Client(status, """{"detail":"Plain words."}""", "application/problem+json");
        using var _ = http;
        using var __ = api;

        var outcome = await api.MoveLibraryItemsAsync(new LibraryMoveCommitRequest { PreviewToken = "token-1" });

        Assert.Equal(expected, outcome.Status);
        Assert.Null(outcome.Value);
    }

    [Fact]
    public async Task AnEngineThatCannotBeReached_IsUnavailableNotAnException()
    {
        var handler = new ThrowingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") };
        using var api = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var outcome = await api.PreviewLibraryMoveAsync(new LibraryMovePreviewRequest { ItemIds = [ItemId], DestinationId = LibraryMoveTestData.AlbumId });

        Assert.Equal(LibraryMoveOutcomeStatus.Unavailable, outcome.Status);
    }

    private sealed class StubHandler(HttpStatusCode status, string body, string contentType) : HttpMessageHandler
    {
        public string? LastRequestUri { get; private set; }
        public string? LastBody { get; private set; }
        public HttpMethod? LastMethod { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri!.PathAndQuery;
            LastMethod = request.Method;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) };
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("The Engine is not running.");
    }
}
