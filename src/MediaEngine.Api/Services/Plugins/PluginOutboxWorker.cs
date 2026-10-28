using MediaEngine.Domain.Plugins;
using MediaEngine.Plugins;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>
/// Delivers what plugins queued (Plugin architecture spec C13): every half minute, for each switched-on plugin with something due, it
/// hands each item to the plugin's delivery and keeps or removes it by the answer. A service that is offline costs nothing but a
/// growing pause (1 minute, 5, 30, 2 hours, then every 12 hours); an item is dropped after a week or 12 tries. A plugin that is paused
/// after repeated failures, waiting for something it needs, or switched off is not asked.
/// </summary>
public sealed class PluginOutboxWorker(
    PluginCatalog catalog,
    IPluginOutboxRepository repository,
    PluginProfileConnectionService connections,
    IPluginExecutionContextFactory contexts,
    PluginActivityService activity,
    TimeProvider clock,
    ILogger<PluginOutboxWorker> logger) : BackgroundService
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(7);

    public const int GiveUpAfterAttempts = 12;

    private const int BatchSize = 20;

    private static readonly TimeSpan[] Pauses = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(12)];

    /// <summary>How long to wait before the next try, after the given number of tries so far.</summary>
    public static TimeSpan PauseAfter(int attempts) => Pauses[Math.Clamp(attempts - 1, 0, Pauses.Length - 1)];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProcessOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Delivering queued plugin items failed; trying again shortly");
                }

                await Task.Delay(CheckInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The Engine is stopping.
        }
    }

    /// <summary>One pass over every plugin with something due. Public so tests can drive it without waiting.</summary>
    public async Task ProcessOnceAsync(CancellationToken ct)
    {
        foreach (var pluginId in await repository.ListPluginsWithDueItemsAsync(clock.GetUtcNow(), ct).ConfigureAwait(false))
        {
            await ProcessPluginAsync(pluginId, ct).ConfigureAwait(false);
        }
    }

    private async Task ProcessPluginAsync(string pluginId, CancellationToken ct)
    {
        var registration = catalog.Get(pluginId);
        var delivery = registration?.Capabilities.OfType<IPluginOutboxDelivery>().FirstOrDefault();
        if (registration is null
            || delivery is null
            || !registration.Enabled
            || registration.LoadError is not null
            || !registration.Manifest.Permissions.Contains(PluginPermissionIds.PluginOutbox, StringComparer.Ordinal)
            || catalog.WaitingFor(registration.Manifest.Id) is not null
            || !activity.Allow(registration.Manifest.Id))
        {
            return;
        }

        var now = clock.GetUtcNow();
        var items = await repository.ListDueAsync(registration.Manifest.Id, now, BatchSize, ct).ConfigureAwait(false);
        if (items.Count == 0)
        {
            return;
        }

        var context = new PluginActivityContext(registration.Manifest.Id, registration.Manifest.Version);
        await using var executionContext = contexts.Create(registration.Manifest.Id, "outbox");
        var delivered = 0;
        var dropped = 0;
        for (var index = 0; index < items.Count; index++)
        {
            var row = items[index];
            if (row.Attempts >= GiveUpAfterAttempts || now - row.CreatedAt > GiveUpAfter)
            {
                await repository.CompleteAsync(row.Id, ct).ConfigureAwait(false);
                dropped++;
                continue;
            }

            var result = await DeliverAsync(delivery, registration, row, executionContext, context, ct).ConfigureAwait(false);
            switch (result.Outcome)
            {
                case PluginDeliveryOutcome.Delivered:
                    await repository.CompleteAsync(row.Id, ct).ConfigureAwait(false);
                    delivered++;
                    break;

                case PluginDeliveryOutcome.GiveUp:
                    await repository.CompleteAsync(row.Id, ct).ConfigureAwait(false);
                    dropped++;
                    break;

                default:
                    // The service is probably down for the whole batch: this item waits longer, the rest wait the same pause without
                    // counting a try, and the plugin is asked about it once.
                    var next = now + PauseAfter(row.Attempts + 1);
                    await repository.RescheduleAsync(row.Id, row.Attempts + 1, next, null, ct).ConfigureAwait(false);
                    for (var rest = index + 1; rest < items.Count; rest++)
                    {
                        await repository.RescheduleAsync(items[rest].Id, items[rest].Attempts, next, null, ct).ConfigureAwait(false);
                    }

                    await activity.RecordFailureAsync(context, PluginFailureReason.ServiceUnreachable, result.Reason, ct: ct).ConfigureAwait(false);
                    index = items.Count;
                    break;
            }
        }

        if (delivered > 0)
        {
            await activity.RecordRunAsync(context, delivered, $"Sent {delivered:N0} queued {(delivered == 1 ? "item" : "items")}.", ct).ConfigureAwait(false);
        }

        if (dropped > 0)
        {
            await activity.RecordNoteAsync(context, $"Dropped {dropped:N0} queued {(dropped == 1 ? "item" : "items")} that could not be sent.", ct).ConfigureAwait(false);
        }
    }

    private async Task<PluginDeliveryResult> DeliverAsync(
        IPluginOutboxDelivery delivery,
        PluginRegistration registration,
        PluginOutboxRow row,
        IPluginExecutionContext executionContext,
        PluginActivityContext activityContext,
        CancellationToken ct)
    {
        var item = new PluginOutboxItem(
            row.Id, row.ProfileId, row.PayloadJson, row.CreatedAt, row.Attempts,
            (name, token) => connections.ReadForHostAsync(registration.Manifest.Id, row.ProfileId, name, token));
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(DeliveryTimeout);
            return await delivery.DeliverAsync(item, executionContext, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return PluginDeliveryResult.RetryLater("It took too long to answer.");
        }
        catch (Exception ex)
        {
            await activity.RecordFailureAsync(activityContext, PluginFailureReason.PluginCrashed, exception: ex, ct: ct).ConfigureAwait(false);
            return PluginDeliveryResult.RetryLater();
        }
    }
}
