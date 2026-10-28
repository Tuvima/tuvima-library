using System.Text;
using System.Text.Json;
using MediaEngine.Plugins;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>
/// The queueing side of the retry queue for plugins that send things to an outside service (Plugin architecture spec C13). Queueing is
/// a quick local write that never waits on the service; the worker delivers later. A plugin can queue only for itself, at most 64 KB
/// at a time and 5,000 waiting items; past that the oldest are dropped so a service that is down for months cannot fill the data store.
/// </summary>
public sealed class PluginOutboxService(IPluginOutboxRepository repository, TimeProvider clock)
{
    public const int MaximumPayloadBytes = 64 * 1024;

    public const int MaximumQueuedPerPlugin = 5000;

    /// <returns>How many of the plugin's oldest waiting items were dropped to make room.</returns>
    public async Task<int> EnqueueAsync(string pluginId, Guid profileId, string payloadJson, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("Name the profile the item belongs to.", nameof(profileId));
        }

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            throw new ArgumentException("There is nothing to queue.", nameof(payloadJson));
        }

        if (Encoding.UTF8.GetByteCount(payloadJson) > MaximumPayloadBytes)
        {
            throw new PluginLimitExceededException($"A queued item can be at most {MaximumPayloadBytes / 1024} KB.");
        }

        try
        {
            using var _ = JsonDocument.Parse(payloadJson);
        }
        catch (JsonException)
        {
            throw new ArgumentException("A queued item must be valid JSON.", nameof(payloadJson));
        }

        return await repository.EnqueueAsync(
            new PluginOutboxRow(Guid.NewGuid(), pluginId, profileId, payloadJson, clock.GetUtcNow(), 0), MaximumQueuedPerPlugin, ct).ConfigureAwait(false);
    }
}

/// <summary>Gives one plugin the retry queue, only with the <c>plugin-outbox</c> permission and only for itself.</summary>
internal sealed class BoundPluginOutbox(string pluginId, IPluginPermissionGate permissions, PluginOutboxService service) : IPluginOutbox
{
    public async Task EnqueueAsync(Guid profileId, string payloadJson, CancellationToken cancellationToken = default)
    {
        var registration = permissions.Demand(pluginId, PluginPermissionIds.PluginOutbox);
        await service.EnqueueAsync(registration.Manifest.Id, profileId, payloadJson, cancellationToken).ConfigureAwait(false);
    }
}
