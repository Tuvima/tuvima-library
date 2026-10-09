using System.Net;
using System.Text;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

public sealed class TvEpisodeCrosswalkTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tuvima-crosswalk-tests", Guid.NewGuid().ToString("N"));
    private static readonly TvdbEpisodeIdentity Episode = new("389597", "10414110", 2, 1, "official");

    [Fact]
    public async Task DivergentNumbering_ReturnsVerifiedTmdbPositionWithoutChangingOwnedIdentity()
    {
        var handler = new CrosswalkHandler();
        var service = CreateService(handler);
        var result = await service.ResolveAsync(Episode, "127532");

        Assert.NotNull(result);
        Assert.Equal("127532", result.ShowId);
        Assert.Equal("5876034", result.EpisodeId);
        Assert.Equal(1, result.SeasonNumber);
        Assert.Equal(13, result.EpisodeNumber);
        Assert.Equal("tmdb_tvdb_external_id", result.Source);
        Assert.NotNull(result.VerifiedAt);
        Assert.Equal(2, Episode.SeasonNumber);
        Assert.Equal(4, handler.Paths.Count);
        Assert.Contains("/3/tv/127532/season/1/episode/13/external_ids", handler.Paths);
    }

    [Fact]
    public async Task MissingTmdbShowBridge_IsResolvedByDirectTvdbShowLink()
    {
        var result = await CreateService(new CrosswalkHandler()).ResolveAsync(Episode);
        Assert.Equal("127532", result?.ShowId);
    }

    [Theory]
    [InlineData(0, 36)]
    [InlineData(3, 1)]
    public async Task SpecialsAndMatchingNumbering_UseTmdbReturnedPosition(int season, int episode)
    {
        var handler = new CrosswalkHandler
        {
            EpisodeBody = $$"""{"tv_episode_results":[{"id":5876034,"show_id":127532,"season_number":{{season}},"episode_number":{{episode}}}]}""",
        };
        var result = await CreateService(handler).ResolveAsync(Episode);
        Assert.Equal(season, result?.SeasonNumber);
        Assert.Equal(episode, result?.EpisodeNumber);
    }

    [Theory]
    [InlineData("{\"tv_results\":[]}")]
    [InlineData("{\"tv_results\":[{\"id\":127532},{\"id\":123}]}")]
    [InlineData("{\"tv_results\":[{\"id\":123}]}")]
    public async Task MissingAmbiguousOrConflictingShow_SkipsEpisodeLookup(string response)
    {
        var handler = new CrosswalkHandler { ShowBody = response };
        Assert.Null(await CreateService(handler).ResolveAsync(Episode, "127532"));
        Assert.Single(handler.Paths);
    }

    [Theory]
    [InlineData("{\"tv_episode_results\":[]}")]
    [InlineData("{\"tv_episode_results\":[{\"id\":5876034,\"show_id\":123,\"season_number\":1,\"episode_number\":13}]}")]
    [InlineData("{\"tv_episode_results\":[{\"id\":5876034,\"show_id\":127532,\"season_number\":1,\"episode_number\":13},{\"id\":99,\"show_id\":127532,\"season_number\":1,\"episode_number\":14}]}")]
    [InlineData("{\"tv_episode_results\":[{\"id\":5876034,\"show_id\":127532,\"season_number\":1}]}")]
    [InlineData("{\"tv_episode_results\":[{\"id\":5876034,\"show_id\":127532,\"season_number\":-1,\"episode_number\":13}]}")]
    [InlineData("{\"tv_episode_results\":[{\"id\":5876034,\"show_id\":127532,\"season_number\":1,\"episode_number\":0}]}")]
    public async Task MissingAmbiguousOrWrongShowEpisode_FailsClosed(string response)
    {
        var handler = new CrosswalkHandler { EpisodeBody = response };
        Assert.Null(await CreateService(handler).ResolveAsync(Episode));
        Assert.Equal(3, handler.Paths.Count);
    }

    [Theory]
    [InlineData("{\"tvdb_id\":1}", "{\"id\":5876034,\"tvdb_id\":10414110}")]
    [InlineData("{\"tvdb_id\":389597}", "{\"id\":5876034,\"tvdb_id\":1}")]
    [InlineData("{\"tvdb_id\":389597}", "{\"id\":1,\"tvdb_id\":10414110}")]
    [InlineData("{\"tvdb_id\":389597}", "{\"id\":5876034,\"tvdb_id\":null}")]
    public async Task ReverseLinkMustAgree(string show, string episode)
    {
        var handler = new CrosswalkHandler { ShowExternalBody = show, EpisodeExternalBody = episode };
        Assert.Null(await CreateService(handler).ResolveAsync(Episode));
    }

    [Fact]
    public async Task CacheRequiresCurrentCredentialsAndRevalidatesOrderChanges()
    {
        var handler = new CrosswalkHandler();
        var service = CreateService(handler);
        var first = await service.ResolveAsync(Episode);
        Assert.Same(first, await service.ResolveAsync(Episode));
        Assert.Equal(4, handler.Paths.Count);

        Configure(null);
        Assert.Null(await service.ResolveAsync(Episode));
        Assert.Equal(4, handler.Paths.Count);

        Configure("replacement-key");
        Assert.NotNull(await service.ResolveAsync(Episode));
        Assert.Equal(8, handler.Paths.Count);
        Assert.NotNull(await service.ResolveAsync(Episode with { Order = "dvd" }));
        Assert.Equal(12, handler.Paths.Count);

        Configure("replacement-key", enabled: false);
        Assert.Null(await service.ResolveAsync(Episode));
        Assert.Equal(12, handler.Paths.Count);
    }

    [Theory]
    [InlineData("0", "10414110")]
    [InlineData("389597", "invalid")]
    public async Task InvalidIds_DoNotContactProvider(string show, string episode)
    {
        var handler = new CrosswalkHandler();
        Assert.Null(await CreateService(handler).ResolveAsync(new(show, episode, 1, 1)));
        Assert.Empty(handler.Paths);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ProviderFailures_ReturnNoMapping(HttpStatusCode status)
    {
        var handler = new CrosswalkHandler { Status = status };
        Assert.Null(await CreateService(handler).ResolveAsync(Episode));
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        var handler = new CrosswalkHandler();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateService(handler).ResolveAsync(Episode, ct: cancellation.Token));
        Assert.Empty(handler.Paths);
    }

    private TvEpisodeCrosswalk CreateService(CrosswalkHandler handler) => new(
        Configure("test-key"), new EmptyProviderConfiguration(), new StubFactory(handler),
        new ProviderRateLimiterCoordinator(), NullLogger<TvEpisodeCrosswalk>.Instance);

    private ConfigurationDirectoryLoader Configure(string? apiKey, bool enabled = true)
    {
        Directory.CreateDirectory(_directory);
        var loader = new ConfigurationDirectoryLoader(_directory);
        loader.SaveProvider(new ProviderConfiguration
        {
            Name = "tmdb",
            Enabled = enabled,
            HttpClient = new HttpClientConfig { ApiKey = apiKey },
        });
        return loader;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class EmptyProviderConfiguration : IProviderConfigurationRepository
    {
        public Task<string?> GetDecryptedValueAsync(string providerId, string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<MediaEngine.Domain.Entities.ProviderConfiguration>> GetAllMaskedAsync(string providerId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpsertAsync(string providerId, string key, string plaintextValue, bool isSecret, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string providerId, string key, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class StubFactory(CrosswalkHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CrosswalkHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public string ShowBody { get; init; } = """{"tv_results":[{"id":127532}]}""";
        public string ShowExternalBody { get; init; } = """{"id":127532,"tvdb_id":389597}""";
        public string EpisodeBody { get; init; } = """{"tv_episode_results":[{"id":5876034,"show_id":127532,"season_number":1,"episode_number":13}]}""";
        public string EpisodeExternalBody { get; init; } = """{"id":5876034,"tvdb_id":10414110}""";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            var body = path switch
            {
                "/3/find/389597" => ShowBody,
                "/3/tv/127532/external_ids" => ShowExternalBody,
                "/3/find/10414110" => EpisodeBody,
                _ when path.StartsWith("/3/tv/127532/season/", StringComparison.Ordinal) && path.EndsWith("/external_ids", StringComparison.Ordinal) => EpisodeExternalBody,
                _ => throw new InvalidOperationException("Unexpected endpoint"),
            };
            if (path.StartsWith("/3/find/", StringComparison.Ordinal))
            {
                Assert.Contains("external_source=tvdb_id", request.RequestUri.Query);
            }
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
