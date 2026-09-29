using System.Net;
using System.Text;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Adapters;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

public sealed class TvdbMetadataProviderLanguageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"tuvima_tvdb_english_{Guid.NewGuid():N}");

    [Fact]
    public async Task SoloLevelingSearch_UsesEnglishTranslationsAndDefaultEpisodeOrder()
    {
        var loader = new ConfigurationDirectoryLoader(_directory);
        loader.SaveProvider(new ProviderConfiguration
        {
            Name = "tvdb", Enabled = true,
            Endpoints = new Dictionary<string, string> { ["api"] = "https://api4.thetvdb.com/v4" },
            HttpClient = new HttpClientConfig { ApiKey = "installation-key" },
        });
        var handler = new SoloLevelingHandler();
        var client = new TvdbRetailClient(loader, new StubFactory(handler), new ProviderRateLimiterCoordinator());
        var provider = new TvdbMetadataProvider(client, NullLogger<TvdbMetadataProvider>.Instance);

        var shows = await provider.SearchAsync(new ProviderLookupRequest
        {
            EntityType = EntityType.Work, MediaType = MediaType.TV, ShowName = "Solo Leveling",
        });
        var show = Assert.Single(shows);
        Assert.Equal("Solo Leveling", show.Title);
        Assert.Equal("389597", show.ProviderItemId);
        Assert.Equal("English show overview", show.Description);

        var episodes = await provider.SearchAsync(new ProviderLookupRequest
        {
            EntityType = EntityType.Work, MediaType = MediaType.TV,
            ShowName = "Solo Leveling", SeasonNumber = "1", EpisodeNumber = "1",
        });
        var episode = Assert.Single(episodes);
        Assert.Equal("I'm Used to It", episode.Title);
        Assert.Equal("English episode overview", episode.Description);
        Assert.Equal("Solo Leveling", episode.Author);
        Assert.Equal("111", episode.ProviderItemId);
        Assert.Contains(handler.Paths, path => path.Contains("/episodes/default/eng?page=0", StringComparison.Ordinal));

        var claims = await provider.FetchAsync(new ProviderLookupRequest
        {
            EntityType = EntityType.MediaAsset, MediaType = MediaType.TV,
            ShowName = "Solo Leveling", SeasonNumber = "1", EpisodeNumber = "1",
        });
        Assert.Contains(claims, claim => claim.Key == "show_name" && claim.Value == "Solo Leveling");
        Assert.Contains(claims, claim => claim.Key == "episode_title" && claim.Value == "I'm Used to It");
        Assert.DoesNotContain(claims, claim => claim.Value.Contains("俺だけ", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private sealed class StubFactory(SoloLevelingHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class SoloLevelingHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            Paths.Add(uri.ToString());
            var body = uri.AbsolutePath switch
            {
                "/v4/login" => """{"data":{"token":"test-token"}}""",
                "/v4/search" => """{"data":[{"id":389597,"name":"俺だけレベルアップな件","year":"2024"}]}""",
                "/v4/series/389597/extended" => """{"data":{"id":389597,"name":"俺だけレベルアップな件"}}""",
                "/v4/series/389597/translations/eng" => """{"data":{"name":"Solo Leveling","overview":"English show overview"}}""",
                "/v4/series/389597/episodes/default/eng" => """{"data":{"episodes":[{"id":111,"seriesId":389597,"seasonNumber":1,"number":1,"name":"俺だけ"}]},"links":{"next":null}}""",
                "/v4/episodes/111/translations/eng" => """{"data":{"name":"I'm Used to It","overview":"English episode overview"}}""",
                _ => """{"data":null}""",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
