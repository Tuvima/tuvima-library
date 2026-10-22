namespace MediaEngine.Api.Services.Notifications;

/// <summary>What a channel is asked to send.</summary>
internal sealed record OutgoingNotification(string EventId, string Title, string Body);

/// <summary>An administrator a message is for.</summary>
internal sealed record NotificationRecipient(Guid AccountId, string Email);

/// <summary>The result of one send. <see cref="Error"/> is plain words, never a secret or a server address.</summary>
internal sealed record ChannelSendResult(bool Success, string? Error)
{
    public static readonly ChannelSendResult Sent = new(true, null);
}

/// <summary>
/// A way a message can leave the Library: email today, channel plugins later (Developer tools spec WP24). The Dashboard inbox is
/// not a channel; the service writes it directly.
/// </summary>
internal interface INotificationChannel
{
    /// <summary>The id the routing table and the channel's health row use.</summary>
    string Id { get; }

    string Name { get; }

    /// <summary>False until the person has set the channel up. A channel that is not configured is skipped, not counted as failing.</summary>
    bool IsConfigured { get; }

    Task<ChannelSendResult> SendAsync(OutgoingNotification message, IReadOnlyList<NotificationRecipient> recipients, CancellationToken ct);
}
