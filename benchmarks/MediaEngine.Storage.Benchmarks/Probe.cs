using System.Diagnostics;
using MediaEngine.Contracts.Paging;
using MediaEngine.Storage.Benchmarks.Queries;

namespace MediaEngine.Storage.Benchmarks;

/// <summary>
/// Times each query once per variant (default: the production Dapper path only) so the cost of a full run can be
/// estimated before committing to it. Not a measurement: no warm-up, no statistics.
/// </summary>
internal static class Probe
{
    public static async Task RunAsync(BenchContext context, bool includeEf, int repeat, string[] only)
    {
        var workload = context.Workload;
        var firstPage = new PagedRequest(0, Workload.FeedPageSize);
        var deepPage = new PagedRequest(workload.FeedDeepOffset, Workload.FeedPageSize);

        var cases = new List<(string Name, Func<Task> Run)>
        {
            ("Q1 Dapper", () => Q1CanonicalValues.Dapper(context, workload.Q1Ids)),
            ("Q2 Dapper", () => Q2MetadataClaims.Dapper(context, workload.Q2Ids)),
            ("Q3 Dapper first page", () => Q3LibraryWorkFeed.Dapper(context, firstPage)),
            ("Q3 Dapper deep page", () => Q3LibraryWorkFeed.Dapper(context, deepPage)),
            ("Q4 Dapper no filter", () => Q4LibraryItems.Dapper(context, workload.ItemsUnfiltered)),
            ("Q4 Dapper media type", () => Q4LibraryItems.Dapper(context, workload.ItemsMediaType)),
            ("Q5 Dapper home", () => Q5DisplayProjection.Dapper(context, Workload.HomeLimit)),
        };

        if (includeEf)
        {
            cases.AddRange(
            [
                ("Q1 EF raw", () => Q1CanonicalValues.EfRaw(context, workload.Q1Ids)),
                ("Q1 EF LINQ", () => Q1CanonicalValues.EfLinq(context, workload.Q1Ids)),
                ("Q2 EF raw", () => Q2MetadataClaims.EfRaw(context, workload.Q2Ids)),
                ("Q2 EF LINQ", () => Q2MetadataClaims.EfLinq(context, workload.Q2Ids)),
                ("Q3 EF raw first page", () => Q3LibraryWorkFeed.EfRaw(context, firstPage)),
                ("Q3 EF raw deep page", () => Q3LibraryWorkFeed.EfRaw(context, deepPage)),
                ("Q4 EF raw no filter", () => Q4LibraryItems.EfRaw(context, workload.ItemsUnfiltered)),
                ("Q4 EF raw media type", () => Q4LibraryItems.EfRaw(context, workload.ItemsMediaType)),
                ("Q5 EF raw home", () => Q5DisplayProjection.EfRaw(context, Workload.HomeLimit)),
            ]);
        }

        foreach (var (name, run) in cases)
        {
            if (only.Length > 0 && !only.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            for (var i = 0; i < repeat; i++)
            {
                var timer = Stopwatch.StartNew();
                await run();
                Console.WriteLine($"{name,-26} run {i + 1}: {timer.Elapsed.TotalMilliseconds,10:N1} ms");
            }
        }
    }
}
