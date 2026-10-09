using System.IO.Compression;
using System.Net;
using System.Text;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

public sealed class SubdlTextTrackProviderTests
{
    [Fact]
    public async Task MovieSearch_UsesVerifiedTmdbIdentityAndDownloadsOneSubtitle()
    {
        var requests = new List<Uri>();
        // The v2 download endpoint returns a JSON file link, not subtitle bytes.
        var provider = CreateProvider(request =>
        {
            requests.Add(request.RequestUri!);
            if (request.RequestUri!.AbsolutePath.EndsWith("/download", StringComparison.Ordinal))
            {
                return Json("""{"url":"https://dl.subdl.com/subtitle/subtitle42/file42"}""");
            }
            if (request.RequestUri.Host == "api.subdl.com")
            {
                Assert.Equal("Bearer example-key", request.Headers.Authorization?.ToString());
                return Json("""{"status":true,"results":[{"type":"movie","tmdb_id":693134}],"subtitles":[{"n_id":"subtitle42","name":"Dune.Part.Two.2024.srt","language":"EN","release_name":"Dune.Part.Two.2024"}]}""");
            }
            Assert.Null(request.Headers.Authorization);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("1\n00:00:01,000 --> 00:00:02,000\nHello\n") };
        });
        var lookup = Lookup(MediaType.Movies, new SubtitleLookupContext(
            Movie: new MovieSubtitleIdentity(null, "693134")));
        var candidate = Assert.Single(await provider.SearchAsync(lookup));
        var download = await provider.DownloadAsync(candidate);

