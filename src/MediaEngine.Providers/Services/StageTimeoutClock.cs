using System.Diagnostics;

namespace MediaEngine.Providers.Services;

/// <summary>
/// A stage timeout that does not run while the job is queued for a provider's
/// request limit. Providers with a slow limit (Comic Vine allows 5 a minute)
/// otherwise made every lookup look stalled: most of the 45 seconds was spent
/// waiting for a turn, so the lookup was abandoned and retried forever.
/// </summary>
public sealed class StageTimeoutClock : IDisposable
{
    private static readonly AsyncLocal<StageTimeoutClock?> Ambient = new();

    private readonly CancellationTokenSource _cts;
    private readonly object _gate = new();
    private TimeSpan _remaining;
    private long _runningSince;
    private int _waiters;
    private bool _disposed;

    private StageTimeoutClock(CancellationToken outer, TimeSpan budget)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        _remaining = budget;
        _runningSince = Stopwatch.GetTimestamp();
        _cts.CancelAfter(budget);
    }

    /// <summary>Cancelled when the stage has used its budget or the caller cancels.</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>Starts a clock and makes it the ambient one for work started from here.</summary>
    public static StageTimeoutClock Start(CancellationToken outer, TimeSpan budget)
    {
        var clock = new StageTimeoutClock(outer, budget);
        Ambient.Value = clock;
        return clock;
    }

    /// <summary>
    /// Pauses the ambient clock (if any) until the returned scope is disposed.
    /// Call it around waiting for a provider's request limit.
    /// </summary>
    public static IDisposable PauseAmbient()
    {
        var clock = Ambient.Value;
        if (clock is null)
        {
            return NoPause.Instance;
        }

        clock.Pause();
        return new PauseScope(clock);
    }

    private void Pause()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_waiters++ == 0)
            {
                _remaining -= Stopwatch.GetElapsedTime(_runningSince);
                _cts.CancelAfter(Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void Resume()
    {
        lock (_gate)
        {
            if (_disposed || _waiters == 0)
            {
                return;
            }

            if (--_waiters == 0)
            {
                _runningSince = Stopwatch.GetTimestamp();
                _cts.CancelAfter(_remaining > TimeSpan.Zero ? _remaining : TimeSpan.FromMilliseconds(1));
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        if (ReferenceEquals(Ambient.Value, this))
        {
            Ambient.Value = null;
        }

        _cts.Dispose();
    }

    private sealed class PauseScope(StageTimeoutClock clock) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                clock.Resume();
            }
        }
    }

    private sealed class NoPause : IDisposable
    {
        public static readonly NoPause Instance = new();

        public void Dispose()
        {
        }
    }
}
