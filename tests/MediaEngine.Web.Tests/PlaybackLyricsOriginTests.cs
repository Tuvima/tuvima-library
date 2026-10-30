using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;

namespace MediaEngine.Web.Tests;

/// <summary>Where lyrics came from, and adding a lyrics file from the panel (spec music-player-and-queue D6, bug F57).</summary>
public sealed class PlaybackLyricsOriginTests
{
    [Theory]
    [InlineData("lrclib", false, "Lyrics from LRCLIB")]
    [InlineData("local", true, "Lyrics from a file next to the song")]
    public async Task TheSourceLine_NamesWhereTheShownLyricsCameFrom(string provider, bool userOwned, string expected)
    {
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetTextTracksAsync), _ => Task.FromResult<IReadOnlyList<TextTrackDto>>(
                [new() { Id = Guid.NewGuid(), Kind = "Lyrics", Provider = provider, IsUserOwned = userOwned, IsPreferred = true }]));
            stub.SetHandler(nameof(IEngineApiClient.GetTextTrackContentAsync), _ => Task.FromResult<string?>("Words"));
        });
        using var presenter = new PlaybackLyricsPresenter(api);
        await presenter.EnsureAsync(Snapshot(), new Sink());

        Assert.Equal(expected, presenter.SourceLine);
    }

    [Fact]
    public async Task WithNoLyrics_ThereIsNoSourceLine()
    {
        var api = EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetTextTracksAsync), _ => Task.FromResult<IReadOnlyList<TextTrackDto>>([])));
        using var presenter = new PlaybackLyricsPresenter(api);
        await presenter.EnsureAsync(Snapshot(), new Sink());

        Assert.Null(presenter.SourceLine);
    }

    [Fact]
    public async Task AddingALyricsFile_SendsItAsLyrics_ThenReloadsAndShowsTheEnginesMessage()
    {
        string? kind = null; string? name = null; var reads = 0;
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetTextTracksAsync), _ =>
            {
                reads++;
                return Task.FromResult<IReadOnlyList<TextTrackDto>>(reads > 1 ? [new() { Id = Guid.NewGuid(), Kind = "Lyrics", IsUserOwned = true, IsPreferred = true }] : []);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetTextTrackContentAsync), _ => Task.FromResult<string?>("[00:01.00]Hello"));
            stub.SetHandler(nameof(IEngineApiClient.ImportTextTrackAsync), args =>
            {
                kind = (string)args![1]!; name = (string)args[3]!;
                return Task.FromResult<RefreshTextTracksResponse?>(new() { message = "Lyrics added." });
            });
        });
        using var presenter = new PlaybackLyricsPresenter(api);
        await presenter.EnsureAsync(Snapshot(), new Sink());

        await presenter.ImportFileAsync("song.lrc", "text/plain", new MemoryStream([1]));

        Assert.Equal(("lyrics", "song.lrc"), (kind, name));
        Assert.Equal("Hello", Assert.Single(presenter.Lines).Text);
        Assert.Equal("Lyrics added.", presenter.Status);
        Assert.False(presenter.Refreshing);
    }

    [Fact]
    public async Task AddingALyricsFile_ThatTheEngineRefuses_SaysSoInPlainWords()
    {
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetTextTracksAsync), _ => Task.FromResult<IReadOnlyList<TextTrackDto>>([]));
            stub.SetHandler(nameof(IEngineApiClient.ImportTextTrackAsync), _ => Task.FromResult<RefreshTextTracksResponse?>(null));
        });
        using var presenter = new PlaybackLyricsPresenter(api);
        await presenter.EnsureAsync(Snapshot(), new Sink());

        await presenter.ImportFileAsync("song.lrc", "text/plain", new MemoryStream([1]));

        Assert.Equal("That lyrics file could not be added.", presenter.Status);
    }

    private static ListenPlaybackSnapshot Snapshot() => new()
    {
        ProfileId = Guid.NewGuid(),
        CurrentIndex = 0,
        PlaybackRequestVersion = 9,
        Queue = [new() { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), MediaType = "Music", Title = "Song", StreamUrl = "stream://song" }]
    };

    private sealed class Sink : IPlaybackCommandSink
    {
        public bool Supports(string action, ListenPlaybackSnapshot snapshot) => true;
        public Task<ListenPlaybackCommandReplyDto?> SendAsync(ListenPlaybackSnapshot snapshot, ListenPlaybackCommandDto command, CancellationToken ct = default) => Task.FromResult<ListenPlaybackCommandReplyDto?>(new());
    }
}
