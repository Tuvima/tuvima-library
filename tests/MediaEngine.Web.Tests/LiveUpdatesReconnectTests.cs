using System.Net;
using System.Net.Http.Json;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

/// <summary>
/// A tab whose live updates were refused (for example the Engine answered 429) waits out a jittered backoff that
/// respects Retry-After and then tries again by itself, instead of staying paused or hammering the Engine on every
/// page load. Each tab asks for one token at a time and reuses it until it nears expiry.
/// </summary>
public sealed class LiveUpdatesReconnectTests : IAsyncDisposable
{
    private readonly TokenEndpoint _engine = new();
    private readonly ControlledDelays _delays = new();
    private readonly UIOrchestratorService _orchestrator;

    public LiveUpdatesReconnectTests()
    {
        var api = EngineApiClientStub.CreateDefault();
        // Nothing listens on the discard port, so a minted token still ends in a refused hub connection.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Engine:BaseUrl"] = "http://127.0.0.1:9" })
            .Build();
        _orchestrator = new UIOrchestratorService(
            api,
            new UniverseStateContainer(),
            new ActiveProfileSessionService(new UnusedJsRuntime(), api),
            configuration,
            NullLogger<UIOrchestratorService>.Instance,
            new DashboardIdentityClient(new SingleClientFactory(_engine)))
        {
            RestartDelay = _delays.Delay,
            // Always the top of the jitter range, so the waits are predictable.
            RetryPolicy = new LiveUpdatesRetryPolicy(() => 1.0),
        };
    }

    [Fact]
    public async Task RefusedToken_WaitsAtLeastTheEnginesRetryAfter_AndPageLoadsDoNotAskAgain()
    {
        _engine.Respond = _ => RateLimited(TimeSpan.FromSeconds(30));

        await _orchestrator.StartSignalRAsync();

        Assert.Equal(1, _engine.Mints);
        Assert.Equal(EngineConnectionState.LiveUpdatesDisconnected, _orchestrator.EngineConnectionState);
        Assert.True(_orchestrator.IsLiveUpdatesRestartScheduled);
        Assert.True(_delays.Requested.Single() >= TimeSpan.FromSeconds(30), $"Waited only {_delays.Requested.Single()}.");

        // Layout, Home and the ingestion page all ask while the wait runs; none of them reaches the Engine.
        await _orchestrator.StartSignalRAsync();
        await _orchestrator.StartSignalRAsync();
        await _orchestrator.StartSignalRAsync();
        Assert.Equal(1, _engine.Mints);
    }

