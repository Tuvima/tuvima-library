namespace MediaEngine.Storage.Contracts;

/// <summary>What an Activity entry records.</summary>
public enum PluginActivityKind
{
    /// <summary>A run that did work: how many items changed.</summary>
    Run = 0,

    /// <summary>A failure, with a cause from the fixed list.</summary>
    Failure,

    /// <summary>The plugin skipped an item on purpose (a Vault item, an audience, a library limit).</summary>
    Skipped,

    /// <summary>Anything else worth a line (the plugin was switched on or removed).</summary>
    Note,
}

/// <summary>One line of a plugin's Activity list, as stored in <c>plugin_activity</c>.</summary>
public sealed record StoredPluginActivity(
    long Id,
    DateTimeOffset OccurredAt,
    string PluginId,
    string PluginVersion,
    PluginActivityKind Kind,
    string? Reason,
    Guid? ItemId,
    int ItemsChanged,
    string Summary,
    string? Detail);

/// <summary>
/// A plugin's durable Activity list (<c>plugin_activity</c>): what it did, when, how many items changed, and every failure with its cause.
/// Every method takes the plugin id and touches only that plugin's rows.
/// </summary>
public interface IPluginActivityRepository
{
    /// <summary>Adds one line and keeps only the newest <paramref name="keepPerPlugin"/> lines for that plugin.</summary>
    Task AddAsync(StoredPluginActivity entry, int keepPerPlugin, CancellationToken ct = default);

    /// <summary>The newest lines for one plugin, newest first, optionally only failures.</summary>
    Task<IReadOnlyList<StoredPluginActivity>> GetRecentAsync(string pluginId, int limit, bool failuresOnly, CancellationToken ct = default);

    /// <summary>How many failures the plugin has had since <paramref name="since"/>.</summary>
    Task<int> CountFailuresSinceAsync(string pluginId, DateTimeOffset since, CancellationToken ct = default);

    /// <summary>Removes every line of one plugin. Returns how many were removed.</summary>
    Task<int> DeleteForPluginAsync(string pluginId, CancellationToken ct = default);

    /// <summary>Removes lines older than <paramref name="olderThan"/> for plugins in <paramref name="pluginIds"/> (a removed plugin's note is kept 30 days).</summary>
    Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default);
}
