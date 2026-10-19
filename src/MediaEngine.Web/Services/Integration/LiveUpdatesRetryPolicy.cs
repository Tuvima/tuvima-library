using Microsoft.AspNetCore.SignalR.Client;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// When a tab's live-updates connection is retried. Every tab reaches the Engine from the Dashboard's own address, so
/// retries are spread out with jitter and back off, never giving up, and always wait at least as long as the Engine
/// asked (Retry-After).
/// </summary>
public sealed class LiveUpdatesRetryPolicy(Func<double>? random = null) : IRetryPolicy
{
    /// <summary>Quick in-place reconnect attempts after a working connection drops; then the connection closes and restarts take over.</summary>
    public static readonly IReadOnlyList<TimeSpan> ReconnectDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
    ];

    public static readonly TimeSpan FirstRestartDelay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaximumRestartDelay = TimeSpan.FromMinutes(1);

    private readonly Func<double> _random = random ?? Random.Shared.NextDouble;

    /// <summary>
    /// The wait before restart attempt <paramref name="attempt"/> (0 for the first): doubling from two seconds up to a
    /// minute, randomised to between half and all of that, and never shorter than the Engine's Retry-After.
    /// </summary>
    public TimeSpan NextRestartDelay(int attempt, TimeSpan? retryAfter)
    {
        var exponent = Math.Clamp(attempt, 0, 16);
        var ceiling = TimeSpan.FromTicks(Math.Min(
            FirstRestartDelay.Ticks * (1L << exponent),
            MaximumRestartDelay.Ticks));
        var delay = Jitter(ceiling);

        if (retryAfter is { } wait && wait > TimeSpan.Zero && delay < wait)
        {
            // Spread tabs that were all told the same wait over the following quarter of it.
            delay = wait + TimeSpan.FromTicks((long)(wait.Ticks * 0.25 * _random()));
        }

        return delay;
    }

    /// <inheritdoc />
    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        if (retryContext.PreviousRetryCount >= ReconnectDelays.Count)
        {
            return null;
        }

        var delay = ReconnectDelays[(int)retryContext.PreviousRetryCount];
        return delay == TimeSpan.Zero ? delay : Jitter(delay);
    }

    private TimeSpan Jitter(TimeSpan ceiling) =>
        TimeSpan.FromTicks(ceiling.Ticks / 2 + (long)(ceiling.Ticks / 2 * _random()));
}
