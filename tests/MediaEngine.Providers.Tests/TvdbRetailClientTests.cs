using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Adapters;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

public sealed class TvdbRetailClientTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tuvima-tvdb-test", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SeriesSearchArtworkAcceptsOnlyTvdbHttpsImages()
    {
        Assert.Equal("https://artworks.thetvdb.com/poster.jpg",
            TvdbMetadataProvider.SeriesArtworkUrl(JsonNode.Parse("""{"image_url":"https://artworks.thetv.com/poster.jpg","image":"https://artworks.thetvdb.com/poster.jpg"}""")));
        Assert.Null(TvdbMetadataProvider.SeriesArtworkUrl(JsonNode.Parse("""{"image_url":"https://other.example/poster.jpg"}""")));
    }

    [Fact]
    public async Task SeriesSearchKeepsDistinctTvdbTitlesAndSuppliesYearAndPoster()
    {
        var adapter = new TvdbMetadataProvider(
            CreateClient(CreateLoader("installation-key"), new TvdbHandler()),
            NullLogger<TvdbMetadataProvider>.Instance);

        var results = await adapter.SearchAsync(new ProviderLookupRequest
        {
            MediaType = MediaType.TV,
            EntityType = EntityType.Work,
            ShowName = "Solo Leveling",
        });

        Assert.Equal(["The Leveling of Solo Leveling", "Solo Leveling"], results.Select(item => item.Title));
        Assert.Equal("2024", results[1].Year);
        Assert.Equal("https://artworks.thetvdb.com/anime.jpg", results[1].ThumbnailUrl);
    }

    [Fact]
    public async Task MissingInstallationKey_PreventsNetworkAccess()
    {
        var loader = CreateLoader(apiKey: null);
        var handler = new TvdbHandler();
        var client = CreateClient(loader, handler);

        Assert.False(client.IsConfigured());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetSeriesAsync("123"));
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task LoginUsesBodyAndEpisodeLookupUsesBearerWithPagination()
    {
        var loader = CreateLoader("installation-key", "subscriber-pin");
        var handler = new TvdbHandler();
        var client = CreateClient(loader, handler);

        var episodes = await client.GetAllEpisodesAsync("123");

        Assert.Equal(2, episodes.Count);
        Assert.Equal("42", episodes[0]["id"]?.ToString());
        Assert.Equal("43", episodes[1]["id"]?.ToString());
        Assert.Equal(1, handler.LoginCount);
        Assert.Equal(2, handler.EpisodeCount);
        Assert.Contains("\"apikey\":\"installation-key\"", handler.LoginBody);
        Assert.Contains("\"pin\":\"subscriber-pin\"", handler.LoginBody);
        Assert.All(handler.Paths, path =>
        {
            Assert.DoesNotContain("installation-key", path, StringComparison.Ordinal);
            Assert.DoesNotContain("subscriber-pin", path, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task EnglishEpisodeListingAndTranslationsUseExplicitLanguagePaths()
    {
        var handler = new TvdbHandler();
        var client = CreateClient(CreateLoader("installation-key"), handler);

        await client.GetAllEpisodesAsync("389597", language: "eng");
        await client.GetSeriesTranslationAsync("389597");
        await client.GetSeasonTranslationAsync("1871836");
        await client.GetEpisodeTranslationAsync("111");

        Assert.Contains(handler.Paths, path => path.Contains("/episodes/default/eng?page=0", StringComparison.Ordinal));
        Assert.Contains(handler.Paths, path => path.Contains("/series/389597/translations/eng", StringComparison.Ordinal));
        Assert.Contains(handler.Paths, path => path.Contains("/seasons/1871836/translations/eng", StringComparison.Ordinal));
        Assert.Contains(handler.Paths, path => path.Contains("/episodes/111/translations/eng", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CachedResponsesRequireCurrentInstallationCredential()
    {
        var loader = CreateLoader("first-key");
        var handler = new TvdbHandler();
        var client = CreateClient(loader, handler);

        Assert.NotNull(await client.GetEpisodeAsync("42"));
        Assert.Equal(1, handler.LoginCount);
        Assert.Equal(1, handler.EpisodeCount);

        CreateLoader(apiKey: null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetEpisodeAsync("42"));
        Assert.Equal(1, handler.EpisodeCount);

        CreateLoader("replacement-key");
        Assert.NotNull(await client.GetEpisodeAsync("42"));
        Assert.Equal(2, handler.LoginCount);
        Assert.Equal(2, handler.EpisodeCount);
    }

    private ConfigurationDirectoryLoader CreateLoader(string? apiKey, string? pin = null)
    {
        Directory.CreateDirectory(_directory);
        var loader = new ConfigurationDirectoryLoader(_directory);
        loader.SaveProvider(new ProviderConfiguration
        {
            Name = "tvdb", Enabled = true,
            Endpoints = new Dictionary<string, string> { ["api"] = "https://api4.thetvdb.com/v4" },
            HttpClient = new HttpClientConfig { ApiKey = apiKey, Pin = pin },
            RateLimit = new ProviderRateLimitConfiguration { RequestsPerSecond = 100, Burst = 100, MaxConcurrency = 2 },
        });
        return loader;
    }

    private static TvdbRetailClient CreateClient(ConfigurationDirectoryLoader loader, TvdbHandler handler) =>
        new(loader, new StubFactory(handler), new ProviderRateLimiterCoordinator());

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private sealed class StubFactory(TvdbHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class TvdbHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public string LoginBody { get; private set; } = string.Empty;
        public int LoginCount { get; private set; }
        public int EpisodeCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.ToString());
            if (request.RequestUri.AbsolutePath.EndsWith("/login", StringComparison.Ordinal))
            {
                LoginCount++;
                LoginBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return Json("{\"data\":{\"token\":\"bearer-token\"}}");
            }
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("bearer-token", request.Headers.Authorization?.Parameter);
            if (request.RequestUri.AbsolutePath.EndsWith("/search", StringComparison.Ordinal))
            {
                return Json("""{"data":[{"tvdb_id":"446883","name":"The Leveling of Solo Leveling","image_url":"https://artworks.thetvdb.com/doc.jpg"},{"tvdb_id":"389597","name":"Solo Leveling","image_url":"https://artworks.thetvdb.com/anime.jpg"}]}""");
            }
            if (request.RequestUri.AbsolutePath.Contains("/translations/eng", StringComparison.Ordinal))
            {
                return request.RequestUri.AbsolutePath.Contains("446883", StringComparison.Ordinal)
                        ? Json("""{"data":{"name":"The Leveling of Solo Leveling"}}""")
                        : Json("""{"data":{"name":"Solo Leveling"}}""");
            }
            if (request.RequestUri.AbsolutePath.Contains("/series/", StringComparison.Ordinal)
                && request.RequestUri.AbsolutePath.EndsWith("/extended", StringComparison.Ordinal))
            {
                return Json("""{"data":{"firstAired":"2024-01-07"}}""");
            }
            EpisodeCount++;
            return request.RequestUri.Query.Contains("page=0", StringComparison.Ordinal)
                ? Json("{\"data\":{\"episodes\":[{\"id\":42}]},\"links\":{\"next\":\"page=1\"}}")
                : Json("{\"data\":{\"episodes\":[{\"id\":43}]},\"links\":{\"next\":null}}");
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}
