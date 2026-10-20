using System.Net;
using System.Text.Json;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Providers;
using MediaEngine.Providers.Tests.ProviderRecording;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// A subtitle source and a lyrics source nobody has heard of run from their files alone, with a
/// different host, different paths and a different response shape from the shipped ones.
/// </summary>
public sealed class TextTrackJsonOnlyProviderTests
{
    private const string SubtitleFile = """
        {
          "name": "cinema_subs", "display_name": "Cinema Subs", "enabled": true, "weight": 1, "domain": "Video",
          "adapter_type": "text_track", "requires_api_key": true,
          "endpoints": { "api": "https://api.cinema-subs.test" },
          "http_client": { "api_key": "k-123", "api_key_delivery": "bearer" },
          "can_handle": { "media_types": ["Movies"], "entity_types": ["MediaAsset"] },
          "text_track": {
            "kind": "Subtitles", "noun": "subtitles",
            "api": { "allowed_hosts": ["api.cinema-subs.test"], "accept": "application/json" },
            "language": { "aliases": { "german": "de" } },
            "search": {
              "requests": [ { "when_media_type": "Movies", "needs": ["imdb_id"], "path": "/v1/find",
                "query": [ ["imdb", "{imdb_id}"], ["lang", "{language}"] ] } ],
              "empty_search_minutes": 5, "titles_path": "movies", "items_path": "files", "max_items": 10,
              "item": { "id_paths": ["id"], "release_paths": ["release"], "name_path": "file", "language_paths": ["lang"] },
              "score": { "base": 0.7, "release_overlap_weight": 0.2, "format_bonus": { "srt": 0.05 } },
              "ambiguity_margin": 0.01, "max_candidates": 2, "source_url": "https://cinema-subs.test/s/{id}"
            },
            "download": {
              "mode": "fetch", "allowed_hosts": ["files.cinema-subs.test"], "max_bytes": 1048576,
              "formats": ["srt"], "output_format": "vtt", "resolve": { "path": "/v1/file/{id}", "url_paths": ["link"] }
            },
            "errors": { "statuses": [ { "codes": [401], "status": "AuthenticationRequired" } ], "default_status": "ProviderError" },
            "messages": { "AuthenticationRequired": "{display_name} did not accept the key." }
          }
        }
        """;

    private static TextTrackLookup Movie(string imdb) => new(
        new MediaAsset { Id = Guid.NewGuid(), FilePathRoot = "Dune.2021.mkv" },
        MediaType.Movies, "Dune", null, null, "2021", "de", null,
        new Dictionary<string, string>(),
        new SubtitleLookupContext(Movie: new MovieSubtitleIdentity(imdb, null)));

    [Fact]
    public async Task UnknownSubtitleSource_IsSearchedAndDownloadedFromItsFile()
    {
        var config = JsonSerializer.Deserialize<ProviderConfiguration>(SubtitleFile)!;
        var handler = new RecordingHandler(request =>
        {
            if (request.Uri.AbsolutePath == "/v1/find")
            {
                return RecordingHandler.Json("""{"movies":[{"type":"x"}],"files":[{"id":"a1","release":"Dune.2021","file":"dune.srt","lang":"German"}]}""");
            }

            return request.Uri.AbsolutePath == "/v1/file/a1"
                ? RecordingHandler.Json("""{"link":"https://files.cinema-subs.test/x/dune.srt"}""")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("1\n00:00:01,000 --> 00:00:02,000\nHallo\n") };
        });
        var provider = new TextTrackProviderAdapter(
            config, new RecordingHttpFactory(handler), NullProviderHealthMonitor.Instance,
            NullLogger<TextTrackProviderAdapter>.Instance);

        var candidate = Assert.Single(await provider.SearchAsync(Movie("tt1160419")));
        var download = await provider.DownloadAsync(candidate);

        Assert.Equal("cinema_subs", candidate.Provider);
        Assert.Equal("https://cinema-subs.test/s/a1", candidate.SourceUrl);
        Assert.Equal("de", candidate.Language);
        Assert.Equal(0.7 + 1.0 * 0.2 + 0.05, candidate.Confidence, 5);
        Assert.NotNull(download);
        Assert.Contains("Hallo", download.Content, StringComparison.Ordinal);
        Assert.Equal(
            [
                "GET https://api.cinema-subs.test/v1/find?imdb=tt1160419&lang=de [auth:Bearer]",
                "GET https://api.cinema-subs.test/v1/file/a1 [auth:Bearer]",
                "GET https://files.cinema-subs.test/x/dune.srt",
            ],
            handler.Lines);
    }

    [Fact]
    public async Task UnknownSubtitleSource_UsesItsOwnErrorMapAndMessages()
    {
        var config = JsonSerializer.Deserialize<ProviderConfiguration>(SubtitleFile)!;
        var handler = new RecordingHandler(_ => RecordingHandler.Status(HttpStatusCode.Unauthorized));
        var provider = new TextTrackProviderAdapter(
            config, new RecordingHttpFactory(handler), NullProviderHealthMonitor.Instance,
            NullLogger<TextTrackProviderAdapter>.Instance);

        var failure = await Assert.ThrowsAsync<TextTrackLookupException>(() => provider.SearchAsync(Movie("tt1160419")));

        Assert.Equal("AuthenticationRequired", failure.Status);
        Assert.Equal("Cinema Subs did not accept the key.", failure.Message);
    }

    [Fact]
    public async Task UnknownSubtitleSource_DoesNotSearchWithoutAVerifiedIdentity()
    {
        var config = JsonSerializer.Deserialize<ProviderConfiguration>(SubtitleFile)!;
        var handler = new RecordingHandler(_ => RecordingHandler.Json("{}"));
        var provider = new TextTrackProviderAdapter(
            config, new RecordingHttpFactory(handler), NullProviderHealthMonitor.Instance,
            NullLogger<TextTrackProviderAdapter>.Instance);

        Assert.Empty(await provider.SearchAsync(Movie("not-an-imdb-id")));
        Assert.Empty(handler.Lines);
    }

    [Fact]
    public void TheShippedSubtitleSourceReportsIncompatibleDownloads_AndALyricsSourceDoesNot()
    {
        var subdl = ShippedProviderHarness.Subdl(new RecordingHandler(_ => RecordingHandler.Json("{}")));
        var lrclib = ShippedProviderHarness.Lrclib(new RecordingHandler(_ => RecordingHandler.Json("{}")));

        Assert.True(subdl.ReportsIncompatibleDownloads);
        Assert.False(lrclib.ReportsIncompatibleDownloads);
    }
}
