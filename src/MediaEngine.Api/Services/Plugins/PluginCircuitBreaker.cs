using System.Collections.Concurrent;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>
/// The automatic pause for any plugin or source (Plugin architecture spec C10). After <see cref="FailuresBeforeOpen"/> failures in a
/// row the breaker opens and the plugin is skipped, so the next source in the order takes over; it retries gently, waiting a minute,
/// then two, doubling up to an hour, and closes again on the first success. In memory: a restart starts clean, like the
/// host-service pauses. Thread-safe.
/// </summary>
public sealed class PluginCircuitBreaker(TimeProvider clock)
{
    public const int FailuresBeforeOpen = 5;
    public static readonly TimeSpan FirstWait = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan LongestWait = TimeSpan.FromHours(1);

    private sealed class State
    {
        public int Failures;
        public int Opens;
        public DateTimeOffset? OpenUntil;
    }

    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the plugin may be asked now: the breaker is closed, or its wait is over (one trial call is allowed).</summary>
    public bool Allow(string key)
    {
        if (!_states.TryGetValue(key, out var state))
        {
            return true;
        }

        lock (state)
        {
            return state.OpenUntil is null || clock.GetUtcNow() >= state.OpenUntil;
        }
    }

    /// <summary>When the plugin will next be tried, or null when it is not paused.</summary>
    public DateTimeOffset? PausedUntil(string key)
    {
        if (!_states.TryGetValue(key, out var state))
        {
            return null;
        }

        lock (state)
        {
            return state.OpenUntil is { } until && clock.GetUtcNow() < until ? until : null;
        }
    }

    public void RecordSuccess(string key)
    {
        if (_states.TryGetValue(key, out var state))
        {
            lock (state)
            {
                state.Failures = 0;
                state.Opens = 0;
                state.OpenUntil = null;
            }
        }
    }

    /// <summary>Counts a failure. Returns true when this failure opened (or re-opened) the breaker.</summary>
    public bool RecordFailure(string key)
    {
        var state = _states.GetOrAdd(key, _ => new State());
        lock (state)
        {
            // A trial call after the wait that fails again re-opens straight away with a longer wait.
            var trial = state.OpenUntil is not null && clock.GetUtcNow() >= state.OpenUntil;
            state.Failures++;
            if (!trial && state.Failures < FailuresBeforeOpen)
            {
                return false;
            }

            var wait = TimeSpan.FromTicks(Math.Min(LongestWait.Ticks, FirstWait.Ticks * (1L << Math.Min(state.Opens, 20))));
            state.Opens++;
            state.OpenUntil = clock.GetUtcNow() + wait;
            return true;
        }
    }

    /// <summary>Forgets a plugin (switched on, off or removed).</summary>
    public void Reset(string key) => _states.TryRemove(key, out _);
}
