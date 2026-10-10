// Linked into every *.Tests project by tests/Directory.Build.props — edit once, applies everywhere.
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace MediaEngine.TestSupport;

/// <summary>
/// Gives each test process a private temp folder (<c>%TEMP%\tuvima-tests\&lt;pid&gt;-&lt;time&gt;</c>) and removes it
/// when the process exits. Every <see cref="Path.GetTempPath"/> call in the tests lands there, so a fixture
/// that forgets (or fails) to clean up cannot leak into the real temp folder. Folders left by killed runs are
/// swept at the next start. Also offers cleanup helpers that release SQLite file handles first.
/// </summary>
internal static class TestTemp
{
    private const string RootName = "tuvima-tests";
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

    /// <summary>This process's private temp folder.</summary>
    internal static string RunRoot { get; private set; } = string.Empty;

#pragma warning disable CA2255 // A module initializer is the only way to run before any fixture touches the temp path.
    [ModuleInitializer]
    internal static void Initialize()
#pragma warning restore CA2255
    {
        try
        {
            var baseDir = Path.Combine(Path.GetTempPath(), RootName);
            Directory.CreateDirectory(baseDir);
            SweepStaleRuns(baseDir, DateTime.UtcNow, IsProcessRunning);

            RunRoot = Path.Combine(baseDir, $"{Environment.ProcessId}-{DateTime.UtcNow:yyyyMMddHHmmss}");
            Directory.CreateDirectory(RunRoot);
            Environment.SetEnvironmentVariable("TMP", RunRoot);
            Environment.SetEnvironmentVariable("TEMP", RunRoot);
            Environment.SetEnvironmentVariable("TMPDIR", RunRoot);
            AppDomain.CurrentDomain.ProcessExit += static (_, _) => CleanupRun();
            System.Runtime.Loader.AssemblyLoadContext.Default.Unloading += static _ => CleanupRun();
            // A start line with no matching "cleaned"/"LEFT" line means the host was killed before exit hooks ran.
            AppendLog($"{DateTime.UtcNow:O} pid {Environment.ProcessId} started {RunRoot}");
        }
        catch (Exception exception)
        {
            // Never fail test discovery over housekeeping; say so, and tests fall back to the system temp folder.
            Console.Error.WriteLine($"[TestTemp] Could not set up the private temp folder: {exception.Message}");
        }
    }

    /// <summary>Deletes a SQLite database with its -wal/-shm/-journal files and legacy backups.</summary>
    internal static void DeleteDatabase(string path)
    {
        ReleaseSqlitePools();
        var directory = Path.GetDirectoryName(path);
        var targets = new List<string> { path, path + "-wal", path + "-shm", path + "-journal" };
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            targets.AddRange(Directory.GetFiles(directory, Path.GetFileName(path) + ".legacy-text-guid.*.bak"));
        }

        foreach (var target in targets)
        {
            DeleteWithRetry(() => File.Delete(target), target);
        }
    }

    /// <summary>Deletes a directory tree, releasing SQLite file handles first.</summary>
    internal static void DeleteDirectory(string path)
    {
        ReleaseSqlitePools();
        DeleteWithRetry(() =>
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }, path);

        // A recursive delete stops at the first locked file; remove everything that is not locked so only
        // the genuinely held files remain for the next run's sweep.
        if (Directory.Exists(path))
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Still held open by a live handle; reported through cleanup.log and removed by the next sweep.
                }
            }
        }
    }

    internal static void SweepStaleRuns(string baseDir, DateTime utcNow, Func<int, bool> isRunning)
    {
        if (!Directory.Exists(baseDir))
        {
            return;
        }

        foreach (var directory in Directory.GetDirectories(baseDir))
        {
            var name = Path.GetFileName(directory);
            var dash = name.IndexOf('-');
            var ownerKnown = dash > 0 && int.TryParse(name.AsSpan(0, dash), out _);
            var owner = ownerKnown ? int.Parse(name.AsSpan(0, dash)) : -1;
            var age = utcNow - Directory.GetCreationTimeUtc(directory);
            if ((ownerKnown && isRunning(owner)) && age < StaleAfter)
            {
                continue; // a parallel test process is still using it
            }

            DeleteWithRetry(() => Directory.Delete(directory, recursive: true), directory);
        }
    }

    private static int _cleanedUp;

    private static void CleanupRun()
    {
        if (RunRoot.Length == 0 || Interlocked.Exchange(ref _cleanedUp, 1) == 1)
        {
            return;
        }

        var outcome = "cleanup threw before finishing";
        try
        {
            DeleteDirectory(RunRoot);

            // The test host's console is often gone at exit, so leave a one-line trace beside the run folders.
            // A folder that survives here is deleted by the next test process's sweep once its file handles are gone.
            var remaining = Directory.Exists(RunRoot)
                ? Directory.EnumerateFiles(RunRoot, "*", SearchOption.AllDirectories).Take(3).ToList()
                : new List<string>();
            outcome = remaining.Count == 0
                ? "cleaned"
                : $"LEFT (e.g. {string.Join("; ", remaining)})";
        }
        catch (Exception exception)
        {
            // Exit hooks must never throw; the outcome is recorded in cleanup.log instead.
            outcome = $"LEFT (cleanup failed: {exception.Message})";
        }
        finally
        {
            AppendLog($"{DateTime.UtcNow:O} pid {Environment.ProcessId} {outcome} {RunRoot}");
        }
    }

    private static void AppendLog(string line)
    {
        try
        {
            var log = Path.Combine(Path.GetDirectoryName(RunRoot)!, "cleanup.log");
            if (File.Exists(log) && new FileInfo(log).Length > 100_000)
            {
                File.Delete(log);
            }

            File.AppendAllText(log, line + Environment.NewLine);
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"[TestTemp] Could not write cleanup.log: {exception.Message}");
        }
    }

    private static bool IsProcessRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // no such process
        }
    }

    // Test projects do not all reference Microsoft.Data.Sqlite, so reach it by reflection when it is loaded.
    private static void ReleaseSqlitePools()
    {
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("Microsoft.Data.Sqlite.SqliteConnection", throwOnError: false))
            .FirstOrDefault(candidate => candidate is not null);
        type?.GetMethod("ClearAllPools", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes)?.Invoke(null, null);
    }

    private static void DeleteWithRetry(Action delete, string path)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                delete();
                return;
            }
            catch (FileNotFoundException)
            {
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                last = exception;
                Thread.Sleep(100);
            }
        }

        // Report instead of swallowing: a leftover file is a leak someone should see.
        Console.Error.WriteLine($"[TestTemp] Could not delete '{path}': {last?.Message}");
    }
}
