using Dapper;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;

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

    public Task FinishAttemptAsync(ApplicationWebhookDelivery delivery, string description, bool delivered, DateTimeOffset now, CancellationToken ct = default) =>
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
            return true;
        }, ct);

    public Task PruneAsync(DateTimeOffset before, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) => connection.Execute(
            "DELETE FROM application_webhook_deliveries WHERE status<>'pending' AND updated_at<@before;", new { before }, transaction), ct);
}
