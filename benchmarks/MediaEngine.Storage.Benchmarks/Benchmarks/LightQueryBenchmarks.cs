using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Benchmarks.Queries;

namespace MediaEngine.Storage.Benchmarks.Benchmarks;

/// <summary>
/// Q1 and Q2: the common single-table batched reads. Millisecond-scale, so BenchmarkDotNet's default job applies
/// (automatic pilot, warm-up and iteration counts). The Dapper method of each group is the baseline.
/// </summary>
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class LightQueryBenchmarks
{
    private BenchContext _context = null!;

    [GlobalSetup]
    public void Setup() => _context = BenchContext.Open(
        Environment.GetEnvironmentVariable(BenchConfig.DatabaseVariable)
        ?? throw new InvalidOperationException($"{BenchConfig.DatabaseVariable} is not set; start the run with 'bench --db <path>'."));

    [GlobalCleanup]
    public void Cleanup() => _context.Dispose();

    [Benchmark(Baseline = true), BenchmarkCategory("Q1 canonical values (500 ids)")]
    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalValue>>> Q1_Dapper() =>
        Q1CanonicalValues.Dapper(_context, _context.Workload.Q1Ids);

    [Benchmark, BenchmarkCategory("Q1 canonical values (500 ids)")]
    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalValue>>> Q1_EfRaw() =>
        Q1CanonicalValues.EfRaw(_context, _context.Workload.Q1Ids);

    [Benchmark, BenchmarkCategory("Q1 canonical values (500 ids)")]
    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalValue>>> Q1_EfLinq_PooledContext() =>
        Q1CanonicalValues.EfLinq(_context, _context.Workload.Q1Ids);

    /// <summary>Same LINQ with a new non-pooled context per call: shows what context creation costs.</summary>
    [Benchmark, BenchmarkCategory("Q1 canonical values (500 ids)")]
    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalValue>>> Q1_EfLinq_FreshContext() =>
        Q1CanonicalValues.EfLinqFreshContext(_context, _context.Workload.Q1Ids);

    [Benchmark(Baseline = true), BenchmarkCategory("Q2 current claims (500 ids)")]
    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<MetadataClaim>>> Q2_Dapper() =>
        Q2MetadataClaims.Dapper(_context, _context.Workload.Q2Ids);

    [Benchmark, BenchmarkCategory("Q2 current claims (500 ids)")]
    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<MetadataClaim>>> Q2_EfRaw() =>
        Q2MetadataClaims.EfRaw(_context, _context.Workload.Q2Ids);

    [Benchmark, BenchmarkCategory("Q2 current claims (500 ids)")]
    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<MetadataClaim>>> Q2_EfLinq() =>
        Q2MetadataClaims.EfLinq(_context, _context.Workload.Q2Ids);
}
