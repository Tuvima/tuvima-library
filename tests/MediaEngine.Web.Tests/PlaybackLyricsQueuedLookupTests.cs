using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;

namespace MediaEngine.Web.Tests;

/// <summary>
/// "Find lyrics" only queues a lookup (spec music-player-and-listen-fixes D3). The presenter follows that one queued lookup with the person's own access
/// and reads the tracks again once, after it has finished: never straight after queueing, and never for another song.
/// </summary>
public sealed class PlaybackLyricsQueuedLookupTests
{
    private readonly Guid _operation = Guid.NewGuid();
    private int _trackReads, _queued;
    private readonly List<string> _statusReads = [];
    private readonly Queue<string> _statuses = new();
    private IReadOnlyList<TextTrackDto> _tracks = [];

    private IEngineApiClient Api() => EngineApiClientStub.Create(stub =>
    {
        stub.SetHandler(nameof(IEngineApiClient.GetTextTracksAsync), _ => { _trackReads++; return Task.FromResult(_tracks); });
        stub.SetHandler(nameof(IEngineApiClient.GetTextTrackContentAsync), _ => Task.FromResult<string?>("Found words"));
        stub.SetHandler(nameof(IEngineApiClient.QueueTextTrackRefreshAsync), _ =>
        {
            _queued++;
            return Task.FromResult<RefreshTextTracksResponse?>(new() { operation_id = _operation, status = "queued" });
        });
        stub.SetHandler(nameof(IEngineApiClient.GetTextTrackRefreshStatusAsync), args =>
        {
            Assert.Equal(_operation, (Guid)args![1]!);
            var status = _statuses.Count > 1 ? _statuses.Dequeue() : _statuses.Peek();
            _statusReads.Add(status);
            return Task.FromResult<RefreshTextTracksResponse?>(new() { operation_id = _operation, status = status });
        });
    });

