using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Paging;
using MediaEngine.Storage.Benchmarks.Queries;

namespace MediaEngine.Storage.Benchmarks.Benchmarks;

/// <summary>
/// Q3: the library work feed (window-function paging query plus three batched follow-up loads). Seconds per call at
/// full scale, so the job is fixed and short: 3 warm-up and 15 measured iterations, one invocation each, no unrolling.
/// Override with BenchmarkDotNet's --warmupCount / --iterationCount if a run needs to be shorter. The Dapper method of
/// each group is the baseline. The EF LINQ variant covers only what LINQ can express (README, "Not practical").
/// </summary>
[WarmupCount(3)]
[IterationCount(15)]
[InvocationCount(1, 1)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class FeedQueryBenchmarks
{
    private BenchContext _context = null!;
    private PagedRequest _firstPage = null!;
    private PagedRequest _deepPage = null!;

    [GlobalSetup]
    public void Setup()
    {
        _context = BenchContext.Open(
            Environment.GetEnvironmentVariable(BenchConfig.DatabaseVariable)
            ?? throw new InvalidOperationException($"{BenchConfig.DatabaseVariable} is not set; start the run with 'bench --db <path>'."));
        _firstPage = new PagedRequest(0, Workload.FeedPageSize);
        _deepPage = new PagedRequest(_context.Workload.FeedDeepOffset, Workload.FeedPageSize);
    }

    [GlobalCleanup]
    public void Cleanup() => _context.Dispose();

    [Benchmark(Baseline = true), BenchmarkCategory("Q3 work feed, first page")]
    public Task<PagedResponse<LibraryWorkListItemDto>> Q3_FirstPage_Dapper() =>
        Q3LibraryWorkFeed.Dapper(_context, _firstPage);

    [Benchmark, BenchmarkCategory("Q3 work feed, first page")]
    public Task<PagedResponse<LibraryWorkListItemDto>> Q3_FirstPage_EfRaw() =>
        Q3LibraryWorkFeed.EfRaw(_context, _firstPage);

    /// <summary>LINQ only for the follow-up loads; the paging query stays raw SQL (window functions).</summary>
    [Benchmark, BenchmarkCategory("Q3 work feed, first page")]
    public Task<PagedResponse<LibraryWorkListItemDto>> Q3_FirstPage_EfLinq() =>
        Q3LibraryWorkFeed.EfLinq(_context, _firstPage);

    [Benchmark(Baseline = true), BenchmarkCategory("Q3 work feed, deep page")]
    public Task<PagedResponse<LibraryWorkListItemDto>> Q3_DeepPage_Dapper() =>
        Q3LibraryWorkFeed.Dapper(_context, _deepPage);

    [Benchmark, BenchmarkCategory("Q3 work feed, deep page")]
    public Task<PagedResponse<LibraryWorkListItemDto>> Q3_DeepPage_EfRaw() =>
        Q3LibraryWorkFeed.EfRaw(_context, _deepPage);

    [Benchmark, BenchmarkCategory("Q3 work feed, deep page")]
    public Task<PagedResponse<LibraryWorkListItemDto>> Q3_DeepPage_EfLinq() =>
        Q3LibraryWorkFeed.EfLinq(_context, _deepPage);
}
