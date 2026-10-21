namespace MediaEngine.Storage.Contracts;

/// <summary>One plugin record as stored in <c>plugin_records</c>.</summary>
public sealed record StoredPluginRecord(string PluginId, string Collection, string Key, string Json, int SizeBytes, DateTimeOffset UpdatedAt);

/// <summary>What one plugin is using: its record count and the summed UTF-8 size of its values.</summary>
public sealed record PluginRecordUsage(int Records, long Bytes);

/// <summary>The limits a write is checked against, in one transaction with the write itself.</summary>
public sealed record PluginRecordLimits(int MaxValueBytes, int MaxRecords, long MaxTotalBytes);

/// <summary>How a write ended. Anything other than <see cref="Saved"/> left the stored records unchanged.</summary>
public enum PluginRecordWriteResult
{
    Saved,
    ValueTooLarge,
    TooManyRecords,
    OutOfSpace,
}

/// <summary>
/// A plugin's own records (<c>plugin_records</c>). Every method takes the plugin id and touches only that plugin's rows;
/// the caller (the Library's plugin host) supplies the id from the plugin's registration, never from the plugin.
/// </summary>
public interface IPluginRecordRepository
{
    Task<StoredPluginRecord?> GetAsync(string pluginId, string collection, string key, CancellationToken ct = default);

    /// <summary>Saves (inserts or replaces) a record if it fits the limits; the checks and the write happen together.</summary>
    Task<PluginRecordWriteResult> PutAsync(
        string pluginId,
        string collection,
        string key,
        string json,
        DateTimeOffset updatedAt,
        PluginRecordLimits limits,
        CancellationToken ct = default);

    Task<bool> DeleteAsync(string pluginId, string collection, string key, CancellationToken ct = default);

    /// <summary>Records in a collection ordered by key; <paramref name="keyPrefix"/> keeps only keys that start with it (case-sensitive).</summary>
    Task<IReadOnlyList<StoredPluginRecord>> ListAsync(
        string pluginId,
        string collection,
        string? keyPrefix,
        int limit,
        CancellationToken ct = default);

    Task<PluginRecordUsage> GetUsageAsync(string pluginId, CancellationToken ct = default);
}
