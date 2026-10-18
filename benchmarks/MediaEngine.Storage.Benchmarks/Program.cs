using System.Globalization;
using BenchmarkDotNet.Running;
using MediaEngine.Storage.Benchmarks.Benchmarks;
using MediaEngine.Storage.Benchmarks.Concurrency;
using MediaEngine.Storage.Benchmarks.Seeding;
using MediaEngine.Storage.Benchmarks.Verification;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Benchmarks;

internal static class Program
{
    private const string Usage = """
        MediaEngine.Storage.Benchmarks - Dapper (production) vs EF Core on the same SQLite file. Measurement only.

        dotnet run -c Release --project benchmarks/MediaEngine.Storage.Benchmarks -- <command> [options]

          seed         --db <path> [--scale full|projection|small]  build the synthetic library once
                       [--works <n>]                           (size-estimation runs only: exact leaf-work count)
          verify       --db <path> [--only Q1,Q2,Q3]           run every query through every variant and compare results
          probe        --db <path> [--ef] [--repeat <n>]       time each query once (sizing aid, not a measurement)
                       [--only Q1,Q4]                          (only cases whose name contains one of these)
          explain      --db <path> --query q4|q5               print SQLite's query plan for a heavy read (diagnostic)
          bench        --db <path> [BenchmarkDotNet args]      timing and allocation run (for example: --filter "*" or --job dry)
          concurrency  --db <path> [--seconds <n>]             reader/writer contention run on a copy of the file
                       [--writer-tps <n>]                      writer rate in transactions/s (default 50; 0 = unthrottled)
          clean        --db <path>                             delete the database, -wal, -shm and dataset description
        """;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        var command = args[0];
        var rest = args[1..];
        try
        {
            switch (command)
            {
                case "seed":
                    DataSeeder.Seed(
                        Options.Required(rest, "--db"),
                        Options.Optional(rest, "--scale") ?? "full",
                        int.TryParse(Options.Optional(rest, "--works"), out var customWorks) ? customWorks : null);
                    return 0;
                case "verify":
                    using (var context = BenchContext.Open(Options.Required(rest, "--db")))
                    {
                        var only = (Options.Optional(rest, "--only") ?? string.Empty)
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        var cases = VerificationSuite.Build(context)
                            .Where(verifyCase => only.Length == 0 || only.Any(token => verifyCase.Query.StartsWith(token, StringComparison.OrdinalIgnoreCase)))
                            .ToList();
                        return await Verifier.RunAsync(cases) ? 0 : 1;
                    }

                case "probe":
                    using (var context = BenchContext.Open(Options.Required(rest, "--db")))
                    {
                        await Probe.RunAsync(
                            context,
                            includeEf: rest.Contains("--ef"),
                            repeat: int.TryParse(Options.Optional(rest, "--repeat"), out var repeat) ? repeat : 1,
                            only: (Options.Optional(rest, "--only") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                        return 0;
                    }

                case "explain":
                    using (var context = BenchContext.Open(Options.Required(rest, "--db")))
                    {
                        Explain.Run(context, Options.Required(rest, "--query"));
                        return 0;
                    }

                case "bench":
                    return RunBenchmarks(rest);
                case "concurrency":
                    return await ConcurrencyHarness.RunAsync(
                        Options.Required(rest, "--db"),
                        int.TryParse(Options.Optional(rest, "--seconds"), out var seconds) ? seconds : 60,
                        Options.WriterTps(rest));
                case "concurrency-run":
                    // Internal: one scenario/stack in its own process. Started by 'concurrency'.
                    return await ConcurrencyHarness.ExecuteChildAsync(
                        Options.Required(rest, "--db"),
                        Options.Required(rest, "--scenario"),
                        Options.Required(rest, "--stack"),
                        int.Parse(Options.Required(rest, "--seconds"), CultureInfo.InvariantCulture),
                        Options.WriterTps(rest));
                case "clean":
                    Clean(Options.Required(rest, "--db"));
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown command '{command}'.{Environment.NewLine}{Usage}");
                    return 1;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static int RunBenchmarks(string[] rest)
    {
        var databasePath = Path.GetFullPath(Options.Required(rest, "--db"));
        if (!File.Exists(databasePath))
        {
            throw new InvalidOperationException($"'{databasePath}' does not exist. Run 'seed --db <path>' first.");
        }

        // BenchmarkDotNet's child processes find the seeded file through this variable; GlobalSetup never reseeds.
        Environment.SetEnvironmentVariable(BenchConfig.DatabaseVariable, databasePath);

        var passThrough = Options.Without(rest, "--db");
        if (!passThrough.Any(argument => argument is "--filter" or "-f" or "--list" or "--anyCategories" or "--allCategories"))
        {
            // Without a selection BenchmarkDotNet would ask interactively.
            passThrough = [.. passThrough, "--filter", "*"];
        }

        var artifacts = Path.Combine(Path.GetDirectoryName(databasePath) ?? ".", "BenchmarkDotNet.Artifacts");
        BenchmarkSwitcher
            .FromTypes([typeof(LightQueryBenchmarks), typeof(FeedQueryBenchmarks), typeof(ProjectionQueryBenchmarks)])
            .Run(passThrough, BenchConfig.Create(artifacts));
        return 0;
    }

    private static void Clean(string databasePath)
    {
        databasePath = Path.GetFullPath(databasePath);
        SqliteConnection.ClearAllPools();
        foreach (var path in new[]
        {
            databasePath,
            databasePath + "-wal",
            databasePath + "-shm",
            DatasetInfo.SidecarPath(databasePath),
        })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                Console.WriteLine($"deleted {path}");
            }
        }
    }
}
