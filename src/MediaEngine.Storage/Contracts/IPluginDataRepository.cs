namespace MediaEngine.Storage.Contracts;

/// <summary>What a plugin has left in the Library's data store, counted for the removal preview (Plugin architecture spec C23).</summary>
/// <param name="Records">Records the plugin keeps for itself.</param>
/// <param name="RecordBytes">Size of those records.</param>
/// <param name="RunRecords">Entries in Scheduled tasks history.</param>
/// <param name="SkipMarkers">Skip markers it detected that nobody has reviewed.</param>
public sealed record PluginDataFootprint(int Records, long RecordBytes, int RunRecords, int SkipMarkers);

/// <summary>What removing a plugin's data took away.</summary>
public sealed record PluginDataRemoved(int Records, int RunRecords, int SkipMarkers);

/// <summary>
/// The one place that knows which tables hold a plugin's own data, so removal cleans every part and a sweep can find what a plugin that
/// is no longer present left behind. Settings, secrets and files are cleaned where they live; Activity history is kept as a short
/// removal note; approval records stay as an audit trail.
/// </summary>
public interface IPluginDataRepository
{
    Task<PluginDataFootprint> GetFootprintAsync(string pluginId, CancellationToken ct = default);

    /// <summary>
    /// Removes the plugin's records, event position, Scheduled tasks history and library limits. With <paramref name="includeOutput"/> it also
    /// removes the skip markers the plugin detected that no person has reviewed; anything a person confirmed, edited or hid stays.
    /// </summary>
    Task<PluginDataRemoved> RemoveAsync(string pluginId, bool includeOutput, CancellationToken ct = default);

    /// <summary>Every plugin id that has records, an event position, Scheduled tasks history or library limits.</summary>
    Task<IReadOnlyList<string>> ListPluginIdsAsync(CancellationToken ct = default);
}
