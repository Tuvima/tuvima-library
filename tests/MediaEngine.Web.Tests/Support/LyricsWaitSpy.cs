namespace MediaEngine.Web.Tests.Support;

/// <summary>
/// Stands in for the pauses between looks at a queued lyrics lookup so tests never sleep. Every pause asked for is recorded; an instant spy lets each
/// pause pass at once, a gated one holds each pause until the test releases it (and ends it if the presenter's token is cancelled).
/// </summary>
internal sealed class LyricsWaitSpy(bool instant = false)
{
    private readonly object _lock = new();
    private readonly List<TaskCompletionSource> _held = [];

    /// <summary>Every pause the presenter asked for, in order.</summary>
    public List<TimeSpan> Requested { get; } = [];

    public Task Wait(TimeSpan span, CancellationToken token)
    {
        lock (_lock)
        {
            Requested.Add(span);
            if (instant)
            {
                return Task.CompletedTask;
            }
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => held.TrySetCanceled(token));
            _held.Add(held);
            return held.Task;
        }
    }

    /// <summary>Lets the oldest held pause pass, waiting for the presenter to ask for it first.</summary>
    public async Task ReleaseNextAsync()
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            lock (_lock)
            {
                var next = _held.FirstOrDefault(pause => !pause.Task.IsCompleted);
                if (next is not null)
                {
                    next.TrySetResult();
                    return;
                }
            }
            await Task.Delay(10);
        }
        throw new TimeoutException("The presenter never asked to wait.");
    }
}
