using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// The MusicBrainz reads the Match tab's music flow makes (universal editor v5, 3c): the releases of an album or of a song, and one release's
/// discs and tracks with lengths and each recording's disambiguation. A fake HTTP handler stands in for the provider; every request goes
/// through the provider's rate limiter, answers are kept for the provider's own cache time, and an empty answer is never kept.
/// </summary>
public sealed class MusicBrainzReleaseSearchTests
{
    private const string ReleaseA = "11111111-1111-4111-8111-111111111111";
    private const string ReleaseB = "22222222-2222-4222-8222-222222222222";

    private const string ReleaseSearch = """
        {"releases":[
          {"id":"11111111-1111-4111-8111-111111111111","title":"Low Water","date":"2011-05-02","country":"GB","track-count":11,
           "artist-credit":[{"name":"The Tides"}],"release-group":{"id":"rg-1"},
           "label-info":[{"label":{"name":"Harbor Records"}}],
           "media":[{"format":"CD","track-count":11}],"cover-art-archive":{"front":true}},
          {"id":"22222222-2222-4222-8222-222222222222","title":"Low Water (Deluxe)","date":"2013","track-count":24,
           "artist-credit":[{"name":"The Tides"}],
           "media":[{"format":"CD","track-count":12},{"format":"CD","track-count":12}]}
        ]}
        """;

    private const string ReleaseDetail = """
        {"id":"22222222-2222-4222-8222-222222222222","title":"Low Water (Deluxe)","date":"2013-06-10","country":"GB",
         "artist-credit":[{"name":"The Tides"}],"release-group":{"id":"rg-2"},
         "media":[
           {"position":2,"format":"CD","tracks":[
              {"id":"t-2-3","position":3,"title":"Harbor Lights","length":222000,
               "recording":{"id":"rec-3","title":"Harbor Lights","disambiguation":"2013 remaster","artist-credit":[{"name":"The Tides"}]}},
              {"id":"t-2-1","position":1,"title":"Slack Water","recording":{"id":"rec-4","title":"Slack Water","length":181000}}]},
           {"position":1,"format":"CD","tracks":[
              {"id":"t-1-1","position":1,"title":"Opening","length":180000,"recording":{"id":"rec-1","title":"Opening"}}]}
         ]}
        """;

    private const string RecordingSearch = """
        {"recordings":[
          {"id":"rec-3","title":"Harbor Lights","artist-credit":[{"name":"The Tides"}],
           "releases":[
             {"id":"11111111-1111-4111-8111-111111111111","title":"Low Water","date":"2011"},
             {"id":"22222222-2222-4222-8222-222222222222","title":"Low Water (Deluxe)","date":"2013"}]},
          {"id":"rec-9","title":"Harbor Lights (live)","artist-credit":[{"name":"The Tides"}],
           "releases":[{"id":"11111111-1111-4111-8111-111111111111","title":"Low Water","date":"2011"}]}
        ]}
        """;

