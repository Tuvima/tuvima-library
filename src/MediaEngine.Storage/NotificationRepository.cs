using System.Globalization;
using Dapper;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>SQLite implementation of <see cref="INotificationRepository"/>.</summary>
public sealed class NotificationRepository(IDatabaseConnection db) : INotificationRepository
{
    private const string NotificationColumns =
        "id AS Id, event_id AS EventId, title AS Title, body AS Body, group_key AS GroupKey, occurrence_count AS OccurrenceCount, created_at AS CreatedAt, updated_at AS UpdatedAt, read_at AS ReadAt";

    private const string StateColumns =
        "channel_id AS ChannelId, consecutive_failures AS ConsecutiveFailures, state AS State, last_error AS LastError, last_success_at AS LastSuccessAt, last_attempt_at AS LastAttemptAt, switched_off_at AS SwitchedOffAt";

    public Task<StoredNotification> AddOrGroupAsync(string eventId, string title, string body, string groupKey, DateTimeOffset now, TimeSpan groupWindow, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((conn, tx, _) =>
        {
            var since = Text(now - groupWindow);
            var existing = conn.QueryFirstOrDefault<NotificationRow>(
                $"SELECT {NotificationColumns} FROM notifications WHERE group_key = @groupKey AND updated_at >= @since ORDER BY updated_at DESC LIMIT 1;",
                new { groupKey, since }, tx);
            if (existing is null)
            {
                var id = Guid.NewGuid();
                conn.Execute(
                    "INSERT INTO notifications (id, event_id, title, body, group_key, occurrence_count, created_at, updated_at, read_at) VALUES (@id, @eventId, @title, @body, @groupKey, 1, @now, @now, NULL);",
                    new { id = id.ToString(), eventId, title, body, groupKey, now = Text(now) }, tx);
                return new StoredNotification(id, eventId, title, body, groupKey, 1, now, now, null);
            }

            conn.Execute(
                "UPDATE notifications SET occurrence_count = occurrence_count + 1, title = @title, body = @body, updated_at = @now, read_at = NULL WHERE id = @id;",
                new { id = existing.Id, title, body, now = Text(now) }, tx);
            return Map(existing) with { Title = title, Body = body, OccurrenceCount = existing.OccurrenceCount + 1, UpdatedAt = now, ReadAt = null };
        }, ct);

    public Task<IReadOnlyList<StoredNotification>> ListAsync(int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var rows = conn.Query<NotificationRow>(
            $"SELECT {NotificationColumns} FROM notifications ORDER BY updated_at DESC LIMIT @limit;",
            new { limit = Math.Clamp(limit, 1, 500) });
        return Task.FromResult<IReadOnlyList<StoredNotification>>(rows.Select(Map).ToList());
    }

    public Task<int> CountUnreadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        return Task.FromResult(conn.ExecuteScalar<int>("SELECT COUNT(*) FROM notifications WHERE read_at IS NULL;"));
    }

    public Task MarkReadAsync(IReadOnlyCollection<Guid> ids, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        if (ids.Count == 0)
        {
            conn.Execute("UPDATE notifications SET read_at = @now WHERE read_at IS NULL;", new { now = Text(now) });
        }
        else
        {
            conn.Execute("UPDATE notifications SET read_at = @now WHERE read_at IS NULL AND id IN @ids;", new { now = Text(now), ids = ids.Select(id => id.ToString()).ToArray() });
        }

        return Task.CompletedTask;
    }

    public Task PruneAsync(int keep, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        conn.Execute(
            "DELETE FROM notifications WHERE id NOT IN (SELECT id FROM notifications ORDER BY updated_at DESC LIMIT @keep);",
            new { keep = Math.Max(1, keep) });
        return Task.CompletedTask;
    }

    public Task<StoredChannelState?> GetChannelStateAsync(string channelId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var row = conn.QueryFirstOrDefault<StateRow>($"SELECT {StateColumns} FROM notification_channel_state WHERE channel_id = @channelId;", new { channelId });
        return Task.FromResult(row is null ? null : Map(row));
    }

