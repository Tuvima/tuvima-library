using System.Net;
using System.Net.Http.Json;
using MediaEngine.Contracts.Activity;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>The Dashboard's two reads for the Overview: the person's recent activity and the address to type into an app.</summary>
public sealed class EngineApiClientPersonalActivityTests
{
    private static readonly string Key = new('c', 64);

    [Fact]
    public async Task RecentActivity_AsksForTheRequestedNumberOfTiles_AndTurnsPictureAddressesIntoDashboardAddresses()
    {
        var tile = new PersonalActivityRecentTileDto(
            PersonalActivityKindValues.MusicSession, string.Empty, null, null, "2026-10-19T14:30:00Z", null, 3900, null, false,
            PersonalActivityShapeValues.Square, $"/api/v1/activity/artwork/{Key}", [$"/api/v1/activity/artwork/{Key}", "https://elsewhere.test/cover.jpg"], 50, 12, null);
        var handler = new RecordingHandler(new PersonalActivityRecentDto([tile]));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:61495") };
        using var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var recent = await client.GetPersonalActivityRecentAsync(20);

        Assert.Equal("/api/v1/activity/recent?limit=20", handler.Requests.Single());
        var mapped = Assert.Single(recent!.Tiles);
        Assert.Equal($"/engine-activity-artwork/{Key}", mapped.ArtworkUrl);
        Assert.Equal([$"/engine-activity-artwork/{Key}"], mapped.CoverUrls);
        Assert.Equal(50, mapped.SongCount);
        Assert.Equal(12, mapped.AlbumCount);
    }

    [Fact]
    public async Task RecentActivity_IsNullWhenTheEngineRefuses_SoThePageCanSaySo()
    {
        var handler = new RecordingHandler(null, HttpStatusCode.Forbidden);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:61495") };
        using var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        Assert.Null(await client.GetPersonalActivityRecentAsync());
    }

    [Fact]
    public async Task AppAddresses_AreReadFromTheSignedInPersonsEndpoint_NotTheAdministratorOnlyStatus()
    {
        var handler = new RecordingHandler(new NetworkAppAddressesDto { AtHome = "http://192.168.1.20:5016", AwayFromHome = "https://tuvima.example.com" });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:61495") };
        using var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var addresses = await client.GetNetworkAppAddressesAsync();

        Assert.Equal("/network/app-addresses", handler.Requests.Single());
        Assert.Equal("http://192.168.1.20:5016", addresses!.AtHome);
        Assert.Equal("https://tuvima.example.com", addresses.AwayFromHome);
    }

    private sealed class RecordingHandler(object? body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            var response = new HttpResponseMessage(status);
            if (body is not null)
            {
                response.Content = JsonContent.Create(body, body.GetType());
            }

            return Task.FromResult(response);
        }
    }
}
