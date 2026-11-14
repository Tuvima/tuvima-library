using System.Net;
using System.Text;
using MediaEngine.Domain;
using MediaEngine.Domain.Entities;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Covers drawn by the editor header and Needs Review reach the browser through the Dashboard's picture route (issue 988). An address
/// the Engine hands out as "/stream/artwork/..." is only fetchable from the browser once it is turned into "/engine-image/...".
/// </summary>
public sealed class EngineApiClientCoverAddressTests
{
    private static readonly Guid CoverId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public async Task TheEditorHeaderCover_IsServedThroughTheDashboardPictureRoute()
    {
        var picture = $"/stream/artwork/{CoverId}?v=ab12";
        var body = "{\"launch_entity_id\":\"" + Guid.NewGuid() + "\",\"media_type\":\"Movies\","
            + "\"header\":{\"kind\":\"movie\",\"root\":{\"id\":\"" + Guid.NewGuid() + "\",\"title\":\"Dune\",\"media_type\":\"Movies\",\"cover_url\":\"" + picture + "\"}}}";
        using var client = ClientFor(body);

        var context = await client.GetMediaEditorContextAsync(Guid.NewGuid());

        var cover = context?.Header?.Root.CoverUrl;
        Assert.NotNull(cover);
        Assert.StartsWith(EngineImageProxyPath.ProxyPrefix + "/stream/artwork/", cover, StringComparison.Ordinal);
        Assert.Contains("v=ab12", cover, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheNeedsReviewPictures_AreServedThroughTheDashboardPictureRoute()
    {
        var picture = $"/stream/artwork/{CoverId}?v=ab12";
        var body = "{\"items\":[{\"key\":\"review:1\",\"cover_url\":\"" + picture + "\",\"family_cover_url\":\"" + picture + "\","
            + "\"best_guess\":{\"action\":\"confirm_match\",\"name\":\"Dune\",\"image_url\":\"" + picture + "\"},"
            + "\"choices\":[{\"id\":\"a\",\"kind\":\"retail\",\"title\":\"Dune\",\"image_url\":\"" + picture + "\"}]}],\"total\":1}";
        using var client = ClientFor(body);

        var attempt = await client.GetNeedsReviewListAsync(null, 0, 20);

        var row = Assert.Single(attempt.List!.Items);
        Assert.All(
            new[] { row.CoverUrl, row.FamilyCoverUrl, row.BestGuess!.ImageUrl, row.Choices[0].ImageUrl },
            url => Assert.StartsWith(EngineImageProxyPath.ProxyPrefix + "/stream/artwork/", url, StringComparison.Ordinal));
    }

    /// <summary>Every kind of picture address the Engine builds for a cover can be fetched by the browser through the Dashboard.</summary>
    [Fact]
    public void EveryCoverAddressTheEngineHandsOut_CanBeFetchedThroughTheDashboard()
    {
        var asset = new EntityAsset { Id = CoverId, LocalImagePath = string.Empty };
        var addresses = new List<string>
        {
            ArtworkCanonicalHelper.StoredArtworkUrl(asset),
            ArtworkCanonicalHelper.StoredArtworkUrl(asset, "s"),
            ArtworkCanonicalHelper.StoredArtworkUrl(asset, "m"),
            ArtworkCanonicalHelper.StoredArtworkUrl(asset, "l"),
            $"{ArtworkCanonicalHelper.StoredArtworkUrlPrefix}{CoverId}",
            $"/stream/{CoverId}/cover",
            $"/stream/{CoverId}/cover?size=s",
            $"/stream/{CoverId}/background?size=m",
            $"/stream/entity/Work/{CoverId}/cover?size=s",
            $"/api/v1/display/artwork/assets/{CoverId}/content",
            $"/persons/{CoverId}/headshot?size=s",
        };

        foreach (var address in addresses)
        {
            Assert.True(EngineImageProxyPath.IsAllowedEnginePath(address), $"The Dashboard would refuse {address}.");
            var browser = EngineImageProxyPath.ToBrowserUrl(address, new Uri("http://engine.test"));
            Assert.StartsWith(EngineImageProxyPath.ProxyPrefix + "/", browser, StringComparison.Ordinal);
        }
    }

    private static EngineApiClient ClientFor(string json)
    {
        var http = new HttpClient(new StaticHandler(json)) { BaseAddress = new Uri("http://engine.test") };
        return new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);
    }

    private sealed class StaticHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
