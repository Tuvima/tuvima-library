namespace MediaEngine.Api.Services.EngineLogs;

/// <summary>What one log-file prune removed.</summary>
public sealed record LogFilePruneResult(int DeletedForAge, int DeletedForSize, int Skipped);

/// <summary>
/// Applies the Engine log retention rule to a folder: files older than the day limit go first, then the oldest
/// remaining files until the total size is under the size limit. The newest file is the one being written, so it is
/// never removed. Serilog's own file-count retention is turned off so this is the only rule.
/// </summary>
public static class LogFilePruner
{
    public static LogFilePruneResult Prune(
        string directory,
        int retentionDays,
        long maxTotalBytes,
        DateTimeOffset now,
        ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Directory.Exists(directory))
        {
            return new LogFilePruneResult(0, 0, 0);
        }

        // Oldest first. Last write time orders them the same way their dates do, and also covers the _001 style names.
        var files = new DirectoryInfo(directory)
            .EnumerateFiles(EngineLogLocation.FilePattern, SearchOption.TopDirectoryOnly)
            .OrderBy(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.Name, StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
        {
            return new LogFilePruneResult(0, 0, 0);
        }

        var active = files[^1];
        var cutoff = now.UtcDateTime.AddDays(-retentionDays);
        var (forAge, forSize, skipped) = (0, 0, 0);

        foreach (var file in files.Where(file => file != active && file.LastWriteTimeUtc < cutoff).ToList())
        {
            if (TryDelete(file, logger))
            {
                forAge++;
                files.Remove(file);
            }
            else
            {
                skipped++;
            }
        }

        var total = files.Sum(file => file.Length);
        foreach (var file in files.Where(file => file != active).ToList())
        {
            if (total <= maxTotalBytes)
            {
                break;
            }

            var length = file.Length;
            if (TryDelete(file, logger))
            {
                forSize++;
                total -= length;
            }
            else
            {
                skipped++;
            }
        }

        return new LogFilePruneResult(forAge, forSize, skipped);
    }

    private static bool TryDelete(FileInfo file, ILogger? logger)
    {
        try
        {
            file.Delete();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file another process still holds open is left for the next nightly run.
            logger?.LogWarning(ex, "Log file {FileName} could not be removed and will be retried tomorrow", file.Name);
            return false;
        }
    }
}
