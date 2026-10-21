namespace MediaEngine.Storage.Contracts;

/// <summary>One message in the in-Dashboard inbox.</summary>
public sealed record StoredNotification(
    Guid Id,
    string EventId,
    string Title,
    string Body,
    string GroupKey,
    int OccurrenceCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ReadAt);

/// <summary>How a channel is doing. <see cref="State"/> is <c>healthy</c>, <c>needs_attention</c> or <c>off</c>.</summary>
public sealed record StoredChannelState(
    string ChannelId,
    int ConsecutiveFailures,
    string State,
    string? LastError,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? SwitchedOffAt);

/// <summary>The answer to "may this message go out now?" for one channel and event.</summary>
/// <param name="Allowed">False when the channel sent the same event less than the minimum interval ago.</param>
/// <param name="HeldBack">How many messages were held back since the last one that went out (only when allowed).</param>
public sealed record RateDecision(bool Allowed, int HeldBack);

/// <summary>
/// The notification service's data: the Dashboard inbox, channel health, send-rate marks and settings. Messages go to
/// administrators only, so nothing here is scoped to a profile.
/// </summary>
public interface INotificationRepository
{
    /// <summary>
    /// Adds a message, or folds it into the newest message with the same group key updated within <paramref name="groupWindow"/>
    /// (count goes up, text and time are replaced, and it becomes unread again).
    /// </summary>
    Task<StoredNotification> AddOrGroupAsync(string eventId, string title, string body, string groupKey, DateTimeOffset now, TimeSpan groupWindow, CancellationToken ct = default);

    Task<IReadOnlyList<StoredNotification>> ListAsync(int limit, CancellationToken ct = default);

    Task<int> CountUnreadAsync(CancellationToken ct = default);

    /// <summary>Marks the given messages read; with no ids, marks every unread message read.</summary>
    Task MarkReadAsync(IReadOnlyCollection<Guid> ids, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Keeps the newest <paramref name="keep"/> messages and removes the rest.</summary>
    Task PruneAsync(int keep, CancellationToken ct = default);

    Task<StoredChannelState?> GetChannelStateAsync(string channelId, CancellationToken ct = default);

    Task<IReadOnlyList<StoredChannelState>> ListChannelStatesAsync(CancellationToken ct = default);

    /// <summary>
    /// Records the result of a send. A success makes the channel healthy. A failure counts; after
    /// <paramref name="failuresBeforeOff"/> in a row the channel is switched off. Returns the state after the result.
    /// </summary>
    Task<StoredChannelState> RecordChannelResultAsync(string channelId, bool success, string? error, DateTimeOffset now, int failuresBeforeOff, CancellationToken ct = default);

    /// <summary>Makes a channel healthy again (an administrator turned it back on, or a test send worked).</summary>
    Task ResetChannelAsync(string channelId, CancellationToken ct = default);

    /// <summary>Claims the right to send now, or counts the message as held back. One claim per channel and event per interval.</summary>
    Task<RateDecision> TryClaimSendAsync(string channelId, string eventId, DateTimeOffset now, TimeSpan minimumInterval, CancellationToken ct = default);

    /// <summary>Clears the held-back count once the message that reported it was actually sent.</summary>
    Task ConfirmSendAsync(string channelId, string eventId, CancellationToken ct = default);

    Task<string?> GetSettingAsync(string key, CancellationToken ct = default);

    Task SetSettingAsync(string key, string json, DateTimeOffset now, CancellationToken ct = default);
}
