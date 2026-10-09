using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

public sealed class ProviderConnectionCheckRepository(IDatabaseConnection database)
    : IProviderConnectionCheckRepository
{
    public Task<IReadOnlyList<ProviderConnectionCheck>> GetAllAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<ConnectionRow>("""
            SELECT provider_name AS ProviderName, status AS Status, message AS Message,
                   checked_at AS CheckedAt, response_time_ms AS ResponseTimeMs
            FROM provider_connection_checks
            """);
        return Task.FromResult<IReadOnlyList<ProviderConnectionCheck>>(rows.Select(row =>
            new ProviderConnectionCheck(row.ProviderName, row.Status, row.Message,
                DateTimeOffset.Parse(row.CheckedAt), row.ResponseTimeMs)).ToList());
    }

    public Task UpsertAsync(ProviderConnectionCheck check, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        connection.Execute("""
            INSERT INTO provider_connection_checks
                (provider_name, status, message, checked_at, response_time_ms)
            VALUES (@ProviderName, @Status, @Message, @CheckedAt, @ResponseTimeMs)
            ON CONFLICT(provider_name) DO UPDATE SET
                status = excluded.status, message = excluded.message,
                checked_at = excluded.checked_at, response_time_ms = excluded.response_time_ms
            """, new
        {
            check.ProviderName,
            check.Status,
            check.Message,
            CheckedAt = check.CheckedAt.ToString("O"),
            check.ResponseTimeMs,
        });
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string providerName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        connection.Execute("DELETE FROM provider_connection_checks WHERE provider_name = @providerName",
            new { providerName });
        return Task.CompletedTask;
    }

    private sealed class ConnectionRow
    {
        public string ProviderName { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public string CheckedAt { get; init; } = string.Empty;
        public int? ResponseTimeMs { get; init; }
    }
}
