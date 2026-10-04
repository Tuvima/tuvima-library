using Dapper;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services;

public sealed class IngestionNotificationReadService(IDatabaseConnection database)
{
    public Task<List<IngestionOperationsBatchDto>> GetRecentAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<IngestionOperationsBatchDto>(new CommandDefinition($"""
            SELECT b.id AS BatchId, b.status AS Status, b.started_at AS StartedAt,
                b.files_review AS ReviewCount,
                CASE WHEN {IngestionBatchActivitySql.HasOutstandingWork} THEN 1 ELSE 0 END AS OutstandingOperations
            FROM ingestion_batches b ORDER BY b.started_at DESC LIMIT 3;
            """, cancellationToken: ct));
        return Task.FromResult(rows.ToList());
    }
}
