using System.Diagnostics;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Storage.Contracts;
using MediaEngine.Storage.Services;

namespace MediaEngine.Api.Services;

/// <summary>
/// Background service that periodically scans all Normal-status assets and
/// verifies their files still exist on disk. Missing files are logged and
/// now-empty media folders are cleaned up.
///
/// Also performs three folder-maintenance passes:
/// <list type="bullet">
///   <item><b>Empty folder pruning:</b> Walks the Library Root bottom-up and
///     deletes any empty directory while leaving category roots intact.</item>
///   <item><b>Orphaned people cleanup:</b> Scans canonical .data/assets/people
///     folders and removes folders whose person record is gone or no longer linked.</item>
///   <item><b>Stale sidecar cleanup:</b> Removes stray category-root metadata sidecars.</item>
/// </list>
///
/// Also exposes <see cref="ReconcileAsync"/> for manual trigger via API.
///
/// Follows the same loop pattern as <see cref="ActivityPruningService"/>.
/// </summary>
public sealed partial class LibraryReconciliationService : BackgroundService, IReconciliationService
{
    private readonly IMediaAssetRepository _assetRepo;
    private readonly IMetadataClaimRepository _claimRepo;
    private readonly ICanonicalValueRepository _canonicalRepo;
    private readonly ISystemActivityRepository _activityRepo;
    private readonly IPersonRepository _personRepo;
    private readonly IReviewQueueRepository _reviewRepo;
    private readonly ICollectionRepository _collectionRepo;
    private readonly IEventPublisher _publisher;
    private readonly WorkHierarchyMaintenanceService _hierarchyMaintenance;
    private readonly IWorkIdentityReconciliationService _workIdentityReconciliation;
    private readonly CollectionBackfillService _collectionBackfill;
    private readonly IConfigurationLoader _configLoader;
    private readonly AssetPathService _assetPaths;
    private readonly IDatabaseConnection _db;
    private readonly ILibraryFolderResolver _libraryFolderResolver;
    private readonly MediaEngine.Ingestion.Contracts.IImportFolderService? _importFolders;
    private readonly MediaEngine.Ingestion.Services.ImportFilingJournal? _importJournal;
    private readonly ScheduledTaskRunner _runner;
    private readonly ILogger<LibraryReconciliationService> _logger;

    /// <summary>Tuvima's own working folder under the library root (staging, caches, images, database).</summary>
    private const string DataFolderName = ".data";

    public LibraryReconciliationService(
        IMediaAssetRepository assetRepo,
        IMetadataClaimRepository claimRepo,
        ICanonicalValueRepository canonicalRepo,
        ISystemActivityRepository activityRepo,
        IPersonRepository personRepo,
        IReviewQueueRepository reviewRepo,
        ICollectionRepository collectionRepo,
        IEventPublisher publisher,
        WorkHierarchyMaintenanceService hierarchyMaintenance,
        IWorkIdentityReconciliationService workIdentityReconciliation,
        CollectionBackfillService collectionBackfill,
        IConfigurationLoader configLoader,
        AssetPathService assetPaths,
        IDatabaseConnection db,
        ILibraryFolderResolver libraryFolderResolver,
        ScheduledTaskRunner runner,
        ILogger<LibraryReconciliationService> logger,
        MediaEngine.Ingestion.Contracts.IImportFolderService? importFolders = null,
        MediaEngine.Ingestion.Services.ImportFilingJournal? importJournal = null)
    {
        _runner = runner;
        _importFolders = importFolders;
        _importJournal = importJournal;
        _assetRepo = assetRepo;
        _claimRepo = claimRepo;
        _canonicalRepo = canonicalRepo;
        _activityRepo = activityRepo;
        _personRepo = personRepo;
        _reviewRepo = reviewRepo;
        _collectionRepo = collectionRepo;
        _publisher = publisher;
        _hierarchyMaintenance = hierarchyMaintenance;
        _workIdentityReconciliation = workIdentityReconciliation;
        _collectionBackfill = collectionBackfill;
        _configLoader = configLoader;
        _assetPaths = assetPaths;
        _db = db;
        _libraryFolderResolver = libraryFolderResolver;
        _logger = logger;
    }

    // ── IReconciliationService ────────────────────────────────────────────

