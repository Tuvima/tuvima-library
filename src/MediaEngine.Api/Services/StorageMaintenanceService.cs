using System.Text.Json;
using Dapper;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services;

public sealed class StorageMaintenanceService : IStorageMaintenanceService
{
    private readonly IDatabaseConnection _db;
    private readonly IConfigurationLoader _configLoader;
    private readonly UISettingsCacheRepository _uiSettingsCache;
    private readonly ISystemActivityRepository _activityRepository;
    private readonly ILogger<StorageMaintenanceService> _logger;

    public StorageMaintenanceService(
        IDatabaseConnection db,
        IConfigurationLoader configLoader,
        UISettingsCacheRepository uiSettingsCache,
        ISystemActivityRepository activityRepository,
        ILogger<StorageMaintenanceService> logger)
    {
        _db = db;
        _configLoader = configLoader;
        _uiSettingsCache = uiSettingsCache;
        _activityRepository = activityRepository;
        _logger = logger;
    }

    public async Task<StorageMaintenanceResult> RunAsync(
        StorageMaintenanceRequest request,
        CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var settings = _configLoader.LoadMaintenance().StorageMaintenance;
        var searchMaxAgeDays = request.SearchCacheMaxAgeDays ?? settings.SearchCacheMaxAgeDays;
        var imageRetentionDays = request.ImageCacheRetentionDays ?? settings.ImageCacheRetentionDays;
        var claimBatchSize = request.ClaimCompactionBatchSize ?? settings.ClaimCompactionBatchSize;
        var steps = new List<StorageMaintenanceStepResult>();

        await _db.AcquireWriteLockAsync(ct).ConfigureAwait(false);
        try
        {
            steps.Add(PurgeExpiredCache(
                "provider_response_cache",
                "Provider response cache",
                "expires_at <= @now",
                new { now = DateTimeOffset.UtcNow.ToString("O") },
                request.DryRun));

            steps.Add(PurgeExpiredCache(
                "resolver_cache",
                "Resolver cache",
                "expires_at <= @now",
                new { now = DateTimeOffset.UtcNow.ToString("O") },
                request.DryRun));

            steps.Add(PurgeExpiredCache(
                "search_results_cache",
                "Search result cache",
                "searched_at < @cutoff",
                new { cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, searchMaxAgeDays)).ToString("O") },
                request.DryRun));

            steps.Add(CleanImageCache(
                Math.Max(1, imageRetentionDays),
                request.DryRun,
                ct));

