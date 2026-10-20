using System.Globalization;
using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Playback;

namespace MediaEngine.Api.Services.Playback;

public sealed class AdaptiveHlsCleanupService(
    AdaptiveHlsPackageRepository packages,
    PlaybackStateRepository playbackState,
    AdaptiveHlsService hls,
    IConfigurationLoader configuration,
    IRecurringTaskRecorder recorder,
    ILogger<AdaptiveHlsCleanupService> logger) : BackgroundService
{
    /// <summary>Minutes between passes when the transcoding settings cannot be read.</summary>
    private const int FallbackIntervalMinutes = 60;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await RunCleanupPassAsync(stoppingToken).ConfigureAwait(false))
            {
                break;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(ReadIntervalMinutes()), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Runs one cleanup pass. A failure is logged as a warning and the next pass tries again; it never stops the
    /// Engine. Returns <see langword="false"/> only when the Engine is shutting down.
    /// </summary>
    internal async Task<bool> RunCleanupPassAsync(CancellationToken stoppingToken)
    {
        try
        {
            await recorder.RecordAsync(
                ScheduledTaskKeys.AdaptiveStreamingCleanup,
                async ct =>
                {
                    await CleanupAsync(ct).ConfigureAwait(false);
                    return null;
                },
                stoppingToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Adaptive HLS cleanup failed; the next pass will try again");
            return true;
        }
    }

    private int ReadIntervalMinutes()
    {
        try
        {
            return Math.Clamp(configuration.LoadTranscoding().AdaptiveHls.CleanupIntervalMinutes, 1, 1440);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the adaptive HLS cleanup interval; using {Minutes} minutes", FallbackIntervalMinutes);
            return FallbackIntervalMinutes;
        }
    }

    internal async Task CleanupAsync(CancellationToken ct = default)
    {
        var settings = configuration.LoadTranscoding();
        if (!settings.CleanupLruEnabled)
        {
            return;
        }

        var root = ResolveCacheRoot(settings, configuration.LoadCore().LibraryRoot);
        var hlsRoot = Path.GetFullPath(Path.Combine(root, "hls"));
        Directory.CreateDirectory(hlsRoot);
        CleanupStagingDirectories(hlsRoot);

        var rows = await packages.ListAsync(ct).ConfigureAwait(false);
        var limitBytes = Math.Max(1L, settings.ShadowStorageLimitGb) * 1024L * 1024L * 1024L;
        var totalBytes = rows.Where(row => row.Status == "ready").Sum(row => Math.Max(0, row.TotalBytes));
        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, settings.VariantRetentionDays));

        foreach (var row in rows)
        {
            if (hls.IsActive(row.Id))
            {
                continue;
            }

            var lastAccessed = DateTimeOffset.TryParse(
                row.LastAccessed,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed)
                ? parsed
                : DateTimeOffset.MinValue;
            var expired = lastAccessed < cutoff;
            var overLimit = totalBytes > limitBytes && row.Status == "ready";
            var failed = row.Status == "failed" && lastAccessed < DateTimeOffset.UtcNow.AddHours(-1);
            if (!expired && !overLimit && !failed)
            {
                continue;
            }

            if (TryResolveManagedDirectory(hlsRoot, row.RootPath, out var path) && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            var staging = row.RootPath + ".staging";
            if (TryResolveManagedDirectory(hlsRoot, staging, out var stagingPath) && Directory.Exists(stagingPath))
            {
                Directory.Delete(stagingPath, recursive: true);
            }

            await packages.DeleteAsync(row.Id, ct).ConfigureAwait(false);
            totalBytes -= Math.Max(0, row.TotalBytes);
            logger.LogInformation("Reclaimed adaptive HLS package {PackageId} ({Bytes} bytes)", row.Id, row.TotalBytes);
        }

        await CleanupExpiredOfflineVariantsAsync(root, ct).ConfigureAwait(false);
        await SweepOrphanJobFoldersAsync(root, ct).ConfigureAwait(false);
    }

    /// <summary>How old a job folder must be before the sweep may remove it when nothing owns it.</summary>
    internal static readonly TimeSpan OrphanJobFolderAge = TimeSpan.FromHours(24);

    /// <summary>
    /// Removes <c>&lt;cache&gt;/&lt;asset&gt;/&lt;profile&gt;/&lt;jobId&gt;</c> folders that a failed or interrupted encode left behind:
    /// the name is a job id, no offline variant keeps a file inside it, no waiting or running job owns it, and it has not been
    /// written to for <see cref="OrphanJobFolderAge"/>. Any other folder is left alone.
    /// </summary>
    internal async Task SweepOrphanJobFoldersAsync(string root, CancellationToken ct)
    {
        if (!Directory.Exists(root))
        {
            // Nothing has been encoded yet (or the cache was removed by hand); there is nothing to sweep.
            return;
        }

        var activeJobs = await playbackState.ListActiveEncodeJobIdsAsync(ct).ConfigureAwait(false);
        var variantFolders = (await playbackState.ListOfflineVariantOutputPathsAsync(ct).ConfigureAwait(false))
            .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .ToList();
        var oldest = DateTime.UtcNow - OrphanJobFolderAge;
        var hlsRoot = Path.Combine(root, "hls");

        foreach (var assetFolder in Directory.EnumerateDirectories(root))
        {
            if (string.Equals(assetFolder, hlsRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var profileFolder in Directory.EnumerateDirectories(assetFolder))
            {
                foreach (var jobFolder in Directory.EnumerateDirectories(profileFolder))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!Guid.TryParseExact(Path.GetFileName(jobFolder), "N", out var jobId)
                        || activeJobs.Contains(jobId)
                        || Directory.GetLastWriteTimeUtc(jobFolder) > oldest
                        || variantFolders.Any(path => path.StartsWith(jobFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    try
                    {
                        Directory.Delete(jobFolder, recursive: true);
                        logger.LogInformation("Removed leftover encode folder for job {JobId}", jobId);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        logger.LogWarning(ex, "Could not remove leftover encode folder for job {JobId}", jobId);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Offline variants are removed with their files once <c>expires_at</c> (written from
    /// <c>variant_retention_days</c> when the variant was prepared) has passed. Expiry wins: a variant that a device's
    /// download list still marks as stored is removed too, and its list entry goes with it.
    /// Files outside the variant cache are never deleted; only the stale row is dropped. A file that another
    /// variant (any profile or device) that has not expired still points at is kept, so one person's expiry cannot
    /// delete another person's download.
    /// </summary>
    private async Task CleanupExpiredOfflineVariantsAsync(string root, CancellationToken ct)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var now = DateTimeOffset.UtcNow;
        var expired = await playbackState.ListExpiredOfflineVariantsAsync(now, ct).ConfigureAwait(false);
        foreach (var variant in expired)
        {
            if (!string.IsNullOrWhiteSpace(variant.OutputPath))
            {
                var file = Path.TrimEndingDirectorySeparator(Path.GetFullPath(variant.OutputPath));
                if (file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (!await playbackState.IsFileUsedByAnotherLiveVariantAsync(variant.Id, variant.OutputPath, now, ct).ConfigureAwait(false))
                        {
                            if (File.Exists(file))
                            {
                                File.Delete(file);
                            }

                            RemoveEmptyParents(Path.GetDirectoryName(file), root);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // A file still in use (for example mid-download) or locked keeps its row; the next pass tries again,
                        // and one such file never stops the rest of the pass.
                        logger.LogWarning(ex, "Could not remove expired offline variant file for {VariantId}", variant.Id);
                        continue;
                    }
                }
                else
                {
                    logger.LogWarning("Expired offline variant {VariantId} points outside the variant cache; its file was left alone", variant.Id);
                }
            }

            await playbackState.DeleteOfflineVariantAsync(variant.Id, ct).ConfigureAwait(false);
            logger.LogInformation("Reclaimed expired offline variant {VariantId} ({Bytes} bytes)", variant.Id, variant.FileSizeBytes ?? 0);
        }
    }

    private static void RemoveEmptyParents(string? directory, string root)
    {
        while (directory is not null
            && directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(directory)
            && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory);
        }
    }

    private static void CleanupStagingDirectories(string root)
    {
        foreach (var directory in Directory.EnumerateDirectories(root, "*.staging", SearchOption.TopDirectoryOnly))
        {
            if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddHours(-1))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static bool TryResolveManagedDirectory(string root, string candidate, out string resolved)
    {
        resolved = Path.GetFullPath(candidate);
        return resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveCacheRoot(TranscodingSettings settings, string? libraryRoot)
    {
        var path = string.IsNullOrWhiteSpace(settings.VariantCachePath) ? ".data/variants" : settings.VariantCachePath;
        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            libraryRoot = AppContext.BaseDirectory;
        }

        // No trailing separator, so the "inside the cache" checks compare like with like.
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(libraryRoot, path)));
    }
}
