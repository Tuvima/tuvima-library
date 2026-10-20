using Cronos;

namespace MediaEngine.Api.Services;

/// <summary>
/// Shared helper that calculates the next occurrence of a cron expression and waits until it.
/// All cron expressions are evaluated in the machine's local timezone.
/// </summary>
/// <remarks>
/// Background services wait for their next run with <see cref="WaitUntilAsync(DateTimeOffset, CancellationToken)"/>,
/// never a single <c>Task.Delay</c> of the whole gap: <c>Task.Delay</c> throws for anything longer than about
/// 49.7 days, and an unhandled exception in a hosted service stops the Engine.
/// </remarks>
public static class CronScheduler
{
    /// <summary>
    /// The longest single timer used while waiting. Comfortably below the ~49.7 day <c>Task.Delay</c> limit; the
    /// clock is re-read after each piece, so a wait never ends early and follows wall-clock changes.
    /// </summary>
    public static readonly TimeSpan LongestSingleWait = TimeSpan.FromDays(24);

    /// <summary>
    /// The next time the cron expression fires after <paramref name="from"/> (default: now), or null when the
    /// expression is empty or invalid.
    /// </summary>
    public static DateTimeOffset? NextOccurrence(string? cronExpression, DateTimeOffset? from = null) =>
        NextOccurrence(cronExpression, from ?? DateTimeOffset.Now, TimeZoneInfo.Local);

    /// <summary>
    /// When a background task on this cron schedule should next run: the next occurrence after now, or now plus
    /// <paramref name="fallback"/> when the expression is empty, invalid or never fires.
    /// </summary>
    public static DateTimeOffset NextRunAt(string? cronExpression, TimeSpan fallback) =>
        NextRunAt(cronExpression, fallback, TimeProvider.System);

    /// <inheritdoc cref="NextRunAt(string?, TimeSpan)"/>
    public static DateTimeOffset NextRunAt(string? cronExpression, TimeSpan fallback, TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        return NextOccurrence(cronExpression, now, clock.LocalTimeZone) ?? now + fallback;
    }

    /// <summary>
    /// Waits until <paramref name="dueAt"/>, however far away it is. Returns at once when it has already passed.
    /// </summary>
    public static Task WaitUntilAsync(DateTimeOffset dueAt, CancellationToken cancellationToken) =>
        WaitUntilAsync(dueAt, TimeProvider.System, cancellationToken);

    /// <inheritdoc cref="WaitUntilAsync(DateTimeOffset, CancellationToken)"/>
    public static async Task WaitUntilAsync(DateTimeOffset dueAt, TimeProvider clock, CancellationToken cancellationToken)
    {
        while (true)
        {
            var remaining = dueAt - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            // Round up to whole milliseconds so a sub-millisecond remainder doesn't spin on zero-length timers.
            var piece = remaining < LongestSingleWait
                ? TimeSpan.FromMilliseconds(Math.Ceiling(remaining.TotalMilliseconds))
                : LongestSingleWait;
            await Task.Delay(piece, clock, cancellationToken).ConfigureAwait(false);
        }
    }

    private static DateTimeOffset? NextOccurrence(string? cronExpression, DateTimeOffset from, TimeZoneInfo zone)
    {
        if (string.IsNullOrWhiteSpace(cronExpression))
        {
            return null;
        }

        try
        {
            return CronExpression.Parse(cronExpression).GetNextOccurrence(from, zone);
        }
        catch (CronFormatException)
        {
            return null;
        }
    }
}
