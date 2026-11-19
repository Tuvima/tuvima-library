using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Workers;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The words a person types in the "pick a result" window (review mock-ups 2026-11-18, D7 follow-up) on the Engine: they replace the item's own
/// details for sources that can be searched by words, a source that cannot is left out and named in plain words, and choosing a result from
/// such a search finds the same result again. Real data store; the source is a hand-written spy that records what it is asked.
/// </summary>
public sealed class TextTrackTextSearchTests : IDisposable
{
    private readonly LibraryMoveTestWorld _world = new(withFiles: true);
    private readonly TextTrackRepository _tracks;
    private readonly BridgeIdRepository _bridges;
    private readonly Guid _songAsset;
    private readonly Guid _movieWork;
    private readonly Guid _movieAsset;

    public TextTrackTextSearchTests()
    {
        var files = _world.Files!;
        _tracks = new TextTrackRepository(files.Database);
        _bridges = new BridgeIdRepository(files.Database);

        var album = _world.AddParent("Music", "The Best Of");
        var song = _world.AddChild(album, "Music", "Song", 1, filePath: MoveFileWorld.WriteFile(files.Managed, "inbox/001 Song.flac", "audio"));
        _songAsset = song.Item3!.Value;
        _world.Put(_songAsset, "media_type", "Music");
        _world.Put(_songAsset, "title", "Song");
        _world.Put(_songAsset, "artist", "Artist");

        var films = _world.AddParent("Movies", "Films");
        var movie = _world.AddChild(films, "Movies", "Film", 1, filePath: MoveFileWorld.WriteFile(files.Managed, "inbox/Film.mkv", "video"));
        _movieWork = movie.WorkId;
        _movieAsset = movie.AssetId!.Value;
        _world.Put(_movieWork, "media_type", "Movies");
        _world.Put(_movieWork, "title", "Film");
        _world.Put(_movieWork, "language", "en");
        // No online identity at all: an ordinary subtitle search would stop at IdentityUnverified.
    }

    public void Dispose() => _world.Dispose();

    private TextTrackEnrichmentWorker Worker(Spy source)
    {
        var files = _world.Files!;
        return new TextTrackEnrichmentWorker(
            new MediaAssetRepository(files.Database),
            new WorkRepository(files.Database),
            new CanonicalValueRepository(files.Database),
            _bridges,
            _tracks,
            [source],
            new AssetPathService(Path.Combine(files.Managed, ".library")),
            NullLogger<TextTrackEnrichmentWorker>.Instance);
    }

    [Fact]
    public async Task TypedWords_ReachTheSource_AndTheListHoldsWhatItAnswers()
    {
        var source = new Spy(TextTrackKind.Lyrics, supportsText: true) { Results = [Lyrics("1"), Lyrics("2")] };

        var list = await Worker(source).FindAlternativesAsync(_songAsset, TextTrackKind.Lyrics, null, query: "  my own words ");

        Assert.Equal("Found", list.Status);
        Assert.Equal(["1", "2"], list.Items.Select(item => item.SourceId).Order());
        Assert.Equal("my own words", Assert.Single(source.Lookups).SearchText);
    }

    [Fact]
    public async Task EveryFindAnotherSearch_IsMarkedInteractive_SoANegativeAnswerForOtherWordsNeverSkipsIt()
    {
        var source = new Spy(TextTrackKind.Lyrics, supportsText: true);

        await Worker(source).FindAlternativesAsync(_songAsset, TextTrackKind.Lyrics, null, query: "other words");
        await Worker(source).FindAlternativesAsync(_songAsset, TextTrackKind.Lyrics, null);

        Assert.All(source.Lookups, lookup => Assert.True(lookup.Interactive));
    }

    [Fact]
    public async Task NoTypedWords_LeaveTheSearchTextEmpty_SoTheItemsOwnDetailsAreUsed()
    {
        var source = new Spy(TextTrackKind.Lyrics, supportsText: true);

        await Worker(source).FindAlternativesAsync(_songAsset, TextTrackKind.Lyrics, null);
        await Worker(source).FindAlternativesAsync(_songAsset, TextTrackKind.Lyrics, null, query: "   ");

        Assert.All(source.Lookups, lookup => Assert.Null(lookup.SearchText));
        Assert.Equal(2, source.Lookups.Count);
    }