    [Fact]
    public async Task AnAlbumSearch_ListsEachReleaseWithTheFactsThatTellPressingsApart()
    {
        var http = new FakeHttp(_ => Json(ReleaseSearch));

        var releases = await Client(http).SearchReleasesAsync("The Tides", "Low Water", null, 10, CancellationToken.None);

        Assert.NotNull(releases);
        Assert.Equal(2, releases.Count);
        var first = releases[0];
        Assert.Equal(ReleaseA, first.ReleaseId);
        Assert.Equal("rg-1", first.ReleaseGroupId);
        Assert.Equal("The Tides", first.Artist);
        Assert.Equal("2011", first.Year);
        Assert.Equal("Harbor Records", first.Label);
        Assert.Equal("CD", first.Format);
        Assert.Equal("GB", first.Country);
        Assert.Equal(11, first.TrackCount);
        Assert.True(first.HasFrontCover);
        Assert.Equal("2 × CD", releases[1].Format);
        Assert.Equal(24, releases[1].TrackCount);
        Assert.Contains("/release?query=", http.Requests[0], StringComparison.Ordinal);
        Assert.Contains("release%3A%22Low%20Water%22", http.Requests[0], StringComparison.Ordinal);
        Assert.Contains("artist%3A%22The%20Tides%22", http.Requests[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASongSearchWithNoAlbum_ListsTheReleasesItsRecordingsAppearOn_EachOnce()
    {
        var http = new FakeHttp(_ => Json(RecordingSearch));

        var releases = await Client(http).SearchReleasesAsync("The Tides", null, "Harbor Lights", 10, CancellationToken.None);

        Assert.Equal([ReleaseA, ReleaseB], releases!.Select(release => release.ReleaseId));
        Assert.All(releases!, release => Assert.Equal("The Tides", release.Artist));
        Assert.Contains("/recording?query=", http.Requests[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARelease_IsReadIntoDiscsAndTracks_WithLengthsAndEachRecordingsDisambiguation()
    {
        var http = new FakeHttp(_ => Json(ReleaseDetail));

        var detail = await Client(http).FetchReleaseDetailAsync(ReleaseB, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal([1, 2], detail.Discs.Select(disc => disc.Number));
        var track = Assert.Single(detail.Discs[1].Tracks, item => item.Track == 3);
        Assert.Equal("Harbor Lights", track.Title);
        Assert.Equal("rec-3", track.RecordingId);
        Assert.Equal("t-2-3", track.ReleaseTrackId);
        Assert.Equal(222d, track.LengthSeconds);
        Assert.Equal("2013 remaster", track.Disambiguation);
        Assert.Equal("The Tides", track.Artist);
        Assert.Equal([1, 3], detail.Discs[1].Tracks.Select(item => item.Track));
        Assert.Equal(181d, detail.Discs[1].Tracks[0].LengthSeconds);
        Assert.Null(detail.Discs[0].Tracks[0].Disambiguation);
        Assert.Contains("/release/" + ReleaseB, http.Requests[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReleaseAnswerForAnotherRelease_IsNotTakenForTheOneAskedFor()
    {
        var detail = await Client(new FakeHttp(_ => Json(ReleaseDetail))).FetchReleaseDetailAsync(ReleaseA, CancellationToken.None);

        Assert.Null(detail);
    }

    [Fact]
    public async Task AnIdThatIsNotAGuid_IsNeverSentToTheProvider()
    {
        var http = new FakeHttp(_ => Json(ReleaseDetail));

        Assert.Null(await Client(http).FetchReleaseDetailAsync("not-an-id", CancellationToken.None));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task NothingToSearchFor_AsksNothing()
    {
        var http = new FakeHttp(_ => Json(ReleaseSearch));

        var releases = await Client(http).SearchReleasesAsync("The Tides", null, null, 10, CancellationToken.None);

        Assert.Empty(releases!);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task AProviderThatDoesNotAnswer_IsNullNotEmpty_SoThePanelCanSayItDidNotAnswer()
    {
        var http = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        Assert.Null(await Client(http).SearchReleasesAsync(null, "Low Water", null, 10, CancellationToken.None));
    }

    [Fact]
    public async Task EveryRequest_GoesThroughTheProvidersRateLimiter()
    {
        var limiter = new LimiterSpy();
        var http = new FakeHttp(_ => Json(ReleaseSearch));

        await Client(http, limiter).SearchReleasesAsync(null, "Low Water", null, 10, CancellationToken.None);
        await Client(http, limiter).FetchReleaseDetailAsync(ReleaseB, CancellationToken.None);

        Assert.Equal(["musicbrainz", "musicbrainz"], limiter.Names);
        Assert.All(limiter.Rates, rate => Assert.Equal(ProviderRateLimitDefaults.MusicBrainz.RequestsPerSecond, rate));
    }

    [Fact]
    public async Task AReleaseAlreadyRead_CanBeLookedAtAgainWithoutAskingTheProvider_AndOneNeverReadIsNotAsked()
    {
        var http = new FakeHttp(_ => Json(ReleaseDetail));
        var client = Client(http, cacheHours: 24);

        Assert.Null(client.KeptReleaseDetail(ReleaseB));
        Assert.Empty(http.Requests);

        await client.FetchReleaseDetailAsync(ReleaseB, CancellationToken.None);
        var kept = client.KeptReleaseDetail(ReleaseB);

        Assert.NotNull(kept);
        Assert.Equal(ReleaseB, kept.Summary.ReleaseId);
        Assert.Single(http.Requests);
        Assert.Null(client.KeptReleaseDetail(ReleaseA));
        Assert.Null(client.KeptReleaseDetail("not-an-id"));
    }

    [Fact]
    public async Task AnAnswerWithContent_IsKeptForTheProvidersCacheTime_SoAskingAgainMakesNoSecondRequest()
    {
        var http = new FakeHttp(_ => Json(ReleaseSearch));
        var client = Client(http, cacheHours: 24);

        await client.SearchReleasesAsync(null, "Low Water", null, 10, CancellationToken.None);
        await client.SearchReleasesAsync(null, "Low Water", null, 10, CancellationToken.None);

        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task AnEmptyAnswer_IsNeverKept_SoTheNextAskGoesToTheProviderAgain()
    {
        var http = new FakeHttp(_ => Json("""{"releases":[]}"""));
        var client = Client(http, cacheHours: 24);

        var first = await client.SearchReleasesAsync(null, "Nothing Here", null, 10, CancellationToken.None);
        var second = await client.SearchReleasesAsync(null, "Nothing Here", null, 10, CancellationToken.None);

        Assert.Empty(first!);
        Assert.Empty(second!);
        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public async Task WithNoCacheTime_NothingIsKept()
    {
        var http = new FakeHttp(_ => Json(ReleaseSearch));
        var client = Client(http, cacheHours: null);

        await client.SearchReleasesAsync(null, "Low Water", null, 10, CancellationToken.None);
        await client.SearchReleasesAsync(null, "Low Water", null, 10, CancellationToken.None);

        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public void TheParsers_ReadAnAnswerWithNoReleasesAsEmpty_AndNullAsEmpty()
    {
        Assert.Empty(MusicBrainzReleaseClient.ParseReleaseSearch(JsonNode.Parse("{}")));
        Assert.Empty(MusicBrainzReleaseClient.ParseRecordingSearch(null));
        Assert.Null(MusicBrainzReleaseClient.ParseReleaseDetail(null, ReleaseA));
    }

    // ----- helpers -------------------------------------------------------------------------------------------------------------

    private static MusicBrainzReleaseClient Client(FakeHttp http, IProviderRateLimiterCoordinator? limiter = null, int? cacheHours = null)
    {
        var snapshot = new PipelineExecutionSnapshot(
            0,
            DateTimeOffset.UtcNow,
            new CoreConfiguration(),
            new HydrationSettings(),
            new PipelineConfiguration(),
            [
                new ProviderConfiguration
                {
                    Name = "musicbrainz",
                    Enabled = true,
                    CacheTtlHours = cacheHours,
                    Endpoints = new Dictionary<string, string> { ["api"] = "https://musicbrainz.test/ws/2" },
                },
            ]);
        return new MusicBrainzReleaseClient(http, limiter ?? new ProviderRateLimiterCoordinator(), NullLogger<MusicBrainzReleaseClient>.Instance, new FixedSnapshots(snapshot));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class FixedSnapshots(PipelineExecutionSnapshot snapshot) : IPipelineExecutionSnapshotProvider
    {
        public PipelineExecutionSnapshot Current => snapshot;
    }

    private sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> responder) : IHttpClientFactory
    {
        public List<string> Requests { get; } = [];

        private HttpResponseMessage Answer(HttpRequestMessage request) => responder(request);

        public HttpClient CreateClient(string name) => new(new Handler(this), disposeHandler: true);

        private sealed class Handler(FakeHttp owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Requests.Add(request.RequestUri!.AbsoluteUri);
                return Task.FromResult(owner.Answer(request));
            }
        }
    }

    private sealed class LimiterSpy : IProviderRateLimiterCoordinator
    {
        public List<string> Names { get; } = [];

        public List<double?> Rates { get; } = [];

        public Task<T> ExecuteAsync<T>(string providerName, ProviderRateLimitConfiguration? rateLimit, Func<CancellationToken, Task<T>> operation, CancellationToken ct)
        {
            Names.Add(providerName);
            Rates.Add(rateLimit?.RequestsPerSecond);
            return operation(ct);
        }

        public Task ExecuteAsync(string providerName, ProviderRateLimitConfiguration? rateLimit, Func<CancellationToken, Task> operation, CancellationToken ct)
        {
            Names.Add(providerName);
            Rates.Add(rateLimit?.RequestsPerSecond);
            return operation(ct);
        }

        public IReadOnlyList<ProviderActivitySnapshot> GetSnapshots() => [];
    }
}
