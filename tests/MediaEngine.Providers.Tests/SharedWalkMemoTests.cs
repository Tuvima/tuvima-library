using MediaEngine.Providers.Services;

namespace MediaEngine.Providers.Tests;

public sealed class SharedWalkMemoTests
{
    [Fact]
    public async Task SameKeyAskedByManyCallers_RunsTheLookupOnce()
    {
        var memo = new SharedWalkMemo<int[]>(TimeSpan.FromMinutes(5));
        var runs = 0;
        Task<int[]> Walk() { Interlocked.Increment(ref runs); return Task.FromResult(new[] { 1, 2 }); }

        var answers = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => memo.GetAsync("show", Walk, a => a.Length > 0, CancellationToken.None)));

        Assert.Equal(1, runs);
        Assert.All(answers, a => Assert.Equal(2, a.Length));
    }

    [Fact]
    public async Task EmptyAnswer_IsNotRemembered_SoTheNextCallerTriesAgain()
    {
        var memo = new SharedWalkMemo<int[]>(TimeSpan.FromMinutes(5));
        var runs = 0;
        Task<int[]> Walk() { runs++; return Task.FromResult(Array.Empty<int>()); }

        await memo.GetAsync("show", Walk, a => a.Length > 0, CancellationToken.None);
        await memo.GetAsync("show", Walk, a => a.Length > 0, CancellationToken.None);

        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task FailedLookup_IsNotRemembered()
    {
        var memo = new SharedWalkMemo<int[]>(TimeSpan.FromMinutes(5));
        var runs = 0;
        Task<int[]> Walk() { runs++; return runs == 1 ? Task.FromException<int[]>(new InvalidOperationException("boom")) : Task.FromResult(new[] { 7 }); }

        await Assert.ThrowsAsync<InvalidOperationException>(() => memo.GetAsync("show", Walk, a => a.Length > 0, CancellationToken.None));
        var second = await memo.GetAsync("show", Walk, a => a.Length > 0, CancellationToken.None);

        Assert.Equal(new[] { 7 }, second);
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task ACallerThatCancels_StopsWaiting_WhileTheLookupFinishesForOthers()
    {
        var memo = new SharedWalkMemo<int[]>(TimeSpan.FromMinutes(5));
        var release = new TaskCompletionSource<int[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var cancelled = memo.GetAsync("show", () => release.Task, a => a.Length > 0, cts.Token);
        var other = memo.GetAsync("show", () => release.Task, a => a.Length > 0, CancellationToken.None);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        release.SetResult([3]);
        Assert.Equal(new[] { 3 }, await other);
    }
}