    [Fact]
    public async Task WhileTheQueuedLookupIsQueuedOrRunning_TheTracksAreNotReadAgain_AndTheStateIsLooking()
    {
        _statuses.Enqueue("queued"); _statuses.Enqueue("running"); _statuses.Enqueue("succeeded");
        var wait = new LyricsWaitSpy();
        using var presenter = new PlaybackLyricsPresenter(Api(), null, wait.Wait);
        await presenter.EnsureAsync(Snapshot(), new Sink());
        Assert.Equal(1, _trackReads);

        var find = presenter.RefreshAsync();
        await wait.ReleaseNextAsync();
        await WaitUntilAsync(() => _statusReads.Count == 1);
        Assert.Equal(PlaybackLyricsLookupState.Looking, presenter.Lookup);
        Assert.True(presenter.Refreshing);
        await wait.ReleaseNextAsync();
        await WaitUntilAsync(() => _statusReads.Count == 2);
        Assert.Equal(PlaybackLyricsLookupState.Looking, presenter.Lookup);
        Assert.Equal(1, _trackReads);

        _tracks = [new() { Id = Guid.NewGuid(), Kind = "Lyrics", IsPreferred = true }];
        await wait.ReleaseNextAsync();
        await find;

        Assert.Equal(["queued", "running", "succeeded"], _statusReads);
        Assert.Equal(2, _trackReads);
        Assert.Equal(PlaybackLyricsLookupState.Found, presenter.Lookup);
        Assert.Equal("Found words", Assert.Single(presenter.Lines).Text);
        Assert.False(presenter.Refreshing);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)], wait.Requested);
    }

    [Theory]
    [InlineData("no_result")]
    [InlineData("blocked")]
    [InlineData("failed_terminal")]
    [InlineData("cancelled")]
    public async Task WhenTheLookupEndsWithNothing_TheTracksAreReadOnce_AndNoLyricsAreFound(string status)
    {
        _statuses.Enqueue(status);
        using var presenter = new PlaybackLyricsPresenter(Api(), null, new LyricsWaitSpy(instant: true).Wait);
        await presenter.EnsureAsync(Snapshot(), new Sink());

        await presenter.RefreshAsync();

        Assert.Equal(2, _trackReads);
        Assert.Equal(PlaybackLyricsLookupState.NoneFound, presenter.Lookup);
        Assert.Empty(presenter.Lines);
        Assert.Null(presenter.Status);
    }

    [Fact]
    public async Task WhenTheLookupCouldNotBeMadeRightNow_TheSourceIsUnreachable_AndTheTracksAreNotReadAgain()
    {
        _statuses.Enqueue("running"); _statuses.Enqueue("failed_retryable");
        using var presenter = new PlaybackLyricsPresenter(Api(), null, new LyricsWaitSpy(instant: true).Wait);
        await presenter.EnsureAsync(Snapshot(), new Sink());

        await presenter.RefreshAsync();

        Assert.Equal(PlaybackLyricsLookupState.Unreachable, presenter.Lookup);
        Assert.Equal(1, _trackReads);
        Assert.False(presenter.Refreshing);

        // Try again asks the Engine afresh.
        _statuses.Clear(); _statuses.Enqueue("succeeded");
        await presenter.RefreshAsync();
        Assert.Equal(2, _queued);
        Assert.Equal(PlaybackLyricsLookupState.NoneFound, presenter.Lookup);
    }

    [Fact]
    public async Task AfterThirtySecondsOfLooking_TheStateIsStillLooking_AndTheTracksAreReadOnceMoreThirtySecondsLater()
    {
        _statuses.Enqueue("running");
        var wait = new LyricsWaitSpy();
        using var presenter = new PlaybackLyricsPresenter(Api(), null, wait.Wait);
        await presenter.EnsureAsync(Snapshot(), new Sink());

        var find = presenter.RefreshAsync();
        // The first look is after 1 second and the rest 2 seconds apart, so the 16th look is the one that reaches 30 seconds.
        for (var look = 0; look < 16; look++)
        {
            await wait.ReleaseNextAsync();
        }
        await WaitUntilAsync(() => presenter.Lookup == PlaybackLyricsLookupState.StillLooking);
        Assert.Equal(16, _statusReads.Count);
        Assert.False(presenter.Refreshing);
        Assert.Equal(1, _trackReads);
        await WaitUntilAsync(() => wait.Requested.Count == 17);
        Assert.Equal(TimeSpan.FromSeconds(30), wait.Requested[^1]);

        _tracks = [new() { Id = Guid.NewGuid(), Kind = "Lyrics", IsPreferred = true }];
        await wait.ReleaseNextAsync();
        await find;

        Assert.Equal(2, _trackReads);
        Assert.Equal(PlaybackLyricsLookupState.Found, presenter.Lookup);
        Assert.Equal("Found words", Assert.Single(presenter.Lines).Text);
    }

    [Fact]
    public async Task ChangingSongWhileLooking_DropsTheResult_AndReadsNothingForTheOtherSong()
    {
        _statuses.Enqueue("running"); _statuses.Enqueue("succeeded");
        var wait = new LyricsWaitSpy();
        using var presenter = new PlaybackLyricsPresenter(Api(), null, wait.Wait);
        var sink = new Sink();
        await presenter.EnsureAsync(Snapshot(), sink);
        var find = presenter.RefreshAsync();
        await wait.ReleaseNextAsync();
        await WaitUntilAsync(() => _statusReads.Count == 1);
        await WaitUntilAsync(() => wait.Requested.Count == 2);

        presenter.Observe(Snapshot(), sink);
        await find;

        Assert.Equal(PlaybackLyricsLookupState.Idle, presenter.Lookup);
        Assert.Equal(1, _trackReads);
        Assert.Single(_statusReads);
        Assert.Empty(presenter.Lines);
        Assert.False(presenter.Refreshing);
    }

    [Fact]
    public async Task DisposingThePresenterWhileLooking_StopsThePolling()
    {
        _statuses.Enqueue("running");
        var wait = new LyricsWaitSpy();
        var presenter = new PlaybackLyricsPresenter(Api(), null, wait.Wait);
        await presenter.EnsureAsync(Snapshot(), new Sink());
        var find = presenter.RefreshAsync();
        await wait.ReleaseNextAsync();
        await WaitUntilAsync(() => _statusReads.Count == 1);
        await WaitUntilAsync(() => wait.Requested.Count == 2);

        presenter.Dispose();
        await find;

        Assert.Single(_statusReads);
        Assert.Equal(1, _trackReads);
        Assert.Equal(2, wait.Requested.Count);
    }

    [Fact]
    public async Task WhenTheLookupCouldNotBeQueued_NothingIsWaitedFor_AndTheMessageSaysSo()
    {
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetTextTracksAsync), _ => Task.FromResult<IReadOnlyList<TextTrackDto>>([]));
            stub.SetHandler(nameof(IEngineApiClient.QueueTextTrackRefreshAsync), _ => Task.FromResult<RefreshTextTracksResponse?>(null));
        });
        var wait = new LyricsWaitSpy(instant: true);
        using var presenter = new PlaybackLyricsPresenter(api, null, wait.Wait);
        await presenter.EnsureAsync(Snapshot(), new Sink());

        await presenter.RefreshAsync();

        Assert.Equal("Lyrics search could not start.", presenter.Status);
        Assert.Equal(PlaybackLyricsLookupState.Idle, presenter.Lookup);
        Assert.Empty(wait.Requested);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }
        Assert.True(condition());
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
