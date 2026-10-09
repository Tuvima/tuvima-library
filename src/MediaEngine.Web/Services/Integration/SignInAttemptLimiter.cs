using System.Collections.Concurrent;
using MediaEngine.Web.Services.Configuration;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// Per-client-address limit on anonymous sign-in attempts. The Engine sees every Dashboard-forwarded sign-in as
/// coming from the Dashboard, so the Dashboard is where each person is limited: 10 attempts a minute from a
/// home or this-computer address, 5 from a remote one. One person's wrong passwords never slow anyone else.
/// </summary>
public sealed class SignInAttemptLimiter
{
    public const int HomePerMinute = 10;
    public const int RemotePerMinute = 5;
    public const string TooManyAttemptsMessage = "Too many attempts. Try again in a minute.";

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly IngressClassifier _classifier;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _attempts = new();
    private readonly object _gate = new();

    public SignInAttemptLimiter(IngressClassifier classifier, TimeProvider? clock = null)
    {
        _classifier = classifier;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Counts one attempt for the request's client address. A request that cannot be placed (no connection
    /// information) shares one "unknown" address and gets the stricter remote allowance.
    /// </summary>
    public bool TryAcquire(HttpContext? context, out TimeSpan retryAfter)
    {
        var address = context?.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var limit = context is not null && _classifier.Classify(context) != IngressKind.Remote
            ? HomePerMinute
            : RemotePerMinute;
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            var mine = _attempts.GetOrAdd(address, _ => new Queue<DateTimeOffset>());
            Trim(mine, now);
            if (mine.Count >= limit)
            {
                retryAfter = mine.Peek() + Window - now;
                if (retryAfter < TimeSpan.FromSeconds(1))
                {
                    retryAfter = TimeSpan.FromSeconds(1);
                }

                return false;
            }

            mine.Enqueue(now);

            // Forget addresses that have gone quiet so the table cannot grow without bound.
            if (_attempts.Count > 1024)
            {
                foreach (var key in _attempts.Where(p => Trim(p.Value, now) == 0).Select(p => p.Key).ToList())
                {
                    _attempts.TryRemove(key, out _);
                }
            }

            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    private static int Trim(Queue<DateTimeOffset> queue, DateTimeOffset now)
    {
        while (queue.Count > 0 && now - queue.Peek() >= Window)
        {
            queue.Dequeue();
        }

        return queue.Count;
    }
}
