using MediaEngine.Domain.Entities;
using MediaEngine.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaEngine.Storage.Benchmarks.Queries;

using Result = IReadOnlyDictionary<Guid, IReadOnlyList<MetadataClaim>>;

/// <summary>
/// Q2: <c>MetadataClaimRepository.GetByEntitiesAsync</c> (src/MediaEngine.Storage/MetadataClaimRepository.cs:211) with 500 ids
/// over the claims table. The high-volume claims read behind enrichment and detail pages.
/// </summary>
internal static class Q2MetadataClaims
{
    // Matches SqliteBatching.MaxParametersPerQuery (internal to Storage): one 500-id batch per statement.
    private const int BatchSize = 500;

    /// <summary>Production code, called as the Engine calls it.</summary>
    public static Task<Result> Dapper(BenchContext context, IReadOnlyList<Guid> ids) =>
        new MetadataClaimRepository(context.Database).GetByEntitiesAsync(ids);

    /// <summary>
    /// The production SQL text (MetadataClaimRepository.GetByEntitiesAsync at commit 2210bc763) through EF raw SQL on
    /// the mapped set, plus the same batching loop and grouping, synchronously like the production read. Column aliases dropped (FromSql maps by column name).
    /// </summary>
    public static Task<Result> EfRaw(BenchContext context, IReadOnlyList<Guid> ids)
    {
        using var lease = context.Ef.Rent();
        var results = new List<MetadataClaim>();
        foreach (var batch in ids.Where(id => id != Guid.Empty).Distinct().Chunk(BatchSize))
        {
            var placeholders = new string[batch.Length];
            var parameters = new object[batch.Length];
            for (var index = 0; index < batch.Length; index++)
            {
                placeholders[index] = "@entityId" + index;
                parameters[index] = new SqliteParameter("@entityId" + index, GuidSql.ToBlob(batch[index]));
            }

#pragma warning disable EF1003 // the concatenated text is only generated '@entityId<n>' placeholders, as in production
            results.AddRange(lease.Context.MetadataClaims
                .FromSqlRaw(
                    """
                    SELECT id, entity_id, provider_id, decision_source_provider_id, observation_set_id, claim_key,
                           claim_value, confidence, claimed_at, is_user_locked, is_current, superseded_at
                    FROM metadata_claims
                    WHERE entity_id IN (
                    """ + string.Join(", ", placeholders) + """
                    ) AND is_current = 1
                    ORDER BY entity_id, claimed_at
                    """,
                    parameters)
                .AsNoTracking()
                .ToList());
#pragma warning restore EF1003
        }

        return Task.FromResult(Group(results));
    }

    /// <summary>Idiomatic LINQ over the mapped set (pooled context).</summary>
    public static Task<Result> EfLinq(BenchContext context, IReadOnlyList<Guid> ids)
    {
        using var lease = context.Ef.Rent();
        var results = new List<MetadataClaim>();
        foreach (var batch in ids.Where(id => id != Guid.Empty).Distinct().Chunk(BatchSize))
        {
            var batchIds = batch;
            results.AddRange(lease.Context.MetadataClaims
                .AsNoTracking()
                .Where(claim => batchIds.Contains(claim.EntityId) && claim.IsCurrent)
                .OrderBy(claim => claim.EntityId)
                .ThenBy(claim => claim.ClaimedAt)
                .ToList());
        }

        return Task.FromResult(Group(results));
    }

    public static IEnumerable<string> Lines(Result result)
    {
        // Enumeration order is kept on purpose: it is the SQL ORDER BY order, which must also match.
        foreach (var (entityId, claims) in result)
        {
            yield return $"entity {entityId} rows={claims.Count}";
            foreach (var claim in claims)
            {
                yield return Describe.Row(claim);
            }
        }
    }

    /// <summary>The production grouping step of GetByEntitiesAsync.</summary>
    private static Result Group(List<MetadataClaim> results) =>
        results
            .GroupBy(claim => claim.EntityId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<MetadataClaim>)group.ToList());
}
