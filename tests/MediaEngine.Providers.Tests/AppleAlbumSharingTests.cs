using System.Net;
using System.Text;
using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Adapters;
using MediaEngine.Providers.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// Apple answers 20 requests a minute, and asking for every song of an album separately was the largest cost of finishing music.
/// A song with a known artist and album now takes the album-level claims from one shared answer per album.
/// </summary>
public sealed class AppleAlbumSharingTests
{
    private const string TrackAnswer = """
        { "resultCount": 1, "results": [ {
            "wrapperType": "track", "kind": "song", "trackId": 111, "trackName": "Karma Police",
            "artistName": "Radiohead", "artistId": 657515, "collectionId": 1097861387,
            "artworkUrl100": "https://example.test/cover/100x100bb.jpg", "primaryGenreName": "Alternative",
            "trackNumber": 6, "trackCount": 12, "discNumber": 1, "discCount": 1, "trackTimeMillis": 263000 } ] }
        """;

    [Fact]
    public async Task SongsOfOneAlbum_AskAppleOnce_AndShareTheAlbumClaims()
    {
        var urls = new List<string>();
        var adapter = Adapter(urls, TrackAnswer);

        var first = await adapter.FetchAsync(Request("Karma Police"));
        var requestsAfterFirst = urls.Count;
        var second = await adapter.FetchAsync(Request("Paranoid Android"));
        var third = await adapter.FetchAsync(Request("Lucky"));

        Assert.NotEmpty(first);
        Assert.Equal(requestsAfterFirst, urls.Count);
        Assert.Contains(second, claim => claim.Key == "apple_music_collection_id" && claim.Value == "1097861387");
        Assert.Contains(third, claim => claim.Key == "cover");
        Assert.Equal(first.Select(claim => claim.Key), second.Select(claim => claim.Key));
    }

    [Fact]
    public async Task SharedAnswer_LeavesOutClaimsAboutOneSong()
    {
        var adapter = Adapter([], TrackAnswer);

        var claims = await adapter.FetchAsync(Request("Karma Police"));

        Assert.DoesNotContain(claims, claim => claim.Key is "apple_music_id" or "track_number" or "duration" or "title");
    }

    [Fact]
    public async Task DifferentAlbums_AreAskedForSeparately()
    {
        var urls = new List<string>();
        var adapter = Adapter(urls, TrackAnswer);

        await adapter.FetchAsync(Request("Karma Police", album: "OK Computer"));
        var afterFirstAlbum = urls.Count;
        await adapter.FetchAsync(Request("Everything In Its Right Place", album: "Kid A"));

        Assert.True(urls.Count > afterFirstAlbum);
    }

    [Fact]
    public async Task ASongWithNoAlbum_AsksForItself()
    {
        var urls = new List<string>();
        var adapter = Adapter(urls, TrackAnswer);

        await adapter.FetchAsync(Request("Karma Police", album: null));
        var afterFirst = urls.Count;
        await adapter.FetchAsync(Request("Paranoid Android", album: null));

        Assert.True(urls.Count > afterFirst);
    }

    [Fact]
    public async Task AnEmptyAnswer_IsNotShared_SoTheNextSongTriesAgain()
    {
        var urls = new List<string>();
        var adapter = Adapter(urls, """{ "resultCount": 0, "results": [] }""");

        await adapter.FetchAsync(Request("Karma Police"));
        var afterFirst = urls.Count;
        await adapter.FetchAsync(Request("Paranoid Android"));

        Assert.True(urls.Count > afterFirst);
    }

    private static ConfigDrivenAdapter Adapter(List<string> requestedUrls, string responseJson)
    {
        var config = LoadConfig();
        var services = new ServiceCollection();
        services.AddHttpClient(config.Name)
            .ConfigurePrimaryHttpMessageHandler(() => new RecordingHandler(requestedUrls, responseJson));
        var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        return new ConfigDrivenAdapter(config, factory, NullLogger<ConfigDrivenAdapter>.Instance, NullProviderHealthMonitor.Instance);
    }

    private static ProviderLookupRequest Request(string title, string? album = "OK Computer")
    {
        var hints = new Dictionary<string, string> { ["title"] = title, ["artist"] = "Radiohead" };
        if (album is not null)
        {
            hints["album"] = album;
        }

        return new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.Work,
            MediaType = MediaType.Music,
            Title = title,
            Artist = "Radiohead",
            Author = "Radiohead",
            Album = album,
            BaseUrl = "https://itunes.apple.com",
            Country = "us",
            Language = "en",
            Hints = hints,
        };
    }

    private static ProviderConfiguration LoadConfig()
    {
        var dir = Path.GetDirectoryName(typeof(AppleAlbumSharingTests).Assembly.Location);
        while (dir is not null && !File.Exists(Path.Combine(dir, "MediaEngine.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        var path = Path.Combine(dir ?? throw new InvalidOperationException("The repository root was not found."), "config", "providers", "apple_api.json");
        return JsonSerializer.Deserialize<ProviderConfiguration>(
                   File.ReadAllText(path),
                   new JsonSerializerOptions { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip })
               ?? throw new InvalidOperationException("The apple_api provider file could not be read.");
    }

    private sealed class RecordingHandler(List<string> urls, string responseJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            urls.Add(request.RequestUri?.ToString() ?? string.Empty);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            });
        }
    }
}
