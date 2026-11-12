using System.Globalization;
using System.Net;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Providers;
using MediaEngine.Providers.Services;
using MediaEngine.Providers.Tests.ProviderRecording;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// The shipped LRCLIB file, answered with answers shaped like the real service's, finds a song whose title only differs in
/// capitals, punctuation or bracketed extras, still rejects a different song by its length, and writes one line per lookup
/// (Music player and Listen fixes spec D2 and D4).
/// </summary>
public sealed class LrclibLyricsReplayTests
{
    private const string NotFound = """{"code":404,"name":"TrackNotFound","message":"Failed to find specified track"}""";

    private static string Entry(string title, string artist, double duration, int id = 1) =>
        $$"""{"id":{{id}},"trackName":"{{title}}","artistName":"{{artist}}","albumName":"Album","duration":{{duration.ToString(CultureInfo.InvariantCulture)}},"instrumental":false,"plainLyrics":"Plain words","syncedLyrics":"[00:01.00]Synced words"}""";

    private static string List(params string[] entries) => "[" + string.Join(',', entries) + "]";

    private static TextTrackLookup Song(string title, string? artist = "The Beatles", double? seconds = 125, string? album = "Album") => new(
        new MediaAsset { Id = Guid.Parse("55555555-5555-5555-5555-555555555555"), FilePathRoot = "song.flac" },
        MediaType.Music, title, artist, album, null, "en", seconds, new Dictionary<string, string>());

    /// <summary>The exact lookup finds nothing (404); the search answers with the given list.</summary>
    private static RecordingHandler SearchAnswers(string list) => new(request =>
        request.Uri.AbsolutePath == "/api/get"
            ? RecordingHandler.Json(NotFound, HttpStatusCode.NotFound)
            : RecordingHandler.Json(list));

    private static (TextTrackProviderAdapter Provider, CapturingLogger Log) Build(
        RecordingHandler handler, Action<ProviderConfiguration>? tweak = null)
    {
        var config = ShippedProviderHarness.LoadShipped("lrclib");
        tweak?.Invoke(config);
        var log = new CapturingLogger();
        var provider = new TextTrackProviderAdapter(
            config, new RecordingHttpFactory(handler), NullProviderHealthMonitor.Instance, log,
            new ProviderRateLimiterCoordinator());
        return (provider, log);
    }

