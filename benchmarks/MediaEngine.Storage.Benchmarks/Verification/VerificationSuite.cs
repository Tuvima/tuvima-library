using MediaEngine.Contracts.Paging;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Benchmarks.Queries;

namespace MediaEngine.Storage.Benchmarks.Verification;

internal static class VerificationSuite
{
    /// <summary>Why a LINQ form was not built. Mirrored in README.md ("Not practical" table).</summary>
    public static class NotPractical
    {
        public const string Q3Paging =
            "Q3 EF LINQ for the paging query: not practical. It needs MIN() OVER and ROW_NUMBER() OVER (PARTITION BY), which EF Core "
            + "cannot translate; re-expressing them as correlated subqueries changes the query rather than porting it. The LINQ variant "
            + "therefore covers only the three follow-up loads and keeps the paging query as raw SQL.";

        public const string Q4 =
            "Q4 EF LINQ: not practical. About 700 lines of SQL: a nine-CTE chain, ROW_NUMBER, ~20 MAX(CASE ...) aggregates, GROUP_CONCAT, "
            + "correlated subqueries with LIMIT/OFFSET and SQLite date functions. LINQ would need raw SQL fragments for most of it, "
            + "so only the raw-SQL variant is measured.";

        public const string Q5 =
            "Q5 EF LINQ: not practical. MATERIALIZED CTEs, window functions, ~40 correlated subqueries per row, json_extract and a "
            + "view with a 4-way UNION of credit sources. EF Core cannot express MATERIALIZED, window functions or json_extract "
            + "over a TEXT column here without raw SQL, so only the raw-SQL variant is measured.";
    }

    public static IReadOnlyList<VerifyCase> Build(BenchContext context)
    {
        var workload = context.Workload;
        var firstPage = new PagedRequest(0, Workload.FeedPageSize);
        var deepPage = new PagedRequest(workload.FeedDeepOffset, Workload.FeedPageSize);

        return
        [
            new VerifyCase(
                "Q1 canonical values by 500 entities",
                $"{workload.Q1Ids.Count} ids",
                async () => Q1CanonicalValues.Lines(await Q1CanonicalValues.Dapper(context, workload.Q1Ids)).ToList(),
                [
                    new("EF raw SQL", async () => Q1CanonicalValues.Lines(await Q1CanonicalValues.EfRaw(context, workload.Q1Ids)).ToList()),
                    new("EF LINQ (pooled)", async () => Q1CanonicalValues.Lines(await Q1CanonicalValues.EfLinq(context, workload.Q1Ids)).ToList()),
                    new("EF LINQ (fresh context)", async () => Q1CanonicalValues.Lines(await Q1CanonicalValues.EfLinqFreshContext(context, workload.Q1Ids)).ToList()),
                ],
                []),
            new VerifyCase(
                "Q2 current metadata claims by 500 entities",
                $"{workload.Q2Ids.Count} ids",
                async () => Q2MetadataClaims.Lines(await Q2MetadataClaims.Dapper(context, workload.Q2Ids)).ToList(),
                [
                    new("EF raw SQL", async () => Q2MetadataClaims.Lines(await Q2MetadataClaims.EfRaw(context, workload.Q2Ids)).ToList()),
                    new("EF LINQ", async () => Q2MetadataClaims.Lines(await Q2MetadataClaims.EfLinq(context, workload.Q2Ids)).ToList()),
                ],
                []),
            FeedCase("Q3 library work feed, first page", firstPage, context),
            FeedCase("Q3 library work feed, deep page", deepPage, context),
            ItemsCase("Q4 library items page, no filter", workload.ItemsUnfiltered, context),
            ItemsCase("Q4 library items page, media type filter", workload.ItemsMediaType, context),
            new VerifyCase(
                "Q5 Home display projection",
                $"limit {Workload.HomeLimit}",
                async () => Q5DisplayProjection.Lines(await Q5DisplayProjection.Dapper(context, Workload.HomeLimit)).ToList(),
                [
                    new("EF raw SQL", async () => Q5DisplayProjection.Lines(await Q5DisplayProjection.EfRaw(context, Workload.HomeLimit)).ToList()),
                ],
                [NotPractical.Q5]),
        ];
    }

    private static VerifyCase FeedCase(string name, PagedRequest page, BenchContext context) =>
        new(
            name,
            $"offset {page.Offset}, limit {page.Limit}",
            async () => Q3LibraryWorkFeed.Lines(await Q3LibraryWorkFeed.Dapper(context, page)).ToList(),
            [
                new("EF raw SQL", async () => Q3LibraryWorkFeed.Lines(await Q3LibraryWorkFeed.EfRaw(context, page)).ToList()),
                new("EF LINQ (follow-up loads; paging query raw)", async () => Q3LibraryWorkFeed.Lines(await Q3LibraryWorkFeed.EfLinq(context, page)).ToList()),
            ],
            [NotPractical.Q3Paging]);

    private static VerifyCase ItemsCase(string name, LibraryItemQuery query, BenchContext context) =>
        new(
            name,
            $"offset {query.Offset}, limit {query.Limit}, media type {query.MediaType ?? "(none)"}",
            async () => Q4LibraryItems.Lines(await Q4LibraryItems.Dapper(context, query)).ToList(),
            [
                new("EF raw SQL", async () => Q4LibraryItems.Lines(await Q4LibraryItems.EfRaw(context, query)).ToList()),
            ],
            [NotPractical.Q4]);
}