    public Task<IReadOnlyList<StoredChannelState>> ListChannelStatesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var rows = conn.Query<StateRow>($"SELECT {StateColumns} FROM notification_channel_state ORDER BY channel_id;");
        return Task.FromResult<IReadOnlyList<StoredChannelState>>(rows.Select(Map).ToList());
    }

    public Task<StoredChannelState> RecordChannelResultAsync(string channelId, bool success, string? error, DateTimeOffset now, int failuresBeforeOff, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((conn, tx, _) =>
        {
            var current = conn.QueryFirstOrDefault<StateRow>($"SELECT {StateColumns} FROM notification_channel_state WHERE channel_id = @channelId;", new { channelId }, tx);
            var failures = success ? 0 : (current?.ConsecutiveFailures ?? 0) + 1;
            var state = success
                ? "healthy"
                : failures >= failuresBeforeOff ? "off" : "needs_attention";
            var switchedOff = state == "off" ? (current?.State == "off" ? current.SwitchedOffAt : Text(now)) : null;
            conn.Execute(
                """
                INSERT INTO notification_channel_state (channel_id, consecutive_failures, state, last_error, last_success_at, last_attempt_at, switched_off_at)
                VALUES (@channelId, @failures, @state, @error, @successAt, @now, @switchedOff)
                ON CONFLICT (channel_id) DO UPDATE SET
                    consecutive_failures = @failures, state = @state, last_error = @error,
                    last_success_at = COALESCE(@successAt, notification_channel_state.last_success_at),
                    last_attempt_at = @now, switched_off_at = @switchedOff;
                """,
                new { channelId, failures, state, error = success ? null : Limit(error, 300), successAt = success ? Text(now) : null, now = Text(now), switchedOff }, tx);
            return Map(conn.QuerySingle<StateRow>($"SELECT {StateColumns} FROM notification_channel_state WHERE channel_id = @channelId;", new { channelId }, tx));
        }, ct);

    public Task ResetChannelAsync(string channelId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        conn.Execute(
            "UPDATE notification_channel_state SET consecutive_failures = 0, state = 'healthy', last_error = NULL, switched_off_at = NULL WHERE channel_id = @channelId;",
            new { channelId });
        return Task.CompletedTask;
    }

    public Task<RateDecision> TryClaimSendAsync(string channelId, string eventId, DateTimeOffset now, TimeSpan minimumInterval, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((conn, tx, _) =>
        {
            var mark = conn.QueryFirstOrDefault<MarkRow>(
                "SELECT last_sent_at AS LastSentAt, suppressed_count AS SuppressedCount FROM notification_rate_marks WHERE channel_id = @channelId AND event_id = @eventId;",
                new { channelId, eventId }, tx);
            if (mark is not null && now - DateTimeOffset.Parse(mark.LastSentAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) < minimumInterval)
            {
                conn.Execute(
                    "UPDATE notification_rate_marks SET suppressed_count = suppressed_count + 1 WHERE channel_id = @channelId AND event_id = @eventId;",
                    new { channelId, eventId }, tx);
                return new RateDecision(false, 0);
            }

            // The held-back count is carried forward (not reset) so a send that then fails does not lose it; ConfirmSend clears it.
            conn.Execute(
                """
                INSERT INTO notification_rate_marks (channel_id, event_id, last_sent_at, suppressed_count) VALUES (@channelId, @eventId, @now, 0)
                ON CONFLICT (channel_id, event_id) DO UPDATE SET last_sent_at = @now;
                """,
                new { channelId, eventId, now = Text(now) }, tx);
            return new RateDecision(true, mark?.SuppressedCount ?? 0);
        }, ct);

    public Task ConfirmSendAsync(string channelId, string eventId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        conn.Execute(
            "UPDATE notification_rate_marks SET suppressed_count = 0 WHERE channel_id = @channelId AND event_id = @eventId;",
            new { channelId, eventId });
        return Task.CompletedTask;
    }

    public Task<string?> GetSettingAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        return Task.FromResult(conn.QueryFirstOrDefault<string?>("SELECT value_json FROM notification_settings WHERE setting_key = @key;", new { key }));
    }

    public Task SetSettingAsync(string key, string json, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        conn.Execute(
            """
            INSERT INTO notification_settings (setting_key, value_json, updated_at) VALUES (@key, @json, @now)
            ON CONFLICT (setting_key) DO UPDATE SET value_json = @json, updated_at = @now;
            """,
            new { key, json, now = Text(now) });
        return Task.CompletedTask;
    }

    private static string Text(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static string? Limit(string? value, int length) => value is null || value.Length <= length ? value : value[..length];

    private static DateTimeOffset? Parse(string? value) =>
        value is null ? null : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static StoredNotification Map(NotificationRow row) => new(
        Guid.Parse(row.Id), row.EventId, row.Title, row.Body, row.GroupKey, row.OccurrenceCount,
        Parse(row.CreatedAt)!.Value, Parse(row.UpdatedAt)!.Value, Parse(row.ReadAt));

    private static StoredChannelState Map(StateRow row) => new(
        row.ChannelId, row.ConsecutiveFailures, row.State, row.LastError, Parse(row.LastSuccessAt), Parse(row.LastAttemptAt), Parse(row.SwitchedOffAt));

    private sealed class NotificationRow
    {
        public string Id { get; set; } = "";
        public string EventId { get; set; } = "";
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
        public string GroupKey { get; set; } = "";
        public int OccurrenceCount { get; set; }
        public string CreatedAt { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
        public string? ReadAt { get; set; }
    }

    private sealed class StateRow
    {
        public string ChannelId { get; set; } = "";
        public int ConsecutiveFailures { get; set; }
        public string State { get; set; } = "";
        public string? LastError { get; set; }
        public string? LastSuccessAt { get; set; }
        public string? LastAttemptAt { get; set; }
        public string? SwitchedOffAt { get; set; }
    }

    private sealed class MarkRow
    {
        public string LastSentAt { get; set; } = "";
        public int SuppressedCount { get; set; }
    }
}
