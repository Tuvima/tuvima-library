using Dapper;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

public sealed class ApplicationWebhookRepository(IDatabaseConnection database)
{
    private const string Fields = """
        id Id,application_id ApplicationId,url Url,event_types_json EventTypesJson,
        allow_local_network AllowLocalNetwork,is_enabled IsEnabled,secret_ciphertext SecretCiphertext,
        version Version,last_event_sequence LastEventSequence,last_status LastStatus,
        created_at CreatedAt,updated_at UpdatedAt,last_attempt_at LastAttemptAt,last_success_at LastSuccessAt
        """;

    public Task<IReadOnlyList<ApplicationWebhook>> ListAsync(Guid? applicationId, CancellationToken ct = default)
    {
        using var connection = database.CreateConnection();
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ApplicationWebhook>>(connection.Query<ApplicationWebhook>(new CommandDefinition(
            $"SELECT {Fields} FROM application_webhooks WHERE (@applicationId IS NULL OR application_id=@applicationId) ORDER BY created_at,id;",
            new { applicationId }, cancellationToken: ct)).ToList());
    }

    public Task<ApplicationWebhook?> FindAsync(Guid id, CancellationToken ct = default)
    {
        using var connection = database.CreateConnection();
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(connection.QuerySingleOrDefault<ApplicationWebhook>(new CommandDefinition(
            $"SELECT {Fields} FROM application_webhooks WHERE id=@id;", new { id }, cancellationToken: ct)));
    }

