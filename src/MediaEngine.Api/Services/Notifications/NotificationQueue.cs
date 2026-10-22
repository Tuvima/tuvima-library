using System.Threading.Channels;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Notifications;

/// <summary>
/// The in-memory line between the code that notices something and the service that tells people. <see cref="Publish"/> returns
/// at once. If a burst fills the line, the oldest waiting event is dropped, so a runaway source can never grow memory or
/// slow the work it reports on.
/// </summary>
internal sealed class NotificationQueue : INotificationPublisher
{
    private readonly Channel<NotificationRequest> _channel = Channel.CreateBounded<NotificationRequest>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public ChannelReader<NotificationRequest> Reader => _channel.Reader;

    public void Publish(NotificationRequest request)
    {
        if (request is not null)
        {
            _channel.Writer.TryWrite(request);
        }
    }
}

/// <summary>Takes events off the <see cref="NotificationQueue"/> one at a time and hands them to the <see cref="NotificationService"/>.</summary>
internal sealed class NotificationDispatchWorker(
    NotificationQueue queue,
    NotificationService service,
    ILogger<NotificationDispatchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var request in queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await service.DispatchAsync(request, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "A notification for {EventId} could not be delivered.", request.EventId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The Engine is stopping.
        }
    }
}
