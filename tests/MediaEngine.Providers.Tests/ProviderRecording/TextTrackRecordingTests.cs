using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Contracts;

namespace MediaEngine.Providers.Tests.ProviderRecording;

/// <summary>
/// Records the exact requests, candidates, downloads and failures of the shipped SubDL and
/// LRCLIB providers. The golden files pin behaviour across the WP9 conversion to the JSON-driven
/// text-track adapter.
/// </summary>
public sealed class TextTrackRecordingTests
{
    private const string Srt = "1\n00:00:01,000 --> 00:00:02,000\nHello\n";

    private static TextTrackLookup SubtitleLookup(MediaType type, SubtitleLookupContext? context) => new(
        new MediaAsset { Id = Guid.Parse("33333333-3333-3333-3333-333333333333"), FilePathRoot = "Dune.Part.Two.2024.1080p.mkv" },
        type, "Dune: Part Two", null, null, "2024", "en", null,
        new Dictionary<string, string>(), context);

    private static TextTrackLookup MusicLookup(
        string? artist = "Artist", string? album = "Album", double? duration = 180, string? language = "en") => new(
        new MediaAsset { Id = Guid.Parse("44444444-4444-4444-4444-444444444444"), FilePathRoot = "fixture.flac" },
        MediaType.Music, "Fixture song", artist, album, null, language, duration, new Dictionary<string, string>());

    private static SubtitleLookupContext Movie(string? imdb, string? tmdb) =>
        new(Movie: new MovieSubtitleIdentity(imdb, tmdb));

    private static SubtitleLookupContext Episode(string show, int season, int episode) =>
        new(TmdbEpisode: new TmdbEpisodeIdentity(show, "789", season, episode));

    private static string Candidate(TextTrackCandidate c) => string.Join('|',
        c.Provider, c.Kind, c.SourceId, c.SourceUrl, c.Language, c.SourceFormat,
        c.Confidence.ToString("0.######", CultureInfo.InvariantCulture), c.IsHearingImpaired,
        c.DurationMatchScore?.ToString("0.######", CultureInfo.InvariantCulture) ?? "-",
        c.IsForced, c.ReleaseName ?? "-", c.IsInstrumental, c.Payload as string ?? "-");

    private static string Download(TextTrackDownload? d) => d is null
        ? "download: null"
        : $"download: {d.SourceFormat}->{d.NormalizedFormat} {d.Content.Replace("\n", "\\n", StringComparison.Ordinal)}";

    private static string Failure(Exception ex)
    {
        var type = ex.GetType();
        var status = type.GetProperty("Status")?.GetValue(ex);
        var retryAt = type.GetProperty("RetryAt")?.GetValue(ex) as DateTimeOffset?;
        return $"EXCEPTION {status}: {ex.Message.Split(" Try again after")[0]}{(retryAt is null ? string.Empty : " [retry-at]")}";
    }

    private static async Task<List<string>> RunAsync(
        ITextTrackProvider provider, TextTrackLookup lookup, bool download = true)
    {
        var lines = new List<string>();
        try
        {
            var candidates = await provider.SearchAsync(lookup);
            lines.AddRange(candidates.Select(Candidate));
            if (download && candidates.Count > 0)
            {
                lines.Add(Download(await provider.DownloadAsync(candidates[0])));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lines.Add(Failure(ex));
        }

        return lines;
    }

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

    private static HttpResponseMessage Bytes(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

    private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };

    [Fact]
    public async Task SubdlMovieByTmdbId_SearchesAndDownloadsThroughResolvedLink()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.Uri.AbsolutePath.EndsWith("/download", StringComparison.Ordinal))
            {
                return RecordingHandler.Json("""{"url":"https://dl.subdl.com/subtitle/subtitle42/file42"}""");
            }

