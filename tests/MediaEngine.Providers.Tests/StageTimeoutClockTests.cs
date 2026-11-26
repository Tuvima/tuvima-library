using MediaEngine.Providers.Services;
using Xunit;

namespace MediaEngine.Providers.Tests;

public sealed class StageTimeoutClockTests
{
    [Fact]
    public async Task Work_PastItsBudget_IsCancelled()
    {
        using var clock = StageTimeoutClock.Start(CancellationToken.None, TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Task.Delay(TimeSpan.FromSeconds(10), clock.Token));
    }

    [Fact]
    public async Task WaitingForAProviderTurn_DoesNotUseUpTheBudget()
    {
        using var clock = StageTimeoutClock.Start(CancellationToken.None, TimeSpan.FromMilliseconds(150));

        using (StageTimeoutClock.PauseAmbient())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), clock.Token);
        }

        Assert.False(clock.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task AfterTheWait_TheRemainingBudgetStillApplies()
    {
        using var clock = StageTimeoutClock.Start(CancellationToken.None, TimeSpan.FromMilliseconds(300));

        using (StageTimeoutClock.PauseAmbient())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(400), CancellationToken.None);
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Task.Delay(TimeSpan.FromSeconds(10), clock.Token));
    }

    [Fact]
    public async Task TwoOverlappingWaits_ResumeOnlyWhenBothHaveEnded()
    {
        using var clock = StageTimeoutClock.Start(CancellationToken.None, TimeSpan.FromMilliseconds(200));

        var first = StageTimeoutClock.PauseAmbient();
        var second = StageTimeoutClock.PauseAmbient();
        await Task.Delay(TimeSpan.FromMilliseconds(400), CancellationToken.None);
        first.Dispose();
        await Task.Delay(TimeSpan.FromMilliseconds(400), CancellationToken.None);
        Assert.False(clock.Token.IsCancellationRequested);

        second.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Task.Delay(TimeSpan.FromSeconds(10), clock.Token));
    }

    [Fact]
    public async Task CallerCancellation_StillCancelsWhileWaiting()
    {
        using var caller = new CancellationTokenSource();
        using var clock = StageTimeoutClock.Start(caller.Token, TimeSpan.FromSeconds(30));

        using (StageTimeoutClock.PauseAmbient())
        {
            await caller.CancelAsync();
            Assert.True(clock.Token.IsCancellationRequested);
        }
    }

    [Fact]
    public void PausingWithNoClock_DoesNothing()
    {
        using var scope = StageTimeoutClock.PauseAmbient();

        Assert.NotNull(scope);
    }
}
