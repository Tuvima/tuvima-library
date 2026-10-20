using MediaEngine.Api.Services;

namespace MediaEngine.Api.Tests;

public sealed class EnrichmentPipelineExecutionGateTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task PauseAndDrain_WaitsForActiveWorkersAndBlocksNewWorkUntilResume()
    {
        var gate = new EnrichmentPipelineExecutionGate();
        var firstLease = await gate.EnterAsync("first");
        var secondLease = await gate.EnterAsync("second");

        var drained = gate.PauseAndDrainAsync(Generous);
        Assert.False(drained.IsCompleted);
        Assert.True(firstLease.PauseCancellationToken.IsCancellationRequested);
        Assert.True(secondLease.PauseCancellationToken.IsCancellationRequested);

        firstLease.Dispose();
        Assert.False(drained.IsCompleted);

        secondLease.Dispose();
        await drained.WaitAsync(TimeSpan.FromSeconds(1));

        var blockedEntry = gate.EnterAsync("third").AsTask();
        Assert.False(blockedEntry.IsCompleted);

        gate.Resume();
        using var resumedLease = await blockedEntry.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ExecutionLease_CanBeDisposedMoreThanOnce()
    {
        var gate = new EnrichmentPipelineExecutionGate();
        var lease = await gate.EnterAsync("worker");

        lease.Dispose();
        lease.Dispose();

        await gate.PauseAndDrainAsync(Generous).WaitAsync(TimeSpan.FromSeconds(1));
        gate.Resume();
    }

    [Fact]
    public async Task PauseAndDrain_WhenAWorkerIgnoresThePause_TimesOutNamingThatWorker()
    {
        var gate = new EnrichmentPipelineExecutionGate();
        using var stuck = await gate.EnterAsync("StuckWorkerHostedService");
        var finished = await gate.EnterAsync("FinishedWorkerHostedService");
        finished.Dispose();

        var error = await Assert.ThrowsAsync<TimeoutException>(
            () => gate.PauseAndDrainAsync(TimeSpan.FromMilliseconds(100)).WaitAsync(Generous));

        Assert.Contains("StuckWorkerHostedService", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("FinishedWorkerHostedService", error.Message, StringComparison.Ordinal);

        // The gate stays paused until the caller resumes it, and then admits new work.
        var blockedEntry = gate.EnterAsync("next").AsTask();
        Assert.False(blockedEntry.IsCompleted);
        gate.Resume();
        using var resumedLease = await blockedEntry.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task PauseAndDrain_WhenTheCallerCancels_StopsWaitingStraightAway()
    {
        var gate = new EnrichmentPipelineExecutionGate();
        using var stuck = await gate.EnterAsync("StuckWorkerHostedService");
        using var cancellation = new CancellationTokenSource();

        var drain = gate.PauseAndDrainAsync(TimeSpan.FromMinutes(5), cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drain.WaitAsync(Generous));
        gate.Resume();
    }

    [Fact]
    public async Task DescribeActiveWorkers_ListsOnlyWorkersStillInside()
    {
        var gate = new EnrichmentPipelineExecutionGate();
        Assert.Equal("none", gate.DescribeActiveWorkers());

        using var active = await gate.EnterAsync("RetailMatchHostedService");
        var left = await gate.EnterAsync("PeopleEnrichmentHostedService");
        left.Dispose();

        var description = gate.DescribeActiveWorkers();
        Assert.StartsWith("RetailMatchHostedService (active for ", description, StringComparison.Ordinal);
        Assert.DoesNotContain("PeopleEnrichmentHostedService", description, StringComparison.Ordinal);
    }
}
