using System.Collections.Concurrent;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>How much of one plugin's Activity list is kept: the newest <see cref="MaxEntries"/> lines, none older than <see cref="MaxDays"/> days.</summary>
public readonly record struct PluginActivityCap(int MaxEntries, int MaxDays);

/// <summary>
/// The per-plugin logging cap (Plugins and Integrations screens v5 D59): by default 200 lines or 14 days, whichever comes first,
/// kept in <c>core.json</c> beside the other Engine-wide choices. The Activity store reads it on every write, so the cap is enforced
/// where the lines are kept and not only where they are shown.
/// </summary>
public interface IPluginActivityLimits
{
    PluginActivityCap Get(string pluginId);
}

public sealed class PluginActivityLimits(IConfigurationLoader configuration) : IPluginActivityLimits
{
    public const int DefaultEntries = 200;
    public const int DefaultDays = 14;
    public const int MinimumEntries = 10;
    public const int MaximumEntries = 1000;
    public const int MinimumDays = 1;
    public const int MaximumDays = 90;

    public static PluginActivityCap Default { get; } = new(DefaultEntries, DefaultDays);

    private readonly ConcurrentDictionary<string, PluginActivityCap> _cache = new(StringComparer.Ordinal);
    private bool _loaded;
    private readonly object _gate = new();

    public PluginActivityCap Get(string pluginId)
    {
        EnsureLoaded();
        return _cache.TryGetValue(Key(pluginId), out var cap) ? cap : Default;
    }

    /// <summary>Saves a cap. Throws <see cref="ArgumentOutOfRangeException"/> when a number is outside what the page allows.</summary>
    public PluginActivityCap Set(string pluginId, int maxEntries, int maxDays)
    {
        if (maxEntries is < MinimumEntries or > MaximumEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries), $"Keep between {MinimumEntries} and {MaximumEntries} entries.");
        }

        if (maxDays is < MinimumDays or > MaximumDays)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDays), $"Keep between {MinimumDays} and {MaximumDays} days.");
        }

        lock (_gate)
        {
            var core = configuration.LoadCore();
            core.PluginActivityLimits[Key(pluginId)] = new PluginActivityLimit { MaxEntries = maxEntries, MaxDays = maxDays };
            configuration.SaveCore(core);
            _loaded = false;
        }

        var cap = new PluginActivityCap(maxEntries, maxDays);
        _cache[Key(pluginId)] = cap;
        return cap;
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        lock (_gate)
        {
            if (_loaded)
            {
                return;
            }

            _cache.Clear();
            foreach (var (id, limit) in configuration.LoadCore().PluginActivityLimits)
            {
                _cache[Key(id)] = new PluginActivityCap(
                    Math.Clamp(limit.MaxEntries, MinimumEntries, MaximumEntries),
                    Math.Clamp(limit.MaxDays, MinimumDays, MaximumDays));
            }

            _loaded = true;
        }
    }

    private static string Key(string pluginId) => pluginId.Trim().ToLowerInvariant();
}