            steps.Add(CompactDuplicateClaims(
                Math.Max(1, claimBatchSize),
                request.DryRun));
        }
        finally
        {
            _db.ReleaseWriteLock();
        }

        // Runs AFTER the write lock is released: the rebuild goes through
        // ExecuteWriteAsync, which acquires the same non-reentrant
        // write lock itself — calling it inside the locked region above
        // would deadlock.
        steps.Add(await RebuildUiSettingsCacheAsync(request.DryRun, ct).ConfigureAwait(false));

        var result = new StorageMaintenanceResult(
            startedAt,
            DateTimeOffset.UtcNow,
            request.DryRun,
            steps);

        if (!request.DryRun)
        {
            await _activityRepository.LogAsync(new SystemActivityEntry
            {
                ActionType = SystemActionType.AutoPurge,
                EntityType = "Storage",
                Detail = $"Storage maintenance completed; {result.TotalAffectedRows} row/file action(s) applied.",
                ChangesJson = JsonSerializer.Serialize(new
                {
                    dry_run = false,
                    total_affected = result.TotalAffectedRows,
                    steps = result.Steps.Select(step => new
                    {
                        name = step.Name,
                        affected = step.AffectedRows,
                    }),
                }),
            }, ct).ConfigureAwait(false);
        }

        return result;
    }

    private StorageMaintenanceStepResult PurgeExpiredCache(
        string table,
        string label,
        string predicate,
        object parameters,
        bool dryRun)
    {
        using var conn = _db.CreateConnection();
        var count = conn.ExecuteScalar<int>($"SELECT COUNT(*) FROM {table} WHERE {predicate};", parameters);
        if (!dryRun && count > 0)
        {
            conn.Execute($"DELETE FROM {table} WHERE {predicate};", parameters);
        }

        return new StorageMaintenanceStepResult(
            label,
            count,
            dryRun ? "expired rows counted" : "expired rows purged");
    }

    private StorageMaintenanceStepResult CleanImageCache(
        int retentionDays,
        bool dryRun,
        CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays).ToString("O");
        var rows = conn.Query<ImageCacheRow>("""
            SELECT content_hash AS ContentHash,
                   file_path AS FilePath,
                   COALESCE(is_user_override, 0) AS IsUserOverride,
                   downloaded_at AS DownloadedAt
            FROM image_cache
            WHERE COALESCE(is_user_override, 0) = 0
              AND downloaded_at < @cutoff;
            """, new { cutoff }).AsList();

        if (rows.Count == 0)
        {
            return new StorageMaintenanceStepResult("Image cache", 0, "no expired non-user rows");
        }

        var referencedPaths = LoadReferencedImagePaths(conn);
        var safeRoot = ResolveSafeAssetRoot();
        var affected = 0;

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            var normalizedPath = NormalizePath(row.FilePath);
            var fileExists = normalizedPath is not null && File.Exists(normalizedPath);
            var referenced = normalizedPath is not null && referencedPaths.Contains(normalizedPath);
            var shouldDelete = !referenced || !fileExists;

            if (!shouldDelete)
            {
                continue;
            }

            affected++;
            if (dryRun)
            {
                continue;
            }

            conn.Execute(
                "DELETE FROM image_cache WHERE content_hash = @contentHash;",
                new { contentHash = row.ContentHash });

            if (fileExists && IsUnderSafeRoot(normalizedPath!, safeRoot))
            {
                try
                {
                    File.Delete(normalizedPath!);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Storage maintenance could not delete cached image {Path}", normalizedPath);
                }
            }
        }

        return new StorageMaintenanceStepResult(
            "Image cache",
            affected,
            dryRun
                ? "expired orphan/missing rows counted"
                : "expired orphan/missing rows purged; safe generated files deleted");
    }

    private StorageMaintenanceStepResult CompactDuplicateClaims(int batchSize, bool dryRun)
    {
        using var conn = _db.CreateConnection();
        var parameters = new { limit = batchSize };
        var count = conn.ExecuteScalar<int>("""
            WITH duplicate_claims AS (
                SELECT rowid
                FROM (
                    SELECT rowid,
                           ROW_NUMBER() OVER (
                               PARTITION BY entity_id, provider_id, claim_key, claim_value, confidence
                               ORDER BY claimed_at DESC, rowid DESC
                           ) AS duplicate_rank
                    FROM metadata_claims
                    WHERE COALESCE(is_user_locked, 0) = 0
                )
                WHERE duplicate_rank > 1
                LIMIT @limit
            )
            SELECT COUNT(*) FROM duplicate_claims;
            """, parameters);

        if (!dryRun && count > 0)
        {
            conn.Execute("""
                WITH duplicate_claims AS (
                    SELECT rowid
                    FROM (
                        SELECT rowid,
                               ROW_NUMBER() OVER (
                                   PARTITION BY entity_id, provider_id, claim_key, claim_value, confidence
                                   ORDER BY claimed_at DESC, rowid DESC
                               ) AS duplicate_rank
                        FROM metadata_claims
                        WHERE COALESCE(is_user_locked, 0) = 0
                    )
                    WHERE duplicate_rank > 1
                    LIMIT @limit
                )
                DELETE FROM metadata_claims
                WHERE rowid IN (SELECT rowid FROM duplicate_claims);
                """, parameters);
        }

        return new StorageMaintenanceStepResult(
            "Metadata claim compaction",
            count,
            dryRun ? "exact duplicate non-user-locked claims counted" : "exact duplicate non-user-locked claims compacted");
    }

    private async Task<StorageMaintenanceStepResult> RebuildUiSettingsCacheAsync(bool dryRun, CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        var before = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM ui_settings_cache;");
        if (!dryRun)
        {
            await _uiSettingsCache.RebuildFromFilesAsync(_configLoader, ct).ConfigureAwait(false);
        }

        return new StorageMaintenanceStepResult(
            "UI settings cache",
            before,
            dryRun ? "current cached scopes counted" : "cache rebuilt from config files");
    }

    private HashSet<string> LoadReferencedImagePaths(System.Data.IDbConnection conn)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] candidates =
        [
            "local_image_path",
            "local_image_path_s",
            "local_image_path_m",
            "local_image_path_l",
        ];

        var selects = candidates
            .Select(column => $"SELECT {column} FROM entity_assets WHERE {column} IS NOT NULL AND TRIM({column}) <> ''")
            .ToList();

        foreach (var path in conn.Query<string>(string.Join(" UNION ALL ", selects)))
        {
            var normalized = NormalizePath(path);
            if (normalized is not null)
            {
                paths.Add(normalized);
            }
        }

        return paths;
    }

    private string? ResolveSafeAssetRoot()
    {
        try
        {
            var libraryRoot = _configLoader.LoadCore().LibraryRoot;
            return string.IsNullOrWhiteSpace(libraryRoot)
                ? null
                : NormalizePath(new AssetPathService(libraryRoot).AssetsRoot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Storage maintenance could not resolve library asset root; cached files will not be deleted");
            return null;
        }
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsUnderSafeRoot(string path, string? safeRoot)
    {
        if (string.IsNullOrWhiteSpace(safeRoot))
        {
            return false;
        }

        var root = Path.GetFullPath(safeRoot);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
        {
            root += Path.DirectorySeparatorChar;
        }

        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ImageCacheRow
    {
        public string ContentHash { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public int IsUserOverride { get; set; }
        public string DownloadedAt { get; set; } = string.Empty;
    }
}
