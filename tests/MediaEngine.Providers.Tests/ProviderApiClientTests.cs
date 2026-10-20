using System.Net;
using System.Text;
using MediaEngine.Domain.Configuration;
using MediaEngine.Providers.Adapters;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Providers.Tests.ProviderRecording;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// The general provider client run on the shipped tvdb.json: sign-in, token use, paging, caching,
/// argument checks and the address check all come from the file.
/// </summary>
public sealed class ProviderApiClientTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tuvima-api-client-test", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SeriesSearch_KeepsDistinctTitlesAndSuppliesYearAndPoster()
    {
        var adapter = CreateAdapter(CreateLoader("installation-key"), new TvdbHandler());

        var results = await adapter.SearchAsync(new ProviderLookupRequest
        {
            MediaType = Domain.Enums.MediaType.TV,
            EntityType = Domain.Enums.EntityType.Work,
            ShowName = "Solo Leveling",
        });

        Assert.Equal(["The Leveling of Solo Leveling", "Solo Leveling"], results.Select(item => item.Title));
        Assert.Equal("2024", results[1].Year);
        Assert.Equal("https://artworks.thetvdb.com/anime.jpg", results[1].ThumbnailUrl);
    }

    [Fact]
    public async Task SeriesSearchArtwork_AcceptsOnlyHttpsImagesOnTheProvidersHosts()
    {
        var handler = new TvdbHandler(search: """{"data":[{"tvdb_id":"1","name":"A","year":"2020","image_url":"https://artworks.thetv.com/x.jpg","image":"https://artworks.thetvdb.com/y.jpg"},{"tvdb_id":"2","name":"B","year":"2021","image_url":"http://artworks.thetvdb.com/z.jpg","image":"https://other.example/w.jpg"}]}""");
        var adapter = CreateAdapter(CreateLoader("installation-key"), handler);

        var results = await adapter.SearchAsync(new ProviderLookupRequest
        {
            MediaType = Domain.Enums.MediaType.TV,
            EntityType = Domain.Enums.EntityType.Work,
            ShowName = "A",
        });

        Assert.Equal("https://artworks.thetvdb.com/y.jpg", results[0].ThumbnailUrl);
        Assert.Null(results[1].ThumbnailUrl);
    }

    [Fact]
    public async Task MissingInstallationKey_PreventsNetworkAccess()
    {
        var handler = new TvdbHandler();
        var client = CreateClient(CreateLoader(apiKey: null), handler);

        Assert.False(client.IsConfigured("tvdb"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetAsync("tvdb", "series_extended", ProviderApiClient.Args(("id", "123"))));
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task LoginUsesBodyAndEpisodeLookupUsesBearerWithPagination()
    {
        var loader = CreateLoader("installation-key", "subscriber-pin");
        var handler = new TvdbHandler();
        var client = CreateClient(loader, handler);

        var episodes = await client.GetAllPagesAsync("tvdb", "series_episodes", ProviderApiClient.Args(("id", "123")));

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
    public async Task LoginOmitsABlankPin()
    {
        var handler = new TvdbHandler();
        var client = CreateClient(CreateLoader("installation-key"), handler);

        await client.GetAsync("tvdb", "series_extended", ProviderApiClient.Args(("id", "5")));

        Assert.Equal("""{"apikey":"installation-key"}""", handler.LoginBody);
    }

    [Fact]
    public async Task EnglishEpisodeListingAndTranslationsUseExplicitLanguagePaths()
    {
        var handler = new TvdbHandler();
        var client = CreateClient(CreateLoader("installation-key"), handler);

        await client.GetAllPagesAsync("tvdb", "series_episodes", ProviderApiClient.Args(("id", "389597"), ("lang", "eng")));
        await client.GetAsync("tvdb", "series_translation", ProviderApiClient.Args(("id", "389597")));
        await client.GetAsync("tvdb", "season_translation", ProviderApiClient.Args(("id", "1871836")));
        await client.GetAsync("tvdb", "episode_translation", ProviderApiClient.Args(("id", "111")));

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

        Assert.NotNull(await client.GetAsync("tvdb", "episode_extended", ProviderApiClient.Args(("id", "42"))));
        Assert.Equal(1, handler.LoginCount);
        Assert.Equal(1, handler.EpisodeCount);

        CreateLoader(apiKey: null);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetAsync("tvdb", "episode_extended", ProviderApiClient.Args(("id", "42"))));
        Assert.Equal(1, handler.EpisodeCount);

        CreateLoader("replacement-key");
        Assert.NotNull(await client.GetAsync("tvdb", "episode_extended", ProviderApiClient.Args(("id", "42"))));
        Assert.Equal(2, handler.LoginCount);
        Assert.Equal(2, handler.EpisodeCount);
    }

    [Fact]
    public async Task ArgumentsAreCheckedBeforeAnythingIsSent()
    {
        var handler = new TvdbHandler();
        var client = CreateClient(CreateLoader("installation-key"), handler);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.GetAsync("tvdb", "series_extended", ProviderApiClient.Args(("id", "12; drop"))));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.GetAsync("tvdb", "series_translation", ProviderApiClient.Args(("id", "1"), ("lang", "english"))));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.GetAllPagesAsync("tvdb", "series_episodes", ProviderApiClient.Args(("id", "1"), ("season_type", "weird"))));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetAsync("tvdb", "no_such_endpoint"));
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task AnUnauthorizedAnswerSignsInAgainOnceAndRepeatsTheRequest()
    {
        var handler = new TvdbHandler { UnauthorizedOnce = true };
        var client = CreateClient(CreateLoader("installation-key"), handler);

        Assert.NotNull(await client.GetAsync("tvdb", "episode_extended", ProviderApiClient.Args(("id", "42"))));

        Assert.Equal(2, handler.LoginCount);
        Assert.Equal(2, handler.EpisodeCount);
    }

    [Fact]
    public async Task ConcurrentCallsShareOneSignIn()
    {
        var handler = new TvdbHandler();
        var client = CreateClient(CreateLoader("installation-key"), handler);

        await Task.WhenAll(Enumerable.Range(1, 8).Select(id =>
            client.GetAsync("tvdb", "episode_extended", ProviderApiClient.Args(("id", id.ToString())))));

        Assert.Equal(1, handler.LoginCount);
    }

    [Fact]
    public async Task AnAddressOutsideTheAllowedHosts_IsRefusedBeforeSigningIn()
    {
        var loader = CreateLoader("installation-key");
        var config = loader.LoadProvider("tvdb")!;
        config.Endpoints["api"] = "https://evil.example/v4";
        loader.SaveProvider(config);
        var handler = new TvdbHandler();
        var client = CreateClient(loader, handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetAsync("tvdb", "series_extended", ProviderApiClient.Args(("id", "1"))));
        Assert.Empty(handler.Paths);
    }

    private ConfigurationDirectoryLoader CreateLoader(string? apiKey, string? pin = null)
    {
        Directory.CreateDirectory(_directory);
        var loader = new ConfigurationDirectoryLoader(_directory);
        ShippedProviderHarness.SaveShipped(loader, "tvdb", apiKey, pin);
        return loader;
    }

    private static ProviderApiClient CreateClient(ConfigurationDirectoryLoader loader, TvdbHandler handler) =>
        new(loader, new StubFactory(handler), new ProviderRateLimiterCoordinator());

    private static ConfigDrivenAdapter CreateAdapter(ConfigurationDirectoryLoader loader, TvdbHandler handler)
    {
        var factory = new StubFactory(handler);
        var limiter = new ProviderRateLimiterCoordinator();
        return new ConfigDrivenAdapter(
            loader.LoadProvider("tvdb")!, factory, NullLogger<ConfigDrivenAdapter>.Instance,
            NullProviderHealthMonitor.Instance, rateLimiter: limiter, apiClient: new ProviderApiClient(loader, factory, limiter));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { /* temp folder */ }
    }

    private sealed class StubFactory(TvdbHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class TvdbHandler(string? search = null) : HttpMessageHandler
    {
        private readonly object _gate = new();
        private int _unauthorizedSent;

        public List<string> Paths { get; } = [];
        public string LoginBody { get; private set; } = string.Empty;
        public int LoginCount { get; private set; }
        public int EpisodeCount { get; private set; }
        public bool UnauthorizedOnce { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var login = request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal);
            var body = login ? await request.Content!.ReadAsStringAsync(cancellationToken) : string.Empty;
            lock (_gate)
            {
                Paths.Add(request.RequestUri.ToString());
                if (login)
                {
                    LoginCount++;
                    LoginBody = body;
                }
            }

            if (login)
            {
                return Json("{\"data\":{\"token\":\"bearer-token\"}}");
            }

            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("bearer-token", request.Headers.Authorization?.Parameter);
            if (request.RequestUri.AbsolutePath.EndsWith("/search", StringComparison.Ordinal))
            {
                return Json(search ?? """{"data":[{"tvdb_id":"446883","name":"The Leveling of Solo Leveling","image_url":"https://artworks.thetvdb.com/doc.jpg"},{"tvdb_id":"389597","name":"Solo Leveling","image_url":"https://artworks.thetvdb.com/anime.jpg"}]}""");
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

            lock (_gate)
            {
                EpisodeCount++;
                if (UnauthorizedOnce && _unauthorizedSent++ == 0)
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }
            }

            return request.RequestUri.Query.Contains("page=0", StringComparison.Ordinal)
                ? Json("{\"data\":{\"episodes\":[{\"id\":42}]},\"links\":{\"next\":\"page=1\"}}")
                : request.RequestUri.AbsolutePath.Contains("/episodes/", StringComparison.Ordinal)
                    && request.RequestUri.Query.Contains("page=1", StringComparison.Ordinal)
                    ? Json("{\"data\":{\"episodes\":[{\"id\":43}]},\"links\":{\"next\":null}}")
                    : Json("{\"data\":{\"id\":42}}");
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}
