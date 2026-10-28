namespace MediaEngine.Storage.Contracts;

/// <summary>One queued item. The payload is the plugin's own text; the Library never reads it.</summary>
public sealed record PluginOutboxRow(Guid Id, string PluginId, Guid ProfileId, string PayloadJson, DateTimeOffset CreatedAt, int Attempts);

/// <summary>The retry queue of plugins that send things to an outside service (see <c>plugin_outbox</c>).</summary>
public interface IPluginOutboxRepository
{
    /// <summary>Adds an item that is due now. When the plugin already has <paramref name="maxQueued"/> items, the oldest are dropped to make room.</summary>
    /// <returns>How many old items were dropped to make room.</returns>
    Task<int> EnqueueAsync(PluginOutboxRow row, int maxQueued, CancellationToken ct = default);

    /// <summary>Items of the plugin that are due at <paramref name="now"/>, oldest first.</summary>
    Task<IReadOnlyList<PluginOutboxRow>> ListDueAsync(string pluginId, DateTimeOffset now, int limit, CancellationToken ct = default);

    /// <summary>Plugins that have an item due at <paramref name="now"/>.</summary>
    Task<IReadOnlyList<string>> ListPluginsWithDueItemsAsync(DateTimeOffset now, CancellationToken ct = default);

    Task CompleteAsync(Guid id, CancellationToken ct = default);

    Task RescheduleAsync(Guid id, int attempts, DateTimeOffset nextAttemptAt, string? reason, CancellationToken ct = default);

    Task<int> CountAsync(string pluginId, CancellationToken ct = default);

    /// <summary>Removes everything queued for one profile and plugin (the profile unlinked its account).</summary>
    Task<int> DeleteForProfileAsync(string pluginId, Guid profileId, CancellationToken ct = default);
}
