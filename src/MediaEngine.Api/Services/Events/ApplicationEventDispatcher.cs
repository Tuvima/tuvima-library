using System.Collections.Concurrent;
using System.Threading.Channels;
using MediaEngine.Api.Realtime;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Events;
using Microsoft.AspNetCore.SignalR;

namespace MediaEngine.Api.Services.Events;

internal sealed record ApplicationEventSubscriber(
    string ConnectionId,
    Guid ApplicationId,
    Guid? CredentialId,
    Guid? TokenId,
    RequestAuthority Authority,
    IReadOnlySet<ApplicationPermissionId> Consent,
    IReadOnlySet<string> EventTypes,
    IReadOnlySet<Guid> LibraryIds,
    HubCallerContext? Connection = null);

public sealed class ApplicationEventDispatcher(
    IHubContext<ApplicationEventsHub> hub,
    IApplicationEventRepository repository,
    IServiceScopeFactory scopes,
    ILogger<ApplicationEventDispatcher> logger) : MediaEngine.Api.Security.IDeviceRevocationNotifier
{
    internal const int QueueCapacity = 128;
    private const int ReplayPageSize = 128;
    private const int MaximumReplayScan = 4096;
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);
    private readonly ConcurrentDictionary<string, Subscription> _subscriptions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _ordering = new(1, 1);

    internal async Task<ApplicationEventSubscriptionResult> SubscribeAsync(
        ApplicationEventSubscriber subscriber,
        Guid? afterEventId,
        CancellationToken ct)
    {
        await _ordering.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Remove(subscriber.ConnectionId);
            var bounds = await repository.GetBoundsAsync(ct).ConfigureAwait(false);
            var snapshotLatest = bounds.LatestSequence ?? 0;
            var gap = false;
            var after = snapshotLatest;
            if (afterEventId is { } cursor)
            {
                var found = await repository.FindSequenceAsync(cursor, ct).ConfigureAwait(false);
                gap = found is null;
                after = found ?? Math.Max(0, (bounds.OldestSequence ?? 1) - 1);
            }

            var replay = new List<StoredApplicationEvent>(QueueCapacity);
            var replayOverflow = false;
            var watermark = after;
            if (afterEventId is not null && after < snapshotLatest)
            {
                var scanned = 0;
                while (watermark < snapshotLatest && scanned < MaximumReplayScan)
                {
                    var limit = Math.Min(ReplayPageSize, MaximumReplayScan - scanned);
                    var page = await repository.ReadAfterAsync(watermark, limit, ct).ConfigureAwait(false);
                    if (page.Count == 0)
                    {
                        break;
                    }

                    foreach (var value in page)
                    {
                        if (value.Sequence > snapshotLatest)
                        {
                            break;
                        }

                        watermark = Math.Max(watermark, value.Sequence);
                        scanned++;
                        if (!Matches(subscriber, value))
                        {
                            continue;
                        }

                        if (replayOverflow)
                        {
                            continue;
                        }

                        if (replay.Count == QueueCapacity)
                        {
                            gap = true;
                            replay.Clear();
                            replayOverflow = true;
                        }
                        else
                        {
                            replay.Add(value);
                        }
                    }
                    if (page.Count < limit || watermark >= snapshotLatest)
                    {
                        break;
                    }
                }
                if (watermark < snapshotLatest)
                {
                    gap = true;
                    replay.Clear();
                }
                watermark = snapshotLatest;
            }

            var subscription = new Subscription(subscriber, QueueCapacity, watermark);
            foreach (var value in replay)
            {
                if (!subscription.Channel.Writer.TryWrite(value))
                {
                    throw new InvalidOperationException("The bounded replay queue rejected a validated replay page.");
                }
            }

            _subscriptions[subscriber.ConnectionId] = subscription;
            _ = PumpAsync(subscription);
            return new(subscription.Id, replay.Count, gap, bounds.OldestEventId, bounds.LatestEventId);
        }
        finally
        {
            _ordering.Release();
        }
    }

    public async Task DispatchAsync(StoredApplicationEvent value, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _ordering.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var subscription in _subscriptions.Values)
            {
                if (value.Sequence <= subscription.Watermark)
                {
                    continue;
                }

                subscription.Watermark = value.Sequence;
                if (!Matches(subscription.Subscriber, value))
                {
                    continue;
                }

                if (!subscription.Channel.Writer.TryWrite(value))
                {
                    Overflow(subscription);
                }
            }
        }
        finally
        {
            _ordering.Release();
        }
    }

    /// <summary>
    /// Sends <see cref="ApplicationEventClientMethods.Revoked"/> to every live connection of a revoked device and
    /// closes it, so the app signs out now instead of at its next request.
    /// </summary>
    public async Task NotifyDeviceRevokedAsync(Guid deviceId, CancellationToken ct = default)
    {
        List<Subscription> revoked;
        await _ordering.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            revoked = _subscriptions.Values.Where(value => value.Subscriber.Authority.DeviceId == deviceId).ToList();
            foreach (var subscription in revoked)
            {
                Remove(subscription);
            }
        }
        finally
        {
            _ordering.Release();
        }

        foreach (var subscription in revoked)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(SendTimeout);
                await hub.Clients.Client(subscription.Subscriber.ConnectionId).SendAsync(
                    ApplicationEventClientMethods.Revoked, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Best effort: the device's tokens are already revoked, so a missed notice only delays its sign-out.
                logger.LogDebug(exception,
                    "Best-effort device revoked notification failed for connection {ConnectionId}.",
                    subscription.Subscriber.ConnectionId);
            }

            // Close the connection too, so the app cannot keep a socket open or subscribe again on it.
            subscription.Subscriber.Connection?.Abort();
        }
    }

    public void Remove(string connectionId)
    {
        if (_subscriptions.TryRemove(connectionId, out var subscription))
        {
            Stop(subscription);
        }
    }

    private void Remove(Subscription subscription)
    {
        var pair = new KeyValuePair<string, Subscription>(subscription.Subscriber.ConnectionId, subscription);
        if (((ICollection<KeyValuePair<string, Subscription>>)_subscriptions).Remove(pair))
        {
            Stop(subscription);
        }
    }

    private static void Stop(Subscription subscription)
    {
        subscription.Cancellation.Cancel();
        subscription.Channel.Writer.TryComplete();
    }

    private async Task PumpAsync(Subscription subscription)
    {
        try
        {
            await foreach (var value in subscription.Channel.Reader.ReadAllAsync(subscription.Cancellation.Token))
            {
                bool allowed;
                using var scope = scopes.CreateScope();
                var live = scope.ServiceProvider.GetRequiredService<ApplicationEventSubscriptionAuthorizer>();
                if (subscription.Subscriber.Authority.PrincipalKind == PrincipalKind.ServiceApplication)
                {
                    allowed = await live.CanDeliverServiceConnectionAsync(subscription.Subscriber, value, subscription.Cancellation.Token).ConfigureAwait(false);
                }
                else
                {
                    allowed = await live.CanDeliverDelegatedAsync(subscription.Subscriber, value, subscription.Cancellation.Token).ConfigureAwait(false);
                }

                if (!allowed)
                {
                    continue;
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(subscription.Cancellation.Token);
                timeout.CancelAfter(SendTimeout);
                await hub.Clients.Client(subscription.Subscriber.ConnectionId).SendAsync(
                    ApplicationEventClientMethods.Event,
                    ApplicationEventEnvelopeMapper.ToEnvelope(value), timeout.Token).ConfigureAwait(false);
                subscription.LastDelivered = value.EventId;
            }
        }
        catch (OperationCanceledException) when (subscription.Cancellation.IsCancellationRequested)
        {
            // Closing or replacing a subscription intentionally stops its pending send.
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Application event transport failed for connection {ConnectionId}; removing the subscription.",
                subscription.Subscriber.ConnectionId);
            Remove(subscription);
        }
    }

    private void Overflow(Subscription subscription)
    {
        Remove(subscription);
        _ = NotifyGapAsync(subscription);
    }

    private async Task NotifyGapAsync(Subscription subscription)
    {
        try
        {
            using var timeout = new CancellationTokenSource(SendTimeout);
            await hub.Clients.Client(subscription.Subscriber.ConnectionId).SendAsync(
                ApplicationEventClientMethods.Gap,
                new ApplicationEventGapDto(
                    subscription.LastDelivered,
                    null,
                    "Subscriber queue capacity was exceeded; reconnect from the last delivered event."),
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            subscription.Channel.Writer.TryComplete();
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception,
                "Best-effort application event gap notification failed for connection {ConnectionId}.",
                subscription.Subscriber.ConnectionId);
        }
    }

    private static bool Matches(ApplicationEventSubscriber subscriber, StoredApplicationEvent value) =>
        subscriber.EventTypes.Contains(value.EventType) &&
        (subscriber.LibraryIds.Count == 0 || value.Subject.LibraryId is { } id && subscriber.LibraryIds.Contains(id));

    private sealed class Subscription(ApplicationEventSubscriber subscriber, int capacity, long watermark)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public ApplicationEventSubscriber Subscriber { get; } = subscriber;
        public Channel<StoredApplicationEvent> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<StoredApplicationEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        public long Watermark { get; set; } = watermark;
        public Guid? LastDelivered { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();
    }
}
