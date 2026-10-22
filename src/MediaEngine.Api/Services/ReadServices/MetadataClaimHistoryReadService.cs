using Dapper;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.ReadServices;

public sealed class MetadataClaimHistoryReadService(
    IMetadataClaimRepository claimRepo,
    IDatabaseConnection db) : IMetadataClaimHistoryReadService
{
    public async Task<List<ClaimDto>> GetClaimHistoryAsync(Guid entityId, CancellationToken ct)
    {
        // The claims of anything in a Vault library are not shown here: the answer is the one an unknown id gets (Videos spec D17).
        if (await IsVaultEntityAsync(entityId, ct).ConfigureAwait(false))
        {
            return [];
        }

        var claims = (await claimRepo.GetByEntityAsync(entityId, ct)).ToList();

        if (claims.Count == 0)
        {
            using var conn = db.CreateConnection();
            var assetIds = (await conn.QueryAsync<Guid>(new CommandDefinition(
                """
                SELECT ma.id
                FROM media_assets ma
                INNER JOIN editions e ON ma.edition_id = e.id
                WHERE e.work_id = @WorkId;
                """,
                new { WorkId = entityId },
                cancellationToken: ct))).ToList();

            if (assetIds.Count > 0)
            {
                var allClaims = new List<MetadataClaim>();
                foreach (var assetId in assetIds)
                {
                    allClaims.AddRange(await claimRepo.GetByEntityAsync(assetId, ct));
                }

                claims = allClaims
                    .GroupBy(c => (c.ClaimKey, c.ClaimValue, c.ProviderId, c.DecisionSourceProviderId))
                    .Select(g => g.OrderByDescending(c => c.Confidence).First())
                    .OrderBy(c => c.ClaimedAt)
                    .ToList();
            }
        }

        return claims.Select(claim => new ClaimDto
        {
            Id = claim.Id,
            ClaimKey = claim.ClaimKey,
            ClaimValue = claim.ClaimValue,
            ProviderId = claim.ProviderId,
            DecisionSourceProviderId = claim.DecisionSourceProviderId,
            Confidence = claim.Confidence,
            IsUserLocked = claim.IsUserLocked,
            ClaimedAt = claim.ClaimedAt,
        }).ToList();
    }

    private async Task<bool> IsVaultEntityAsync(Guid entityId, CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            $"SELECT NOT ({VaultSql.NotVaultAnyEntity("@EntityId")});",
            new { EntityId = GuidSql.ToBlob(entityId) },
            cancellationToken: ct)).ConfigureAwait(false);
    }
}
