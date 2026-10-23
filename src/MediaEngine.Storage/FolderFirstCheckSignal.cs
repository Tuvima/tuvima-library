namespace MediaEngine.Storage;

/// <summary>
/// Wakes the first-check evaluator when a file has reached a final identification outcome in a folder that is being checked, so a folder that
/// has gone over the line is paused straight away rather than at the next look. One wake-up covers every outcome recorded before the
/// evaluator next looks.
/// </summary>
public sealed class FolderFirstCheckSignal : IDisposable
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>Asks the evaluator to look now. Cheap and safe to call from anywhere.</summary>
    public void Signal()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // The evaluator is already due to wake; one wake-up covers every outcome before it.
        }
        catch (ObjectDisposedException)
        {
            // The Engine is stopping, so there is no evaluator left to wake.
        }
    }

    /// <summary>Waits up to <paramref name="delay"/> for a wake-up. Returns true when woken.</summary>
    public Task<bool> WaitAsync(TimeSpan delay, CancellationToken ct) => _wake.WaitAsync(delay, ct);

    public void Dispose() => _wake.Dispose();
}
