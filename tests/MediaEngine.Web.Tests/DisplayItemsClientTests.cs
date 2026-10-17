using System.Net;
using System.Text;
using System.Text.Json;
using MediaEngine.Contracts.Display;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>The Dashboard's side of the Engine's item lookup: long id lists are split into calls the Engine accepts and joined back.</summary>
public sealed class DisplayItemsClientTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static DisplayCardDto Card(Guid id)
    {
        var fields = typeof(DisplayArtworkDto).GetProperties()
            .Where(p => p.Name.EndsWith("Url", StringComparison.Ordinal))
            .ToDictionary(p => p.Name, _ => (string?)null);
        var art = JsonSerializer.Deserialize<DisplayArtworkDto>(JsonSerializer.Serialize(fields))!;
        return new(id, id, null, null, "Movie", "work", id.ToString("N"), null, [], art, "portrait", "default", "caption", "bottom",
            null, [], new(true, false, false, false), DateTimeOffset.UnixEpoch);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private static EngineApiClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new Handler(respond)) { BaseAddress = new Uri("http://localhost:61495") }, NullLogger<EngineApiClient>.Instance);

    [Fact]
    public async Task ALongListGoesOutInCallsOfAtMostTwoHundredAndComesBackAsOnePage()
    {
        var calls = new List<int>();
        using var client = Client(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/v1/display/items", request.RequestUri!.AbsolutePath);
            using var reader = new StreamReader(request.Content!.ReadAsStream());
            var body = JsonSerializer.Deserialize<DisplayItemsRequest>(reader.ReadToEnd(), Web)!;
            lock (calls)
            {
                calls.Add(body.Ids.Count);
            }

            var page = new DisplayPageDto("items", "Items", null, null, [], body.Ids.Select(Card).ToList());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(page, Web), Encoding.UTF8, "application/json"),
            };
        });
        var ids = Enumerable.Range(0, 450).Select(_ => Guid.NewGuid()).ToList();

        var result = await client.GetDisplayItemsAsync(ids);

        Assert.NotNull(result);
        Assert.Equal([50, 200, 200], calls.Order().ToArray());
        Assert.Equal(ids.Order(), result.Catalog.Select(card => card.Id).Order());
    }

    [Fact]
    public async Task AFailedCallFailsTheWholeLookupSoThePageCanSayItCouldNotLoadInsteadOfShowingAnEmptyRow()
    {
        var count = 0;
        using var client = Client(_ =>
        {
            if (Interlocked.Increment(ref count) == 2)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new DisplayPageDto("items", "Items", null, null, [], []), Web), Encoding.UTF8, "application/json"),
            };
        });

        Assert.Null(await client.GetDisplayItemsAsync(Enumerable.Range(0, 250).Select(_ => Guid.NewGuid()).ToList()));
    }

    [Fact]
    public async Task NoIdsMeansNoCall()
    {
        using var client = Client(_ => throw new InvalidOperationException("No request should be sent."));

        var result = await client.GetDisplayItemsAsync([Guid.Empty]);

        Assert.NotNull(result);
        Assert.Empty(result.Catalog);
    }
}
