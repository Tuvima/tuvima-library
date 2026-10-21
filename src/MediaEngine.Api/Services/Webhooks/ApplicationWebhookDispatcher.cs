using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaEngine.Api.Services.Events;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Events;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;

namespace MediaEngine.Api.Services.Webhooks;

internal sealed class ApplicationWebhookDispatcher(
    ApplicationWebhookRepository repository,
    IApplicationEventRepository events,
    IApplicationEventDeliveryAuthorizer authorization,
    ApplicationWebhookService management,
    IWebhookTransport transport,
    TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task TickAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        foreach (var endpoint in await repository.ListAsync(null, ct))
        {
            if (!endpoint.IsEnabled)
            {
                continue;
            }

            var types = JsonSerializer.Deserialize<string[]>(endpoint.EventTypesJson) ?? [];
            var items = await events.ReadAfterAsync(endpoint.LastEventSequence, 100, ct);
            if (items.Count == 0)
            {
                continue;
            }

            var pending = new List<ApplicationWebhookDelivery>();
            foreach (var item in items)
            {
                if (!types.Contains(item.EventType, StringComparer.Ordinal) || !await authorization.CanDeliverAsync(endpoint.ApplicationId, item, ct))
                {
                    continue;
                }

                pending.Add(new()
                {
                    Id = Guid.NewGuid(),
                    WebhookId = endpoint.Id,
                    WebhookVersion = endpoint.Version,
                    EventId = item.EventId,
                    EventSequence = item.Sequence,
                    EventJson = JsonSerializer.Serialize(ApplicationEventEnvelopeMapper.ToEnvelope(item), Json),
                    CreatedAt = now,
                    UpdatedAt = now,
                    NextAttemptAt = now,
                });
            }
            await repository.QueueAsync(endpoint, items[^1].Sequence, pending, ct);
        }

        foreach (var delivery in await repository.DueAsync(now, ct))
        {
            ct.ThrowIfCancellationRequested();
            var endpoint = await repository.FindAsync(delivery.WebhookId, ct);
            var envelope = JsonSerializer.Deserialize<ApplicationEventEnvelope>(delivery.EventJson, Json)!;
            var item = new StoredApplicationEvent(delivery.EventSequence, envelope.EventId, envelope.EventType,
                envelope.Version, envelope.OccurredAt, envelope.ServerId,
                new(envelope.Subject.Type, envelope.Subject.Id, envelope.Subject.LibraryId, envelope.Subject.ProfileId), envelope.Payload.GetRawText());
            WebhookSendResult result;
            var started = Stopwatch.GetTimestamp();
            if (endpoint is null || !endpoint.IsEnabled || endpoint.Version != delivery.WebhookVersion)
            {
                result = new(false, "Skipped: webhook disabled or changed", false);
            }
            else if (!await authorization.CanDeliverAsync(endpoint.ApplicationId, item, ct))
            {
                result = new(false, "Skipped: application access revoked", false);
            }
            else if (now - delivery.CreatedAt >= TimeSpan.FromDays(1))
            {
                result = new(false, "Delivery expired", false);
            }
            else
            {
                try
                {
                    var secret = management.RevealSecret(endpoint);
                    result = await transport.SendAsync(endpoint.Url, endpoint.AllowLocalNetwork, secret,
                        delivery.Id, delivery.EventId, Encoding.UTF8.GetBytes(delivery.EventJson), ct);
                }
                catch (CryptographicException) { result = new(false, "Signing secret unavailable; rotate it to resume delivery", false); }
            }
            var elapsed = Stopwatch.GetElapsedTime(started);
            delivery.AttemptCount++;
            delivery.UpdatedAt = clock.GetUtcNow();
            delivery.Status = result.Delivered ? "delivered" : result.Status.StartsWith("Skipped:", StringComparison.Ordinal) ? "skipped"
                : result.Retryable && delivery.AttemptCount < 6 ? "pending" : "failed";
            delivery.NextAttemptAt = delivery.UpdatedAt + RetryDelay(delivery.AttemptCount);
            var description = delivery.Status switch
            {
                "pending" => $"Retry scheduled after attempt {delivery.AttemptCount}/6: {result.Status}",
                "failed" => $"Failed: {result.Status}",
                _ => result.Status,
            };
            var attempt = WebhookAttempts.Describe(delivery, item.EventType, result, delivery.UpdatedAt, elapsed, isTest: false);
            await repository.FinishAttemptAsync(delivery, description, result.Delivered, delivery.UpdatedAt, attempt, ct);
        }
        await repository.PruneAsync(now.AddDays(-7), ct);
    }

    internal static TimeSpan RetryDelay(int attempt) => TimeSpan.FromSeconds(attempt switch
    { <= 1 => 30, 2 => 120, 3 => 600, 4 => 1800, _ => 7200 });
}

internal sealed class ApplicationWebhookWorker(IServiceScopeFactory scopes, ILogger<ApplicationWebhookWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ApplicationWebhookDispatcher>().TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception) { logger.LogWarning("Webhook delivery cycle failed; pending deliveries will be retried."); }
        }
    }
}
