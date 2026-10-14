namespace MediaEngine.Api.Security;

/// <summary>
/// A per-device allowance for a paired app's repeatable actions (listing and switching profile): 30 a minute, counted
/// across token rotation because it is keyed by the device, not the token. Only a device the Engine has already
/// verified as a live paired device of the caller's own account is ever counted, so a made-up bearer string, a
/// browser carrying a forged device id, or any unauthenticated caller can never allocate an entry. The table is
/// bounded and drops devices that have been idle for a whole window.
/// </summary>
public sealed class DeviceActionThrottle(TimeProvider clock)
{
    public const int PermitLimit = 30;
    public const int MaxTrackedDevices = 4096;
    private const int EvictIdleAbove = 256;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Queue<DateTimeOffset>> _recent = [];

    /// <summary>Devices currently remembered (for tests and diagnostics).</summary>
    public int TrackedDevices
    {
        get
        {
            lock (_gate)
            {
                return _recent.Count;
            }
        }
    }

    /// <summary>
    /// Counts one action for <paramref name="deviceId"/> and returns true, or returns false (counting nothing) with the
    /// seconds to wait when the device already used its allowance in the last minute.
    /// </summary>
    public bool TryAcquire(Guid deviceId, out int retryAfterSeconds)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            if (!_recent.TryGetValue(deviceId, out var stamps))
            {
                if (_recent.Count >= EvictIdleAbove)
                {
                    EvictIdle(now);
                }

                if (_recent.Count >= MaxTrackedDevices)
                {
                    // More live devices than the table holds: fail closed for the newcomer rather than grow without bound.
                    retryAfterSeconds = (int)Window.TotalSeconds;
                    return false;
                }

                stamps = new Queue<DateTimeOffset>();
                _recent[deviceId] = stamps;
            }

            while (stamps.Count > 0 && now - stamps.Peek() >= Window)
            {
                stamps.Dequeue();
            }

            if (stamps.Count >= PermitLimit)
            {
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((stamps.Peek() + Window - now).TotalSeconds));
                return false;
            }

            stamps.Enqueue(now);
            retryAfterSeconds = 0;
            return true;
        }
    }

    private void EvictIdle(DateTimeOffset now)
    {
        foreach (var (deviceId, stamps) in _recent.ToArray())
        {
            if (stamps.Count == 0 || now - stamps.Last() >= Window)
            {
                _recent.Remove(deviceId);
            }
        }
    }
}
