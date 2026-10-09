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
/// A remote caller whose address cannot be determined, or who arrives through a reverse proxy that is pointed at the
/// main port (so every visitor shows the proxy's address), is refused with a plain message instead of being counted in
/// a bucket shared with strangers.
/// </summary>
public enum SignInAttemptResult
{
    Allowed,
    TooManyAttempts,

    /// <summary>A remote caller whose address could not be determined.</summary>
    AddressUnknown,

    /// <summary>A trusted reverse proxy connected to the main port, hiding the visitor's address.</summary>
    UseProxyPort,
}

public sealed class SignInAttemptLimiter
{
    public const int HomePerMinute = 10;
    public const int RemotePerMinute = 5;
    public const int RemoteTotalPerMinute = 120;
    public const int MaxTrackedKeys = 4096;
    public const string TooManyAttemptsMessage = "Too many attempts. Try again in a minute.";
    public const string AddressUnknownMessage = "Tuvima Library could not tell where this connection came from, so sign-in is not available from here.";
    public const string UseProxyPortMessage = "This address reaches Tuvima Library through a reverse proxy on the main port, so sign-in is turned off here. Point the reverse proxy at the proxy port (remote.proxy_port) and use that address instead.";

    private const string RemoteTotalKey = "remote:*";

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly IngressClassifier _classifier;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, Queue<DateTimeOffset>> _attempts = new();
    private readonly ILogger<SignInAttemptLimiter>? _logger;
    private readonly object _gate = new();
    private DateTimeOffset _lastSweep;
    private int _proxyOnMainPortWarned;

    public SignInAttemptLimiter(IngressClassifier classifier, TimeProvider? clock = null, ILogger<SignInAttemptLimiter>? logger = null)
    {
        _classifier = classifier;
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
        _lastSweep = _clock.GetUtcNow();
    }

    /// <summary>Number of addresses currently remembered (for tests and diagnostics).</summary>
    public int TrackedKeyCount
    {
        get { lock (_gate) { return _attempts.Count; } }
    }

    /// <summary>
    /// The reason a request cannot sign in at all from where it is (no usable address, or a reverse proxy pointed at the
    /// main port), or null when it can. Logs the proxy mistake once per process so the owner can fix it.
    /// </summary>
    public string? PlaceRefusal(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var address = context.Connection.RemoteIpAddress;
        if (_classifier.IsProxyOnMainPort(address, context.Connection.LocalPort))
        {
            if (Interlocked.Exchange(ref _proxyOnMainPortWarned, 1) == 0)
            {
                _logger?.LogWarning(
                    "A trusted reverse proxy ({Proxy}) is connecting to the main port, so every visitor looks like the same remote address. " +
                    "Sign-in and setup are refused for those visitors. Point the reverse proxy at the proxy port (remote.proxy_port) instead.",
                    address);
            }

            return UseProxyPortMessage;
        }

        return address is null && _classifier.Classify(context) == IngressKind.Remote ? AddressUnknownMessage : null;
    }

    /// <summary>Counts one attempt for the request's connection address.</summary>
    public bool TryAcquire(HttpContext context, out TimeSpan retryAfter) =>
        Acquire(context, out retryAfter) == SignInAttemptResult.Allowed;

    /// <summary>Counts one attempt for the request's connection address and says why it was refused, if it was.</summary>
    public SignInAttemptResult Acquire(HttpContext context, out TimeSpan retryAfter)
    {
        if (PlaceRefusal(context) is { } refusal)
        {
            retryAfter = TimeSpan.Zero;
            return refusal == UseProxyPortMessage ? SignInAttemptResult.UseProxyPort : SignInAttemptResult.AddressUnknown;
        }

        return Acquire(context.Connection.RemoteIpAddress, _classifier.Classify(context), out retryAfter);
    }

    /// <summary>Counts one attempt for a known address and place. Refused attempts are reported as <c>false</c>.</summary>
    public bool TryAcquire(IPAddress? address, IngressKind kind, out TimeSpan retryAfter) =>
        Acquire(address, kind, out retryAfter) == SignInAttemptResult.Allowed;

    /// <summary>
    /// Counts one attempt for a known address and place. A remote caller without an address is refused (there is no
    /// fair bucket for it); a caller on this computer or the home network without an address is allowed uncounted.
    /// </summary>
    public SignInAttemptResult Acquire(IPAddress? address, IngressKind kind, out TimeSpan retryAfter)
    {
        var isRemote = kind == IngressKind.Remote;
        if (address is null)
        {
            retryAfter = TimeSpan.Zero;
            return isRemote ? SignInAttemptResult.AddressUnknown : SignInAttemptResult.Allowed;
        }

        var key = (isRemote ? "remote:" : "home:") + KeyFor(address, isRemote);
        var limit = isRemote ? RemotePerMinute : HomePerMinute;
        return TryAcquireCore(key, limit, isRemote ? RemoteTotalKey : null, out retryAfter)
            ? SignInAttemptResult.Allowed
            : SignInAttemptResult.TooManyAttempts;
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

    private static string KeyFor(IPAddress address, bool isRemote)
    {
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
