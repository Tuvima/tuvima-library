using Dapper;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

public static class IngestionAvailability
{
    public static string UpdatingWorkPredicate(string workId) => $"""
        EXISTS (
            SELECT 1 FROM editions lifecycle_e
            JOIN media_assets lifecycle_a ON lifecycle_a.edition_id = lifecycle_e.id
            WHERE lifecycle_e.work_id = {workId} AND (
                EXISTS (SELECT 1 FROM identity_jobs lifecycle_j WHERE lifecycle_j.entity_id = lifecycle_a.id
                    AND lifecycle_j.state NOT IN ('Ready','ReadyWithoutUniverse','Failed','RetailNoMatch','QidNoMatch','QidNeedsReview'))
                OR EXISTS (SELECT 1 FROM media_operations lifecycle_o
                    WHERE (lifecycle_o.entity_id = lifecycle_a.id OR lifecycle_o.source_path = lifecycle_a.file_path_root)
                    AND lifecycle_o.operation_kind IN ('ingestion','identity')
                    AND lifecycle_o.status IN ('pending','queued','leased','running','retry_waiting','failed_retryable','interrupted'))
            )
        )
        """;

    public static Task<bool> IsUpdatingAsync(IDatabaseConnection database, Guid entityId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        return Task.FromResult(connection.ExecuteScalar<bool>(new CommandDefinition($"""
            WITH RECURSIVE affected(id) AS (
                SELECT id FROM works WHERE id = @entityId OR collection_id = @entityId
                UNION SELECT work_id FROM editions WHERE id = @entityId
                UNION SELECT e.work_id FROM editions e JOIN media_assets a ON a.edition_id=e.id WHERE a.id=@entityId
                UNION SELECT w.id FROM works w JOIN affected parent ON w.parent_work_id=parent.id
            )
            SELECT EXISTS(SELECT 1 FROM affected WHERE {UpdatingWorkPredicate("affected.id")});
            """, new { entityId }, cancellationToken: ct)));
    }
}
