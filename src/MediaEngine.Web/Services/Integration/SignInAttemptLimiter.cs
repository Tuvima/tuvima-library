using System.Net;
using System.Net.Sockets;
using MediaEngine.Web.Services.Configuration;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// Per-client-address limit on anonymous sign-in attempts. The Engine sees every Dashboard-forwarded sign-in as
/// coming from the Dashboard, so the Dashboard is where each person is limited: 10 attempts a minute from a
/// home or this-computer address, 5 from a remote one (an IPv6 remote visitor is keyed by its /64, because a
/// single device can rotate through the whole prefix). All remote visitors together are capped at
/// <see cref="RemoteTotalPerMinute"/>, below the Engine's 300 a minute for the Dashboard, so home sign-ins
/// always keep a share. One person's wrong passwords never slow anyone else down.
/// </summary>
public sealed class SignInAttemptLimiter
{
    public const int HomePerMinute = 10;
    public const int RemotePerMinute = 5;
    public const int RemoteTotalPerMinute = 120;
    public const int MaxTrackedKeys = 4096;
    public const string TooManyAttemptsMessage = "Too many attempts. Try again in a minute.";

    private const string RemoteTotalKey = "remote:*";

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly IngressClassifier _classifier;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, Queue<DateTimeOffset>> _attempts = new();
    private readonly object _gate = new();
    private DateTimeOffset _lastSweep;

    public SignInAttemptLimiter(IngressClassifier classifier, TimeProvider? clock = null)
    {
        _classifier = classifier;
        _clock = clock ?? TimeProvider.System;
        _lastSweep = _clock.GetUtcNow();
    }

    /// <summary>Number of addresses currently remembered (for tests and diagnostics).</summary>
    public int TrackedKeyCount
    {
        get { lock (_gate) { return _attempts.Count; } }
    }

    /// <summary>Counts one attempt for the request's connection address.</summary>
    public bool TryAcquire(HttpContext context, out TimeSpan retryAfter)
    {
        ArgumentNullException.ThrowIfNull(context);
        return TryAcquire(context.Connection.RemoteIpAddress, _classifier.Classify(context), out retryAfter);
    }

    /// <summary>
    /// Counts one attempt for a known address and place. An address that cannot be placed counts as
    /// <see cref="IngressKind.Remote"/> and shares the single remote "unknown" key.
    /// </summary>
    public bool TryAcquire(IPAddress? address, IngressKind kind, out TimeSpan retryAfter)
    {
        var isRemote = kind == IngressKind.Remote;
        var key = (isRemote ? "remote:" : "home:") + KeyFor(address, isRemote);
        var limit = isRemote ? RemotePerMinute : HomePerMinute;
        return TryAcquireCore(key, limit, isRemote ? RemoteTotalKey : null, out retryAfter);
    }

    /// <summary>Counts one attempt against an arbitrary key, such as a profile whose PIN is being guessed.</summary>
    public bool TryAcquireKey(string key, int perMinute, out TimeSpan retryAfter) =>
        TryAcquireCore("key:" + key, perMinute, null, out retryAfter);

    private bool TryAcquireCore(string key, int limit, string? totalKey, out TimeSpan retryAfter)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            Sweep(now);

            _attempts.TryGetValue(key, out var mine);
            Queue<DateTimeOffset>? total = null;
            if (totalKey is not null)
            {
                _attempts.TryGetValue(totalKey, out total);
            }

            if (mine is null && _attempts.Count >= MaxTrackedKeys)
            {
                // The table is full of live entries: refuse new addresses rather than grow without bound.
                retryAfter = Window;
                return false;
            }

            if (mine is not null)
            {
                Trim(mine, now);
            }

            if (total is not null)
            {
                Trim(total, now);
            }

            if (mine is not null && mine.Count >= limit)
            {
                retryAfter = RetryAfter(mine, now);
                return false;
            }

            if (total is not null && total.Count >= RemoteTotalPerMinute)
            {
                retryAfter = RetryAfter(total, now);
                return false;
            }

            if (mine is null)
            {
                mine = new Queue<DateTimeOffset>();
                _attempts[key] = mine;
            }

            mine.Enqueue(now);
            if (totalKey is not null)
            {
                if (total is null)
                {
                    total = new Queue<DateTimeOffset>();
                    _attempts[totalKey] = total;
                }

                total.Enqueue(now);
            }

            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    private static string KeyFor(IPAddress? address, bool isRemote)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (isRemote && address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // One device can use any address in its /64, so the whole prefix shares one allowance.
            var bytes = address.GetAddressBytes();
            Array.Clear(bytes, 8, 8);
            return new IPAddress(bytes) + "/64";
        }

        return address.ToString();
    }

    // Expired entries are removed at most once per window, so a flood cannot make every call walk the table.
    private void Sweep(DateTimeOffset now)
    {
        if (now - _lastSweep < Window)
        {
            return;
        }

        _lastSweep = now;
        foreach (var key in _attempts.Where(p => Trim(p.Value, now) == 0).Select(p => p.Key).ToList())
        {
            _attempts.Remove(key);
        }
    }

    private static TimeSpan RetryAfter(Queue<DateTimeOffset> queue, DateTimeOffset now)
    {
        var retryAfter = queue.Peek() + Window - now;
        return retryAfter < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : retryAfter;
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
