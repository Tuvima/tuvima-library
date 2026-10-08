using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Api.Services;

/// <summary>
/// Background service that runs once daily and permanently deletes rejected files
/// from <c>.staging/rejected/</c> that exceed the configured retention period.
///
/// Retention period is read from <c>config/maintenance.json → rejected_retention_days</c>.
/// Default: 30 days.  Set to 0 to disable automatic cleanup.
///
/// For each expired file the service:
/// <list type="number">
///   <item>Deletes the physical file from <c>.staging/rejected/</c>.</item>
///   <item>Removes the media_asset record (CASCADE handles editions → metadata_claims → canonical_values).</item>
///   <item>Removes the work and collection when no other editions remain.</item>
///   <item>Logs a <c>FileExpired</c> activity entry.</item>
/// </list>
/// </summary>
public sealed class RejectedFileCleanupService : BackgroundService
{
    public static bool IsRejectedStagingPath(string filePath, string rejectedDirectory)
    {
        try
        {
            if (!IsSameOrChild(rejectedDirectory, filePath)
                || string.Equals(Path.GetFullPath(filePath), Path.GetFullPath(rejectedDirectory), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (File.Exists(filePath) && (File.GetAttributes(filePath) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
            for (var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(filePath))!); directory is not null; directory = directory.Parent)
            {
                if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        { return false; }
    }

    private static bool IsSameOrChild(string root, string path) =>
        string.Equals(Path.GetFullPath(root), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)
        || Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private readonly IDatabaseConnection _db;
    private readonly ISystemActivityRepository _activityRepo;
    private readonly IConfigurationLoader _configLoader;
    private readonly ILogger<RejectedFileCleanupService> _logger;

    /// <summary>Cron expression for the cleanup schedule. Default: 4 AM daily.</summary>
    private const string DefaultSchedule = "0 4 * * *";

    public RejectedFileCleanupService(
        IDatabaseConnection db,
        ISystemActivityRepository activityRepo,
        IConfigurationLoader configLoader,
        ILogger<RejectedFileCleanupService> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(activityRepo);
        ArgumentNullException.ThrowIfNull(configLoader);
        ArgumentNullException.ThrowIfNull(logger);

        _db = db;
        _activityRepo = activityRepo;
        _configLoader = configLoader;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RejectedFileCleanupService started — schedule: {Schedule}", DefaultSchedule);

        // Initial delay to let the rest of the app start.
        await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCleanupPassAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Rejected file cleanup pass failed; will retry next cycle");
            }

            string cleanupSchedule;
            try
            {
                var maintenanceConfig = _configLoader.LoadMaintenance();
                cleanupSchedule = maintenanceConfig.Schedules.GetValueOrDefault("rejected_file_cleanup", DefaultSchedule);
                if (string.IsNullOrWhiteSpace(cleanupSchedule))
                {
                    cleanupSchedule = DefaultSchedule;
                }
            }
            catch
            {
                cleanupSchedule = DefaultSchedule;
            }
            var delay = CronScheduler.UntilNext(cleanupSchedule, TimeSpan.FromHours(24));
            _logger.LogInformation("Next rejected-file cleanup at {NextRun}", DateTimeOffset.Now.Add(delay));
            await Task.Delay(delay, stoppingToken);
        }
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private async Task RunCleanupPassAsync(CancellationToken ct)
    {
        var maintenance = _configLoader.LoadMaintenance();
        int retentionDays = maintenance.RejectedRetentionDays;

        if (retentionDays <= 0)
        {
            _logger.LogDebug("RejectedFileCleanupService: cleanup disabled (rejected_retention_days=0)");
            return;
        }

        // Determine the library root so we can build the rejected folder path.
        string libraryRoot;
        try
        {
            var core = _configLoader.LoadCore();
            libraryRoot = core.LibraryRoot ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RejectedFileCleanupService: could not load LibraryRoot — skipping cleanup pass");
            return;
        }

        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            _logger.LogDebug("RejectedFileCleanupService: LibraryRoot not configured — skipping cleanup pass");
            return;
        }

        var rejectedDir = Path.Combine(libraryRoot, ".data", "staging", "rejected");
        if (!Directory.Exists(rejectedDir))
        {
            _logger.LogDebug("RejectedFileCleanupService: rejected folder does not exist — nothing to clean");
            return;
        }

        // Find all works with curator_state = 'rejected' whose rejected_at timestamp
        // is older than retentionDays.
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays).ToString("o");

        var expiredAssets = new List<(Guid AssetId, string FilePath, Guid? WorkId, Guid? CollectionId, string? WorkTitle)>();

        using (var conn = _db.CreateConnection())
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT ma.id         AS AssetId,
                           ma.file_path_root AS FilePath,
                           e.work_id     AS WorkId,
                           w.collection_id      AS CollectionId,
                           cv.value      AS WorkTitle
                    FROM works w
                    INNER JOIN editions e ON e.work_id = w.id
                    INNER JOIN media_assets ma ON ma.edition_id = e.id
                    LEFT JOIN canonical_values cv ON cv.entity_id = ma.id AND cv.key = 'title'
                    WHERE w.curator_state = 'rejected'
                      AND w.rejected_at IS NOT NULL
                      AND w.rejected_at <= @cutoff
                    """;
                cmd.Parameters.AddWithValue("@cutoff", cutoff);
    
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    expiredAssets.Add((
                        AssetId: GuidSql.FromDb(reader.GetValue(0)),
                        FilePath: reader.GetString(1),
                        WorkId: reader.IsDBNull(2) ? null : GuidSql.FromDb(reader.GetValue(2)),
                        CollectionId: reader.IsDBNull(3) ? null : GuidSql.FromDb(reader.GetValue(3)),
                        WorkTitle: reader.IsDBNull(4) ? null : reader.GetString(4)));
                }
            }
        }

        if (expiredAssets.Count == 0)
        {
            _logger.LogDebug(
                "RejectedFileCleanupService: no rejected files exceed {Days}d retention — nothing to clean",
                retentionDays);
            return;
        }

        _logger.LogInformation(
            "RejectedFileCleanupService: {Count} rejected file(s) exceed {Days}d retention — cleaning up",
            expiredAssets.Count, retentionDays);

        int cleaned = 0;

        foreach (var (assetId, filePath, workId, collectionId, workTitle) in expiredAssets)
        {
            ct.ThrowIfCancellationRequested();

            // Rejection is a catalogue decision, never permission to delete an
            // existing-library original whose path is still attached to the asset.
            if (!IsRejectedStagingPath(filePath, rejectedDir)
                || _configLoader.LoadLibraries().Libraries.SelectMany(l => l.Sources).Any(source =>
                    !source.AllowsFileMutation && IsSameOrChild(source.Path, filePath)))
            {
                _logger.LogWarning("Rejected-file cleanup preserved protected or non-staging original {Path}", filePath);
                continue;
            }

            try
            {
                // 1. Delete only the verified staging file.
                try
                {
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "RejectedFileCleanupService: could not delete file {Path} — skipping", filePath);
                    continue;
                }

                // 2. Clear curator_state and remove any remaining review_queue entries.
                using (var conn = _db.CreateConnection())
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "DELETE FROM review_queue WHERE entity_id = @assetId";
                        cmd.Parameters.Add("@assetId", SqliteType.Blob).Value = GuidSql.ToBlob(assetId);
                        cmd.ExecuteNonQuery();
                    }
                }

                // 3. Remove the media_asset (CASCADE handles metadata_claims, canonical_values).
                using (var conn = _db.CreateConnection())
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "DELETE FROM media_assets WHERE id = @assetId";
                        cmd.Parameters.Add("@assetId", SqliteType.Blob).Value = GuidSql.ToBlob(assetId);
                        cmd.ExecuteNonQuery();
                    }
                }

                // 4. Remove the edition if it has no remaining assets.
                if (workId is not null)
                {
                    using var conn = _db.CreateConnection();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = """
                        DELETE FROM editions
                        WHERE work_id = @workId
                          AND NOT EXISTS (SELECT 1 FROM media_assets ma WHERE ma.edition_id = editions.id)
                        """;
                    cmd.Parameters.Add("@workId", SqliteType.Blob).Value = GuidSql.ToBlob(workId.Value);
                    cmd.ExecuteNonQuery();
                }

                // 5a. Clear curator_state on the work.
                if (workId is not null)
                {
                    using var conn = _db.CreateConnection();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "UPDATE works SET curator_state = NULL, rejected_at = NULL WHERE id = @workId";
                    cmd.Parameters.Add("@workId", SqliteType.Blob).Value = GuidSql.ToBlob(workId.Value);
                    cmd.ExecuteNonQuery();
                }

                // 5. Remove the work if it has no remaining editions.
                if (workId is not null)
                {
                    using var conn = _db.CreateConnection();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = """
                        DELETE FROM works WHERE id = @workId
                          AND NOT EXISTS (SELECT 1 FROM editions WHERE work_id = @workId)
                        """;
                    cmd.Parameters.Add("@workId", SqliteType.Blob).Value = GuidSql.ToBlob(workId.Value);
                    cmd.ExecuteNonQuery();
                }

                // 6. Remove the collection if it has no remaining works.
                if (collectionId is not null)
                {
                    using var conn = _db.CreateConnection();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = """
                        DELETE FROM collections WHERE id = @collectionId
                          AND NOT EXISTS (SELECT 1 FROM works WHERE collection_id = @collectionId)
                        """;
                    cmd.Parameters.Add("@collectionId", SqliteType.Blob).Value = GuidSql.ToBlob(collectionId.Value);
                    cmd.ExecuteNonQuery();
                }

                // 7. Log the expiry.
                await _activityRepo.LogAsync(new SystemActivityEntry
                {
                    OccurredAt = DateTimeOffset.UtcNow,
                    ActionType = SystemActionType.AutoPurge,
                    CollectionName = workTitle,
                    EntityId = workId ?? Guid.Empty,
                    EntityType = "Work",
                    Detail = $"Rejected file '{workTitle ?? Path.GetFileName(filePath)}' expired after {retentionDays} days and was permanently deleted.",
                    ChangesJson = $"{{\"asset_id\":\"{assetId}\",\"retention_days\":{retentionDays}}}",
                }, ct);

                cleaned++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RejectedFileCleanupService: error cleaning asset {AssetId} — skipping", assetId);
            }
        }

        _logger.LogInformation(
            "RejectedFileCleanupService: cleaned {Cleaned} of {Total} expired rejected file(s)",
            cleaned, expiredAssets.Count);
    }
}
