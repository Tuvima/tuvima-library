using Dapper;
using MediaEngine.Storage;

namespace MediaEngine.Api.DevSupport;

/// <summary>
/// Offline command <c>--open-baseline-store &lt;run folder | run id | label&gt;</c>: makes a copy of a finished baseline run that
/// the Dashboard can open for a visual check. A run marks first-time setup as finished but never creates the administrator's
/// sign-in, so the Dashboard would bounce between the setup page and the sign-in page. The copy puts setup back to its first
/// step (the run's own data store and key-free config are untouched); the setup wizard then makes the administrator on the
/// Owner profile that is already there, and every folder linked to it stays linked.
/// </summary>
public static class BaselineOpenStore
{
    public const string CopyFolder = "dashboard-copy";

    public static string ResolveRun(string value, string outRoot)
    {
        bool IsRun(string folder) => File.Exists(Path.Combine(folder, BaselinePrepare.RunMarkerFile));
        if (Directory.Exists(value) && IsRun(Path.GetFullPath(value)))
        {
            return Path.GetFullPath(value);
        }

        var found = Directory.Exists(outRoot)
            ? Directory.GetDirectories(outRoot)
                .Where(folder => IsRun(folder) && Path.GetFileName(folder) is var name && (name == value || name.EndsWith("-" + value, StringComparison.Ordinal)))
                .OrderDescending(StringComparer.Ordinal)
                .FirstOrDefault()
            : null;
        return found ?? throw new DirectoryNotFoundException($"No baseline run found for '{value}' (a run folder, run id or label under {outRoot}).");
    }

    public static Task<int> RunAsync(string[] arguments, string repoDefault)
    {
        if (arguments.Length < 1)
        {
            throw new ArgumentException("Give the run folder, run id or label.");
        }

        var repoIndex = Array.IndexOf(arguments, "--repo");
        var repo = repoIndex >= 0 && repoIndex + 1 < arguments.Length ? Path.GetFullPath(arguments[repoIndex + 1]) : repoDefault;
        var outRoot = Path.Combine(repo, ".tmp", "ingestion-baseline");
        var run = ResolveRun(arguments[0], outRoot);
        var target = MakeCopy(run);
        Console.WriteLine($"Dashboard copy ready: {target}");
        Console.WriteLine("Start the Engine and Dashboard against it (ports other than 61495/5016 if another Engine runs):");
        Console.WriteLine($"  TUVIMA_CONFIG_DIR={Path.Combine(target, "config")}");
        Console.WriteLine($"  TUVIMA_DATA_DIR={Path.Combine(target, "data")}");
        Console.WriteLine("Then open the Dashboard: it starts first-time setup; choose a name and finish it. The copy holds no provider keys, so nothing is looked up.");
        return Task.FromResult(0);
    }

    /// <summary>Copies the run's config and data into <c>&lt;run&gt;/dashboard-copy</c> and puts setup back to its first step. Returns the copy's folder.</summary>
    public static string MakeCopy(string run)
    {
        var target = Path.Combine(run, CopyFolder);
        if (Directory.Exists(target))
        {
            throw new InvalidOperationException($"{target} already exists; delete it first or open that copy as it is.");
        }

        var configDirectory = Path.Combine(run, "config");
        var dataDirectory = Path.Combine(run, "data");
        if (!Directory.Exists(configDirectory) || !Directory.Exists(dataDirectory))
        {
            throw new DirectoryNotFoundException($"'{run}' has no config and data folders.");
        }

        var newConfig = Path.Combine(target, "config");
        var newData = Path.Combine(target, "data");
        Copy(configDirectory, newConfig, name => name is "secrets" or ".secrets" or "backups");
        Copy(dataDirectory, newData, _ => false);

        // The copied files name the run's own data folder; point them at the copy.
        foreach (var file in new[] { "core.json", "libraries.json" })
        {
            var path = Path.Combine(newConfig, file);
            if (File.Exists(path))
            {
                File.WriteAllText(path, File.ReadAllText(path)
                    .Replace(dataDirectory.Replace("\\", "\\\\"), newData.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase)
                    .Replace(dataDirectory, newData, StringComparison.OrdinalIgnoreCase));
            }
        }

        var previous = Environment.GetEnvironmentVariable("TUVIMA_DATA_DIR");
        Environment.SetEnvironmentVariable("TUVIMA_DATA_DIR", newData);
        try
        {
            var databasePath = Path.GetFullPath(TuvimaDataPathResolver.ResolveDatabasePath(newConfig, null, null));
            using var database = new DatabaseConnection(databasePath, new DataStoreStartupOptions { BackupDirectory = Path.Combine(newConfig, "backups") });
            ResetSetup(database);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TUVIMA_DATA_DIR", previous);
        }

        return target;
    }

    /// <summary>Puts first-time setup back to its first step, with no administrator and no setup sign-ins.</summary>
    public static void ResetSetup(DatabaseConnection database)
    {
        using var connection = database.CreateConnection();
        var now = DateTimeOffset.UtcNow.ToString("O");
        connection.Execute("""
            DELETE FROM onboarding_sessions WHERE workflow_version = 1;
            UPDATE onboarding_steps SET status = 'not_started', detail = NULL, repair_target = NULL,
                completed_at = NULL, updated_at = @now WHERE workflow_version = 1;
            UPDATE onboarding_workflows SET state = 'in_progress', current_step = 'welcome',
                administrator_profile_id = NULL, completed_at = NULL, updated_at = @now,
                revision = revision + 1 WHERE workflow_version = 1;
            """, new { now });
    }

    private static void Copy(string source, string target, Func<string, bool> skip)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.GetDirectories(source).Where(d => !skip(Path.GetFileName(d))))
        {
            Copy(directory, Path.Combine(target, Path.GetFileName(directory)), skip);
        }

        foreach (var file in Directory.GetFiles(source).Where(f => !skip(Path.GetFileName(f))))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
    }
}