            return request.Uri.Host == "api.subdl.com"
                ? RecordingHandler.Json("""{"status":true,"results":[{"type":"movie","tmdb_id":693134}],"subtitles":[{"n_id":"subtitle42","name":"Dune.Part.Two.2024.srt","language":"EN","release_name":"Dune.Part.Two.2024.1080p"},{"n_id":"hi1","name":"Dune.hi.vtt","language":"en","hi":true,"release_name":"Other"}]}""")
                : Text(Srt);
        });

        var lines = await RunAsync(ShippedProviderHarness.Subdl(handler), SubtitleLookup(MediaType.Movies, Movie(null, "693134")));

        RecordingSnapshot.Verify("subdl-movie-tmdb",
            RecordingText.Section("requests", handler.Lines), RecordingText.Section("results", lines));
    }

    [Fact]
    public async Task SubdlMovieByImdbId_UsesImdbQuery()
    {
        var handler = new RecordingHandler(request => RecordingHandler.Json(
            """{"status":true,"results":[{"type":"movie","imdb_id":"tt15239678"}],"subtitles":[{"n_id":"imdb1","name":"Dune.vtt","lang":"english","release_name":"Dune"}]}"""));

        var lines = await RunAsync(ShippedProviderHarness.Subdl(handler),
            SubtitleLookup(MediaType.Movies, Movie("tt15239678", null)), download: false);

        RecordingSnapshot.Verify("subdl-movie-imdb",
            RecordingText.Section("requests", handler.Lines), RecordingText.Section("results", lines));
    }

    [Fact]
    public async Task SubdlTvPack_ChoosesExactEpisodeMemberAndExtractsFromZip()
    {
        var zip = Zip(("Exact.srt", Srt), ("Other.srt", "other"));
        var handler = new RecordingHandler(request => request.Uri.Host == "api.subdl.com"
            ? RecordingHandler.Json("""
                {"status":true,"results":[{"type":"tv","tmdb_id":1396}],"subtitles":[
                  {"n_id":"wrong","season":2,"episode":1,"name":"Wrong.srt","language":"en"},
                  {"n_id":"pack","season":1,"full_season":true,"release_name":"Breaking.Bad.S01","unpack_files":[
                    {"file_n_id":"file41","name":"Other.srt","season":1,"episode":12,"language":"en","url":"/subtitle/pack/file41"},
                    {"file_n_id":"file42","name":"Exact.srt","season":1,"episode":13,"language":"en","hi":true,"url":"/subtitle/pack/file42"},
                    {"file_n_id":"file43","name":"Exact.fr.srt","season":1,"episode":13,"language":"fr","url":"/subtitle/pack/file43"},
                    {"file_n_id":"bad id","name":"Bad.srt","season":1,"episode":13,"language":"en","url":"/subtitle/pack/bad"},
                    {"file_n_id":"txt1","name":"Notes.txt","season":1,"episode":13,"language":"en","url":"/subtitle/pack/txt1"}]}]}
                """)
            : Bytes(zip));

        var lines = await RunAsync(ShippedProviderHarness.Subdl(handler), SubtitleLookup(MediaType.TV, Episode("1396", 1, 13)));

        RecordingSnapshot.Verify("subdl-tv-pack-zip",
            RecordingText.Section("requests", handler.Lines), RecordingText.Section("results", lines));
    }

    [Fact]
    public async Task SubdlEquallyScoredReleases_AreReportedAmbiguous()
    {
        var handler = new RecordingHandler(request => RecordingHandler.Json("""
            {"status":true,"results":[{"type":"movie","tmdb_id":693134}],"subtitles":[
              {"n_id":"a","name":"A.srt","language":"en","release_name":"Dune.Part.Two.2024.1080p"},
              {"n_id":"b","name":"B.srt","language":"en","release_name":"Dune.Part.Two.2024.1080p"}]}
            """));

        var lines = await RunAsync(ShippedProviderHarness.Subdl(handler), SubtitleLookup(MediaType.Movies, Movie(null, "693134")));

        RecordingSnapshot.Verify("subdl-ambiguous",
            RecordingText.Section("requests", handler.Lines), RecordingText.Section("results", lines));
    }

    [Fact]
    public async Task SubdlWrongTitleOrNoCrosswalk_IsRejectedWithoutGuessing()
    {
        var handler = new RecordingHandler(request => RecordingHandler.Json(
            """{"status":true,"results":[{"type":"tv","tmdb_id":999}],"subtitles":[{"n_id":"one","season":1,"episode":1,"name":"One.srt","language":"en"}]}"""));
        var provider = ShippedProviderHarness.Subdl(handler);

        var lines = new List<string>();
        lines.AddRange(await RunAsync(provider, SubtitleLookup(MediaType.TV,
            new SubtitleLookupContext(TvdbEpisode: new TvdbEpisodeIdentity("1", "2", 1, 1)))));
        lines.AddRange(await RunAsync(provider, SubtitleLookup(MediaType.TV, Episode("1396", 1, 1))));
        lines.AddRange(await RunAsync(provider, SubtitleLookup(MediaType.Movies, null)));
        lines.AddRange(await RunAsync(provider, SubtitleLookup(MediaType.Movies, Movie("not-an-id", null))));
        lines.AddRange(await RunAsync(provider, SubtitleLookup(MediaType.Music, null)));

        RecordingSnapshot.Verify("subdl-identity-guards",
            RecordingText.Section("requests", handler.Lines), RecordingText.Section("results", lines));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{}", "401")]
    [InlineData(HttpStatusCode.Forbidden, "{}", "403")]
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":{"code":"quota_exceeded"}}""", "429-quota")]
    [InlineData(HttpStatusCode.TooManyRequests, "{}", "429-plain")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{}", "503")]
    [InlineData(HttpStatusCode.Redirect, "{}", "302")]
    [InlineData(HttpStatusCode.BadRequest, "not json", "400")]
    public async Task SubdlFailures_MapToStableStatuses(HttpStatusCode code, string body, string tag)
    {
        var handler = new RecordingHandler(request =>
        {
            var response = new HttpResponseMessage(code) { Content = new StringContent(body) };
            if (code == HttpStatusCode.TooManyRequests)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
            }

            return response;
        });

        var lines = await RunAsync(ShippedProviderHarness.Subdl(handler), SubtitleLookup(MediaType.Movies, Movie(null, "693134")));

        RecordingSnapshot.Verify($"subdl-failure-{tag}",
            RecordingText.Section("requests", handler.Lines), RecordingText.Section("results", lines));
    }

    [Fact]
    public async Task SubdlDownloads_RejectUntrustedHostsTraversalAndOversizedFiles()
    {
        var traversal = Zip(("../Exact.srt", Srt));
        var mode = "evil";
        var handler = new RecordingHandler(request =>
        {
            if (request.Uri.Host == "api.subdl.com" && request.Uri.AbsolutePath.EndsWith("/download", StringComparison.Ordinal))
            {
                return RecordingHandler.Json(mode == "evil"
                    ? """{"url":"https://evil.example/subtitle/file.srt"}"""
                    : """{"link":"https://dl.subdl.com/subtitle/a/b"}""");
            }

            if (request.Uri.Host == "api.subdl.com")
            {
                return RecordingHandler.Json("""{"status":true,"results":[{"type":"movie","tmdb_id":693134}],"subtitles":[{"n_id":"one","name":"One.srt","language":"en"}]}""");
            }

            return mode switch
            {
                "traversal" => Bytes(traversal),
                "empty" => Bytes([]),
                "binary" => Bytes([1, 2, 3, 4]),
                "bad-vtt" => Text("no cues in here"),
                _ => Text(Srt),
            };
        });
        var provider = ShippedProviderHarness.Subdl(handler);
        var candidate = Assert.Single(await provider.SearchAsync(SubtitleLookup(MediaType.Movies, Movie(null, "693134"))));

        var lines = new List<string>();
        foreach (var next in new[] { "evil", "traversal", "empty", "binary", "bad-vtt", "good" })
        {
            mode = next;
            lines.Add($"{next}: {Download(await provider.DownloadAsync(candidate))}");
        }

        RecordingSnapshot.Verify("subdl-download-guards",
            RecordingText.Section("requests", handler.Lines), RecordingText.Section("results", lines));
    }

    [Fact]
    public async Task SubdlEmptySearch_IsDeduplicatedUntilCredentialsRotate()
    {
        var handler = new RecordingHandler(request => RecordingHandler.Json(
            """{"status":true,"results":[{"type":"movie","tmdb_id":693134}],"subtitles":[]}"""));
        var provider = ShippedProviderHarness.Subdl(handler);
        var lookup = SubtitleLookup(MediaType.Movies, Movie(null, "693134"));

        var lines = new List<string>();
        lines.AddRange(await RunAsync(provider, lookup));
        lines.AddRange(await RunAsync(provider, lookup));
        ((IProviderCredentialConsumer)provider).ApplyCredentials(new Dictionary<string, string?> { ["api_key"] = "rotated-key" });
        lines.AddRange(await RunAsync(provider, lookup));
        lines.Add($"availability: {provider.GetAvailability(MediaType.Movies).Status}");

        RecordingSnapshot.Verify("subdl-empty-dedupe",
            RecordingText.Section("requests", handler.Lines), RecordingText.Section("results", lines));
    }

    [Fact]
    public async Task SubdlWithoutApiKey_ReportsAuthenticationRequiredBeforeAnyRequest()
    {
        var handler = new RecordingHandler(request => RecordingHandler.Json("{}"));
        var provider = ShippedProviderHarness.Subdl(handler, apiKey: null);

        var lines = await RunAsync(provider, SubtitleLookup(MediaType.Movies, Movie(null, "693134")));
        lines.Add($"availability: {provider.GetAvailability(MediaType.Movies).Status}|{provider.GetAvailability(MediaType.Music).Status}");

        RecordingSnapshot.Verify("subdl-no-key",
            RecordingText.Section("requests", handler.Lines), RecordingText.Section("results", lines));
    }

    [Fact]
    public async Task LrclibSyncedLyrics_RecordsRequestCandidateAndDownload()
    {
        var handler = new RecordingHandler(request => RecordingHandler.Json(
            """{"id":4711,"duration":181,"syncedLyrics":"[00:01.00]Line one\n[00:02.00]Line two","plainLyrics":"Line one\nLine two","instrumental":false}"""));

        var lines = await RunAsync(ShippedProviderHarness.Lrclib(handler), MusicLookup(artist: "Artist & Co", album: "Album/Name"));

        RecordingSnapshot.Verify("lrclib-synced",
            RecordingText.Section("requests", handler.Lines), RecordingText.Section("results", lines));
    }

    [Fact]
    public async Task LrclibVariants_CoverPlainInstrumentalMismatchAndMinimalQueries()
    {
        var lines = new List<string>();
        var requests = new List<string>();
        foreach (var (tag, json, lookup) in new (string, string, TextTrackLookup)[]
        {
            ("plain-only", """{"id":5,"duration":180,"syncedLyrics":null,"plainLyrics":"Just words"}""", MusicLookup()),
            ("plain-far-duration", """{"id":6,"duration":300,"syncedLyrics":"","plainLyrics":"Just words"}""", MusicLookup()),
            ("synced-far-duration", """{"id":7,"duration":300,"syncedLyrics":"[00:01.00]X","plainLyrics":"X"}""", MusicLookup()),
            ("synced-no-duration-info", """{"id":8,"syncedLyrics":"[00:01.00]X","plainLyrics":"X"}""", MusicLookup()),
            ("synced-no-request-duration", """{"id":9,"duration":100,"syncedLyrics":"[00:01.00]X","plainLyrics":"X"}""", MusicLookup(duration: null)),
            ("instrumental", """{"id":10,"duration":180,"syncedLyrics":null,"plainLyrics":null,"instrumental":true}""", MusicLookup()),
            ("empty", """{"id":11,"duration":180,"syncedLyrics":null,"plainLyrics":null}""", MusicLookup()),
            ("no-id-title-only", """{"duration":180,"plainLyrics":"No id"}""", MusicLookup(artist: null, album: null, duration: null, language: null)),
            ("rounded-duration", """{"id":12,"duration":181.4,"syncedLyrics":"[00:01.00]X"}""", MusicLookup(duration: 180.6)),
        })
        {
            var handler = new RecordingHandler(request => RecordingHandler.Json(json));
            var found = await RunAsync(ShippedProviderHarness.Lrclib(handler), lookup);
            lines.Add($"-- {tag}");
            lines.AddRange(found);
            requests.AddRange(handler.Lines);
        }

        RecordingSnapshot.Verify("lrclib-variants",
            RecordingText.Section("requests", requests), RecordingText.Section("results", lines));
    }

    [Fact]
    public async Task LrclibNotFoundOrUnavailable_ReturnsNothing()
    {
        var lines = new List<string>();
        var requests = new List<string>();
        foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.InternalServerError })
        {
            var handler = new RecordingHandler(request => RecordingHandler.Status(status));
            lines.Add($"-- {(int)status}");
            lines.AddRange(await RunAsync(ShippedProviderHarness.Lrclib(handler), MusicLookup()));
            requests.AddRange(handler.Lines);
        }

        var provider = ShippedProviderHarness.Lrclib(new RecordingHandler(request => RecordingHandler.Json("{}")));
        lines.Add($"availability: {provider.GetAvailability(MediaType.Music).Status}|{provider.GetAvailability(MediaType.Movies).Status}");

        RecordingSnapshot.Verify("lrclib-failures",
            RecordingText.Section("requests", requests), RecordingText.Section("results", lines));
    }
}