    [Fact]
    public async Task ASourceThatCannotBeSearchedByWords_IsNotAsked_AndSaysSoInPlainWords()
    {
        var source = new Spy(TextTrackKind.Lyrics, supportsText: false) { Results = [Lyrics("1")] };

        var list = await Worker(source).FindAlternativesAsync(_songAsset, TextTrackKind.Lyrics, null, query: "something typed");

        Assert.Equal("TextSearchUnsupported", list.Status);
        Assert.Equal("Spy Source", list.SourceName);
        Assert.Equal("Spy Source can only find lyrics for the item itself, not by words you type.", list.Message);
        Assert.Empty(list.Items);
        Assert.Empty(source.Lookups);
    }

    [Fact]
    public async Task SubtitlesSearchedByWords_NeedNoVerifiedMovieOrEpisode()
    {
        var source = new Spy(TextTrackKind.Subtitles, supportsText: true) { Results = [Subtitle("s1")] };
        var worker = Worker(source);

        var withoutWords = await worker.FindAlternativesAsync(_movieAsset, TextTrackKind.Subtitles, null);
        var withWords = await worker.FindAlternativesAsync(_movieAsset, TextTrackKind.Subtitles, null, query: "film 2021");

        Assert.Equal("IdentityUnverified", withoutWords.Status);
        Assert.Equal("Found", withWords.Status);
        Assert.Equal("s1", Assert.Single(withWords.Items).SourceId);
        Assert.Equal("film 2021", Assert.Single(source.Lookups).SearchText);
    }

    [Fact]
    public async Task ChoosingFromATextSearch_SearchesWithTheSameWords_AndSavesThatResult()
    {
        var source = new Spy(TextTrackKind.Lyrics, supportsText: true) { Results = [Lyrics("7")] };

        var chosen = await Worker(source).ChooseAlternativeAsync(
            _songAsset, TextTrackKind.Lyrics, source.Name, "7", null, query: "my own words");

        Assert.Equal("Updated", chosen.Status);
        Assert.Equal("my own words", Assert.Single(source.Lookups).SearchText);
        Assert.Single(await _tracks.GetByAssetAsync(_songAsset, TextTrackKind.Lyrics));
    }

    [Fact]
    public async Task ChoosingWithoutWords_DoesNotCarryAnySearchText()
    {
        var source = new Spy(TextTrackKind.Lyrics, supportsText: true) { Results = [Lyrics("7")] };

        await Worker(source).ChooseAlternativeAsync(_songAsset, TextTrackKind.Lyrics, source.Name, "7", null);

        Assert.Null(Assert.Single(source.Lookups).SearchText);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("a plain name", true)]
    [InlineData("bad\ncontrol", false)]
    public void TheEngineConnectionPoint_AcceptsOnlyShortPlainText(string? text, bool valid)
    {
        Assert.Equal(valid, Check(text));
        Assert.False(Check(new string('a', 201)));
        Assert.True(Check(new string('a', 200)));

        static bool Check(string? value) => (bool)typeof(MediaEngine.Api.Endpoints.StreamEndpoints)
            .GetMethod("IsValidSearchText", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [value])!;
    }

    private static TextTrackCandidate Lyrics(string id) =>
        new("spy", TextTrackKind.Lyrics, id, null, "en", "lrc", 0.8, false, null);

    private static TextTrackCandidate Subtitle(string id) =>
        new("spy", TextTrackKind.Subtitles, id, null, "en", "srt", 0.9, false, null, ReleaseName: $"Film.{id}");

    private sealed class Spy(TextTrackKind kind, bool supportsText) : ITextTrackProvider
    {
        public string Name => "spy";

        public string DisplayName => "Spy Source";

        public TextTrackKind Kind => kind;

        public bool IsEnabled => true;

        public IReadOnlyList<TextTrackCandidate> Results { get; set; } = [];

        public List<TextTrackLookup> Lookups { get; } = [];

        public bool CanHandle(MediaType mediaType) =>
            kind == TextTrackKind.Lyrics ? mediaType == MediaType.Music : mediaType is MediaType.Movies or MediaType.TV;

        public bool SupportsSearchText(MediaType mediaType) => supportsText;

        public Task<IReadOnlyList<TextTrackCandidate>> SearchAsync(TextTrackLookup lookup, CancellationToken ct = default)
        {
            Lookups.Add(lookup);
            return Task.FromResult(Results);
        }

        public Task<TextTrackDownload?> DownloadAsync(TextTrackCandidate candidate, CancellationToken ct = default) =>
            Task.FromResult<TextTrackDownload?>(candidate.Kind == TextTrackKind.Lyrics
                ? new TextTrackDownload(candidate, $"[00:01.00]words {candidate.SourceId}", "lrc", "lrc")
                : new TextTrackDownload(candidate, "1\n00:00:01,000 --> 00:00:02,000\nHello\n", "srt", "vtt"));
    }
}
