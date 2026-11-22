using System.Net;
using System.Text;
using System.Text.Json;
using MediaEngine.Contracts.Collections;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>The Dashboard's side of the playlist actions: position moves instead of a whole list, add counts, Duplicate and Pin.</summary>
public sealed class PlaylistActionsClientTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private static EngineApiClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new Handler(respond)) { BaseAddress = new Uri("http://localhost:61495") }, NullLogger<EngineApiClient>.Instance);

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body, Web), Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task ARefusedPlaylistCreate_KeepsTheEnginesReasonAndStatus_SoTheScreenCanSayWhy()
    {
        using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(
                "{\"title\":\"Access denied.\",\"status\":403,\"detail\":\"The requested work is outside the caller's available libraries.\"}",
                Encoding.UTF8,
                "application/json"),
        });

        var id = await client.CreateCollectionWithItemsAsync(
            "Mix", null, null, "Playlist", new MediaEngine.Web.Models.ViewDTOs.CollectionRuleDefinitionViewModel(), "title", "asc", "private", [Guid.NewGuid()]);

        Assert.Null(id);
        Assert.Equal(403, client.LastStatusCode);
        Assert.Equal("The requested work is outside the caller's available libraries.", EngineFailureText.FromClient(client));
    }

    [Fact]
    public async Task ReorderingAPlaylistByADrag_SendsOneMoveWithThePositionAndNeverTheWholeList()
    {
        var ids = Enumerable.Range(1, 6).Select(_ => Guid.NewGuid()).ToList();
        var target = ids.ToList();
        target.RemoveAt(4);
        target.Insert(1, ids[4]);
        var requests = new List<(HttpMethod Method, string Path, string Body)>();
        var playlist = Guid.NewGuid();
        using var client = Client(request =>
        {
            using var reader = new StreamReader(request.Content!.ReadAsStream());
            requests.Add((request.Method, request.RequestUri!.AbsolutePath, reader.ReadToEnd()));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var saved = await client.ApplyCollectionItemOrderAsync(playlist, ids, target);

        Assert.True(saved);
        var only = Assert.Single(requests);
        Assert.Equal(HttpMethod.Put, only.Method);
        Assert.Equal($"/collections/{playlist}/items/{ids[4]}/move", only.Path);
        Assert.Equal(2, JsonSerializer.Deserialize<CollectionItemMoveRequest>(only.Body, Web)!.Position);
        Assert.DoesNotContain("item_ids", only.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnchangedOrder_SendsNothing()
    {
        var ids = Enumerable.Range(1, 3).Select(_ => Guid.NewGuid()).ToList();
        var calls = 0;
        using var client = Client(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        Assert.True(await client.ApplyCollectionItemOrderAsync(Guid.NewGuid(), ids, ids));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task MoveToTopAndToBottom_SendTheirOwnFlagsSoThePlaylistSizeIsNotNeeded()
    {
        var bodies = new List<string>();
        using var client = Client(request =>
        {
            using var reader = new StreamReader(request.Content!.ReadAsStream());
            bodies.Add(reader.ReadToEnd());
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        Assert.True(await client.MoveCollectionItemAsync(Guid.NewGuid(), Guid.NewGuid(), new CollectionItemMoveRequest { ToTop = true }));
        Assert.True(await client.MoveCollectionItemAsync(Guid.NewGuid(), Guid.NewGuid(), new CollectionItemMoveRequest { ToBottom = true }));

        Assert.True(JsonSerializer.Deserialize<CollectionItemMoveRequest>(bodies[0], Web)!.ToTop);
        Assert.True(JsonSerializer.Deserialize<CollectionItemMoveRequest>(bodies[1], Web)!.ToBottom);
    }

    [Fact]
    public async Task AddingSeveralSongs_ReturnsHowManyWereAddedAndHowManyWereAlreadyThere()
    {
        using var client = Client(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            using var reader = new StreamReader(request.Content!.ReadAsStream());
            Assert.Contains("work_ids", reader.ReadToEnd(), StringComparison.Ordinal);
            return Json(new { added = 2, already_there = 1 });
        });

        var result = await client.AddCollectionItemsAsync(Guid.NewGuid(), [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()]);

        Assert.Equal(new CollectionItemsAddedResponse(2, 1), result);
    }

    [Fact]
    public async Task AFailedAdd_ReturnsNothingSoTheCallerCanSayItFailed()
    {
        using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        Assert.Null(await client.AddCollectionItemsAsync(Guid.NewGuid(), [Guid.NewGuid()]));
    }

    [Fact]
    public async Task PinningAPlaylist_SendsThePinChoiceToThePinnedRoute()
    {
        string? path = null;
        string? body = null;
        using var client = Client(request =>
        {
            path = request.RequestUri!.AbsolutePath;
            using var reader = new StreamReader(request.Content!.ReadAsStream());
            body = reader.ReadToEnd();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var playlist = Guid.NewGuid();

        Assert.True(await client.SetCollectionPinnedAsync(playlist, true));

        Assert.Equal($"/collections/{playlist}/pinned", path);
        Assert.True(JsonSerializer.Deserialize<CollectionPinRequest>(body!, Web)!.Pinned);
    }

    [Fact]
    public async Task DuplicatingAPlaylist_ReturnsTheNewPlaylist()
    {
        var copyId = Guid.NewGuid();
        using var client = Client(request =>
        {
            Assert.EndsWith("/duplicate", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            return Json(new { id = copyId, name = "Road trip copy", item_count = 4 });
        });

        var copy = await client.DuplicateCollectionAsync(Guid.NewGuid());

        Assert.NotNull(copy);
        Assert.Equal(copyId, copy.Id);
        Assert.Equal("Road trip copy", copy.Name);
        Assert.Equal(4, copy.ItemCount);
    }
}
