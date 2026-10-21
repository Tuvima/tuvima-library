namespace MediaEngine.Storage.Contracts;

/// <summary>
/// Where each plugin has got to in the Library's event list (<c>plugin_event_cursors</c>): the sequence of the newest
/// event already handed to it.
/// </summary>
public interface IPluginEventCursorRepository
{
    /// <summary>The last delivered sequence, or null when the plugin has never been given a starting point.</summary>
    Task<long?> GetAsync(string pluginId, CancellationToken ct = default);

    Task SetAsync(string pluginId, long lastSequence, DateTimeOffset updatedAt, CancellationToken ct = default);

    Task DeleteAsync(string pluginId, CancellationToken ct = default);
}
