using System.Globalization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Playback;

namespace MediaEngine.Api.Services.Playback;

public sealed class AdaptiveHlsCleanupService(
    AdaptiveHlsPackageRepository packages,
    PlaybackStateRepository playbackState,
    AdaptiveHlsService hls,
    IConfigurationLoader configuration,
    ILogger<AdaptiveHlsCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await CleanupAsync(stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            var minutes = Math.Clamp(
                configuration.LoadTranscoding().AdaptiveHls.CleanupIntervalMinutes,
                1,
                1440);
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(minutes), stoppingToken).ConfigureAwait(false);
                await CleanupAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Adaptive HLS cleanup failed");
            }
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
    }

    /// <summary>
    /// Offline variants are removed with their files once <c>expires_at</c> (written from
    /// <c>variant_retention_days</c> when the variant was prepared) has passed. Expiry wins: a variant that a device's
    /// download list still marks as stored is removed too, and its list entry goes with it.
    /// Files outside the variant cache are never deleted; only the stale row is dropped.
    /// </summary>
    private async Task CleanupExpiredOfflineVariantsAsync(string root, CancellationToken ct)
    {
        var expired = await playbackState.ListExpiredOfflineVariantsAsync(DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        foreach (var variant in expired)
        {
            if (!string.IsNullOrWhiteSpace(variant.OutputPath))
            {
                var file = Path.GetFullPath(variant.OutputPath);
                if (file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (File.Exists(file))
                        {
                            File.Delete(file);
                        }

                        RemoveEmptyParents(Path.GetDirectoryName(file), root);
                    }
                    catch (IOException ex)
                    {
                        // A file still in use (for example mid-download) keeps its row; the next pass tries again.
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

        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(libraryRoot, path));
    }
}
