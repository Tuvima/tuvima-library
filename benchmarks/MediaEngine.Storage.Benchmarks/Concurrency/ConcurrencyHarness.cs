using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MediaEngine.Storage.Benchmarks.Seeding;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Benchmarks.Concurrency;

/// <summary>
/// Parent side of the <c>concurrency</c> command. For each scenario (A shared Dapper writer, B writer on the stack under
/// test) and each stack (Dapper, EF) it copies the seeded file, runs the child process <c>concurrency-run</c> against the
/// copy, deletes the copy, and finally prints one table and writes it as CSV. A fresh copy and a fresh process per run
/// keep the four runs independent: no growth from earlier writers, and per-run peak working set and GC counts.
/// </summary>
internal static class ConcurrencyHarness
{
    private const string ResultPrefix = "RESULT:";

    public static async Task<int> RunAsync(string databasePath, int seconds)
    {
        databasePath = Path.GetFullPath(databasePath);
        if (!File.Exists(databasePath))
        {
            throw new InvalidOperationException($"'{databasePath}' does not exist. Run 'seed --db <path>' first.");
        }

        var info = DatasetInfo.Load(databasePath);
        var results = new List<ConcurrencyResult>();
        foreach (var (scenario, stack) in new[] { ("A", "dapper"), ("A", "ef"), ("B", "dapper"), ("B", "ef") })
        {
            Console.WriteLine($"-- scenario {scenario}, stack {stack}: {seconds} s with {ConcurrencyRun.ReaderTasks} readers --");
            var copyPath = databasePath + $".concurrency-{scenario}-{stack}.db";
            DeleteFiles(copyPath);
            try
            {
                File.Copy(databasePath, copyPath);
                CopyIfPresent(databasePath + "-wal", copyPath + "-wal");
                info.Save(copyPath);
                results.Add(await RunChildAsync(copyPath, scenario, stack, seconds));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                DeleteFiles(copyPath);
            }
        }

        Print(results);
        var csvPath = WriteCsv(databasePath, results);
        Console.WriteLine($"CSV written to {csvPath}");

        var failed = results.Where(result => result.OtherErrors > 0).ToList();
        foreach (var result in failed)
        {
            Console.WriteLine($"ERRORS in scenario {result.Scenario} / {result.Stack}:");
            foreach (var sample in result.ErrorSamples)
            {
                Console.WriteLine($"    {sample}");
            }
        }

        return failed.Count == 0 ? 0 : 1;
    }

    /// <summary>Child entry point: runs one scenario/stack against the given file and prints one RESULT line.</summary>
    public static async Task<int> ExecuteChildAsync(string databasePath, string scenario, string stack, int seconds)
    {
        using var context = BenchContext.Open(databasePath);
        var result = await new ConcurrencyRun(context, scenario, stack, TimeSpan.FromSeconds(seconds)).ExecuteAsync();
        Console.WriteLine(ResultPrefix + JsonSerializer.Serialize(result));
        return 0;
    }

    private static async Task<ConcurrencyResult> RunChildAsync(string copyPath, string scenario, string stack, int seconds)
    {
        var start = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the current executable."),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        // Started with 'dotnet run', the host is dotnet(.exe) and the program is the entry assembly's dll.
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("Cannot locate the entry assembly."));
        }

        foreach (var argument in new[] { "concurrency-run", "--db", copyPath, "--scenario", scenario, "--stack", stack, "--seconds", seconds.ToString(CultureInfo.InvariantCulture) })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("The concurrency child process did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var text = await output;
        var errorText = await errors;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"The concurrency child exited with code {process.ExitCode}.{Environment.NewLine}{errorText}{Environment.NewLine}{text}");
        }

        var line = text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith(ResultPrefix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"The concurrency child printed no result.{Environment.NewLine}{errorText}{Environment.NewLine}{text}");
        return JsonSerializer.Deserialize<ConcurrencyResult>(line[ResultPrefix.Length..])
            ?? throw new InvalidOperationException("The concurrency child result could not be read.");
    }

    private static void Print(IReadOnlyList<ConcurrencyResult> results)
    {
        Console.WriteLine();
        Console.WriteLine("Concurrency results (writer: 50 claims + 20 value updates per transaction; readers: Q2 and Q4 on the stack under test)");
        Console.WriteLine(new string('-', 150));
        Console.WriteLine($"{"scenario",-9}{"stack",-8}{"writer tx",10}{"writer BUSY",12}{"reader BUSY",12}{"errors",8}  {"reader operation",-28}{"ops",6}{"p50 ms",10}{"p95 ms",10}{"p99 ms",10}{"peak WS MiB",13}{"GC0/1/2",12}{"alloc MiB",11}{"WAL MiB",9}");
        foreach (var result in results)
        {
            var first = true;
            foreach (var operation in result.ReaderOperations)
            {
                var lead = first
                    ? $"{result.Scenario,-9}{result.Stack,-8}{result.WriterTransactions,10:N0}{result.WriterBusyErrors,12:N0}{result.ReaderBusyErrors,12:N0}{result.OtherErrors,8:N0}"
                    : new string(' ', 59);
                var tail = first
                    ? $"{result.PeakWorkingSetMiB,13:N0}{result.Gen0Collections + "/" + result.Gen1Collections + "/" + result.Gen2Collections,12}{result.AllocatedMiB,11:N0}{result.WalFileMiB,9:N0}"
                    : string.Empty;
                Console.WriteLine($"{lead}  {operation.Operation,-28}{operation.Count,6:N0}{operation.P50Ms,10:N1}{operation.P95Ms,10:N1}{operation.P99Ms,10:N1}{tail}");
                first = false;
            }
        }

        Console.WriteLine(new string('-', 150));
        Console.WriteLine("Scenario A and B are the same run for the Dapper stack (the writer is Dapper either way); comparing them shows run-to-run noise.");
    }

    private static string WriteCsv(string databasePath, IReadOnlyList<ConcurrencyResult> results)
    {
        var directory = Path.Combine(Path.GetDirectoryName(databasePath) ?? ".", "BenchmarkDotNet.Artifacts", "results");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"concurrency-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

        var csv = new StringBuilder();
        csv.AppendLine("scenario,stack,seconds,writer_transactions,writer_busy_errors,reader_busy_errors,other_errors,operation,operations,p50_ms,p95_ms,p99_ms,max_ms,peak_working_set_mib,gen0,gen1,gen2,allocated_mib,wal_mib");
        foreach (var result in results)
        {
            foreach (var operation in result.ReaderOperations)
            {
                csv.AppendLine(string.Join(
                    ',',
                    result.Scenario,
                    result.Stack,
                    Number(result.Seconds),
                    result.WriterTransactions,
                    result.WriterBusyErrors,
                    result.ReaderBusyErrors,
                    result.OtherErrors,
                    $"\"{operation.Operation}\"",
                    operation.Count,
                    Number(operation.P50Ms),
                    Number(operation.P95Ms),
                    Number(operation.P99Ms),
                    Number(operation.MaxMs),
                    Number(result.PeakWorkingSetMiB),
                    result.Gen0Collections,
                    result.Gen1Collections,
                    result.Gen2Collections,
                    Number(result.AllocatedMiB),
                    Number(result.WalFileMiB)));
            }
        }

        File.WriteAllText(path, csv.ToString());
        return path;
    }

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static void CopyIfPresent(string source, string destination)
    {
        if (File.Exists(source))
        {
            File.Copy(source, destination, overwrite: true);
        }
    }

    private static void DeleteFiles(string databasePath)
    {
        foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm", DatasetInfo.SidecarPath(databasePath) })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
