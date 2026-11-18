using System.Net;
using System.Text;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Providers;
using MediaEngine.Providers.Tests.ProviderRecording;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// Searching by words a person typed in the "pick a result" window (review mock-ups 2026-11-18, D7 follow-up): the shipped lyrics and subtitle
/// sources are asked for the text as written instead of the item's own details. Shipped provider files run by the general adapter; the web
/// is a hand-written stub that records what it was asked, never the live source.
/// </summary>
public sealed class TextTrackTextSearchProviderTests
{
    private const string Hits = """
        [
          {"id":1,"trackName":"Bohemian Rhapsody","artistName":"Queen","duration":354,"syncedLyrics":"[00:01.00]Is this the real life"},
          {"id":2,"trackName":"Bohemian Rhapsody (Live)","artistName":"Queen","duration":400,"plainLyrics":"Live words"},
          {"id":3,"trackName":"Under Pressure","artistName":"Queen","duration":248,"syncedLyrics":"[00:01.00]Pressure"},
          {"id":4,"trackName":"Rhapsody","artistName":"Someone Else","duration":200,"plainLyrics":"Other"},
          {"id":5,"trackName":"Bohemian Rhapsody","artistName":"Queen","duration":354,"instrumental":true}
        ]
        """;

    // -- Lyrics (LRCLIB) -------------------------------------------------------

