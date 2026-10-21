using System.Diagnostics;
using System.Text;
using Dapper;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.HealthChecks;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit.Abstractions;

namespace MediaEngine.Performance.Tests;

/// <summary>
/// Bug B17 (local check 2026-10-21): the library summary tiles in Settings took a while to appear. The tiles waited for two
/// Engine reads together: the library overview counts and the full readiness report. The full report runs the data store
/// integrity scan (quick_check and foreign_key_check read every page) and a write probe that queues behind ingestion's writes,
/// although the summary only shows the folder checks. This measures the overview, the full report (what the tiles waited for)
/// and the storage-only report (what the summary asks for now), idle and while ingestion holds the write lock. It is a
/// measurement: the numbers go to the test output and <c>.tmp/b17-library-summary-measure.md</c>, with no budget asserted.
/// It shares its class (and so its generated libraries) with the overview guardrails.
/// </summary>
public sealed partial class LibraryOverviewGuardrailTests
{
    private const int Runs = 5;
    private static readonly TimeSpan IngestionWriteHold = TimeSpan.FromSeconds(2);

    [MeasurementFact]
    [Trait("Category", "Measurement")]
    public async Task LibrarySummary_ReadsMeasuredAtTwoLibrarySizes()
    {
        var table = new StringBuilder()
            .AppendLine("| Library | Data store | Read | SQLite work (thousand instructions) | Median ms idle | ms while ingestion holds the write lock 2 s |")
            .AppendLine("|---|---|---|---|---|---|");

        foreach (var (label, database) in new[] { ($"{LibraryListFixture.SmallLeafWorks:N0} works", _fixture.Small), ($"{LibraryListFixture.LargeLeafWorks:N0} works", _fixture.Large) })
        {
            var size = DataStoreMegabytes(database);
            var counter = new SqliteWorkCounter(database);

            var overview = await MeasureAsync(counter, () => ReadOverviewAsync(counter));
            table.AppendLine($"| {label} | {size:F1} MB | Library overview counts | {overview.Work:N0} | {overview.Ms:F1} | {await WhileIngestionWritesAsync(database, () => ReadOverviewAsync(counter)):F0} |");
            foreach (var (part, read) in OverviewParts(counter))
            {
                var measured = await MeasureAsync(counter, read);
                table.AppendLine($"| {label} | {size:F1} MB | &nbsp;&nbsp;of which {part} | {measured.Work:N0} | {measured.Ms:F1} | |");
            }


            var full = await MeasureAsync(counter, () => ReadinessAsync(counter, category: null));
            table.AppendLine($"| {label} | {size:F1} MB | Full readiness (before) | {full.Work:N0} | {full.Ms:F1} | {await WhileIngestionWritesAsync(database, () => ReadinessAsync(counter, category: null)):F0} |");

            var storage = await MeasureAsync(counter, () => ReadinessAsync(counter, category: "storage"));
            table.AppendLine($"| {label} | {size:F1} MB | Storage readiness (after) | {storage.Work:N0} | {storage.Ms:F1} | {await WhileIngestionWritesAsync(database, () => ReadinessAsync(counter, category: "storage")):F0} |");
        }

        var report = table.ToString();
        _output.WriteLine(report);
        var directory = Path.Combine(RepositoryRoot(), ".tmp");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "b17-library-summary-measure.md"), report);
        Assert.NotEmpty(report);
    }

    private static async Task ReadOverviewAsync(IDatabaseConnection database)
    {
        // The same reads GET /library/overview makes.
        var items = new LibraryItemRepository(database);
        var overview = new LibraryOverviewReadService(database);
        await items.GetOverviewCountsAsync(CancellationToken.None);
        await Task.WhenAll(
            items.GetOwnedMediaTypeCountsAsync(CancellationToken.None),
            overview.GetOverviewAggregatesAsync(CancellationToken.None));
    }

    private static IEnumerable<(string Part, Func<Task> Read)> OverviewParts(IDatabaseConnection database)
    {
        var items = new LibraryItemRepository(database);
        var overview = new LibraryOverviewReadService(database);
        yield return ("lifecycle counts and projection summary (one pass)", () => items.GetOverviewCountsAsync(CancellationToken.None));
        yield return ("owned media type counts", () => items.GetOwnedMediaTypeCountsAsync(CancellationToken.None));
        yield return ("added and pipeline aggregates", () => overview.GetOverviewAggregatesAsync(CancellationToken.None));
    }

    private static async Task ReadinessAsync(IDatabaseConnection database, string? category)
    {
        // The real data store check plus stand-ins for the other checks: the storage ones look only at folders, and the rest
        // (configuration, models, providers, runtimes, workers) are left out, so the full report here is a lower bound.
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(database)
            .AddHealthChecks()
            .AddCheck<SqliteHealthCheck>("database_integrity", tags: ["readiness", "database", "required"])
            .AddCheck("library_storage", () => HealthCheckResult.Healthy("Folder is writable."), tags: ["readiness", "storage", "required"])
            .AddCheck("watch_folders", () => HealthCheckResult.Healthy("Folders are readable."), tags: ["readiness", "storage"])
            .Services
            .BuildServiceProvider();
        var readiness = new StartupReadinessService(services.GetRequiredService<HealthCheckService>());
        var response = await readiness.GetAsync(category, CancellationToken.None);
        Assert.NotEmpty(response.Checks);
    }

    private static async Task<(long Work, double Ms)> MeasureAsync(SqliteWorkCounter counter, Func<Task> read)
    {
        await read();
        var times = new List<double>();
        long work = 0;
        for (var run = 0; run < Runs; run++)
        {
            counter.Reset();
            var watch = Stopwatch.StartNew();
            await read();
            times.Add(watch.Elapsed.TotalMilliseconds);
            work = counter.KiloInstructions;
        }

        times.Sort();
        return (work, times[Runs / 2]);
    }

    // Ingestion writes in short transactions under the Engine's one write lock; a read that needs that lock waits its turn.
    private static async Task<double> WhileIngestionWritesAsync(IDatabaseConnection database, Func<Task> read)
    {
        await database.AcquireWriteLockAsync(CancellationToken.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(IngestionWriteHold);
            database.ReleaseWriteLock();
        });
        var watch = Stopwatch.StartNew();
        await read();
        var elapsed = watch.Elapsed.TotalMilliseconds;
        await release;
        return elapsed;
    }

    private static double DataStoreMegabytes(IDatabaseConnection database)
    {
        using var connection = database.CreateConnection();
        var pages = connection.ExecuteScalar<long>("PRAGMA page_count;");
        var pageSize = connection.ExecuteScalar<long>("PRAGMA page_size;");
        return pages * pageSize / 1024d / 1024d;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