    public Task SaveAsync(ApplicationWebhook webhook, bool create, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            if (create && connection.ExecuteScalar<int>("SELECT COUNT(*) FROM application_webhooks WHERE application_id=@ApplicationId", webhook, transaction) >= 8)
            {
                throw new InvalidOperationException("An application can have at most eight webhooks.");
            }

            if (create)
            {
                connection.Execute("""
                    INSERT INTO application_webhooks(id,application_id,url,event_types_json,allow_local_network,is_enabled,
                        secret_ciphertext,version,last_event_sequence,last_status,created_at,updated_at)
                    VALUES(@Id,@ApplicationId,@Url,@EventTypesJson,@AllowLocalNetwork,@IsEnabled,@SecretCiphertext,@Version,
                        @LastEventSequence,@LastStatus,@CreatedAt,@UpdatedAt);
                    """, webhook, transaction);
            }
            else if (connection.Execute("""
                    UPDATE application_webhooks SET url=@Url,event_types_json=@EventTypesJson,allow_local_network=@AllowLocalNetwork,
                        is_enabled=@IsEnabled,secret_ciphertext=@SecretCiphertext,version=version+1,last_event_sequence=@LastEventSequence,
                        last_status=@LastStatus,updated_at=@UpdatedAt WHERE id=@Id AND application_id=@ApplicationId AND version=@Version;
                    """, webhook, transaction) != 1)
            {
                throw new InvalidOperationException("The webhook changed. Reload before saving.");
            }

            return true;
        }, ct);

    public Task DeleteAsync(Guid applicationId, Guid id, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
            connection.Execute("DELETE FROM application_webhooks WHERE id=@id AND application_id=@applicationId", new { id, applicationId }, transaction), ct);

    /// <summary>Delivery insertion and cursor movement commit together; a crash cannot lose or duplicate an event.</summary>
    public Task<bool> QueueAsync(ApplicationWebhook webhook, long cursor, IReadOnlyList<ApplicationWebhookDelivery> deliveries, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            var pending = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM application_webhook_deliveries WHERE webhook_id=@Id AND status='pending'", webhook, transaction);
            if (pending + deliveries.Count > 256)
            {
                return false;
            }

            if (connection.Execute("UPDATE application_webhooks SET last_event_sequence=@cursor WHERE id=@Id AND version=@Version AND last_event_sequence=@LastEventSequence", new { webhook.Id, webhook.Version, webhook.LastEventSequence, cursor }, transaction) != 1)
            {
                return false;
            }

            foreach (var delivery in deliveries)
            {
                connection.Execute("""
                    INSERT OR IGNORE INTO application_webhook_deliveries(id,webhook_id,webhook_version,event_id,event_sequence,event_json,
                        status,attempt_count,next_attempt_at,created_at,updated_at)
                    VALUES(@Id,@WebhookId,@WebhookVersion,@EventId,@EventSequence,@EventJson,@Status,@AttemptCount,@NextAttemptAt,@CreatedAt,@UpdatedAt);
                    """, delivery, transaction);
            }

            return true;
        }, ct);

    public Task<IReadOnlyList<ApplicationWebhookDelivery>> DueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        using var connection = database.CreateConnection();
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ApplicationWebhookDelivery>>(connection.Query<ApplicationWebhookDelivery>(new CommandDefinition("""
            SELECT id Id,webhook_id WebhookId,webhook_version WebhookVersion,event_id EventId,event_sequence EventSequence,
                event_json EventJson,status Status,attempt_count AttemptCount,next_attempt_at NextAttemptAt,created_at CreatedAt,updated_at UpdatedAt
            FROM application_webhook_deliveries WHERE status='pending' AND next_attempt_at<=@now ORDER BY next_attempt_at,id LIMIT 100;
            """, new { now }, cancellationToken: ct)).ToList());
    }

    /// <summary>The most recent delivery attempt across all webhooks, or null when none has been attempted (or all were pruned).</summary>
    public Task<ApplicationWebhookLastAttempt?> LastAttemptAsync(CancellationToken ct = default)
    {
        using var connection = database.CreateConnection();
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(connection.QuerySingleOrDefault<ApplicationWebhookLastAttempt>(new CommandDefinition("""
            SELECT updated_at At,status Status
            FROM application_webhook_deliveries WHERE attempt_count>0 ORDER BY updated_at DESC LIMIT 1;
            """, cancellationToken: ct)));
    }

    /// <summary>
    /// Saves the delivery's new state and the webhook's last status together. When <paramref name="attempt"/> is given,
    /// its history row is written in the same step, so the history can never disagree with the delivery.
    /// </summary>
    public Task FinishAttemptAsync(ApplicationWebhookDelivery delivery, string description, bool delivered, DateTimeOffset now,
        ApplicationWebhookAttempt? attempt = null, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            connection.Execute("""
                UPDATE application_webhook_deliveries SET status=@Status,attempt_count=@AttemptCount,next_attempt_at=@NextAttemptAt,
                    updated_at=@UpdatedAt WHERE id=@Id;
                """, delivery, transaction);
            connection.Execute("""
                UPDATE application_webhooks SET last_status=@description,last_attempt_at=@now,
                    last_success_at=CASE WHEN @delivered=1 THEN @now ELSE last_success_at END WHERE id=@WebhookId AND version=@WebhookVersion;
                """, new { delivery.WebhookId, delivery.WebhookVersion, description, delivered, now }, transaction);
            if (attempt is not null)
            {
                InsertAttempt(connection, transaction, attempt);
            }

            return true;
        }, ct);

    /// <summary>
    /// Records a "Send test" send: a finished delivery row, its attempt row and the webhook's last status commit together.
    /// A test is never queued and never retried.
    /// </summary>
    public Task RecordTestAsync(ApplicationWebhookDelivery delivery, ApplicationWebhookAttempt attempt, string description, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            connection.Execute("""
                INSERT INTO application_webhook_deliveries(id,webhook_id,webhook_version,event_id,event_sequence,event_json,
                    status,attempt_count,next_attempt_at,created_at,updated_at)
                VALUES(@Id,@WebhookId,@WebhookVersion,@EventId,@EventSequence,@EventJson,@Status,@AttemptCount,@NextAttemptAt,@CreatedAt,@UpdatedAt);
                """, delivery, transaction);
            connection.Execute("""
                UPDATE application_webhooks SET last_status=@description,last_attempt_at=@at,
                    last_success_at=CASE WHEN @delivered=1 THEN @at ELSE last_success_at END WHERE id=@WebhookId;
                """, new { delivery.WebhookId, description, at = attempt.AttemptedAt, delivered = attempt.Delivered }, transaction);
            InsertAttempt(connection, transaction, attempt);
            return true;
        }, ct);

    /// <summary>
    /// The id this server puts in every event it sends (the same value <see cref="ApplicationEventOutboxWriter"/> uses), so a
    /// test event names the same server as real ones. Created on first use, exactly as the outbox writer does.
    /// </summary>
    public Task<string> EventServerIdAsync(CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            connection.Execute("""
                INSERT OR IGNORE INTO storage_metadata(key,value) VALUES ('application_event_server_id', @serverId);
                """, new { serverId = Guid.NewGuid().ToString("D") }, transaction);
            return connection.QuerySingle<string>(
                "SELECT value FROM storage_metadata WHERE key='application_event_server_id';", transaction: transaction);
        }, ct);

    /// <summary>A webhook's delivery attempts, newest first.</summary>
    public Task<IReadOnlyList<ApplicationWebhookAttempt>> ListAttemptsAsync(Guid webhookId, int limit, CancellationToken ct = default)
    {
        using var connection = database.CreateConnection();
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ApplicationWebhookAttempt>>(connection.Query<ApplicationWebhookAttempt>(new CommandDefinition("""
            SELECT id Id,webhook_id WebhookId,delivery_id DeliveryId,event_id EventId,event_type EventType,attempt_number AttemptNumber,
                attempted_at AttemptedAt,delivered Delivered,result Result,http_status HttpStatus,duration_ms DurationMs,is_test IsTest
            FROM application_webhook_attempts WHERE webhook_id=@webhookId ORDER BY attempted_at DESC,rowid DESC LIMIT @limit;
            """, new { webhookId, limit }, cancellationToken: ct)).ToList());
    }

    private static void InsertAttempt(SqliteConnection connection, SqliteTransaction transaction, ApplicationWebhookAttempt attempt) =>
        connection.Execute("""
            INSERT INTO application_webhook_attempts(id,webhook_id,delivery_id,event_id,event_type,attempt_number,attempted_at,
                delivered,result,http_status,duration_ms,is_test)
            VALUES(@Id,@WebhookId,@DeliveryId,@EventId,@EventType,@AttemptNumber,@AttemptedAt,@Delivered,@Result,@HttpStatus,@DurationMs,@IsTest);
            """, attempt, transaction);

    /// <summary>
    /// Removes finished deliveries and attempt history older than <paramref name="before"/>. Each webhook keeps its
    /// newest <see cref="RetainedAttemptsPerWebhook"/> attempts however old, so a quiet failing webhook still shows what happened.
    /// </summary>
    public Task PruneAsync(DateTimeOffset before, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            var removed = connection.Execute(
                "DELETE FROM application_webhook_deliveries WHERE status<>'pending' AND updated_at<@before;", new { before }, transaction);
            return removed + connection.Execute("""
                DELETE FROM application_webhook_attempts WHERE attempted_at<@before AND id NOT IN (
                    SELECT kept.id FROM application_webhook_attempts kept WHERE kept.webhook_id=application_webhook_attempts.webhook_id
                    ORDER BY kept.attempted_at DESC,kept.rowid DESC LIMIT @keep);
                """, new { before, keep = RetainedAttemptsPerWebhook }, transaction);
        }, ct);

    public const int RetainedAttemptsPerWebhook = 20;
}
