using MediaEngine.Api.Services;

namespace MediaEngine.Api.Tests;

public sealed class CronSchedulerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task YearlySchedule_WaitsInPiecesWithoutThrowing_AndFiresExactlyOnTime()
    {
        var clock = new SteppingClock(Start);
        var dueAt = Start.AddDays(400);

        var wait = CronScheduler.WaitUntilAsync(dueAt, clock, CancellationToken.None);

        var pieces = 0;
        while (!wait.IsCompleted)
        {
            var timer = await clock.NextTimerAsync(wait);
            if (timer is null)
            {
                break;
            }

            Assert.True(timer.DueTime <= CronScheduler.LongestSingleWait, $"A single wait of {timer.DueTime} is too long.");
            Assert.True(clock.GetUtcNow() < dueAt, "The wait was still running after its due time.");
            pieces++;
            clock.Fire(timer);
        }

        await wait;
        Assert.Equal(dueAt, clock.GetUtcNow());
        Assert.Equal((int)Math.Ceiling((dueAt - Start) / CronScheduler.LongestSingleWait), pieces);
    }

    [Fact]
    public async Task DailySchedule_WaitsOnceAndFiresOnTime()
    {
        var clock = new SteppingClock(Start);
        var dueAt = CronScheduler.NextRunAt("0 3 * * *", TimeSpan.FromHours(24), clock);
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 3, 0, 0, TimeSpan.Zero), dueAt);

        var wait = CronScheduler.WaitUntilAsync(dueAt, clock, CancellationToken.None);
        var timer = await clock.NextTimerAsync(wait);

        Assert.NotNull(timer);
        Assert.Equal(TimeSpan.FromHours(15), timer.DueTime);
        clock.Fire(timer);
        await wait;
        Assert.Equal(dueAt, clock.GetUtcNow());
    }

    [Fact]
    public async Task ClockMovedBackDuringWait_WaitsLongerInsteadOfFiringEarly()
    {
        var clock = new SteppingClock(Start);
        var dueAt = Start.AddHours(10);

        var wait = CronScheduler.WaitUntilAsync(dueAt, clock, CancellationToken.None);
        var first = await clock.NextTimerAsync(wait);
        Assert.NotNull(first);

        // The timer fires, but the wall clock was set back two hours meanwhile.
        clock.Fire(first, wallClockShift: TimeSpan.FromHours(-2));
        var second = await clock.NextTimerAsync(wait);

        Assert.NotNull(second);
        Assert.False(wait.IsCompleted);
        Assert.Equal(TimeSpan.FromHours(2), second.DueTime);
        clock.Fire(second);
        await wait;
        Assert.Equal(dueAt, clock.GetUtcNow());
    }

    [Fact]
    public async Task DueTimeAlreadyPassed_ReturnsAtOnce()
    {
        var clock = new SteppingClock(Start);

        await CronScheduler.WaitUntilAsync(Start.AddMinutes(-5), clock, CancellationToken.None);

        Assert.Equal(0, clock.TimersCreated);
    }

    [Fact]
    public async Task Cancellation_StopsALongWait()
    {
        var clock = new SteppingClock(Start);
        using var cts = new CancellationTokenSource();

        var wait = CronScheduler.WaitUntilAsync(Start.AddDays(400), clock, cts.Token);
        await clock.NextTimerAsync(wait);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public void YearlyCron_NextRunIsMonthsAway()
    {
        var clock = new SteppingClock(Start);

        var next = CronScheduler.NextRunAt("0 4 1 1 *", TimeSpan.FromHours(24), clock);

        Assert.Equal(new DateTimeOffset(2027, 1, 1, 4, 0, 0, TimeSpan.Zero), next);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a cron")]
    [InlineData("0 0 30 2 *")]
    public void EmptyInvalidOrNeverFiringCron_UsesTheFallback(string? cron)
    {
        var clock = new SteppingClock(Start);

        var next = CronScheduler.NextRunAt(cron, TimeSpan.FromMinutes(15), clock);

        Assert.Equal(Start.AddMinutes(15), next);
    }

    /// <summary>
    /// A clock in UTC whose timers only fire when the test fires them; firing a timer moves the clock to its due time.
    /// </summary>
    private sealed class SteppingClock(DateTimeOffset start) : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<SteppingTimer> _pending = [];
        private DateTimeOffset _now = start;

        public int TimersCreated { get; private set; }

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_sync)
            {
                var timer = new SteppingTimer(this, callback, state, dueTime, _now + dueTime);
                _pending.Add(timer);
                TimersCreated++;
                return timer;
            }
        }

        /// <summary>The next armed timer, or null once <paramref name="wait"/> has finished without arming one.</summary>
        public async Task<SteppingTimer?> NextTimerAsync(Task wait)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                lock (_sync)
                {
                    if (_pending.Count > 0)
                    {
                        return _pending[0];
                    }
                }

                if (wait.IsCompleted)
                {
                    return null;
                }

                await Task.Delay(1);
            }

            throw new TimeoutException("The wait never armed a timer.");
        }

        public void Fire(SteppingTimer timer, TimeSpan wallClockShift = default)
        {
            lock (_sync)
            {
                _pending.Remove(timer);
                _now = timer.FiresAt + wallClockShift;
            }

            timer.Invoke();
        }

        internal void Remove(SteppingTimer timer)
        {
            lock (_sync)
            {
                _pending.Remove(timer);
            }
        }
    }

    private sealed class SteppingTimer(
        SteppingClock clock,
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        DateTimeOffset firesAt) : ITimer
    {
        public TimeSpan DueTime { get; } = dueTime;

        public DateTimeOffset FiresAt { get; } = firesAt;

        public void Invoke() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();

        public void Dispose() => clock.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
