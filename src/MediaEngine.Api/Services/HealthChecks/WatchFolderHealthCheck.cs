using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Realtime;
using MediaEngine.Ingestion.Models;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Services.HealthChecks;

public sealed class WatchFolderHealthCheck(IOptions<IngestionOptions> options, IVaultPathClassifier vaultPaths) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // A Vault library's folder is never named in a health answer (Videos spec D17).
        var watchDirs = await IngestionEndpoints
            .NonVaultDirectoriesAsync(options.Value.EffectiveWatchDirectories, vaultPaths, cancellationToken)
            .ConfigureAwait(false);
        if (watchDirs.Count == 0)
        {
            return HealthCheckResult.Degraded(
                "Watch Folder is not configured.",
                data: new Dictionary<string, object> { ["category"] = "storage", ["required"] = false });
        }

        var missing = watchDirs.Where(path => !Directory.Exists(path)).ToList();
        if (missing.Count > 0)
        {
            return HealthCheckResult.Degraded(
                $"Watch Folder does not exist: {string.Join(", ", missing)}",
                data: new Dictionary<string, object>
                {
                    ["category"] = "storage",
                    ["required"] = false,
                    ["missing"] = string.Join(", ", missing),
                });
        }

        try
        {
            foreach (var path in watchDirs)
            {
                _ = Directory.EnumerateFileSystemEntries(path).Take(1).ToList();
            }

            return HealthCheckResult.Healthy(
                $"Watch Folder is readable: {string.Join(", ", watchDirs)}",
                new Dictionary<string, object>
                {
                    ["category"] = "storage",
                    ["required"] = false,
                    ["paths"] = string.Join(", ", watchDirs),
                });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return HealthCheckResult.Degraded(
                "One or more Watch Folders cannot be read.",
                ex,
                new Dictionary<string, object> { ["category"] = "storage", ["required"] = false });
        }
    }
}
