using System.Net;
using System.Text;
using System.Text.Json;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Providers;
using MediaEngine.Providers.Tests.ProviderRecording;
using Microsoft.Extensions.Logging.Abstractions;
namespace MediaEngine.Providers.Tests;

public sealed class LrclibRequestTests
{
    [Fact]
    public async Task SongWithAllDetails_UsesTheExactLookup()
    {
        var seen = new List<string>();
        var provider = Provider(seen, request => Json(new { id = 1, duration = 180, syncedLyrics = "[00:01]Line" }));
        Assert.Single(await provider.SearchAsync(Lookup(album: "Album")));
        Assert.StartsWith("/api/get?", Assert.Single(seen));
    }

    [Fact]
    public async Task SongWithNoAlbum_UsesSearchInsteadOfAnExactLookupThatWouldBeRefused()
    {
        var seen = new List<string>();
        var hits = new[]
        {
            new { id = 5, trackName = "Fixture song", artistName = "Artist", duration = 240, syncedLyrics = "[00:01]Wrong length" },
            new { id = 6, trackName = "Fixture song", artistName = "Artist", duration = 181, syncedLyrics = "[00:01]Right one" },
        };
        var provider = Provider(seen, _ => Json(hits));
        var candidate = Assert.Single(await provider.SearchAsync(Lookup(album: null)));
        Assert.Equal("6", candidate.SourceId);
        Assert.All(seen, url => Assert.StartsWith("/api/search?", url));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task NotFoundOrRefusedAnswer_MeansNoLyricsAndDoesNotThrow(HttpStatusCode status)
    {
        var provider = Provider([], _ => new HttpResponseMessage(status));
        Assert.Empty(await provider.SearchAsync(Lookup(album: "Album")));
    }

    [Fact]
    public async Task ExactLookupThatFindsNothing_FallsBackToSearch()
    {
        var seen = new List<string>();
        var provider = Provider(seen, request => request.StartsWith("/api/get", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Json(new[] { new { id = 9, trackName = "Fixture song", artistName = "Artist", duration = 180, plainLyrics = "Words" } }));
        var candidate = Assert.Single(await provider.SearchAsync(Lookup(album: "Album")));
        Assert.Equal("9", candidate.SourceId);
        Assert.Equal(2, seen.Count);
    }

    private static HttpResponseMessage Json(object value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    private static TextTrackLookup Lookup(string? album) =>
        new(new MediaAsset { Id = Guid.NewGuid(), FilePathRoot = "fixture.wav" }, MediaType.Music, "Fixture song", "Artist", album, null, "en", 180, new Dictionary<string, string>());

    private static TextTrackProviderAdapter Provider(List<string> seen, Func<string, HttpResponseMessage> answer) => new(
        ShippedProviderHarness.LoadShipped("lrclib"),
        new Factory(seen, answer),
        NullProviderHealthMonitor.Instance,
        NullLogger<TextTrackProviderAdapter>.Instance);

    private sealed class Factory(List<string> seen, Func<string, HttpResponseMessage> answer) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(seen, answer));
    }

    private sealed class Handler(List<string> seen, Func<string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery;
            seen.Add(path);
            return Task.FromResult(answer(path));
        }
    }
}
