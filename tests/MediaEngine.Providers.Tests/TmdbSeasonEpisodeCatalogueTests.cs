using System.Net;
using System.Text;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

public sealed class TmdbSeasonEpisodeCatalogueTests
{
    [Fact]
    public async Task WithoutAnApiKey_ReturnsNull_AndMakesNoRequest()
    {
        var http = new SpyHttpClientFactory(_ => Json("""{ "episodes": [ { "episode_number": 1 } ] }"""));
        var catalogue = Build(http, apiKey: null);

        var numbers = await catalogue.GetSeasonEpisodeNumbersAsync("5739", 1);

        Assert.Null(numbers);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task WhenTmdbIsDisabled_ReturnsNull_AndMakesNoRequest()
    {
        var http = new SpyHttpClientFactory(_ => Json("""{ "episodes": [ { "episode_number": 1 } ] }"""));
        var catalogue = Build(http, apiKey: "key", enabled: false);

        var numbers = await catalogue.GetSeasonEpisodeNumbersAsync("5739", 1);

        Assert.Null(numbers);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task ReadsEpisodeNumbersFromTheSeasonDocument()
    {
        var http = new SpyHttpClientFactory(_ => Json(
            """{ "episodes": [ { "episode_number": 1 }, { "episode_number": 2 }, { "episode_number": 3 } ] }"""));
        var catalogue = Build(http, apiKey: "key");

        var numbers = await catalogue.GetSeasonEpisodeNumbersAsync("5739", 1);

        Assert.Equal(new HashSet<int> { 1, 2, 3 }, numbers);
        Assert.Contains("/tv/5739/season/1", Assert.Single(http.Requests), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheEpisodesArrayIsMissing_ReturnsNull()
    {
        var catalogue = Build(new SpyHttpClientFactory(_ => Json("""{ "name": "Season 1" }""")), apiKey: "key");

        Assert.Null(await catalogue.GetSeasonEpisodeNumbersAsync("5739", 1));
    }

    [Fact]
    public async Task WhenTheEpisodesArrayIsEmpty_ReturnsAnEmptySet()
    {
        var catalogue = Build(new SpyHttpClientFactory(_ => Json("""{ "episodes": [] }""")), apiKey: "key");

        var numbers = await catalogue.GetSeasonEpisodeNumbersAsync("5739", 1);

        Assert.NotNull(numbers);
        Assert.Empty(numbers);
    }

    [Fact]
    public async Task EpisodesWithoutAReadableNumber_AreSkipped()
    {
        var catalogue = Build(
            new SpyHttpClientFactory(_ => Json(
                """{ "episodes": [ { "episode_number": "x" }, { "name": "no number" }, null, { "episode_number": 2 } ] }""")),
            apiKey: "key");

        var numbers = await catalogue.GetSeasonEpisodeNumbersAsync("5739", 1);

        Assert.Equal(new HashSet<int> { 2 }, numbers);
    }

    [Fact]
    public async Task WhenTmdbDoesNotKnowTheSeason_ReturnsNull()
    {
        var catalogue = Build(
            new SpyHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.NotFound)), apiKey: "key");

        Assert.Null(await catalogue.GetSeasonEpisodeNumbersAsync("5739", 1));
    }

    [Fact]
    public async Task WhenTheRequestTimesOut_ReturnsNull_InsteadOfThrowing()
    {
        var http = new SpyHttpClientFactory(_ => throw new TaskCanceledException("The request timed out."));
        var catalogue = Build(http, apiKey: "key");

        var numbers = await catalogue.GetSeasonEpisodeNumbersAsync("5739", 1, CancellationToken.None);

        Assert.Null(numbers);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task WhenTheCallerCancels_TheCancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        var http = new SpyHttpClientFactory(_ =>
        {
            cts.Cancel();
            throw new TaskCanceledException("cancelled", null, cts.Token);
        });
        var catalogue = Build(http, apiKey: "key");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => catalogue.GetSeasonEpisodeNumbersAsync("5739", 1, cts.Token));
    }

    private static TmdbSeasonEpisodeCatalogue Build(
        SpyHttpClientFactory http,
        string? apiKey,
        bool enabled = true)
    {
        var client = new TmdbRetailClient(
            http,
            new RetailRequestBuilder(),
            new ProviderRateLimiterCoordinator(),
            NullLogger<TmdbRetailClient>.Instance);
        var snapshot = new PipelineExecutionSnapshot(
            0,
            DateTimeOffset.UtcNow,
            new CoreConfiguration(),
            new HydrationSettings(),
            new PipelineConfiguration(),
            [
                new ProviderConfiguration
                {
                    Name = "tmdb",
                    Enabled = enabled,
                    HttpClient = new HttpClientConfig { ApiKey = apiKey },
                },
            ]);
        return new TmdbSeasonEpisodeCatalogue(
            client, new FixedSnapshots(snapshot), NullLogger<TmdbSeasonEpisodeCatalogue>.Instance);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class FixedSnapshots(PipelineExecutionSnapshot snapshot) : IPipelineExecutionSnapshotProvider
    {
        public PipelineExecutionSnapshot Current => snapshot;
    }

    private sealed class SpyHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> responder) : IHttpClientFactory
    {
        public List<string> Requests { get; } = [];

        public HttpClient CreateClient(string name) => new(new Handler(this), disposeHandler: true);

        private sealed class Handler(SpyHttpClientFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Requests.Add(request.RequestUri!.ToString());
                return Task.FromResult(owner.Respond(request));
            }
        }

        private HttpResponseMessage Respond(HttpRequestMessage request) => responder(request);
    }
}
