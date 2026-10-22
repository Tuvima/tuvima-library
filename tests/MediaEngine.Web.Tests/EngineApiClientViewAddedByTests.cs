using System.Net;
using System.Text;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class EngineApiClientViewAddedByTests
{
    private static readonly Guid Alice = Guid.Parse("b5028d65-b179-4bed-a3b6-2e6011b90d31");
    private static readonly Guid Bob = Guid.Parse("bf270036-a9ad-4d95-a65f-672bf163a074");

    [Fact]
    public async Task AssetAndTimelineIndexQueriesSendEveryAddedByProfile()
    {
        var seen = new List<string>();
        using var http = CreateHttpClient(request =>
        {
            seen.Add(request.RequestUri!.PathAndQuery);
            var body = request.RequestUri.AbsolutePath.EndsWith("timeline-index", StringComparison.Ordinal)
                ? """{"buckets":[]}"""
                : """{"items":[],"next_cursor":null,"has_more":false}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        });
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);
        var options = new ViewAssetQueryOptions(ViewScopeKind.Shared, AddedBy: [Alice, Bob]);

        await client.GetViewAssetsAsync(options);
        await client.GetViewTimelineIndexAsync(options);
        await client.GetViewAssetsAsync(new ViewAssetQueryOptions(ViewScopeKind.Shared));

        Assert.Equal(3, seen.Count);
        foreach (var path in seen.Take(2))
        {
            Assert.Contains($"added_by={Alice:D}", path, StringComparison.Ordinal);
            Assert.Contains($"added_by={Bob:D}", path, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("added_by", seen[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTimelineIndexSendsTheTagFiltersAlongsideAddedBy()
    {
        string? path = null;
        using var http = CreateHttpClient(request =>
        {
            path = request.RequestUri!.PathAndQuery;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"buckets":[]}""", Encoding.UTF8, "application/json"),
            });
        });
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);
        var tag = Guid.Parse("0f5c5d0c-1a4a-4b5e-8c3e-3b5a0f6f1c11");

        await client.GetViewTimelineIndexAsync(new ViewAssetQueryOptions(
            ViewScopeKind.Shared,
            Documents: new MediaEngine.Contracts.LocalAssets.ViewDocumentFilterDto([tag]),
            AddedBy: [Alice]));

        Assert.Contains($"tag={tag:D}", path, StringComparison.Ordinal);
        Assert.Contains($"added_by={Alice:D}", path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAddedByListAsksForTheSameScopeAndKindsAndReadsProfilesWithCounts()
    {
        string? path = null;
        using var http = CreateHttpClient(request =>
        {
            path = request.RequestUri!.PathAndQuery;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""[{"profile_id":"{{Alice:D}}","name":"Alice","item_count":4},{"profile_id":"{{Bob:D}}","name":"Bob","item_count":1}]""",
                    Encoding.UTF8, "application/json"),
            });
        });
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var people = await client.GetViewAssetsAddedByAsync(new ViewAssetQueryOptions(ViewScopeKind.Shared, Kinds: ["image", "video"]));

        Assert.StartsWith("/view/assets/added-by?", path, StringComparison.Ordinal);
        Assert.Contains("scope=shared", path, StringComparison.Ordinal);
        Assert.Contains("kind=image", path, StringComparison.Ordinal);
        Assert.Contains("kind=video", path, StringComparison.Ordinal);
        Assert.NotNull(people);
        Assert.Equal([("Alice", 4), ("Bob", 1)], people.Select(person => (person.Name, person.ItemCount)).ToArray());
    }

    private static HttpClient CreateHttpClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) =>
        new(new StubHttpMessageHandler(responder))
        {
            BaseAddress = new Uri("http://localhost:61495/"),
        };

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responder(request);
    }
}
