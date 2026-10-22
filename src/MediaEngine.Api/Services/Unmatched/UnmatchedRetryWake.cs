namespace MediaEngine.Api.Services.Unmatched;

/// <summary>
/// Wakes the Unmatched retry loop ahead of its usual wait: a provider or plugin changed, or Retry now made a library's files due. One wake-up
/// covers every request made before the loop next looks. Shared between the retry actions and <see cref="UnmatchedRetryHostedService"/>.
/// </summary>
public sealed class UnmatchedRetryWake : IDisposable
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>Asks the loop to run a pass now. Cheap and safe to call from anywhere.</summary>
    public void Signal()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // The loop is already due to wake; one wake-up covers every request before it.
        }
        catch (ObjectDisposedException)
        {
            // The Engine is stopping, so there is no loop left to wake.
        }
    }

    /// <summary>Waits up to <paramref name="delay"/> for a wake-up. Returns true when woken.</summary>
    public Task<bool> WaitAsync(TimeSpan delay, CancellationToken ct) => _wake.WaitAsync(delay, ct);

    public void Dispose() => _wake.Dispose();
}
