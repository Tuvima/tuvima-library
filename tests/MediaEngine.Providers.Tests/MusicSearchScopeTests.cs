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
/// Bug F50: a typed Match search carried only a title, which none of MusicBrainz's recording searches could use (they need an artist
/// or an album as well), so MusicBrainz never answered, and an album search came back with songs. A search that says what it looks
/// for (hint <c>search_scope</c>) now runs the search that fits it, over the provider files the Engine ships in <c>config/providers</c>.
/// </summary>
public sealed class MusicSearchScopeTests
{
    [Fact]
    public async Task MusicBrainz_ATypedTrackSearchWithOnlyATitle_AsksForRecordings()
    {
        var urls = new List<string>();
        var adapter = Adapter("musicbrainz", urls, """
            { "recordings": [ {
                "id": "rec-1", "title": "Karma Police", "artist-credit": [{ "name": "Radiohead" }],
                "releases": [ { "id": "rel-1", "title": "OK Computer", "status": "Official", "date": "1997-05-21" } ] } ] }
            """);

        var results = await adapter.SearchAsync(Request("Karma Police", album: null, scope: "track"));

        Assert.Equal("Karma Police", Assert.Single(results).Title);
        var url = Uri.UnescapeDataString(Assert.Single(urls));
        Assert.Contains("/recording?", url, StringComparison.Ordinal);
        Assert.Contains("recording:\"Karma Police\"", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MusicBrainz_AnAlbumSearch_AsksForReleasesAndNeverForRecordings()
    {
        var urls = new List<string>();
        var adapter = Adapter("musicbrainz", urls, """
            { "releases": [ {
                "id": "rel-1", "title": "Smile Empty Soul", "status": "Official", "date": "1998-09-29",
                "artist-credit": [{ "name": "Smile Empty Soul" }], "release-group": { "id": "rg-1" } } ] }
            """);

        var results = await adapter.SearchAsync(Request("Smile Empty Soul", album: "Smile Empty Soul", scope: "album"));

        var album = Assert.Single(results);
        Assert.Equal("Smile Empty Soul", album.Title);
        Assert.Equal("1998", album.Year);
        var url = Uri.UnescapeDataString(Assert.Single(urls));
        Assert.Contains("/release?", url, StringComparison.Ordinal);
        Assert.Contains("release:\"Smile Empty Soul\"", url, StringComparison.Ordinal);
        Assert.DoesNotContain("/recording", url, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("album", "entity=album", "entity=musicTrack")]
    [InlineData("track", "entity=musicTrack", "entity=album")]
    public async Task Apple_Music_RunsOnlyTheSearchForWhatWasAskedFor(string scope, string expected, string unexpected)
    {
        var urls = new List<string>();
        var adapter = Adapter("apple_api", urls, """{ "resultCount": 0, "results": [] }""");

        await adapter.SearchAsync(Request("Smile Empty Soul", album: "Smile Empty Soul", scope: scope));

        Assert.NotEmpty(urls);
        Assert.All(urls, url => Assert.Contains(expected, url, StringComparison.Ordinal));
        Assert.All(urls, url => Assert.DoesNotContain(unexpected, url, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Apple_Music_WithNoScope_StillTriesEveryStrategy()
    {
        var urls = new List<string>();
        var adapter = Adapter("apple_api", urls, """{ "resultCount": 0, "results": [] }""");

        await adapter.SearchAsync(Request("Smile Empty Soul", album: null, scope: null));

        Assert.Contains(urls, url => url.Contains("entity=musicTrack", StringComparison.Ordinal));
        Assert.Contains(urls, url => url.Contains("entity=album", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MusicBrainz_WithNoScope_NeverRunsTheSearchesOnlyAManualMatchUses()
    {
        var urls = new List<string>();
        var adapter = Adapter("musicbrainz", urls, """{ "recordings": [], "releases": [] }""");

        await adapter.SearchAsync(Request("Karma Police", album: null, scope: null));

        Assert.Empty(urls);
    }

    private static ConfigDrivenAdapter Adapter(string provider, List<string> requestedUrls, string responseJson)
    {
        var config = LoadConfig(provider);
        var services = new ServiceCollection();
        services.AddHttpClient(config.Name)
            .ConfigurePrimaryHttpMessageHandler(() => new RecordingHandler(requestedUrls, responseJson));
        var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        return new ConfigDrivenAdapter(config, factory, NullLogger<ConfigDrivenAdapter>.Instance, NullProviderHealthMonitor.Instance);
    }

    private static ProviderLookupRequest Request(string title, string? album, string? scope)
    {
        var hints = new Dictionary<string, string> { ["title"] = title };
        if (album is not null)
        {
            hints["album"] = album;
        }

        if (scope is not null)
        {
            hints["search_scope"] = scope;
        }

        return new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.Work,
            MediaType = MediaType.Music,
            Title = title,
            Album = album,
            BaseUrl = "https://musicbrainz.org/ws/2",
            Country = "us",
            Language = "en",
            Hints = hints,
        };
    }

    private static ProviderConfiguration LoadConfig(string provider)
    {
        var dir = Path.GetDirectoryName(typeof(MusicSearchScopeTests).Assembly.Location);
        while (dir is not null && !File.Exists(Path.Combine(dir, "MediaEngine.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        var path = Path.Combine(dir ?? throw new InvalidOperationException("The repository root was not found."), "config", "providers", $"{provider}.json");
        return JsonSerializer.Deserialize<ProviderConfiguration>(
                   File.ReadAllText(path),
                   new JsonSerializerOptions { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip })
               ?? throw new InvalidOperationException($"The {provider} provider file could not be read.");
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