        Assert.NotNull(download);
        Assert.Equal("srt", download.SourceFormat);
        Assert.Contains("Hello", download.Content);
        Assert.Contains(requests, uri => uri.Query.Contains("tmdb_id=693134&type=movie", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TvSearch_UsesCrosswalkCoordinatesAndRejectsWrongEpisode()
    {
        Uri? searchUri = null;
        var provider = CreateProvider(request =>
        {
            searchUri = request.RequestUri;
            return Json("""
                {"status":true,"results":[{"type":"tv","tmdb_id":1396}],"subtitles":[
                  {"n_id":"wrong","season":2,"episode":1,"name":"Wrong.srt","language":"en"},
                  {"n_id":"right","season":1,"episode":13,"name":"Right.srt","language":"en"}]}
                """);
        });
        var context = new SubtitleLookupContext(
            TvdbEpisode: new TvdbEpisodeIdentity("81189", "123", 2, 1),
            TmdbEpisode: new TmdbEpisodeIdentity("1396", "789", 1, 13));

        var result = await provider.SearchAsync(Lookup(MediaType.TV, context));

        Assert.Equal("right", Assert.Single(result).SourceId);
        Assert.Contains("season=1&episode=13", searchUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingCrosswalkOrMismatchedTitle_DoesNotSearchOrSelect()
    {
        var calls = 0;
        var provider = CreateProvider(_ =>
        {
            calls++;
            return Json("""{"status":true,"results":[{"type":"tv","tmdb_id":999}],"subtitles":[{"n_id":"one","season":1,"episode":1,"name":"One.srt","language":"en"}]}""");
        });
        Assert.Empty(await provider.SearchAsync(Lookup(MediaType.TV,
            new SubtitleLookupContext(TvdbEpisode: new TvdbEpisodeIdentity("1", "2", 1, 1)))));
        Assert.Equal(0, calls);
        var mismatch = await Assert.ThrowsAsync<SubdlLookupException>(() => provider.SearchAsync(Lookup(MediaType.TV,
            new SubtitleLookupContext(TmdbEpisode: new TmdbEpisodeIdentity("1396", "789", 1, 1)))));
        Assert.Equal("IdentityMismatch", mismatch.Status);
    }

    [Fact]
    public async Task PackWithoutExactMemberAndUntrustedDownloadUrl_AreRejected()
    {
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/download", StringComparison.Ordinal))
            {
                return Json("""{"url":"https://evil.example/subtitle/file.srt"}""");
            }
            return Json("""
                {"status":true,"results":[{"type":"tv","tmdb_id":1396}],"subtitles":[
                  {"n_id":"pack","full_season":true,"season":1,"episode":1,"name":"Season.zip","language":"en"},
                  {"n_id":"single","season":1,"episode":13,"name":"Exact.srt","language":"en"}]}
                """);
        });
        var candidates = await provider.SearchAsync(Lookup(MediaType.TV,
            new SubtitleLookupContext(TmdbEpisode: new TmdbEpisodeIdentity("1396", "789", 1, 13))));
        var candidate = Assert.Single(candidates);
        Assert.Equal("single", candidate.SourceId);
        Assert.Null(await provider.DownloadAsync(candidate));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "AuthenticationRequired")]
    [InlineData(HttpStatusCode.TooManyRequests, "QuotaExceeded")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "ProviderUnavailable")]
    [InlineData(HttpStatusCode.Redirect, "ProviderError")]
    public async Task ApiFailures_AreNotReportedAsNoSubtitle(HttpStatusCode responseCode, string expectedStatus)
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(responseCode)
        {
            Content = new StringContent(responseCode == HttpStatusCode.TooManyRequests
                ? """{"error":{"code":"quota_exceeded"}}""" : "{}")
        });

        var exception = await Assert.ThrowsAsync<SubdlLookupException>(() =>
            provider.SearchAsync(Lookup(MediaType.Movies,
                new SubtitleLookupContext(Movie: new MovieSubtitleIdentity(null, "693134")))));

        Assert.Equal(expectedStatus, exception.Status);
    }

    [Fact]
    public async Task PackEntry_RejectsArchivePathTraversal()
    {
        byte[] archiveBytes;
        using (var buffer = new MemoryStream())
        {
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var writer = new StreamWriter(archive.CreateEntry("../Exact.srt").Open());
                writer.Write("1\n00:00:01,000 --> 00:00:02,000\nHello\n");
            }
            archiveBytes = buffer.ToArray();
        }
        var provider = CreateProvider(request => request.RequestUri!.Host == "api.subdl.com"
            ? Json("""
                {"status":true,"results":[{"type":"tv","tmdb_id":1396}],"subtitles":[
                  {"n_id":"pack","season":1,"full_season":true,"unpack_files":[
                    {"file_n_id":"file42","name":"Exact.srt","season":1,"episode":13,"language":"en","url":"/subtitle/pack/file42"}]}]}
                """)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archiveBytes) });
        var candidate = Assert.Single(await provider.SearchAsync(Lookup(MediaType.TV,
            new SubtitleLookupContext(TmdbEpisode: new TmdbEpisodeIdentity("1396", "789", 1, 13)))));

        Assert.Null(await provider.DownloadAsync(candidate));
    }

    [Fact]
    public async Task EmptySearch_IsBrieflyDeduplicatedAndCredentialRotationClearsIt()
    {
        var calls = 0;
        var provider = CreateProvider(request =>
        {
            calls++;
            Assert.Equal(calls == 1 ? "example-key" : "rotated-key", request.Headers.Authorization?.Parameter);
            return Json("""{"status":true,"results":[{"type":"movie","tmdb_id":693134}],"subtitles":[]}""");
        });
        var lookup = Lookup(MediaType.Movies,
            new SubtitleLookupContext(Movie: new MovieSubtitleIdentity(null, "693134")));

        Assert.Empty(await provider.SearchAsync(lookup));
        Assert.Empty(await provider.SearchAsync(lookup));
        Assert.Equal(1, calls);
        provider.ApplyCredentials(new Dictionary<string, string?> { ["api_key"] = "rotated-key" });
        Assert.Empty(await provider.SearchAsync(lookup));
        Assert.Equal(2, calls);
    }

    private static TextTrackLookup Lookup(MediaType type, SubtitleLookupContext? context) => new(
        new MediaAsset { Id = Guid.NewGuid(), FilePathRoot = "Dune.Part.Two.2024.mkv" },
        type, "Dune: Part Two", null, null, "2024", "en", null,
        new Dictionary<string, string>(), context);

    private static SubdlTextTrackProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var config = new ProviderConfiguration
        {
            Name = "SubDL",
            Enabled = true,
            HttpClient = new HttpClientConfig { ApiKey = "example-key" }
        };
        return new(config, new StubFactory(respond), NullProviderHealthMonitor.Instance,
            NullLogger<SubdlTextTrackProvider>.Instance);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubFactory(Func<HttpRequestMessage, HttpResponseMessage> respond) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(respond));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