    [Fact]
    public async Task LyricsTextSearch_AsksTheSourceForTheTypedTextAlone_NotTheSongsOwnDetails()
    {
        var seen = new List<string>();
        var provider = Lyrics(seen, _ => Json(Hits));

        await provider.SearchAsync(SongLookup("bohemian queen"));

        var request = Assert.Single(seen);
        Assert.StartsWith("/api/search?q=bohemian%20queen", request, StringComparison.Ordinal);
        Assert.DoesNotContain("track_name", request, StringComparison.Ordinal);
        Assert.DoesNotContain("artist_name", request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LyricsTextSearch_OffersEveryEntryWhoseTitleOrArtistCarriesAllTheWords_NotJustOne()
    {
        var provider = Lyrics([], _ => Json(Hits));

        var found = await provider.SearchAsync(SongLookup("bohemian queen"));

        // The two Bohemian Rhapsody entries (and the instrumental one, which the caller filters out) carry both words; the others do not.
        Assert.Equal(["1", "2", "5"], found.Select(candidate => candidate.SourceId).Order());
        Assert.Equal("[00:01.00]Is this the real life", found.First(candidate => candidate.SourceId == "1").Payload);
    }

    [Fact]
    public async Task LyricsTextSearch_IgnoresTheSongsOwnLength_SoAnotherVersionIsNotRejected()
    {
        var provider = Lyrics([], _ => Json(Hits));

        // The song the file holds is 180 seconds; the entry the person is after is 354 seconds long.
        var found = await provider.SearchAsync(SongLookup("under pressure") with { DurationSeconds = 180 });

        var candidate = Assert.Single(found);
        Assert.Equal("3", candidate.SourceId);
        Assert.Null(candidate.DurationMatchScore);
    }

    [Fact]
    public async Task LyricsTextSearch_ThatFindsNothing_IsAnEmptyList()
    {
        var provider = Lyrics([], _ => new HttpResponseMessage(HttpStatusCode.NotFound));

        Assert.Empty(await provider.SearchAsync(SongLookup("nothing like this")));
    }

    [Fact]
    public async Task LyricsTextSearch_ThatCannotReachTheSource_IsAnUnreachableFailure_NotNoLyrics()
    {
        var provider = Lyrics([], _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var failure = await Assert.ThrowsAsync<TextTrackLookupException>(() => provider.SearchAsync(SongLookup("anything")));

        Assert.Equal("ProviderUnavailable", failure.Status);
    }

    [Fact]
    public async Task ASongSearchWithoutTypedText_StillUsesTitleAndArtist()
    {
        var seen = new List<string>();
        var provider = Lyrics(seen, _ => Json(Hits));

        await provider.SearchAsync(SongLookup(null));

        Assert.StartsWith("/api/search?track_name=Bohemian%20Rhapsody&artist_name=Queen", Assert.Single(seen), StringComparison.Ordinal);
    }

    [Fact]
    public void LyricsSource_SaysItCanBeSearchedByWords_ForMusicOnly()
    {
        var provider = Lyrics([], _ => Json("[]"));

        Assert.True(provider.SupportsSearchText(MediaType.Music));
        Assert.False(provider.SupportsSearchText(MediaType.Movies));
    }

    // -- Subtitles (SubDL) ------------------------------------------------------

    [Fact]
    public async Task SubtitleTextSearch_ForAMovie_NeedsNoVerifiedIdentity_AndSearchesByTheTypedName()
    {
        var seen = new List<Uri>();
        var provider = Subtitles(request =>
        {
            seen.Add(request.RequestUri!);
            return Json("""
                {"status":true,"results":[{"type":"movie","name":"Dune"}],"subtitles":[
                  {"n_id":"a","name":"Dune.2021.srt","language":"EN","release_name":"Dune.2021.1080p"},
                  {"n_id":"b","name":"Dune.2021.web.srt","language":"EN","release_name":"Dune.2021.WEB"}]}
                """);
        });

        var found = await provider.SearchAsync(MovieLookup("dune 2021", context: null));

        var uri = Assert.Single(seen);
        Assert.Contains("film_name=dune%202021&type=movie", uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("tmdb_id", uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("imdb_id", uri.Query, StringComparison.Ordinal);
        // Two equally good releases are both offered: a person is choosing, so a tie is not an error.
        Assert.Equal(["a", "b"], found.Select(candidate => candidate.SourceId).Order());
    }

    [Fact]
    public async Task SubtitleTextSearch_ForAnEpisode_KeepsTheSeasonAndEpisodeAndDropsTheOnlineId()
    {
        var seen = new List<Uri>();
        var provider = Subtitles(request =>
        {
            seen.Add(request.RequestUri!);
            return Json("""
                {"status":true,"results":[{"type":"tv"}],"subtitles":[
                  {"n_id":"wrong","season":1,"episode":2,"name":"Wrong.srt","language":"en"},
                  {"n_id":"right","season":1,"episode":13,"name":"Right.srt","language":"en"}]}
                """);
        });
        var context = new SubtitleLookupContext(TmdbEpisode: new TmdbEpisodeIdentity("1396", "789", 1, 13));

        var found = await provider.SearchAsync(Lookup(MediaType.TV, "breaking bad", context));

        var uri = Assert.Single(seen);
        Assert.Contains("film_name=breaking%20bad&type=tv&season=1&episode=13", uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("tmdb_id", uri.Query, StringComparison.Ordinal);
        Assert.Equal("right", Assert.Single(found).SourceId);
    }

    [Fact]
    public async Task SubtitleTextSearch_ForAnEpisodeThatCannotBeIdentified_SearchesByNameAlone()
    {
        var seen = new List<Uri>();
        var provider = Subtitles(request =>
        {
            seen.Add(request.RequestUri!);
            return Json("""{"status":true,"results":[{"type":"tv"}],"subtitles":[{"n_id":"x","season":1,"episode":1,"name":"X.srt","language":"en"}]}""");
        });

        Assert.Single(await provider.SearchAsync(Lookup(MediaType.TV, "the show", context: null)));

        var uri = Assert.Single(seen);
        Assert.Contains("film_name=the%20show&type=tv", uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("season=", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASubtitleSearchWithoutTypedText_StillNeedsAVerifiedIdentity()
    {
        var calls = 0;
        var provider = Subtitles(_ => { calls++; return Json("""{"status":true,"results":[],"subtitles":[]}"""); });

        Assert.Empty(await provider.SearchAsync(MovieLookup(null, context: null)));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void SubtitleSource_SaysItCanBeSearchedByWords_ForMoviesAndEpisodes()
    {
        var provider = Subtitles(_ => Json("{}"));

        Assert.True(provider.SupportsSearchText(MediaType.Movies));
        Assert.True(provider.SupportsSearchText(MediaType.TV));
        Assert.False(provider.SupportsSearchText(MediaType.Music));
    }

    // -- The matching rule -----------------------------------------------------

    [Theory]
    [InlineData("bohemian queen", true)]
    [InlineData("QUEEN rhapsody", true)]
    [InlineData("Bohemian Rhapsody", true)]
    [InlineData("rhapsody beatles", false)]
    [InlineData("   ", false)]
    public void TypedWords_MatchWhenEveryWordIsInTheTitleOrArtist_InAnyOrderAndCase(string typed, bool expected)
    {
        var matcher = new LyricsTitleMatcher(
            ShippedProviderHarness.LoadShipped("lrclib").TextTrack!.Search.Lyrics.Match, NullLogger.Instance, "lrclib");

        Assert.Equal(expected, matcher.MatchesSearchText(typed, "Bohemian Rhapsody (Remastered 2011)", "Queen"));
    }

    // -- Helpers ---------------------------------------------------------------

    private static TextTrackLookup SongLookup(string? text) => new(
        new MediaAsset { Id = Guid.NewGuid(), FilePathRoot = "song.flac" }, MediaType.Music,
        "Bohemian Rhapsody", "Queen", "A Night at the Opera", null, "en", 354, new Dictionary<string, string>(), SearchText: text);

    private static TextTrackLookup MovieLookup(string? text, SubtitleLookupContext? context) => Lookup(MediaType.Movies, text, context);

    private static TextTrackLookup Lookup(MediaType type, string? text, SubtitleLookupContext? context) => new(
        new MediaAsset { Id = Guid.NewGuid(), FilePathRoot = "Dune.2021.mkv" }, type, "Dune", null, null, "2021", "en", null,
        new Dictionary<string, string>(), context, text);

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static TextTrackProviderAdapter Lyrics(List<string> seen, Func<string, HttpResponseMessage> answer) => new(
        ShippedProviderHarness.LoadShipped("lrclib"),
        new StubFactory(request => { seen.Add(request.RequestUri!.PathAndQuery); return answer(request.RequestUri.PathAndQuery); }),
        NullProviderHealthMonitor.Instance,
        NullLogger<TextTrackProviderAdapter>.Instance);

    private static TextTrackProviderAdapter Subtitles(Func<HttpRequestMessage, HttpResponseMessage> answer) => new(
        ShippedProviderHarness.LoadShipped("subdl", "example-key"),
        new StubFactory(answer),
        NullProviderHealthMonitor.Instance,
        NullLogger<TextTrackProviderAdapter>.Instance);

    private sealed class StubFactory(Func<HttpRequestMessage, HttpResponseMessage> answer) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(answer));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(answer(request));
    }
}
