using MediaEngine.Providers.Workers;

namespace MediaEngine.Providers.Tests;

public sealed class WikidataBridgeStallWatchdogTests
{
    [Fact]
    public async Task StalledWork_IsStopped_AndNamesTheStepItWasIn()
    {
        string? reported = null;
        var cancelledSeen = false;

        var failure = await Assert.ThrowsAsync<TimeoutException>(() =>
            WikidataBridgeWorker.RunWithStallWatchdogAsync(
                async token =>
                {
                    token.Register(() => cancelledSeen = true);
                    await Task.Delay(Timeout.Infinite, CancellationToken.None);
                },
                TimeSpan.FromMilliseconds(50),
                () => "saving bridge IDs",
                step => reported = step,
                CancellationToken.None));

        Assert.Equal("saving bridge IDs", reported);
        Assert.Contains("saving bridge IDs", failure.Message);
        Assert.True(cancelledSeen);
    }

    [Fact]
    public async Task FinishedWork_DoesNotReportAStall()
    {
        var reported = false;

        await WikidataBridgeWorker.RunWithStallWatchdogAsync(
            _ => Task.CompletedTask,
            TimeSpan.FromSeconds(5),
            () => "starting",
            _ => reported = true,
            CancellationToken.None);

        Assert.False(reported);
    }

    [Fact]
    public async Task WorkThatFailsOnItsOwn_KeepsItsOwnError()
    {
        var reported = false;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WikidataBridgeWorker.RunWithStallWatchdogAsync(
                _ => Task.FromException(new InvalidOperationException("write failed")),
                TimeSpan.FromSeconds(5),
                () => "starting",
                _ => reported = true,
                CancellationToken.None));

        Assert.Equal("write failed", failure.Message);
        Assert.False(reported);
    }

    [Fact]
    public async Task WorkThatThrowsItsOwnTimeout_IsNotTreatedAsAStall()
    {
        var reported = false;

        var failure = await Assert.ThrowsAsync<TimeoutException>(() =>
            WikidataBridgeWorker.RunWithStallWatchdogAsync(
                _ => Task.FromException(new TimeoutException("provider timed out")),
                TimeSpan.FromSeconds(5),
                () => "starting",
                _ => reported = true,
                CancellationToken.None));

        Assert.Equal("provider timed out", failure.Message);
        Assert.False(reported);
    }
}
