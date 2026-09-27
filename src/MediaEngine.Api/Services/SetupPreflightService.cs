using System.Runtime.InteropServices;
using Dapper;
using MediaEngine.Contracts.Setup;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services;

public sealed class SetupPreflightService(
    IConfigurationLoader configuration,
    IDatabaseConnection database,
    IFFmpegService ffmpeg)
{
    public async Task<SetupPreflightDto> RunAsync(CancellationToken ct)
    {
        var core = configuration.LoadCore();
        var configPath = Path.GetFullPath(configuration.ConfigDirectoryPath);
        var managedDataPath = new AssetPathService(
            core.LibraryRoot,
            core.StoragePolicy,
            core.DataRoot).DataRoot;
        var checks = new List<SetupPathCheckDto>
        {
            ProbeFile("database", "Database", ResolveDatabasePath(configPath), "TUVIMA_DB_PATH or platform default"),
            Probe("config", "Configuration", configPath, "TUVIMA_CONFIG_DIR or application configuration", requireWrite: true),
            Probe("artwork", "Artwork and generated data", managedDataPath, "core.json data_root or library_root/.data", requireWrite: true),
            Probe("backups", "Backups", Environment.GetEnvironmentVariable("TUVIMA_BACKUP_DIR") ?? Path.Combine(configPath, "backups"), "TUVIMA_BACKUP_DIR or config/backups", requireWrite: true),
        };
        checks.AddRange(configuration.LoadLibraries().StorageLocations.Select(location =>
            Probe($"storage-{location.Id}", location.Label, location.Path,
                "Editable setup storage location", requireWrite: location.AllowWrite)));

        var databaseHealthy = true;
        try
        {
            using var connection = database.CreateConnection();
            databaseHealthy = string.Equals(connection.ExecuteScalar<string>("PRAGMA quick_check;"), "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            databaseHealthy = false;
        }
        if (!databaseHealthy)
        {
            var current = checks[0];
            checks[0] = current with { Status = "blocked", Detail = "SQLite quick_check did not pass." };
        }

        var ffmpegStatus = ffmpeg.IsAvailable ? "available" : "unavailable";
        if (ffmpeg.IsAvailable)
        {
            try
            {
                var result = await ffmpeg.RunAsync("-version", ct).ConfigureAwait(false);
                ffmpegStatus = result.ExitCode == 0
                    ? result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "available"
                    : "probe-failed";
            }
            catch when (!ct.IsCancellationRequested) { ffmpegStatus = "probe-failed"; }
        }

        var inContainer = string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TUVIMA_CONTAINER_NETWORK_MODE"));
        return new SetupPreflightDto(
            checks.All(check => check.Status == "passed"),
            inContainer,
            inContainer
                ? "These are paths inside the Tuvima container. Tuvima can use only host folders already mounted into the container."
                : "These paths are resolved on the computer running Tuvima Library.",
            RuntimeInformation.RuntimeIdentifier,
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            ffmpegStatus,
            checks);
    }

    private static SetupPathCheckDto Probe(string key, string label, string rawPath, string source, bool requireWrite)
    {
        try
        {
            var path = Path.GetFullPath(rawPath);
            if (requireWrite) Directory.CreateDirectory(path);
            // A read-only source must never be created or tested with a temporary write.
            using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            _ = entries.MoveNext();
            var readable = true;
            var writable = false;
            var probe = Path.Combine(path, $".tuvima-setup-{Guid.NewGuid():N}.tmp");
            try
            {
                if (requireWrite)
                {
                    File.WriteAllText(probe, "setup-path-probe");
                    writable = true;
                }
            }
            finally
            {
                if (requireWrite && File.Exists(probe))
                {
                    File.Delete(probe);
                }
            }
            var free = GetFreeSpace(path);
            var passed = readable && (!requireWrite || writable);
            return new SetupPathCheckDto(key, label, path, source, passed ? "passed" : "blocked", readable, writable, free,
                passed ? "Path is available." : "The path does not provide the required access.");
        }
        catch (Exception ex)
        {
            var path = string.IsNullOrWhiteSpace(rawPath) ? "Not configured" : rawPath;
            return new SetupPathCheckDto(key, label, path, source, "blocked", false, false, null,
                $"{ex.GetType().Name}: the path could not be prepared.");
        }
    }

    private static SetupPathCheckDto ProbeFile(string key, string label, string rawPath, string source)
    {
        var path = Path.GetFullPath(rawPath);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The database path has no parent directory.");
        try
        {
            Directory.CreateDirectory(directory);
            var readable = File.Exists(path);
            var writable = false;
            var probe = Path.Combine(directory, $".tuvima-setup-{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(probe, "setup-database-probe");
                writable = true;
            }
            finally
            {
                if (File.Exists(probe))
                {
                    File.Delete(probe);
                }
            }

            var passed = readable && writable;
            return new SetupPathCheckDto(
                key, label, path, source, passed ? "passed" : "blocked", readable, writable,
                GetFreeSpace(directory),
                passed ? "SQLite is readable and its volume accepts durable writes." : "The SQLite file or its parent volume is unavailable.");
        }
        catch (Exception ex)
        {
            return new SetupPathCheckDto(
                key, label, path, source, "blocked", false, false, null,
                $"{ex.GetType().Name}: the database path could not be checked.");
        }
    }

    private static long? GetFreeSpace(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string ResolveDatabasePath(string configPath) =>
        Path.GetFullPath(TuvimaDataPathResolver.ResolveDatabasePath(
            configPath,
            Environment.GetEnvironmentVariable("TUVIMA_DB_PATH"),
            Environment.GetEnvironmentVariable("TUVIMA_LIBRARY_ROOT")));
}
