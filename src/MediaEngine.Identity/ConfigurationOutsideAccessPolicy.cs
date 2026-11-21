using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Identity.Contracts;

namespace MediaEngine.Identity;

/// <summary>
/// Outside access is on only while the network settings name a way in this build knows (Tailscale, a direct connection or a custom
/// address). A missing, blank or unrecognised connection mode counts as off, so a hand-edited file never opens the door. The answer
/// is kept for a few seconds so an outside request does not read the file each time; saving the network settings or switching
/// outside access off clears it at once.
/// </summary>
public sealed class ConfigurationOutsideAccessPolicy(IConfigurationLoader configuration, TimeProvider? time = null) : IOutsideAccessPolicy
{
    /// <summary>How long a read of the network settings is reused.</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private bool _cachedOn;
    private long _cachedAt;
    private bool _hasCache;

    public bool IsOutsideAccessOn()
    {
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            if (_hasCache && _time.GetElapsedTime(_cachedAt, now) < CacheLifetime)
            {
                return _cachedOn;
            }
        }

        // Read outside the lock: the file read is the slow part, and two racing readers get the same answer.
        var on = IsKnownOutsideMode(configuration.LoadNetwork().Remote.ConnectionMode);
        lock (_gate)
        {
            _cachedOn = on;
            _cachedAt = _time.GetTimestamp();
            _hasCache = true;
        }

        return on;
    }

    /// <inheritdoc />
    public void Invalidate()
    {
        lock (_gate)
        {
            _hasCache = false;
        }
    }

    private static bool IsKnownOutsideMode(string? mode) => mode?.Trim().ToLowerInvariant() is
        NetworkConnectionModes.Tailscale or NetworkConnectionModes.DirectOnly or NetworkConnectionModes.Custom;
}
