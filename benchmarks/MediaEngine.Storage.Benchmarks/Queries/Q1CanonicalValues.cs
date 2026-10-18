using MediaEngine.Domain.Entities;
using MediaEngine.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaEngine.Storage.Benchmarks.Queries;

using Result = IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalValue>>;

/// <summary>
/// Q1: <c>CanonicalValueRepository.GetByEntitiesAsync</c> (src/MediaEngine.Storage/CanonicalValueRepository.cs:139) with 500 ids.
/// The most common production shape: a batched single-table read, then grouping in memory.
/// </summary>
internal static class Q1CanonicalValues
{
    // Matches SqliteBatching.MaxParametersPerQuery (internal to Storage): one 500-id batch per statement.
    private const int BatchSize = 500;

    /// <summary>Production code, called as the Engine calls it.</summary>
    public static Task<Result> Dapper(BenchContext context, IReadOnlyList<Guid> ids) =>
        new CanonicalValueRepository(context.Database).GetByEntitiesAsync(ids);

    /// <summary>
    /// The production SQL text (CanonicalValueRepository.GetByEntitiesAsync at commit 2210bc763) through EF raw SQL
    /// on the mapped set, plus the same per-batch loop and in-memory grouping. The column aliases are dropped
    /// because FromSql on a mapped set matches by column name.
    /// </summary>
    public static async Task<Result> EfRaw(BenchContext context, IReadOnlyList<Guid> ids)
    {
        using var lease = context.Ef.Rent();
        var rows = new List<CanonicalValue>();
        foreach (var batch in ids.Where(id => id != Guid.Empty).Distinct().Chunk(BatchSize))
        {
            var placeholders = new string[batch.Length];
            var parameters = new object[batch.Length];
            for (var i = 0; i < batch.Length; i++)
            {
                placeholders[i] = "@entityId" + i;
                parameters[i] = new SqliteParameter("@entityId" + i, GuidSql.ToBlob(batch[i]));
            }

#pragma warning disable EF1003 // the concatenated text is only generated '@entityId<n>' placeholders, as in production
            rows.AddRange(await lease.Context.CanonicalValues
                .FromSqlRaw(
                    """
                    SELECT entity_id, key, value, last_scored_at, is_conflicted, winning_provider_id, needs_review
                    FROM   canonical_values
                    WHERE  entity_id IN (
                    """ + string.Join(", ", placeholders) + """
                    )
                    ORDER  BY entity_id, key ASC
                    """,
                    parameters)
                .AsNoTracking()
                .ToListAsync());
#pragma warning restore EF1003
        }

        return Group(rows);
    }

    /// <summary>Idiomatic LINQ over the mapped set (pooled context).</summary>
    public static Task<Result> EfLinq(BenchContext context, IReadOnlyList<Guid> ids) =>
        EfLinqCore(context.Ef.Rent(), ids);

    /// <summary>Same LINQ, but a fresh non-pooled context per call, to show what context creation costs.</summary>
    public static Task<Result> EfLinqFreshContext(BenchContext context, IReadOnlyList<Guid> ids) =>
        EfLinqCore(context.Ef.RentFresh(), ids);

    private static async Task<Result> EfLinqCore(Ef.EfLease lease, IReadOnlyList<Guid> ids)
    {
        using (lease)
        {
            var rows = new List<CanonicalValue>();
            foreach (var batch in ids.Where(id => id != Guid.Empty).Distinct().Chunk(BatchSize))
            {
                var batchIds = batch;
                rows.AddRange(await lease.Context.CanonicalValues
                    .AsNoTracking()
                    .Where(value => batchIds.Contains(value.EntityId))
                    .OrderBy(value => value.EntityId)
                    .ThenBy(value => value.Key)
                    .ToListAsync());
            }

            return Group(rows);
        }
    }

    public static IEnumerable<string> Lines(Result result)
    {
        // Enumeration order is kept on purpose: it is the SQL ORDER BY order, which must also match.
        foreach (var (entityId, values) in result)
        {
            yield return $"entity {entityId} rows={values.Count}";
            foreach (var value in values)
            {
                yield return Describe.Row(value);
            }
        }
    }

    /// <summary>The production grouping step of GetByEntitiesAsync.</summary>
    private static Result Group(List<CanonicalValue> rows) =>
        rows.GroupBy(row => row.EntityId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<CanonicalValue>)group.ToList());
}