    [Fact]
    public async Task ATitleWithABracketedYear_FindsTheListEntryWithoutIt_AfterTheExactLookupMissed()
    {
        var handler = SearchAnswers(List(Entry("Yesterday", "The Beatles", 125.03)));
        var (provider, log) = Build(handler);

        var found = Assert.Single(await provider.SearchAsync(Song("Yesterday (Remastered 2009)")));

        Assert.Equal("1", found.SourceId);
        Assert.Equal(2, handler.Lines.Count);
        Assert.Contains("/api/get?", handler.Lines[0], StringComparison.Ordinal);
        Assert.Contains("/api/search?", handler.Lines[1], StringComparison.Ordinal);
        Assert.Contains("Remastered", handler.Lines[0], StringComparison.Ordinal);
        Assert.Contains("Found", Assert.Single(log.Information), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Song - Remastered 2011")]
    [InlineData("Song (feat. Someone)")]
    [InlineData("Song feat. Someone")]
    [InlineData("SONG")]
    [InlineData("Song!")]
    public async Task ATitleWithARemasterOrGuestSuffix_MatchesTheBareTitle(string title)
    {
        var (provider, _) = Build(SearchAnswers(List(Entry("Song", "The Beatles", 125))));

        Assert.Single(await provider.SearchAsync(Song(title)));
    }

    [Fact]
    public async Task ATitleWithAccentsAndCurlyApostrophes_MatchesTheSameTitleWithout()
    {
        var (provider, _) = Build(SearchAnswers(List(Entry("Dont Stop Believin", "Journey", 125))));

        Assert.Single(await provider.SearchAsync(Song("Don’t Stop Believin’ (Remaster)", "Journey")));
        Assert.Single(await provider.SearchAsync(Song("Dónt Stop Believin", "Journey")));
    }

    [Fact]
    public async Task ADifferentSongWithTheSameTitleButAnotherLength_IsRejected_AndLoggedAsLengthTooDifferent()
    {
        var (provider, log) = Build(SearchAnswers(List(Entry("Song", "Someone Else", 165))));

        Assert.Empty(await provider.SearchAsync(Song("Song")));

        Assert.Contains("LengthTooDifferent", Assert.Single(log.Information), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATitleThatOnlySharesAPrefix_DoesNotMatch_AndIsLoggedAsNoMatch()
    {
        var (provider, log) = Build(SearchAnswers(List(Entry("Yesterday", "The Beatles", 125))));

        Assert.Empty(await provider.SearchAsync(Song("Yesterday Once More")));

        var line = Assert.Single(log.Information);
        Assert.Contains("NoMatch", line, StringComparison.Ordinal);
        Assert.Contains("1 candidate(s)", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASourceThatCannotBeReached_IsLoggedAsSourceUnreachable()
    {
        var (provider, log) = Build(new RecordingHandler(_ => throw new HttpRequestException("connection refused")));

        Assert.Empty(await provider.SearchAsync(Song("Song")));

        Assert.Contains("SourceUnreachable", Assert.Single(log.Information), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExactLookupThatMissedFollowedByASearchThatFailedInTransport_IsLoggedAsSourceUnreachable_NotNoMatch()
    {
        var handler = new RecordingHandler(request => request.Uri.AbsolutePath == "/api/get"
            ? RecordingHandler.Json(NotFound, HttpStatusCode.NotFound)
            : throw new HttpRequestException("connection reset"));
        var (provider, log) = Build(handler);

        Assert.Empty(await provider.SearchAsync(Song("Song")));

        Assert.Equal(2, handler.Lines.Count);
        var line = Assert.Single(log.Information);
        Assert.Contains("SourceUnreachable", line, StringComparison.Ordinal);
        Assert.DoesNotContain("NoMatch", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASongTheSourceHasNothingFor_IsLoggedAsNoMatch_NotAsUnreachable()
    {
        var (provider, log) = Build(new RecordingHandler(_ => RecordingHandler.Json(NotFound, HttpStatusCode.NotFound)));

        Assert.Empty(await provider.SearchAsync(Song("Song")));

        Assert.Contains("NoMatch", Assert.Single(log.Information), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExactAnswerThatIsInstrumental_IsLoggedAsInstrumental()
    {
        var instrumental = """{"id":9,"trackName":"Song","artistName":"A","duration":125,"instrumental":true,"plainLyrics":null,"syncedLyrics":null}""";
        var (provider, log) = Build(new RecordingHandler(_ => RecordingHandler.Json(instrumental)));

        var found = Assert.Single(await provider.SearchAsync(Song("Song")));

        Assert.True(found.IsInstrumental);
        Assert.Contains("Instrumental", Assert.Single(log.Information), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALookupMissingADetailTheFileNeeds_IsLoggedAsMissingDetails_AndAsksNothing()
    {
        var handler = SearchAnswers("[]");
        var (provider, log) = Build(handler, config => config.TextTrack!.Search.Needs.Add("album"));

        Assert.Empty(await provider.SearchAsync(Song("Song", album: null)));

        Assert.Empty(handler.Lines);
        Assert.Contains("MissingDetails", Assert.Single(log.Information), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EachLookupWritesOneInformationLine_NamingTheSongTheArtistAndTheLengthSent()
    {
        var (provider, log) = Build(SearchAnswers(List(Entry("Song", "The Beatles", 125))));

        await provider.SearchAsync(Song("Song"));
        await provider.SearchAsync(Song("Other"));

        Assert.Equal(2, log.Information.Count);
        Assert.Contains("lrclib", log.Information[0], StringComparison.Ordinal);
        Assert.Contains("\"Song\"", log.Information[0], StringComparison.Ordinal);
        Assert.Contains("\"The Beatles\"", log.Information[0], StringComparison.Ordinal);
        Assert.Contains("(125s)", log.Information[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAMatchBlock_OnlyCapitalsAreIgnored()
    {
        var (provider, _) = Build(
            SearchAnswers(List(Entry("Yesterday", "The Beatles", 125))),
            config => config.TextTrack!.Search.Lyrics.Match = new TextTrackLyricsMatchConfiguration());

        Assert.Single(await provider.SearchAsync(Song("YESTERDAY")));
        Assert.Empty(await provider.SearchAsync(Song("Yesterday (Remastered 2009)")));
        Assert.Empty(await provider.SearchAsync(Song("Yesterday!")));
    }

    [Fact]
    public async Task ASourcesOwnFieldNames_AreUsedToReadTheTitle()
    {
        var list = """[{"id":3,"title":"Song","duration":125,"plainLyrics":"Words"}]""";
        var (provider, _) = Build(SearchAnswers(list), config =>
            config.TextTrack!.Search.Lyrics.Match = new TextTrackLyricsMatchConfiguration { TitlePath = "title" });

        Assert.Single(await provider.SearchAsync(Song("song")));
    }

    [Fact]
    public async Task AnInvalidPatternInTheFile_IsIgnoredAtLookupTime_NotThrown()
    {
        var (provider, _) = Build(SearchAnswers(List(Entry("Song", "The Beatles", 125))), config =>
            config.TextTrack!.Search.Lyrics.Match.StripSuffixPatterns.Add("(unclosed"));

        Assert.Single(await provider.SearchAsync(Song("Song (Live)")));
    }

    [Fact]
    public void TitlesThatReduceToNothing_NeverMatch()
    {
        var matcher = new LyricsTitleMatcher(
            new TextTrackLyricsMatchConfiguration { IgnorePunctuation = true, IgnoreBracketed = true },
            NullLogger.Instance, "test");

        Assert.False(matcher.Matches("(Intro)", "(Outro)"));
        Assert.False(matcher.Matches("...", "!!!"));
        Assert.True(matcher.Matches("  Wake   Up! ", "wake up"));
    }

    [Fact]
    public void ATitleTheRulesReduceToNothing_IsComparedAsWrittenIgnoringCapitals()
    {
        var matcher = new LyricsTitleMatcher(
            new TextTrackLyricsMatchConfiguration { IgnorePunctuation = true, IgnoreBracketed = true },
            NullLogger.Instance, "test");

        Assert.True(matcher.Matches("(Untitled)", "(untitled)"));
        Assert.True(matcher.Matches(" [Intro] ", "[INTRO]"));
        Assert.False(matcher.Matches("(Untitled)", "Untitled Song"));
        Assert.False(matcher.Matches("", ""));
    }

    private sealed class CapturingLogger : ILogger<TextTrackProviderAdapter>
    {
        public List<string> Information { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Information)
            {
                Information.Add(formatter(state, exception));
            }
        }
    }
}
