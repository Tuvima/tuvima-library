using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Validators;

namespace MediaEngine.Storage.Benchmarks.Benchmarks;

/// <summary>
/// BenchmarkDotNet settings shared by both benchmark classes: Release only, memory diagnoser, and the columns the
/// report needs (Median, P95, Mean, StdDev, Allocated, Gen0/1/2, ratio to the Dapper baseline). Results are exported
/// as markdown and CSV into an artifacts folder beside the database file, never into the repository.
/// </summary>
internal static class BenchConfig
{
    /// <summary>The seeded file path travels to BenchmarkDotNet's child processes through this variable.</summary>
    public const string DatabaseVariable = "TUVIMA_BENCH_DB";

    public static IConfig Create(string artifactsPath) =>
        ManualConfig.CreateEmpty()
            .WithArtifactsPath(artifactsPath)
            .AddLogger(ConsoleLogger.Default)
            .AddColumn(
                CategoriesColumn.Default,
                TargetMethodColumn.Method,
                StatisticColumn.Median,
                StatisticColumn.P95,
                StatisticColumn.Mean,
                StatisticColumn.StdDev,
                BaselineRatioColumn.RatioMean)
            .AddColumnProvider(DefaultColumnProviders.Metrics)
            .AddDiagnoser(MemoryDiagnoser.Default)
            .AddExporter(MarkdownExporter.GitHub, CsvExporter.Default)
            .AddValidator(JitOptimizationsValidator.FailOnError, BaselineValidator.FailOnError)
            .WithOrderer(new DefaultOrderer(SummaryOrderPolicy.Declared, MethodOrderPolicy.Declared));
}
