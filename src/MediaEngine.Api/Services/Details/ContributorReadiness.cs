using Dapper;
using MediaEngine.Contracts.Details;
using MediaEngine.Storage.Contracts;
using MediaEngine.Storage;

namespace MediaEngine.Api.Services.Details;

public static class ContributorReadiness
{
    public static async Task ApplyAsync(DetailPageViewModel detail, IDatabaseConnection database, CancellationToken ct)
    {
        var credits = detail.ContributorGroups.Concat(detail.FullContributorGroups)
            .SelectMany(group => group.Credits).Concat(detail.PreviewContributors).ToArray();
        var ids = credits.Select(credit => credit.EntityId).Where(id => Guid.TryParse(id, out _)).Select(Guid.Parse).Distinct().Select(GuidSql.ToBlob).ToArray();
        if (ids.Length == 0) return;
        using var connection = database.CreateConnection();
        var pending = (await connection.QueryAsync<Guid>(new CommandDefinition("""
            SELECT DISTINCT p.id FROM persons p
            WHERE p.id IN @ids AND p.enriched_at IS NULL AND EXISTS (
                SELECT 1 FROM person_media_links link
                WHERE link.person_id = p.id AND (
                    EXISTS (SELECT 1 FROM media_operations operation
                        WHERE operation.entity_id = link.media_asset_id
                          AND operation.operation_type = 'enrichment.people'
                          AND operation.status IN ('pending','queued','leased','running','retry_waiting','failed_retryable'))
                    OR EXISTS (SELECT 1 FROM identity_jobs job
                        WHERE job.entity_id = link.media_asset_id
                          AND job.state IN ('Queued','RetailSearching','RetailMatched','BridgeSearching','Hydrating','UniverseEnriching'))))
            """, new { ids }, cancellationToken: ct))).Select(id => id.ToString("D")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var credit in credits) credit.IsUpdatingDetails = pending.Contains(credit.EntityId);
    }
}