    /// <inheritdoc />
    async Task<ReconciliationSummary> IReconciliationService.ReconcileAsync(CancellationToken ct)
    {
        var result = await ReconcileAsync(ct).ConfigureAwait(false);
        return new ReconciliationSummary(result.TotalScanned, result.MissingCount, result.ElapsedMs);
    }

    // ── BackgroundService ─────────────────────────────────────────────────

    /// <summary>Cron expression for the reconciliation schedule. Default: 5 AM daily.</summary>
    private const string DefaultSchedule = "0 5 * * *";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("LibraryReconciliationService started — schedule: {Schedule}", DefaultSchedule);

        // The IngestionEngine already runs reconciliation at startup (Step 2),
        // so this background loop waits the full interval before its first run
        // to avoid a duplicate reconciliation within seconds of boot.

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var maintenance = _configLoader.LoadMaintenance();
                var intervalHours = maintenance.ReconciliationIntervalHours;

                if (intervalHours > 0)
                {
                    var schedule = maintenance.Schedules.GetValueOrDefault("library_reconciliation", DefaultSchedule);
                    if (string.IsNullOrWhiteSpace(schedule))
                    {
                        schedule = DefaultSchedule;
                    }

                    var nextRun = CronScheduler.NextRunAt(schedule, TimeSpan.FromHours(24));
                    await CronScheduler.WaitUntilAsync(nextRun, stoppingToken);
                    await _runner.RunAsync(ScheduledTaskKeys.LibraryReconciliation, async ct =>
                    {
                        var result = await ReconcileAsync(ct);
                        return TaskRunResult.Succeeded(
                            result.TotalScanned, $"Checked {result.TotalScanned} files; {result.MissingCount} missing.");
                    }, stoppingToken);
                }
                else
                {
                    // Disabled — check again in 1 hour in case config changes.
                    await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Library reconciliation failed; will retry next cycle");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }

    /// <summary>
    /// Scans all Normal-status assets, verifies files exist on disk,
    /// and cleans up any orphans found.
    /// </summary>
    public async Task<ReconciliationResult> ReconcileAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var startedAt = DateTimeOffset.UtcNow;
        var allAssets = await _assetRepo.ListByStatusAsync(AssetStatus.Normal, ct);
        // A Vault file is never counted, listed or logged here (Videos spec D17, D32). Its missing-file cleanup runs quietly below.
        var vaultAssetIds = ReadVaultAssetIds();
        IReadOnlyList<MediaAsset> assets = vaultAssetIds.Count == 0
            ? allAssets
            : allAssets.Where(asset => !vaultAssetIds.Contains(asset.Id)).ToList();
        await RemoveMissingVaultFilesQuietlyAsync(allAssets.Where(asset => vaultAssetIds.Contains(asset.Id)), ct).ConfigureAwait(false);
        int missingCount = 0;

        // Collect missing-file details for the completion summary.
        var missingFiles = new List<object>();

        foreach (var asset in assets)
        {
            ct.ThrowIfCancellationRequested();

            if (File.Exists(asset.FilePathRoot))
            {
                continue;
            }

            // A file in the Import folder is only missing for real when the folder itself is reachable and no filing of it is
            // part-way through; an offline drive or a move in flight must not cost the file its identity and history.
            if (_importFolders?.GetImportPath() is { } importPath
                && _importFolders.IsInImportFolder(asset.FilePathRoot)
                && (!Directory.Exists(importPath) || _importJournal?.IsInProgress(asset.Id) == true))
            {
                _logger.LogWarning(
                    "Reconciliation deferred missing-file cleanup for asset {Id}; the Import folder is unavailable or the file is being filed",
                    asset.Id);
                continue;
            }

            var configuredSource = _libraryFolderResolver.ResolveSourceForPath(asset.FilePathRoot);
            if (configuredSource is not null && !IsConfiguredSourceAvailable(configuredSource.Source.Path))
            {
                _logger.LogWarning(
                    "Reconciliation deferred missing-file cleanup for asset {Id}; source {SourceId} is unavailable at {SourcePath}",
                    asset.Id,
                    configuredSource.Source.Id,
                    configuredSource.Source.Path);
                continue;
            }

            _logger.LogInformation(
                "Reconciliation: file missing for asset {Id} at {Path}",
                asset.Id, asset.FilePathRoot);

            // Clean filesystem artifacts that are still current sidecar metadata.
            var editionFolder = Path.GetDirectoryName(asset.FilePathRoot);
            if (!string.IsNullOrEmpty(editionFolder) && Directory.Exists(editionFolder))
            {
                SafeDeleteFile(Path.Combine(editionFolder, "library.xml"));
                TryDeleteEmptyDirectory(editionFolder);

                var collectionFolder = Path.GetDirectoryName(editionFolder);
                if (!string.IsNullOrEmpty(collectionFolder) && Directory.Exists(collectionFolder))
                {
                    SafeDeleteFile(Path.Combine(collectionFolder, "library.xml"));
                    TryDeleteEmptyDirectory(collectionFolder);
                }
            }

            // Delete DB records.
            await _claimRepo.DeleteByEntityAsync(asset.Id, ct);
            await _canonicalRepo.DeleteByEntityAsync(asset.Id, ct);
            await _assetRepo.DeleteAsync(asset.Id, ct);
            // Dismiss any pending review queue items for this entity so they
            // no longer appear in the Needs Review tab after the file is gone.
            await _reviewRepo.DismissAllByEntityAsync(asset.Id, ct);

            // Collect details for the completion summary (no separate per-file entry needed).
            missingFiles.Add(new
            {
                entity_id = asset.Id.ToString(),
                filename = Path.GetFileName(asset.FilePathRoot),
                action = "Records and artifacts deleted",
            });

            // Still log a per-file entry so the entity ID is recorded in the activity log
            // (used by the Dashboard to hide the reprocess button for deleted assets).
            await _activityRepo.LogAsync(new SystemActivityEntry
            {
                ActionType = SystemActionType.ReconciliationMissing,
                EntityId = asset.Id,
                EntityType = "MediaAsset",
                Detail = $"Missing file detected: {Path.GetFileName(asset.FilePathRoot)}",
            }, ct);

            missingCount++;
        }

        // ── Database hierarchy pruning ────────────────────────────────────────
        // After deleting MediaAssets, remove any Editions / Works / Collections that
        // are now empty so they stop appearing on the home page.
        var collectionBackfill = await _collectionBackfill.RunAutomaticAsync(ct).ConfigureAwait(false);
        if (collectionBackfill.ProcessedCount > 0)
        {
            _logger.LogInformation(
                "Reconciliation: collection backfill processed {Processed}/{Candidates} candidate work(s), assigned {Assigned}, created {Created}, skipped {Skipped}, failed {Failed}",
                collectionBackfill.ProcessedCount,
                collectionBackfill.CandidateCount,
                collectionBackfill.AssignedCount,
                collectionBackfill.CreatedCollectionCount,
                collectionBackfill.SkippedCount,
                collectionBackfill.FailedCount);
        }

        int hierarchyPruned = 0;
        if (missingCount > 0)
        {
            hierarchyPruned = await _collectionRepo.PruneOrphanedHierarchyAsync(ct);
            hierarchyPruned += await _hierarchyMaintenance.CleanupEmptyParentsAsync(ct);
            if (hierarchyPruned > 0)
            {
                _logger.LogInformation(
                    "Reconciliation: pruned {Count} orphaned hierarchy records (editions/works/collections)",
                    hierarchyPruned);
            }
        }

        var duplicateReadWorksMerged = await _workIdentityReconciliation
            .MergeDuplicateReadWorksByQidAsync(ct)
            .ConfigureAwait(false);
        var audiobookAuthorsAligned = await _workIdentityReconciliation
            .AlignAudiobookAuthorsWithBooksByQidAsync(ct)
            .ConfigureAwait(false);

        // ── Folder Maintenance Passes ───────────────────────────────────────

        var core = _configLoader.LoadCore();
        int foldersCleanedCount = 0;
        int orphanPeopleCount = 0;
        int staleSidecarsCount = 0;

        if (!string.IsNullOrWhiteSpace(core.LibraryRoot) && Directory.Exists(core.LibraryRoot))
        {
            // Pass 1: Empty folder pruning (bottom-up).
            var libraryRoots = ConfiguredLibraryRoots(_configLoader.LoadLibraries());
            foldersCleanedCount = PruneEmptyFolders(core.LibraryRoot, libraryRoots, _logger, ct);

            // Pass 2: Orphaned people cleanup.
            orphanPeopleCount = await CleanOrphanedPeopleAsync(core.LibraryRoot, ct);

            // Pass 3: Stale category-root sidecar cleanup.
            // Removes library.xml files that were incorrectly written directly inside a
            // category folder (e.g., Books/library.xml) by AutoOrganizeService when a
            // shallow organization template caused collectionFolder to resolve to the category root.
            staleSidecarsCount = CleanStaleRootSidecars(core.LibraryRoot);
        }

        sw.Stop();

        // Log a single consolidated FolderCleaned entry when any folder maintenance pass
        // cleaned up at least one item. Sub-items in ChangesJson let the Dashboard
        // show detail without flooding the timeline with separate entries per pass.
        int totalFolderOps = foldersCleanedCount + orphanPeopleCount + staleSidecarsCount;
        if (totalFolderOps > 0)
        {
            var subItems = new List<object>();
            if (foldersCleanedCount > 0)
            {
                subItems.Add(new { label = "Empty folders pruned", count = foldersCleanedCount });
            }

            if (orphanPeopleCount > 0)
            {
                subItems.Add(new { label = "Orphaned people folders removed", count = orphanPeopleCount });
            }

            if (staleSidecarsCount > 0)
            {
                subItems.Add(new { label = "Stale root sidecars cleaned", count = staleSidecarsCount });
            }

            await _activityRepo.LogAsync(new SystemActivityEntry
            {
                ActionType = SystemActionType.FolderCleaned,
                EntityType = "System",
                Detail = $"Cleaned {totalFolderOps} folder(s)",
                ChangesJson = JsonSerializer.Serialize(new { items = subItems }),
            }, ct);
        }

        // Log completion with full details so the Dashboard can show a single grouped entry.
        await _activityRepo.LogAsync(new SystemActivityEntry
        {
            ActionType = SystemActionType.ReconciliationCompleted,
            EntityType = "System",
            Detail = $"Reconciliation complete — scanned {assets.Count}, {missingCount} missing, {audiobookAuthorsAligned} audiobook author identities aligned, {hierarchyPruned} hierarchy rows pruned, {foldersCleanedCount} empty folders cleaned, {orphanPeopleCount} orphan people removed, {staleSidecarsCount} stale root sidecars cleaned",
            ChangesJson = JsonSerializer.Serialize(new
            {
                total_scanned = assets.Count,
                missing_count = missingCount,
                hierarchy_pruned = hierarchyPruned,
                duplicate_read_works_merged = duplicateReadWorksMerged,
                audiobook_authors_aligned = audiobookAuthorsAligned,
                folders_cleaned = foldersCleanedCount,
                orphan_people = orphanPeopleCount,
                stale_root_sidecars = staleSidecarsCount,
                elapsed_ms = sw.ElapsedMilliseconds,
                started_at = startedAt.ToString("O"),
                missing_files = missingFiles,
            }),
        }, ct);

        // Broadcast a library-changed event so Dashboard circuits that are already
        // open invalidate their collection cache and refresh the home page.
        if (missingCount > 0
            || duplicateReadWorksMerged > 0
            || audiobookAuthorsAligned > 0
            || collectionBackfill.AssignedCount > 0)
        {
            try
            {
                var eventName = missingCount > 0 || duplicateReadWorksMerged > 0
                    ? SignalREvents.MediaRemoved
                    : SignalREvents.MediaAdded;

                await _publisher.PublishAsync(
                    eventName,
                    new ReconciliationLibraryChangedEvent(
                        "reconciliation",
                        missingCount,
                        duplicateReadWorksMerged,
                        collectionBackfill.AssignedCount),
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to publish MediaRemoved SignalR event after reconciliation");
            }
        }

        _logger.LogInformation(
            "Reconciliation complete: {Total} scanned, {Missing} missing, {CollectionBackfillAssigned} collection assignments repaired, {HierarchyPruned} hierarchy rows pruned, " +
            "{DuplicateReadWorksMerged} duplicate read works merged, {AudiobookAuthorsAligned} audiobook author identities aligned, {FoldersCleaned} empty folders, {OrphanPeople} orphan people, " +
            "{StaleSidecars} stale root sidecars, {Elapsed}ms",
            assets.Count, missingCount, collectionBackfill.AssignedCount, hierarchyPruned, duplicateReadWorksMerged, audiobookAuthorsAligned, foldersCleanedCount,
            orphanPeopleCount, staleSidecarsCount, sw.ElapsedMilliseconds);

        return new ReconciliationResult(
            assets.Count,
            missingCount,
            sw.ElapsedMilliseconds,
            duplicateReadWorksMerged,
            audiobookAuthorsAligned);
    }

    private HashSet<Guid> ReadVaultAssetIds()
    {
        using var connection = _db.CreateConnection();
        return connection.Query<Guid>(
            """
            SELECT ma.id
            FROM media_assets ma
            JOIN vault_libraries vl ON vl.library_id = ma.library_id COLLATE NOCASE;
            """).ToHashSet();
    }

    /// <summary>
    /// A Vault file whose source has gone is forgotten without a word: no log line, no activity entry, no count and no event,
    /// so nothing about it reaches an administrator without an unlocked Vault mode session.
    /// </summary>
    private async Task RemoveMissingVaultFilesQuietlyAsync(IEnumerable<MediaAsset> vaultAssets, CancellationToken ct)
    {
        foreach (var asset in vaultAssets)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(asset.FilePathRoot))
            {
                continue;
            }

            var configuredSource = _libraryFolderResolver.ResolveSourceForPath(asset.FilePathRoot);
            if (configuredSource is not null && !IsConfiguredSourceAvailable(configuredSource.Source.Path))
            {
                continue;
            }

            await _claimRepo.DeleteByEntityAsync(asset.Id, ct).ConfigureAwait(false);
            await _canonicalRepo.DeleteByEntityAsync(asset.Id, ct).ConfigureAwait(false);
            await _assetRepo.DeleteAsync(asset.Id, ct).ConfigureAwait(false);
            await _reviewRepo.DismissAllByEntityAsync(asset.Id, ct).ConfigureAwait(false);
        }
    }

    internal static bool IsConfiguredSourceAvailable(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return false;
        }

        try
        {
            return Directory.Exists(Path.GetFullPath(sourcePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }
    }

    // ── Pass 1: Empty Folder Pruning ────────────────────────────────────────

    /// <summary>
    /// Walks the Library Root tree bottom-up and deletes any empty directory (no files, no subdirectories).
    /// Skips the library root itself and every configured library folder (<paramref name="libraryRoots"/>,
    /// e.g. Books, Movies, TV), and never looks inside Tuvima's own <c>.data</c> working folder: caches there
    /// (such as <c>.data/variants/hls</c>) are created empty on purpose and other services expect them to stay.
    /// </summary>
    internal static int PruneEmptyFolders(
        string libraryRoot, IReadOnlySet<string> libraryRoots, ILogger logger, CancellationToken ct)
    {
        int cleaned = 0;

        // Bottom-up: process deepest directories first by sorting by depth descending.
        var allDirs = Directory.EnumerateDirectories(libraryRoot)
            .Where(dir => !string.Equals(Path.GetFileName(dir), DataFolderName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(dir => Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories).Prepend(dir))
            .OrderByDescending(d => d.Count(c => c is '\\' or '/'))
            .ToList();

        foreach (var dir in allDirs)
        {
            ct.ThrowIfCancellationRequested();

            // A library's own folder stays even when empty; start-up would only create it again.
            if (libraryRoots.Contains(NormalizeFolderPath(dir)))
            {
                continue;
            }

            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                    cleaned++;
                    logger.LogDebug("Pruned empty folder: {Path}", dir);
                }
            }
            catch (IOException ex)
            {
                logger.LogDebug(ex, "Could not prune folder: {Path}", dir);
            }
        }

        // Individual pass logging removed — the orchestrator logs a single
        // consolidated FolderCleaned entry after all passes complete.

        return cleaned;
    }

    /// <summary>
    /// The folders of every configured library source in <c>config/libraries.json</c> — the same list start-up
    /// creates — so the empty-folder pass protects exactly the library folders that exist today.
    /// </summary>
    internal static IReadOnlySet<string> ConfiguredLibraryRoots(LibrariesConfiguration libraries) =>
        libraries.Libraries
            .SelectMany(library => library.Sources)
            .Select(source => source.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeFolderPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string NormalizeFolderPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    // ── Pass 2: Orphaned People Cleanup ─────────────────────────────────────

    /// <summary>
    /// Scans canonical .data/assets/people subfolders. Folder names are person IDs;
    /// folders without matching people, or people with no durable graph references,
    /// are deleted. Group members and other relationship-only people are retained.
    /// </summary>
    private async Task<int> CleanOrphanedPeopleAsync(string libraryRoot, CancellationToken ct)
    {
        _ = libraryRoot;
        int cleaned = 0;

        if (!Directory.Exists(_assetPaths.PeopleRoot))
        {
            return 0;
        }

        foreach (var subDir in Directory.GetDirectories(_assetPaths.PeopleRoot))
        {
            ct.ThrowIfCancellationRequested();

            var folderName = Path.GetFileName(subDir);
            if (!Guid.TryParse(folderName, out var personId))
            {
                continue;
            }

            var person = await _personRepo.FindByIdAsync(personId, ct);
            if (person is null)
            {
                SafeDeleteDirectory(subDir);
                cleaned++;
                _logger.LogDebug("Removed orphan person asset folder (no DB record): {Path}", subDir);
                continue;
            }

            var referenceCount = await _personRepo.CountGraphReferencesAsync(person.Id, ct);
            if (referenceCount == 0)
            {
                await _personRepo.DeleteAsync(person.Id, ct);
                SafeDeleteDirectory(subDir);
                cleaned++;
                _logger.LogDebug("Removed orphan person asset folder (no graph references): {Name}", person.Name);
            }
        }

        return cleaned;
    }

    // ── Pass 4: Stale Category-Root Sidecar Cleanup ─────────────────────────

    /// <summary>
    /// Deletes <c>library.xml</c> files found directly inside category root folders
    /// (e.g., <c>{LibraryRoot}/Books/library.xml</c>).
    ///
    /// These stray collection sidecars are created by <c>AutoOrganizeService</c> when a
    /// shallow organization template (such as <c>{Category}/{Author}/{Title}{Ext}</c>)
    /// causes <c>GetDirectoryName(editionFolder)</c> to resolve to the category root
    /// rather than a dedicated collection subfolder.  The guard added to AutoOrganizeService
    /// prevents new strays from being created; this pass cleans up any that exist from
    /// prior runs.
    ///
    /// Only examines direct children of <see cref="LibraryRoot"/> that are non-hidden
    /// directories (i.e., actual category folders).
    /// </summary>
    private int CleanStaleRootSidecars(string libraryRoot)
    {
        int cleaned = 0;

        try
        {
            foreach (var categoryDir in Directory.EnumerateDirectories(libraryRoot))
            {
                var folderName = Path.GetFileName(categoryDir);
                // Skip hidden folders owned by the engine.
                if (string.IsNullOrEmpty(folderName) || folderName.StartsWith(".", StringComparison.Ordinal))
                {
                    continue;
                }

                var staleXml = Path.Combine(categoryDir, "library.xml");
                if (!File.Exists(staleXml))
                {
                    continue;
                }

                SafeDeleteFile(staleXml);
                cleaned++;
                _logger.LogInformation(
                    "Removed stale category-root sidecar: {Path}", staleXml);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Error during stale root sidecar cleanup");
        }

        return cleaned;
    }

    // ── Shared Helpers ──────────────────────────────────────────────────────

    private void SafeDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Best-effort reconciliation cleanup could not delete file {Path}", path);
        }
    }

    private void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Best-effort reconciliation cleanup could not delete empty directory {Path}", path);
        }
    }

    private void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Best-effort reconciliation cleanup could not delete directory {Path}", path);
        }
    }

}

/// <summary>
/// Result of a library reconciliation scan.
/// </summary>
public sealed record ReconciliationResult(
    int TotalScanned,
    int MissingCount,
    long ElapsedMs,
    int DuplicateReadWorksMerged,
    int AudiobookAuthorsAligned);