    [Fact]
    public async Task ScheduledRetry_TriesAgainByItself_AndBacksOffWhileTheEngineKeepsRefusing()
    {
        _engine.Respond = _ => RateLimited(retryAfter: null);

        await _orchestrator.StartSignalRAsync();
        _delays.ReleaseNext();
        await WaitUntilAsync(() => _delays.Requested.Count == 2);
        _delays.ReleaseNext();
        await WaitUntilAsync(() => _delays.Requested.Count == 3);

        Assert.Equal(3, _engine.Mints);
        Assert.Equal(
            [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)],
            _delays.Requested);
    }

    [Fact]
    public async Task ScheduledRetry_ConnectsOnceTheEngineAcceptsAgain_ReusingTheMintedToken()
    {
        _engine.Respond = mint => mint == 1 ? RateLimited(retryAfter: null) : Token(TimeSpan.FromMinutes(10));

        await _orchestrator.StartSignalRAsync();
        _delays.ReleaseNext();
        // The token is minted, then the hub itself is unreachable here, so another restart is scheduled.
        await WaitUntilAsync(() => _delays.Requested.Count == 2);
        Assert.Equal(2, _engine.Mints);

        _delays.ReleaseNext();
        await WaitUntilAsync(() => _delays.Requested.Count == 3);
        Assert.Equal(2, _engine.Mints);
    }

    [Fact]
    public async Task ATokenAboutToExpire_IsRenewedOnTheNextAttempt()
    {
        _engine.Respond = _ => Token(TimeSpan.FromSeconds(30));

        await _orchestrator.StartSignalRAsync();
        _delays.ReleaseNext();
        await WaitUntilAsync(() => _delays.Requested.Count == 2);

        Assert.Equal(2, _engine.Mints);
    }

    [Fact]
    public async Task RetryButton_SkipsTheWait()
    {
        _engine.Respond = _ => RateLimited(TimeSpan.FromSeconds(30));
        await _orchestrator.StartSignalRAsync();

        await _orchestrator.RetryLiveUpdatesNowAsync();

        Assert.Equal(2, _engine.Mints);
        Assert.True(_orchestrator.IsLiveUpdatesRestartScheduled);
    }

    [Fact]
    public async Task StartsAskedForAtOnce_MintOneToken()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.Gate = release.Task;
        _engine.Respond = _ => RateLimited(TimeSpan.FromSeconds(30));

        var starts = Enumerable.Range(0, 3).Select(_ => _orchestrator.StartSignalRAsync()).ToArray();
        release.SetResult();
        await Task.WhenAll(starts);

        Assert.Equal(1, _engine.Mints);
    }

    [Fact]
    public void Reconnects_GiveUpAfterTheQuickAttemptsSoRestartsTakeOver()
    {
        var policy = new LiveUpdatesRetryPolicy(() => 0.0);

        var delays = Enumerable.Range(0, 5)
            .Select(attempt => policy.NextRetryDelay(new Microsoft.AspNetCore.SignalR.Client.RetryContext
            {
                PreviousRetryCount = attempt,
            }))
            .ToList();

        Assert.Equal([TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), null], delays);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void RestartWaits_AreJitteredAndCappedAtAMinute(double random)
    {
        var policy = new LiveUpdatesRetryPolicy(() => random);

        for (var attempt = 0; attempt < 20; attempt++)
        {
            var ceiling = TimeSpan.FromTicks(Math.Min(
                LiveUpdatesRetryPolicy.FirstRestartDelay.Ticks << Math.Min(attempt, 16),
                LiveUpdatesRetryPolicy.MaximumRestartDelay.Ticks));
            var delay = policy.NextRestartDelay(attempt, retryAfter: null);

            Assert.InRange(delay, ceiling / 2, ceiling);
        }
    }

    public async ValueTask DisposeAsync() => await _orchestrator.DisposeAsync();

    private static HttpResponseMessage RateLimited(TimeSpan? retryAfter)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        if (retryAfter is { } wait)
        {
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(wait);
        }

        return response;
    }

    private static HttpResponseMessage Token(TimeSpan lifetime) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new IntercomTokenResponse("token-" + Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.Add(lifetime))),
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the scheduled restart.");
            await Task.Delay(20);
        }
    }

    private sealed class TokenEndpoint : HttpMessageHandler
    {
        private int _mints;

        public Func<int, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        public Task Gate { get; set; } = Task.CompletedTask;

        public int Mints => Volatile.Read(ref _mints);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/auth/intercom-token", request.RequestUri?.AbsolutePath);
            var mint = Interlocked.Increment(ref _mints);
            await Gate;
            return Respond(mint);
        }
    }

    private sealed class ControlledDelays
    {
        private readonly object _gate = new();
        private readonly Queue<TaskCompletionSource> _pending = new();
        private readonly List<TimeSpan> _requested = [];

        public IReadOnlyList<TimeSpan> Requested
        {
            get
            {
                lock (_gate)
                {
                    return [.. _requested];
                }
            }
        }

        public Task Delay(TimeSpan delay, CancellationToken ct)
        {
            var wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => wait.TrySetCanceled(ct));
            lock (_gate)
            {
                _requested.Add(delay);
                _pending.Enqueue(wait);
            }

            return wait.Task;
        }

        public void ReleaseNext()
        {
            lock (_gate)
            {
                _pending.Dequeue().TrySetResult();
            }
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("http://engine.test") };
    }

    private sealed class UnusedJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new InvalidOperationException("Live-updates tests do not call the browser.");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new InvalidOperationException("Live-updates tests do not call the browser.");
    }
}
