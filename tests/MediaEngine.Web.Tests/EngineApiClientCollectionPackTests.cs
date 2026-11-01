using System.Net;
using System.Text;
using MediaEngine.Contracts.Collections;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The pack calls of the Collection editor: the Engine's plain-English reason for a refusal reaches the person, and a
/// failure never looks like an empty result.
/// </summary>
public sealed class EngineApiClientCollectionPackTests
{
    [Fact]
    public async Task ListingPacks_ReadsThemAndPassesTheMediaTypes()
    {
        var (client, handler) = Create(
            HttpStatusCode.OK,
            """{"packs":[{"id":"decades","name":"Decades","description":"d","creates_set":true,"would_create_count":2,"uses_per_profile_fields":false,"collections":[{"name":"1990s","item_count":5,"media_type_counts":{"Movies":5}}]}]}""");
        using var _ = client;

        var packs = await client.GetCollectionPacksAsync(["Movies", "TV"]);

        var pack = Assert.Single(packs);
        Assert.Equal("decades", pack.Id);
        Assert.Equal(5, pack.Collections[0].ItemCount);
        Assert.Equal("/collections/packs?media_types=Movies%2CTV", handler.LastPathAndQuery);
        Assert.Null(client.LastError);
    }

    [Fact]
    public async Task ListingPacks_WhenTheEngineRefuses_ReturnsNothingAndKeepsTheReason()
    {
        var (client, _) = Create(HttpStatusCode.Forbidden, """{"title":"Forbidden","status":403,"detail":"You can't make collections."}""");
        using var _ = client;

        Assert.Empty(await client.GetCollectionPacksAsync());
        Assert.False(string.IsNullOrWhiteSpace(client.LastError));
    }

    [Fact]
    public async Task CreatingPacks_SendsTheRequestAndReadsWhatWasMade()
    {
        var (client, handler) = Create(
            HttpStatusCode.OK,
            """{"sets":[],"collections":[{"id":"40000000-0000-0000-0000-000000000004","name":"1990s"}],"skipped":[{"name":"2010s","reason":"Not enough titles."}]}""");
        using var _ = client;

        var result = await client.CreateCollectionPacksAsync(new CollectionPackCreateRequest { PackIds = ["decades"] });

        Assert.NotNull(result);
        Assert.Equal("1990s", Assert.Single(result.Collections).Name);
        Assert.Equal("Not enough titles.", Assert.Single(result.Skipped).Reason);
        Assert.Equal("/collections/packs", handler.LastPathAndQuery);
        Assert.Contains("\"pack_ids\":[\"decades\"]", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewingABadFile_ReturnsNullWithTheEnginesReason()
    {
        var (client, _) = Create(HttpStatusCode.BadRequest, """{"title":"Bad Request","status":400,"detail":"This is not a collection pack."}""");
        using var _ = client;

        Assert.Null(await client.PreviewCollectionPackFileAsync("{}"));
        Assert.Contains("not a collection pack", client.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemovingASet_UsesTheSetRoute_AndReadsTheCount()
    {
        var setId = Guid.Parse("40000000-0000-0000-0000-000000000009");
        var (client, handler) = Create(HttpStatusCode.OK, """{"removed_collections":3}""");
        using var _ = client;

        var result = await client.RemoveCollectionSetAsync(setId);

        Assert.Equal(3, result!.RemovedCollections);
        Assert.Equal($"/collections/{setId:D}/set", handler.LastPathAndQuery);
        Assert.Equal("DELETE", handler.LastMethod);
    }

    [Fact]
    public async Task ExportingASet_ReadsThePackFile()
    {
        var setId = Guid.Parse("40000000-0000-0000-0000-000000000009");
        var (client, handler) = Create(
            HttpStatusCode.OK,
            """{"file":{"format":"tuvima-collection-pack","version":1,"name":"Decades","creates_set":true,"collections":[]},"omitted":["Local folder"]}""");
        using var _ = client;

        var result = await client.ExportCollectionSetAsync(setId);

        Assert.Equal("Decades", result!.File.Name);
        Assert.Equal(["Local folder"], result.Omitted);
        Assert.Equal($"/collections/{setId:D}/pack-file", handler.LastPathAndQuery);
    }

    private static (EngineApiClient Client, StaticHandler Handler) Create(HttpStatusCode status, string body)
    {
        var handler = new StaticHandler(status, body);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") };
        return (new EngineApiClient(http, NullLogger<EngineApiClient>.Instance), handler);
    }

    private sealed class StaticHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? LastPathAndQuery { get; private set; }
        public string? LastMethod { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPathAndQuery = request.RequestUri!.PathAndQuery;
            LastMethod = request.Method.Method;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
