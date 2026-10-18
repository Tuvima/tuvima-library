using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using MediaEngine.Api.Services.Display;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Benchmarks.Queries;

namespace MediaEngine.Storage.Benchmarks.Benchmarks;

/// <summary>
/// Q4 and Q5: the CTE-chain and correlated-subquery projections. Their cost grows faster than the library does (measured:
/// minutes per call at 100,000 works), so this class is meant to run on a smaller seeded file; see README, "Sizing".
/// Fixed short job: 3 warm-up and 15 measured iterations, one invocation each, no unrolling. The Dapper method of each
/// group is the baseline. LINQ variants are not practical for either query (README, "Not practical").
/// </summary>
[WarmupCount(3)]
[IterationCount(15)]
[InvocationCount(1, 1)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class ProjectionQueryBenchmarks
{
    private BenchContext _context = null!;

    [GlobalSetup]
    public void Setup() => _context = BenchContext.Open(
        Environment.GetEnvironmentVariable(BenchConfig.DatabaseVariable)
        ?? throw new InvalidOperationException($"{BenchConfig.DatabaseVariable} is not set; start the run with 'bench --db <path>'."));

    [GlobalCleanup]
    public void Cleanup() => _context.Dispose();

    [Benchmark(Baseline = true), BenchmarkCategory("Q4 items page, no filter")]
    public Task<LibraryItemsPage> Q4_NoFilter_Dapper() =>
        Q4LibraryItems.Dapper(_context, _context.Workload.ItemsUnfiltered);

    [Benchmark, BenchmarkCategory("Q4 items page, no filter")]
    public Task<LibraryItemsPage> Q4_NoFilter_EfRaw() =>
        Q4LibraryItems.EfRaw(_context, _context.Workload.ItemsUnfiltered);

    [Benchmark(Baseline = true), BenchmarkCategory("Q4 items page, media type filter")]
    public Task<LibraryItemsPage> Q4_MediaType_Dapper() =>
        Q4LibraryItems.Dapper(_context, _context.Workload.ItemsMediaType);

    [Benchmark, BenchmarkCategory("Q4 items page, media type filter")]
    public Task<LibraryItemsPage> Q4_MediaType_EfRaw() =>
        Q4LibraryItems.EfRaw(_context, _context.Workload.ItemsMediaType);

    [Benchmark(Baseline = true), BenchmarkCategory("Q5 Home projection (limit 1000)")]
    public Task<IReadOnlyList<DisplayWorkRow>> Q5_Dapper() =>
        Q5DisplayProjection.Dapper(_context, Workload.HomeLimit);

    [Benchmark, BenchmarkCategory("Q5 Home projection (limit 1000)")]
    public Task<IReadOnlyList<DisplayWorkRow>> Q5_EfRaw() =>
        Q5DisplayProjection.EfRaw(_context, Workload.HomeLimit);
}
