using System.Net;
using System.Text;
using MediaEngine.Contracts.Artwork;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class EngineApiClientProviderArtworkTests
{
    [Fact]
    public async Task DiscoveryCarriesExactContextAndPaging()
    {
        Uri? requested = null;
        using var http = new HttpClient(new DelegateHandler(request =>
        {
            requested = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"items":[],"message":null,"page":2,"pageSize":25,"totalCount":60,"hasMore":true}""", Encoding.UTF8, "application/json"),
            };
        })) { BaseAddress = new Uri("http://localhost:61495") };
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var result = await client.DiscoverProviderArtworkAsync(Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "episode", "Primary", new ProviderArtworkDiscoveryRequestDto(
                2, 25, "EpisodeStill", "tvdb", "98765", null, "season_number=2;episode_number=7"));

        Assert.NotNull(result);
        Assert.True(result.HasMore);
        var query = requested!.Query;
        Assert.Contains("page=2", query, StringComparison.Ordinal);
        Assert.Contains("pageSize=25", query, StringComparison.Ordinal);
        Assert.Contains("sourceAssetType=EpisodeStill", query, StringComparison.Ordinal);
        Assert.Contains("provider=tvdb", query, StringComparison.Ordinal);
        Assert.Contains("providerItemId=98765", query, StringComparison.Ordinal);
        Assert.Contains("orderContext=season_number%3D2%3Bepisode_number%3D7", query, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
