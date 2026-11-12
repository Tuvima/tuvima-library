using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Tests;

/// <summary>
/// "Find lyrics" only queues a lookup (spec music-player-and-listen-fixes D3). The presenter follows that one queued lookup with the person's own access
/// and reads the tracks again once, after it has finished: never straight after queueing, and never for another song.
/// </summary>
public sealed class PlaybackLyricsQueuedLookupTests
{
    private readonly Guid _operation = Guid.NewGuid();
    private int _trackReads, _queued, _pendingReads;
    private RefreshTextTracksResponse? _pending;
    private TaskCompletionSource<RefreshTextTracksResponse?>? _pendingGate;
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
        stub.SetHandler(nameof(IEngineApiClient.GetPendingTextTrackRefreshAsync), _ =>
        {
            _pendingReads++;
            return _pendingGate?.Task ?? Task.FromResult(_pending);
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
    public async Task AfterThirtySecondsOfLooking_TheStateIsStillLooking_AndTheLookupIsCheckedEveryTenSecondsUntilItFinishes()
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

        // It never turns into "none" while the lookup is queued or running: it keeps asking, 10 seconds apart.
        for (var look = 17; look <= 20; look++)
        {
            await WaitUntilAsync(() => wait.Requested.Count == look);
            Assert.Equal(TimeSpan.FromSeconds(10), wait.Requested[^1]);
            await wait.ReleaseNextAsync();
            await WaitUntilAsync(() => _statusReads.Count == look);
            Assert.Equal(PlaybackLyricsLookupState.StillLooking, presenter.Lookup);
            Assert.Equal(1, _trackReads);
        }

        _tracks = [new() { Id = Guid.NewGuid(), Kind = "Lyrics", IsPreferred = true }];
        _statuses.Clear(); _statuses.Enqueue("succeeded");
        await WaitUntilAsync(() => wait.Requested.Count == 21);
        await wait.ReleaseNextAsync();
        await find;

        Assert.Equal(2, _trackReads);
        Assert.Equal(PlaybackLyricsLookupState.Found, presenter.Lookup);
        Assert.Equal("Found words", Assert.Single(presenter.Lines).Text);
    }

    [Fact]
    public async Task AStillLookingLookupThatEndsWithNothing_ReadsTheTracksOnce_ThenSaysNoneFound()
    {
        _statuses.Enqueue("running");
        var wait = new LyricsWaitSpy();
        using var presenter = new PlaybackLyricsPresenter(Api(), null, wait.Wait);
        await presenter.EnsureAsync(Snapshot(), new Sink());

        var find = presenter.RefreshAsync();
        for (var look = 0; look < 16; look++)
        {
            await wait.ReleaseNextAsync();
        }
        await WaitUntilAsync(() => presenter.Lookup == PlaybackLyricsLookupState.StillLooking);

        _statuses.Clear(); _statuses.Enqueue("no_result");
        await WaitUntilAsync(() => wait.Requested.Count == 17);
        await wait.ReleaseNextAsync();
        await find;

        Assert.Equal(PlaybackLyricsLookupState.NoneFound, presenter.Lookup);
        Assert.Equal(2, _trackReads);
    }

    [Fact]
    public async Task AStillLookingLookupThatCouldNotBeMade_EndsAsUnreachable_WithoutReadingTheTracks()
    {
        _statuses.Enqueue("running");
        var wait = new LyricsWaitSpy();
        using var presenter = new PlaybackLyricsPresenter(Api(), null, wait.Wait);
        await presenter.EnsureAsync(Snapshot(), new Sink());

        var find = presenter.RefreshAsync();
        for (var look = 0; look < 16; look++)
        {
            await wait.ReleaseNextAsync();
        }
        await WaitUntilAsync(() => presenter.Lookup == PlaybackLyricsLookupState.StillLooking);

        _statuses.Clear(); _statuses.Enqueue("failed_retryable");
        await WaitUntilAsync(() => wait.Requested.Count == 17);
        await wait.ReleaseNextAsync();
        await find;

        Assert.Equal(PlaybackLyricsLookupState.Unreachable, presenter.Lookup);
        Assert.Equal(1, _trackReads);
    }

    [Fact]
    public async Task AfterFifteenMinutesOfLooking_ThePollingStops_AndTheStateStaysStillLookingNeverNone()
    {
        _statuses.Enqueue("running");
        var wait = new LyricsWaitSpy(instant: true);
        using var presenter = new PlaybackLyricsPresenter(Api(), null, wait.Wait);
        await presenter.EnsureAsync(Snapshot(), new Sink());

        await presenter.RefreshAsync();

        var total = wait.Requested.Aggregate(TimeSpan.Zero, (sum, span) => sum + span);
        Assert.InRange(total, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(10));
        Assert.Equal(wait.Requested.Count, _statusReads.Count);
        Assert.Equal(PlaybackLyricsLookupState.StillLooking, presenter.Lookup);
        Assert.False(presenter.Refreshing);
        Assert.Equal(1, _trackReads);
        Assert.Empty(presenter.Lines);
    }

    [Fact]
    public async Task ASongOpenedWhileItsAutomaticLookupIsStillQueued_SaysLookingAtOnce_AndShowsTheLyricsWhenItFinishes()
    {
        _pending = new() { operation_id = _operation, status = "queued" };
        _statuses.Enqueue("running"); _statuses.Enqueue("succeeded");
        var wait = new LyricsWaitSpy();
        using var presenter = new PlaybackLyricsPresenter(Api(), null, wait.Wait);

        await presenter.EnsureAsync(Snapshot(), new Sink());

        Assert.Equal(PlaybackLyricsLookupState.Looking, presenter.Lookup);
        Assert.True(presenter.Refreshing);
        Assert.Equal(1, _pendingReads);
        Assert.Equal(0, _queued);
        await wait.ReleaseNextAsync();
        await WaitUntilAsync(() => _statusReads.Count == 1);
        Assert.Equal(PlaybackLyricsLookupState.Looking, presenter.Lookup);
        Assert.Equal(1, _trackReads);

        _tracks = [new() { Id = Guid.NewGuid(), Kind = "Lyrics", IsPreferred = true }];
        await wait.ReleaseNextAsync();
        await WaitUntilAsync(() => presenter.Lookup == PlaybackLyricsLookupState.Found && !presenter.Refreshing);

        Assert.Equal(["running", "succeeded"], _statusReads);
        Assert.Equal(2, _trackReads);
        Assert.Equal(1, _pendingReads);
        Assert.Equal("Found words", Assert.Single(presenter.Lines).Text);
        Assert.False(presenter.Refreshing);
    }

    [Fact]
    public async Task ASongWithNoLyricsAndNoPendingLookup_IsAskedAboutOnce_AndIsNotLooking()
    {
        var wait = new LyricsWaitSpy(instant: true);
        using var presenter = new PlaybackLyricsPresenter(Api(), null, wait.Wait);
        var sink = new Sink();

        await presenter.EnsureAsync(Snapshot(), sink);
        await presenter.RetryAsync();

        Assert.Equal(1, _pendingReads);
        Assert.Equal(PlaybackLyricsLookupState.Idle, presenter.Lookup);
        Assert.False(presenter.Refreshing);
        Assert.Empty(wait.Requested);
        Assert.Empty(_statusReads);
    }

    [Fact]
    public async Task ASongThatAlreadyHasLyrics_NeverAsksForAPendingLookup()
    {
        _tracks = [new() { Id = Guid.NewGuid(), Kind = "Lyrics", IsPreferred = true }];
        _pending = new() { operation_id = _operation, status = "queued" };
        using var presenter = new PlaybackLyricsPresenter(Api(), null, new LyricsWaitSpy(instant: true).Wait);

        await presenter.EnsureAsync(Snapshot(), new Sink());

        Assert.Equal(0, _pendingReads);
        Assert.Equal(PlaybackLyricsLookupState.Idle, presenter.Lookup);
        Assert.Equal("Found words", Assert.Single(presenter.Lines).Text);
    }

    [Fact]
    public async Task ChangingSongWhileFollowingAnAutomaticLookup_StopsFollowingIt_AndAsksAboutTheNewSongOnce()
    {
        _pending = new() { operation_id = _operation, status = "running" };
        _statuses.Enqueue("running");
        var wait = new LyricsWaitSpy();
        using var presenter = new PlaybackLyricsPresenter(Api(), null, wait.Wait);
        var sink = new Sink();
        await presenter.EnsureAsync(Snapshot(), sink);
        await WaitUntilAsync(() => wait.Requested.Count == 1);

        _pending = null;
        await presenter.EnsureAsync(Snapshot(), sink);
        await WaitUntilAsync(() => presenter.Lookup == PlaybackLyricsLookupState.Idle && !presenter.Refreshing);

        Assert.Empty(_statusReads);
        Assert.Equal(2, _pendingReads);
        Assert.Empty(presenter.Lines);
    }

    [Fact]
    public async Task WhenTheSourceFailedAndTheLookupWaitsToBeTriedAgain_TheSourceIsUnreachable_NotStillLookingAndNotNone()
    {
        _statuses.Enqueue("running"); _statuses.Enqueue("retry_waiting");
        using var presenter = new PlaybackLyricsPresenter(Api(), null, new LyricsWaitSpy(instant: true).Wait);
        await presenter.EnsureAsync(Snapshot(), new Sink());

        await presenter.RefreshAsync();

        Assert.Equal(PlaybackLyricsLookupState.Unreachable, presenter.Lookup);
        Assert.Equal(1, _trackReads);
    }

    [Fact]
    public async Task ASongOpenedWhileItsAutomaticLookupWaitsToBeTriedAgainAfterAFailure_SaysItCouldNotBeLookedUp_AtOnce()
    {
        _pending = new() { operation_id = _operation, status = "retry_waiting" };
        var wait = new LyricsWaitSpy();
        using var presenter = new PlaybackLyricsPresenter(Api(), null, wait.Wait);

        await presenter.EnsureAsync(Snapshot(), new Sink());

        Assert.Equal(PlaybackLyricsLookupState.Unreachable, presenter.Lookup);
        Assert.False(presenter.Refreshing);
        Assert.Empty(wait.Requested);
        Assert.Empty(_statusReads);
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

    [Theory]
    [InlineData("interrupted")]
    [InlineData("paused")]
    public async Task ASongOpenedWhileItsAutomaticLookupWasInterruptedOrPaused_SaysStillLooking_NotCouldNotBeLookedUp(string status)
    {
        _pending = new() { operation_id = _operation, status = status };
        _statuses.Enqueue("running");
        var wait = new LyricsWaitSpy();
        var presenter = new PlaybackLyricsPresenter(Api(), null, wait.Wait);

        await presenter.EnsureAsync(Snapshot(), new Sink());

        Assert.Equal(PlaybackLyricsLookupState.Looking, presenter.Lookup);
        Assert.True(presenter.Refreshing);
        presenter.Dispose();
    }

    [Theory]
    [InlineData("retry_waiting")]
    [InlineData("failed_retryable")]
    [InlineData("dead_lettered")]
    public async Task ASongOpenedWhileItsAutomaticLookupCouldNotBeMade_SaysItCouldNotBeLookedUpRightNow(string status)
    {
        _pending = new() { operation_id = _operation, status = status };
        using var presenter = new PlaybackLyricsPresenter(Api(), null, new LyricsWaitSpy().Wait);

        await presenter.EnsureAsync(Snapshot(), new Sink());

        Assert.Equal(PlaybackLyricsLookupState.Unreachable, presenter.Lookup);
        Assert.False(presenter.Refreshing);
    }

    [Fact]
    public async Task PressingFindLyricsWhileTheAutomaticLookupIsBeingAskedAbout_FollowsOnlyTheOneLookupPressedFor()
    {
        _pendingGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _statuses.Enqueue("running"); _statuses.Enqueue("succeeded");
        var wait = new LyricsWaitSpy();
        using var presenter = new PlaybackLyricsPresenter(Api(), null, wait.Wait);
        var snapshot = Snapshot();
        var sink = new Sink();
        var opened = presenter.EnsureAsync(snapshot, sink);
        await WaitUntilAsync(() => _pendingReads == 1);

        var find = presenter.RefreshAsync();
        await WaitUntilAsync(() => wait.Requested.Count == 1);
        _pendingGate.SetResult(new() { operation_id = _operation, status = "running" });
        await opened;

        // The late answer about the automatic lookup starts no second polling loop beside the one Find lyrics started.
        Assert.Equal(1, _queued);
        Assert.Single(wait.Requested);
        await wait.ReleaseNextAsync();
        await WaitUntilAsync(() => _statusReads.Count == 1);
        await wait.ReleaseNextAsync();
        await find;
        Assert.Equal(["running", "succeeded"], _statusReads);
        Assert.Equal(2, wait.Requested.Count);
    }

    [Fact]
    public async Task PressingFindLyricsWhileStillLooking_DoesNothing_SoNoSecondLoopStarts()
    {
        _statuses.Enqueue("running");
        var wait = new LyricsWaitSpy();
        var presenter = new PlaybackLyricsPresenter(Api(), null, wait.Wait);
        await presenter.EnsureAsync(Snapshot(), new Sink());
        var find = presenter.RefreshAsync();
        for (var look = 0; look < 16; look++)
        {
            await wait.ReleaseNextAsync();
        }
        await WaitUntilAsync(() => presenter.Lookup == PlaybackLyricsLookupState.StillLooking);
        var asked = wait.Requested.Count;

        await presenter.RefreshAsync();

        Assert.Equal(1, _queued);
        Assert.Equal(asked, wait.Requested.Count);
        Assert.Equal(PlaybackLyricsLookupState.StillLooking, presenter.Lookup);
        presenter.Dispose();
        await find;
    }

    [Fact]
    public async Task AnUnexpectedFailureWhileFollowingALookup_IsLogged_AndTheMessageSaysTheSearchCouldNotStart()
    {
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetTextTracksAsync), _ => Task.FromResult<IReadOnlyList<TextTrackDto>>([]));
            stub.SetHandler(nameof(IEngineApiClient.QueueTextTrackRefreshAsync), _ =>
                Task.FromResult<RefreshTextTracksResponse?>(new() { operation_id = _operation, status = "queued" }));
            stub.SetHandler(nameof(IEngineApiClient.GetTextTrackRefreshStatusAsync), _ => throw new InvalidOperationException("boom"));
        });
        var logger = new RecordingLogger();
        using var presenter = new PlaybackLyricsPresenter(api, null, new LyricsWaitSpy(instant: true).Wait, logger);
        await presenter.EnsureAsync(Snapshot(), new Sink());

        await presenter.RefreshAsync();

        Assert.Equal("Lyrics search could not start.", presenter.Status);
        var entry = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.IsType<InvalidOperationException>(entry.Exception);
    }

    [Fact]
    public async Task AnUnexpectedFailureWhileFollowingAnAutomaticLookup_IsLogged()
    {
        _pending = new() { operation_id = _operation, status = "running" };
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetTextTracksAsync), _ => Task.FromResult<IReadOnlyList<TextTrackDto>>([]));
            stub.SetHandler(nameof(IEngineApiClient.GetPendingTextTrackRefreshAsync), _ => Task.FromResult(_pending));
            stub.SetHandler(nameof(IEngineApiClient.GetTextTrackRefreshStatusAsync), _ => throw new InvalidOperationException("boom"));
        });
        var logger = new RecordingLogger();
        using var presenter = new PlaybackLyricsPresenter(api, null, new LyricsWaitSpy(instant: true).Wait, logger);

        await presenter.EnsureAsync(Snapshot(), new Sink());
        await WaitUntilAsync(() => presenter.Status == "Lyrics search could not start." && !presenter.Refreshing);

        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task ChangingSongWhileLooking_IsNotLoggedAsAnError()
    {
        _statuses.Enqueue("running");
        var wait = new LyricsWaitSpy();
        var logger = new RecordingLogger();
        using var presenter = new PlaybackLyricsPresenter(Api(), null, wait.Wait, logger);
        var sink = new Sink();
        await presenter.EnsureAsync(Snapshot(), sink);
        var find = presenter.RefreshAsync();
        await WaitUntilAsync(() => wait.Requested.Count == 1);

        presenter.Observe(Snapshot(), sink);
        await find;

        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    private sealed class RecordingLogger : ILogger<PlaybackLyricsPresenter>
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception));
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
