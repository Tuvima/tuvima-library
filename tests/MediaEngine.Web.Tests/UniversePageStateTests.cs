using MediaEngine.Contracts.Universe;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Universe page re-reads its overview when enrichment moves: the Engine's event names no Universe, so the page asks
/// again, at most once per ten seconds, and never loses the last thing learned.
/// </summary>
public sealed class UniversePageStateTests
{
    private readonly ManualClock _clock = new();
    private readonly List<string> _reads = [];
    private Func<UniversePageLoadResult> _answer = () => UniversePageLoadResult.Loaded(new UniversePageDto());

    [Fact]
    public async Task AFirstRead_ShowsLoadingUntilTheEngineAnswers_ThenTheLoadedPage()
    {
        var state = NewState();
        Assert.Equal(UniversePageStatus.Loading, state.Status);

        await state.LoadAsync("Q1", quiet: false);

        Assert.Equal(UniversePageStatus.Loaded, state.Status);
        Assert.NotNull(state.Page);
        Assert.Equal(["Q1"], _reads);
    }

    [Fact]
    public async Task ANotFoundAnswer_ClearsThePage_AndAnUnreachableEngineIsADifferentState()
    {
        var state = NewState();

        _answer = () => UniversePageLoadResult.NotFound;
        await state.LoadAsync("Q1", quiet: false);
        Assert.Equal(UniversePageStatus.NotFound, state.Status);
        Assert.Null(state.Page);

        _answer = () => UniversePageLoadResult.Unavailable;
        await state.LoadAsync("Q1", quiet: false);
        Assert.Equal(UniversePageStatus.Unavailable, state.Status);
        Assert.Null(state.Page);
    }

    [Fact]
    public async Task AQuietRead_KeepsThePageOnScreen_WhenTheEngineCannotBeReached()
    {
        var state = NewState();
        await state.LoadAsync("Q1", quiet: false);
        var shown = state.Page;

        _answer = () => UniversePageLoadResult.Unavailable;
        await state.LoadAsync("Q1", quiet: true);

        Assert.Equal(UniversePageStatus.Loaded, state.Status);
        Assert.Same(shown, state.Page);
    }

    [Fact]
    public async Task AQuietRead_ReplacesThePageWithTheNewerOne()
    {
        var state = NewState();
        await state.LoadAsync("Q1", quiet: false);
        var newer = new UniversePageDto { GraphEntityCount = 9 };

        _answer = () => UniversePageLoadResult.Loaded(newer);
        await state.LoadAsync("Q1", quiet: true);

        Assert.Same(newer, state.Page);
    }

    [Fact]
    public void BeforeAnyPageIsShown_AnEnrichmentEventTriggersNothing()
    {
        var state = NewState();

        Assert.Null(state.RequestEnrichmentRefresh(_clock.GetUtcNow()));
    }

    [Fact]
    public async Task AnEnrichmentEvent_ReadsAgainAtOnce_WhenTheLastReadIsTenSecondsOld()
    {
        var state = NewState();
        await state.LoadAsync("Q1", quiet: false);
        _clock.Advance(UniversePageState.RefreshInterval);

        Assert.Equal(TimeSpan.Zero, state.RequestEnrichmentRefresh(_clock.GetUtcNow()));
    }

    [Fact]
    public async Task AnEnrichmentEvent_InsideTheWindow_WaitsForTheWindowToEnd_SoTheLastEventIsNeverMissed()
    {
        var state = NewState();
        await state.LoadAsync("Q1", quiet: false);
        _clock.Advance(TimeSpan.FromSeconds(4));

        var wait = state.RequestEnrichmentRefresh(_clock.GetUtcNow());

        Assert.Equal(TimeSpan.FromSeconds(6), wait);
    }

    [Fact]
    public async Task ABurstOfEvents_ScheduleOneRead_NotOnePerEvent()
    {
        var state = NewState();
        await state.LoadAsync("Q1", quiet: false);
        _clock.Advance(TimeSpan.FromSeconds(2));

        var first = state.RequestEnrichmentRefresh(_clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromSeconds(1));
        var second = state.RequestEnrichmentRefresh(_clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromSeconds(1));
        var third = state.RequestEnrichmentRefresh(_clock.GetUtcNow());

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Null(third);
    }

    [Fact]
    public async Task OnceTheScheduledReadStarts_ALaterEventMayScheduleTheNext_ButTheSameEventNeverTwice()
    {
        var state = NewState();
        await state.LoadAsync("Q1", quiet: false);
        _clock.Advance(UniversePageState.RefreshInterval);
        var eventAt = _clock.GetUtcNow();
        Assert.NotNull(state.RequestEnrichmentRefresh(eventAt));

        state.RefreshStarted();
        await state.LoadAsync("Q1", quiet: true);

        // The same event, seen again by a later state change, is already answered.
        Assert.Null(state.RequestEnrichmentRefresh(eventAt));
        _clock.Advance(UniversePageState.RefreshInterval);
        Assert.Equal(TimeSpan.Zero, state.RequestEnrichmentRefresh(_clock.GetUtcNow()));
    }

    [Fact]
    public async Task AReplyThatArrivesAfterANewerReadBegan_IsDropped()
    {
        var first = new TaskCompletionSource<UniversePageLoadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<UniversePageLoadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replies = new Queue<TaskCompletionSource<UniversePageLoadResult>>([first, second]);
        var api = EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetUniversePageAsync), _ => replies.Dequeue().Task));
        var state = new UniversePageState(api, _clock);

        // The first Universe is slow; the person moves on (or presses Retry) before it answers.
        var slow = state.LoadAsync("Q1", quiet: false);
        var current = state.LoadAsync("Q2", quiet: false);
        var newer = new UniversePageDto { GraphEntityCount = 2 };
        var older = new UniversePageDto { GraphEntityCount = 1 };
        second.SetResult(UniversePageLoadResult.Loaded(newer));
        await current;
        first.SetResult(UniversePageLoadResult.Loaded(older));
        await slow;

        Assert.Same(newer, state.Page);
        Assert.Equal(UniversePageStatus.Loaded, state.Status);
    }

    [Fact]
    public async Task AnOlderFailure_NeverTurnsALoadedPageIntoAnError()
    {
        var first = new TaskCompletionSource<UniversePageLoadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<UniversePageLoadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replies = new Queue<TaskCompletionSource<UniversePageLoadResult>>([first, second]);
        var api = EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetUniversePageAsync), _ => replies.Dequeue().Task));
        var state = new UniversePageState(api, _clock);

        var failing = state.LoadAsync("Q1", quiet: false);
        var retry = state.LoadAsync("Q1", quiet: false);
        second.SetResult(UniversePageLoadResult.Loaded(new UniversePageDto()));
        await retry;
        first.SetResult(UniversePageLoadResult.Unavailable);
        await failing;

        Assert.Equal(UniversePageStatus.Loaded, state.Status);
        Assert.NotNull(state.Page);
    }

    private UniversePageState NewState()
    {
        var api = EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetUniversePageAsync), args =>
            {
                _reads.Add((string)args![0]!);
                return Task.FromResult(_answer());
            }));
        return new UniversePageState(api, _clock);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
