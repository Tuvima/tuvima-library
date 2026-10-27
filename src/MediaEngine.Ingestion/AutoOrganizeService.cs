using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Organise;
using MediaEngine.Ingestion.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaEngine.Ingestion;

/// <summary>
/// Promotes a media asset from the Watch Folder (or staging, if present) into the
/// organised library after Stage 1 retail match has produced a resolved title.
/// A Wikidata QID is NOT required — files are organised as soon as they have a
/// usable title. Wikidata enrichment continues in-place after promotion.
///
/// After promotion: moves companion artwork files and publishes the completion event.
/// </summary>
public sealed partial class AutoOrganizeService : IAutoOrganizeService
{
    private readonly IMediaAssetRepository _assetRepo;
    private readonly ICanonicalValueRepository _canonicalRepo;
    private readonly IFileOrganizer _organizer;
    private readonly ISystemActivityRepository _activityRepo;
    private readonly IReviewQueueRepository _reviewRepo;
    private readonly IEventPublisher _publisher;
    private readonly IOrganizationGate _gate;
    private readonly IngestionOptions _options;
    private readonly IEntityAssetRepository? _entityAssetRepo;
    private readonly IWorkRepository? _workRepo;
    private readonly IMediaAssetCoverageRepository? _coverageRepo;
    private readonly AssetPathService? _assetPathService;
    private readonly ILibraryFolderResolver? _libraryResolver;
    private readonly ISourceMutationPolicyGate _sourceMutationGate;
    private readonly IImportFolderService? _importFolders;
    private readonly ImportFileMover? _importMover;
    private readonly ImportFilingJournal? _importJournal;
    private readonly SidecarMover? _sidecars;
    private readonly IFilingQueue? _filingQueue;
    private readonly IFilingHold _filingHold;

    // Filing out of the Import folder runs one file at a time, so two passes (the pipeline and the sweep) can't file the
    // same file twice or race for the same unique name.
    private readonly SemaphoreSlim _importFilingGate = new(1, 1);
    private readonly ILogger<AutoOrganizeService> _logger;

    public AutoOrganizeService(
        IMediaAssetRepository assetRepo,
        ICanonicalValueRepository canonicalRepo,
        IFileOrganizer organizer,
        ISystemActivityRepository activityRepo,
        IReviewQueueRepository reviewRepo,
        IEventPublisher publisher,
        IOrganizationGate gate,
        IOptions<IngestionOptions> options,
        ILogger<AutoOrganizeService> logger,
        IEntityAssetRepository? entityAssetRepo = null,
        IWorkRepository? workRepo = null,
        AssetPathService? assetPathService = null,
        ILibraryFolderResolver? libraryResolver = null,
        ISourceMutationPolicyGate? sourceMutationGate = null,
        IMediaAssetCoverageRepository? coverageRepo = null,
        IImportFolderService? importFolders = null,
        ImportFileMover? importMover = null,
        ImportFilingJournal? importJournal = null,
        SidecarMover? sidecars = null,
        IFilingQueue? filingQueue = null,
        IFilingHold? filingHold = null)
    {
        _filingQueue = filingQueue;
        _filingHold = filingHold ?? new NoFilingHold();
        _sidecars = sidecars;
        _importJournal = importJournal;
        _importFolders = importFolders;
        _importMover = importMover;
        _coverageRepo = coverageRepo;
        _assetRepo = assetRepo;
        _canonicalRepo = canonicalRepo;
        _organizer = organizer;
        _activityRepo = activityRepo;
        _reviewRepo = reviewRepo;
        _publisher = publisher;
        _gate = gate;
        _options = options.Value;
        _entityAssetRepo = entityAssetRepo;
        _workRepo = workRepo;
        _assetPathService = assetPathService;
        _libraryResolver = libraryResolver;
        _sourceMutationGate = sourceMutationGate ?? new SourceMutationPolicyGate();
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task TryAutoOrganizeAsync(Guid assetId, CancellationToken ct = default)
    {
        var asset = await _assetRepo.FindByIdAsync(assetId, ct).ConfigureAwait(false);
        if (asset is null)
        {
            _logger.LogDebug("Auto-organize skipped: asset {Id} not found", assetId);
            return;
        }

        if (!File.Exists(asset.FilePathRoot))
        {
            _logger.LogWarning(
                "Auto-organize skipped for {Id}: file missing at {Path}",
                assetId, asset.FilePathRoot);
            return;
        }

        // A file in the Import folder is filed into its type's one managed folder, or left exactly where it is.
        if (_importFolders is not null && _importMover is not null && _importFolders.IsInImportFolder(asset.FilePathRoot))
        {
            await _importFilingGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Another pass may have filed it while this one waited.
                var current = await _assetRepo.FindByIdAsync(assetId, ct).ConfigureAwait(false);
                if (current is not null
                    && File.Exists(current.FilePathRoot)
                    && _importFolders.IsInImportFolder(current.FilePathRoot))
                {
                    await TryFileFromImportAsync(current, assetId, immediate: false, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                _importFilingGate.Release();
            }

            return;
        }

        // ── Per-library ReadOnly gate ──────────────────────────────────────
        // Side-by-side-with-Plex plan §C/§I. When the file belongs to a
        // library marked ReadOnly (the user's "Plex owns this tree" opt-out),
        // we never move, rename, or tag it — we index in place and return.
        var owningLibrary = _libraryResolver?.ResolveForPath(asset.FilePathRoot)
            ?? (asset.LibraryId is { Length: > 0 }
                ? _libraryResolver?.ResolveById(asset.LibraryId)
                : null);
        var libraryRoot = !string.IsNullOrWhiteSpace(owningLibrary?.LibraryRoot)
            ? owningLibrary.LibraryRoot
            : _options.LibraryRoot;
        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            _logger.LogDebug(
                "Auto-organize skipped for {Id}: no destination root is configured", assetId);
            return;
        }

        if (owningLibrary is { ReadOnly: true })
        {
            _logger.LogDebug(
                "Auto-organize skipped for {Id}: owning library is ReadOnly ({Path})",
                assetId, asset.FilePathRoot);
            return;
        }

        var canonicals = await _canonicalRepo.GetByEntityAsync(assetId, ct)
            .ConfigureAwait(false);
        if (canonicals.Count == 0)
        {
            _logger.LogDebug(
                "Auto-organize skipped for {Id}: no canonical values", assetId);
            return;
        }

        var metadata = canonicals.ToDictionary(
            c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase);

        var mediaType = metadata.TryGetValue("media_type", out var mtStr)
            && Enum.TryParse<MediaType>(mtStr, ignoreCase: true, out var mt)
                ? mt
                : (MediaType?)null;

        await ApplyRecordedEpisodeRangeAsync(assetId, metadata, mediaType, ct).ConfigureAwait(false);

        // Determine where the file currently is.
        var stagingPath = Path.Combine(libraryRoot, ".data", "staging");
        bool isInStaging = !string.IsNullOrWhiteSpace(stagingPath)
            && asset.FilePathRoot.StartsWith(
                stagingPath, StringComparison.OrdinalIgnoreCase);

        // A file that already lives in one of the library's folders is only ever tidied inside that same folder. It is
        // never moved into the New media folder or into any other folder, so a managed folder that is not marked New
        // media keeps its files (it just receives nothing new), and an as-is folder is left exactly as it is. Only a file
        // still waiting in staging is promoted into the New media folder.
        var owningSource = isInStaging ? null : _libraryResolver?.ResolveSourceForPath(asset.FilePathRoot);
        if (owningSource is not null)
        {
            if (!owningSource.Source.AllowsFileMutation)
            {
                _logger.LogDebug(
                    "Auto-organize skipped for {Id}: its folder is used as-is ({Path})",
                    assetId, asset.FilePathRoot);
                return;
            }

            await HandleAlreadyOrganizedAsync(
                asset, assetId, metadata, mediaType, owningSource.Source.Path, owningSource.Source.Path,
                owningSource.Library, ct)
                .ConfigureAwait(false);
            return;
        }

        bool alreadyOrganized = !isInStaging
            && asset.FilePathRoot.StartsWith(
                libraryRoot, StringComparison.OrdinalIgnoreCase);

        if (alreadyOrganized)
        {
            await HandleAlreadyOrganizedAsync(
                asset, assetId, metadata, mediaType,
                LibraryOrganizationPath.TypeFolder(owningLibrary, libraryRoot, mediaType), libraryRoot, owningLibrary, ct)
                .ConfigureAwait(false);
            return;
        }

        // ── Blocking review check ──────────────────────────────────────────
        // ANY pending review item blocks promotion. The file stays in staging
        // until the user resolves all review items.
        var pendingReviews = await _reviewRepo.GetPendingByEntityAsync(assetId, ct)
            .ConfigureAwait(false);

        var blockingReviews = pendingReviews
            .Where(r => r.ReviewReadyAt is not null
                        && !string.Equals(r.Trigger, nameof(ReviewTrigger.WritebackFailed), StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (blockingReviews.Any())
        {
            _logger.LogInformation(
                "Auto-organize blocked for {Id}: pending review '{Trigger}' must be resolved first",
                assetId, blockingReviews[0].Trigger);
            return;
        }

        // ── Title gate — a resolved title (from retail match) is sufficient to organize ──
        // QID is no longer required at this stage. Files are moved from the watch
        // folder into the library as soon as Stage 1 produces a title. Wikidata
        // enrichment continues in-place after promotion.
        var title = metadata.GetValueOrDefault("title");
        if (string.IsNullOrWhiteSpace(title) || title is "Untitled" or "Unknown")
        {
            _logger.LogInformation(
                "Auto-organize blocked for {Id}: no resolved title — file stays in watch folder",
                assetId);
            return;
        }

        // ── Promote from staging to library ──────────────────────────────

        var synth = new IngestionCandidate
        {
            Path = asset.FilePathRoot,
            EventType = FileEventType.Created,
            DetectedAt = DateTimeOffset.UtcNow,
            ReadyAt = DateTimeOffset.UtcNow,
            Metadata = metadata,
            DetectedMediaType = mediaType,
        };

        var template = _options.ResolveTemplate(mediaType?.ToString());
        var relative = LibraryOrganizationPath.Calculate(_organizer, synth, owningLibrary, template);

        // Evaluate all promotion guards through the centralized gate.
        // AutoOrganizeService runs post-hydration so media type is always resolved;
        // pass mediaTypeNeedsReview: false.
        var gateResult = _gate.Evaluate(
            overallConfidence: 1.0, // post-hydration: confidence gate already passed at ingestion time
            canonicalValues: metadata,
            hasUserLock: false,     // user-lock check is not needed here — gate is used for path/title guards only
            mediaTypeNeedsReview: false,
            filingCategory: LibraryOrganizationPath.FilingCategory(owningLibrary, mediaType));

        if (!gateResult.CanOrganize)
        {
            _logger.LogDebug(
                "Auto-organize blocked for {Id}: {Reason}", assetId, gateResult.BlockReason);
            return;
        }

        var destPath = Path.Combine(LibraryOrganizationPath.TypeFolder(owningLibrary, libraryRoot, mediaType), relative);
        var stagingFolder = Path.GetDirectoryName(asset.FilePathRoot) ?? string.Empty;

        if (!CanMoveBetween(asset.FilePathRoot, destPath))
        {
            _logger.LogInformation(
                "Auto-organize blocked by source policy for {Id}: {Source} → {Dest}",
                assetId, asset.FilePathRoot, destPath);
            return;
        }

        // The name actually used can differ from the one asked for (a taken name gets a unique suffix); the data store and the
        // files that travel with it follow the real name.
        var promotedPath = await _organizer.ExecuteMoveAsync(asset.FilePathRoot, destPath, ct)
            .ConfigureAwait(false);

        if (promotedPath is null)
        {
            _logger.LogWarning(
                "Auto-organize move failed for {Id}: {Source} → {Dest}",
                assetId, asset.FilePathRoot, destPath);
            return;
        }

        await _assetRepo.UpdateFilePathAsync(assetId, promotedPath, ct).ConfigureAwait(false);
        await MarkPresentedAsync(assetId, ct).ConfigureAwait(false);

        string sourcePath = asset.FilePathRoot;

        // Move companion artwork and sidecars from staging to the library edition folder.
        // CoverArtWorker writes poster.jpg next to the media file (even in staging) via
        // AssetPathService.GetMediaFilePosterPath, so companion files must always be moved
        // on promotion. Nothing is overwritten or removed: a name that is already taken stays where it is.
        var keptBehind = MoveSidecarFiles(sourcePath, promotedPath);
        await LogKeptBehindAsync(assetId, mediaType, keptBehind, ct).ConfigureAwait(false);

        // Clean up any .tuvima.bak files left behind by metadata taggers.
        // These backups are normally deleted on success, but can persist when the
        // tagger fails partway through (e.g. M4B cover art write on a locked file).
        CleanStagingBakFiles(stagingFolder);

        // Clean empty staging subdirectories left behind.
        if (!string.IsNullOrWhiteSpace(stagingPath))
        {
            CleanEmptyParents(stagingFolder, stagingPath);
        }

        _logger.LogInformation(
            "Promoted asset {Id} from staging to library: {Source} → {Dest}",
            assetId, asset.FilePathRoot, promotedPath);

        // History: promoted to library.
        try
        {
            await _activityRepo.LogAsync(new SystemActivityEntry
            {
                ActionType = "Promoted",
                EntityId = assetId,
                Detail = "Promoted to library",
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to log item history (Promoted) for {Id}", assetId); }

        try
        {
            await _activityRepo.LogAsync(new SystemActivityEntry
            {
                ActionType = SystemActionType.PathUpdated,
                EntityId = assetId,
                EntityType = "MediaAsset",
                CollectionName = metadata.GetValueOrDefault("title", "Unknown"),
                Detail = $"Promoted from staging: {Path.GetFileName(asset.FilePathRoot)} → {Path.GetRelativePath(libraryRoot, promotedPath)}",
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Activity log failed for auto-organize — continuing");
        }

        // All pending review items were cleared before reaching this point
        // (any pending review blocks promotion). Resolve any that may have
        // been created concurrently during promotion.
        try
        {
            var resolved = await _reviewRepo.ResolveAllByEntityAsync(assetId, "system:auto-organize", ct)
                .ConfigureAwait(false);
            if (resolved > 0)
            {
                _logger.LogInformation(
                    "Auto-resolved {Count} review items for {Id} after successful promotion", resolved, assetId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Review auto-resolve failed for {Id} — continuing", assetId);
        }

        try
        {
            await _publisher.PublishAsync(
                SignalREvents.IngestionCompleted,
                new AutoOrganizedIngestionCompletedEvent(
                    promotedPath,
                    mediaType?.ToString() ?? "Unknown",
                    DateTimeOffset.UtcNow),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Event publish failed for auto-organize — continuing");
        }
    }

    // -------------------------------------------------------------------------
    // Import folder
    // -------------------------------------------------------------------------

    /// <summary>
    /// Files one asset out of the Import folder. The file goes to exactly one place: the managed folder marked New media for
    /// its type, named with that type's preset. When it can't go there (a review is still open, the type has no managed
    /// folder, or the folder is offline, full or unwritable) it is left untouched in the Import folder and the next pass,
    /// the periodic sweep or a manual folder check, tries again. Nothing is ever filed into another folder.
    /// </summary>
    /// <remarks>
    /// With a filing queue the move is not made here: once every check has passed the file is recorded as waiting for the next run
    /// and stays where it is (Library setup D62). The run asks again with <paramref name="immediate"/> set, which makes the move.
    /// </remarks>
    private async Task<AssetRefileOutcome> TryFileFromImportAsync(
        Domain.Aggregates.MediaAsset asset,
        Guid assetId,
        bool immediate,
        CancellationToken ct)
    {
        var canonicals = await _canonicalRepo.GetByEntityAsync(assetId, ct).ConfigureAwait(false);
        if (canonicals.Count == 0)
        {
            _logger.LogDebug("Import filing skipped for {Id}: no canonical values yet", assetId);
            return KeptInPlace(asset, "Nothing is known about this file yet.");
        }

        var metadata = canonicals.ToDictionary(
            c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase);
        if (!metadata.TryGetValue("media_type", out var mediaTypeText)
            || !Enum.TryParse<MediaType>(mediaTypeText, ignoreCase: true, out var mediaType)
            || mediaType == MediaType.Unknown)
        {
            _logger.LogDebug("Import filing waits for {Id}: its media type is not settled", assetId);
            return KeptInPlace(asset, "Its type is not settled yet.");
        }

        var pendingReviews = await _reviewRepo.GetPendingByEntityAsync(assetId, ct).ConfigureAwait(false);
        var blocking = pendingReviews.FirstOrDefault(review =>
            review.ReviewReadyAt is not null
            && !string.Equals(review.Trigger, nameof(ReviewTrigger.WritebackFailed), StringComparison.OrdinalIgnoreCase));
        if (blocking is not null)
        {
            _logger.LogDebug(
                "Import filing blocked for {Id}: review '{Trigger}' must be resolved first", assetId, blocking.Trigger);
            return KeptInPlace(asset, "A review must be resolved first.");
        }

        var title = metadata.GetValueOrDefault("title");
        if (string.IsNullOrWhiteSpace(title) || title is "Untitled" or "Unknown")
        {
            _logger.LogDebug("Import filing blocked for {Id}: no resolved title", assetId);
            return KeptInPlace(asset, "It has no resolved title yet.");
        }

        await ApplyRecordedEpisodeRangeAsync(assetId, metadata, mediaType, ct).ConfigureAwait(false);

        var fileSize = new FileInfo(asset.FilePathRoot).Length;
        var destination = _importFolders!.ResolveDestination(mediaType, fileSize);
        if (!destination.IsReady)
        {
            _logger.LogDebug(
                "Import filing held for {Id}: {State} for {Type}. {Reason}",
                assetId, destination.State, destination.TypeLabel, destination.Reason);
            return KeptInPlace(asset, destination.Reason ?? "Its folder is not ready.");
        }

        var synth = new IngestionCandidate
        {
            Path = asset.FilePathRoot,
            EventType = FileEventType.Created,
            DetectedAt = DateTimeOffset.UtcNow,
            ReadyAt = DateTimeOffset.UtcNow,
            Metadata = metadata,
            DetectedMediaType = mediaType,
        };

        var relative = LibraryOrganizationPath.Calculate(
            _organizer, synth, destination.Library, _options.ResolveTemplate(mediaType.ToString()));
        var gateResult = _gate.Evaluate(
            overallConfidence: 1.0,
            canonicalValues: metadata,
            hasUserLock: false,
            mediaTypeNeedsReview: false,
            filingCategory: LibraryOrganizationPath.FilingCategory(destination.Library, mediaType));
        if (!gateResult.CanOrganize)
        {
            _logger.LogDebug("Import filing blocked for {Id}: {Reason}", assetId, gateResult.BlockReason);
            return KeptInPlace(asset, "The file has no proper name to take yet.");
        }

        var destPath = Path.GetFullPath(Path.Combine(destination.Source!.Path, relative));
        var sourcePath = asset.FilePathRoot;
        if (!CanMoveBetween(sourcePath, destPath, destination))
        {
            _logger.LogInformation(
                "Import filing blocked by source policy for {Id}: {Source} -> {Dest}", assetId, sourcePath, destPath);
            return KeptInPlace(asset, "The file is protected, so it is not touched.");
        }

        if (!immediate && _filingQueue is not null)
        {
            // Every check has passed; the move itself waits for the next run with the tags, and the file stays where it is, playable.
            await QueueFilingAsync(assetId, sourcePath, destination.Library!.Id, destination.Source!.Id, ct).ConfigureAwait(false);
            return KeptInPlace(asset, "Waiting to be filed at the next run.");
        }

        // The intent is written first and removed only once the data store points at the new location, so a crash between
        // the move and the update is finished on the next start (see ImportFolderFiler.RecoverInterruptedFilingsAsync).
        if (_importJournal is not null
            && !_importJournal.TryBegin(new ImportFilingIntent(
                assetId, sourcePath, destPath, asset.ContentHash, destination.Library!.Id, DateTimeOffset.UtcNow)))
        {
            return KeptInPlace(asset, "Another pass is already filing it.");
        }

        var finalPath = await _importMover!.MoveAsync(sourcePath, destPath, asset.ContentHash, adoptIdenticalCopy: true, ct: ct).ConfigureAwait(false);
        if (finalPath is null)
        {
            _importJournal?.Complete(assetId);
            _logger.LogWarning("Import filing did not move {Id}; it stays in the Import folder: {Source}", assetId, sourcePath);
            return new AssetRefileOutcome(
                AssetRefileOutcomeKind.Failed, AssetRefileDisposition.Moves, sourcePath,
                "The file could not be moved out of the Import folder (it may be in use). It will be tried again.");
        }

        await _assetRepo.UpdateFilePathAsync(assetId, finalPath, ct).ConfigureAwait(false);
        if (!string.Equals(asset.LibraryId, destination.Library!.Id, StringComparison.OrdinalIgnoreCase))
        {
            await _assetRepo.SetLibraryIdAsync(assetId, destination.Library.Id, ct).ConfigureAwait(false);
        }

        _importJournal?.Complete(assetId);

        await MarkPresentedAsync(assetId, ct).ConfigureAwait(false);

        var keptBehind = MoveSidecarFiles(sourcePath, finalPath, destination);
        await LogKeptBehindAsync(assetId, mediaType, keptBehind, ct).ConfigureAwait(false);
        var sourceFolder = Path.GetDirectoryName(sourcePath) ?? string.Empty;
        CleanStagingBakFiles(sourceFolder);
        if (CanMutate(sourceFolder, SourceMutationKind.Delete, allowDelete: true))
        {
            _importFolders.RemoveEmptyFolders(sourceFolder);
        }

        _logger.LogInformation("Filed asset {Id} from the Import folder: {Source} -> {Dest}", assetId, sourcePath, finalPath);

        try
        {
            await _activityRepo.LogAsync(new SystemActivityEntry
            {
                ActionType = SystemActionType.PathUpdated,
                EntityId = assetId,
                EntityType = "MediaAsset",
                CollectionName = title,
                Detail = $"Filed from the Import folder: {Path.GetFileName(sourcePath)} -> {Path.GetRelativePath(destination.Source.Path, finalPath)}",
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Activity log failed for import filing of {Id}; continuing", assetId);
        }

        try
        {
            await _reviewRepo.ResolveAllByEntityAsync(assetId, "system:auto-organize", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Review auto-resolve failed for {Id} after import filing; continuing", assetId);
        }

        try
        {
            await _publisher.PublishAsync(
                SignalREvents.IngestionCompleted,
                new AutoOrganizedIngestionCompletedEvent(finalPath, mediaType.ToString(), DateTimeOffset.UtcNow),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Event publish failed for import filing of {Id}; continuing", assetId);
        }

        return new AssetRefileOutcome(AssetRefileOutcomeKind.Moved, AssetRefileDisposition.Moves, finalPath, null);
    }

    private static AssetRefileOutcome KeptInPlace(Domain.Aggregates.MediaAsset asset, string reason) =>
        new(AssetRefileOutcomeKind.Kept, AssetRefileDisposition.Unavailable, asset.FilePathRoot, reason);

    /// <summary>
    /// Records that the file is to be filed on the next run, or held when its newly added Managed folder has not passed its first
    /// check (D61). The file is not touched and stays playable where it is; it is presented now, as it would have been once filed.
    /// </summary>
    private async Task QueueFilingAsync(Guid assetId, string path, string libraryId, string sourceId, CancellationToken ct)
    {
        var held = _filingHold.IsHeld(libraryId, sourceId, path);
        var recorded = await _filingQueue!.RequestFilingAsync(assetId, held, ct).ConfigureAwait(false);
        await MarkPresentedAsync(assetId, ct).ConfigureAwait(false);
        if (recorded)
        {
            _logger.LogInformation(
                "Filing for asset {Id} is {State}: {Path} stays where it is until the next run", assetId, held ? "held" : "waiting", path);
        }
        else
        {
            // Asked again by a sweep while it already waits (or a person's own move is asked for): nothing changes.
            _logger.LogDebug(
                "Filing for asset {Id} was not queued again: it is already waiting, or a person's own move is asked for: {Path}", assetId, path);
        }
    }

    // -------------------------------------------------------------------------
    // Already-organized path (QID updated after hydration)
    // -------------------------------------------------------------------------

    /// <summary>
    /// A combined TV file is named from the episodes recorded for it (filename match or a person's
    /// choice in the editor), never from the filename claim alone: a file waiting in Review, or one
    /// whose picker changed, must not be named for a range nobody confirmed. Only an unbroken run of
    /// recorded episodes becomes a range; anything else is named as a single episode.
    /// </summary>
    internal async Task ApplyRecordedEpisodeRangeAsync(
        Guid assetId, Dictionary<string, string> metadata, MediaType? mediaType, CancellationToken ct)
    {
        if (mediaType != MediaType.TV)
        {
            return;
        }

        metadata.Remove(MetadataFieldConstants.EpisodeEnd);
        if (_coverageRepo is null)
        {
            return;
        }

        var numbers = await _coverageRepo.ListCoveredEpisodeNumbersAsync(assetId, ct).ConfigureAwait(false);
        if (!EpisodeRangeParser.TryGetUnbrokenRun(numbers, out var first, out var last))
        {
            return;
        }

        metadata["episode"] = first.ToString(System.Globalization.CultureInfo.InvariantCulture);
        metadata[MetadataFieldConstants.EpisodeEnd] = last.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Tidies a file to its standard name. The template is joined onto <paramref name="typeFolder"/> (the type's own
    /// folder), and the new name must stay inside <paramref name="libraryRoot"/>, the folder the file already lives in.
    /// </summary>
    private async Task HandleAlreadyOrganizedAsync(
        Domain.Aggregates.MediaAsset asset, Guid assetId,
        Dictionary<string, string> metadata, MediaType? mediaType,
        string typeFolder,
        string libraryRoot,
        LibraryFolderEntry? library,
        CancellationToken ct)
    {
        var checkSynth = new IngestionCandidate
        {
            Path = asset.FilePathRoot,
            EventType = FileEventType.Created,
            DetectedAt = DateTimeOffset.UtcNow,
            ReadyAt = DateTimeOffset.UtcNow,
            Metadata = metadata,
            DetectedMediaType = mediaType,
        };
        var checkTemplate = _options.ResolveTemplate(mediaType?.ToString());
        var checkLibrary = library ?? _libraryResolver?.ResolveById(asset.LibraryId ?? "");
        var checkRelative = LibraryOrganizationPath.Calculate(_organizer, checkSynth, checkLibrary, checkTemplate);
        var newDest = Path.GetFullPath(Path.Combine(typeFolder, checkRelative));

        if (string.Equals(asset.FilePathRoot, newDest, StringComparison.OrdinalIgnoreCase))
        {
            await MarkPresentedAsync(assetId, ct).ConfigureAwait(false);

            // Path unchanged — nothing to do.
            _logger.LogDebug(
                "Already organized at {Path} for {Id}",
                asset.FilePathRoot, assetId);
            return;
        }

        // A file still waiting for a person to confirm what it is is not renamed in place: the name would come from an
        // unconfirmed match. (A name clash or a merge arrival is not a question about identity and does not hold it back.)
        var blockingReview = await FindBlockingReviewAsync(assetId, ct).ConfigureAwait(false);
        if (blockingReview is not null)
        {
            _logger.LogDebug(
                "Re-organization held for {Id}: review '{Trigger}' must be resolved first", assetId, blockingReview.Trigger);
            return;
        }

        // Path changed (QID now available) — move file to new location.
        var oldPath = asset.FilePathRoot;

        // The new name must stay inside the folder the file already lives in. Anything that would leave it is refused,
        // so tidying can never carry a file across folders or drives.
        if (!StaysInsideRoot(oldPath, newDest, libraryRoot))
        {
            _logger.LogWarning(
                "Re-organization refused for {Id}: {Dest} is outside the folder {Root} the file lives in",
                assetId, newDest, libraryRoot);
            return;
        }

        var gateResult = _gate.Evaluate(
            overallConfidence: 1.0,
            canonicalValues: metadata,
            hasUserLock: false,
            mediaTypeNeedsReview: false,
            filingCategory: LibraryOrganizationPath.FilingCategory(checkLibrary, mediaType));
        if (!gateResult.CanOrganize)
        {
            _logger.LogDebug(
                "Re-organization blocked for {Id}: {Reason}", assetId, gateResult.BlockReason);
            return;
        }

        if (!CanMoveBetween(asset.FilePathRoot, newDest))
        {
            _logger.LogInformation(
                "Re-organization blocked by source policy for {Id}: {Source} → {Dest}",
                assetId, asset.FilePathRoot, newDest);
            return;
        }

        // Tidying never renames one file onto the name another file already holds (two editions can share a standard name):
        // the file stays as it is rather than being given a "(2)" name that the next pass would change again.
        if (File.Exists(newDest))
        {
            _logger.LogInformation(
                "Re-organization skipped for {Id}: {Dest} is already taken by another file, so {Source} stays where it is",
                assetId, newDest, oldPath);
            return;
        }

        if (_filingQueue is not null)
        {
            // Library's own filing waits for the next run with the tags; the file stays where it is, playable (Library setup D62, D63).
            var resolved = _libraryResolver?.ResolveSourceForPath(oldPath);
            await QueueFilingAsync(
                assetId, oldPath, resolved?.Library.Id ?? library?.Id ?? asset.LibraryId ?? string.Empty, resolved?.Source.Id ?? string.Empty, ct)
                .ConfigureAwait(false);
            return;
        }

        await MoveFiledAssetAsync(asset, assetId, mediaType, newDest, "after hydration", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Renames and moves an already-filed asset's file to <paramref name="newDest"/> (every check has been made by the caller):
    /// the file moves without overwriting anything, then the stored path, the files that travel with it and the folders it left
    /// follow the path the file really ended up at. Returns that path, or null when the file did not move.
    /// </summary>
    private async Task<string?> MoveFiledAssetAsync(
        Domain.Aggregates.MediaAsset asset, Guid assetId, MediaType? mediaType, string newDest, string reason, CancellationToken ct,
        IRefileStepRecorder? steps = null)
    {
        var oldPath = asset.FilePathRoot;
        long sizeBefore = -1;
        if (steps is not null)
        {
            // A file that cannot be seen (a drive that went away) is not measured; the move below then does not happen and the
            // recorded plan is looked at again when the drive is back.
            if (File.Exists(oldPath))
            {
                sizeBefore = new FileInfo(oldPath).Length;
            }

            // The plan named a free name; if something took it since, the move is dropped rather than renamed to "(2)", which the
            // recorded plan would not know about after a stop.
            if (!string.Equals(Path.GetFullPath(oldPath), Path.GetFullPath(newDest), StringComparison.OrdinalIgnoreCase)
                && File.Exists(newDest))
            {
                _logger.LogInformation("Library move: {Dest} for {Id} was taken after the move was planned, so the move is tried again later", newDest, assetId);
                await steps.NotMovedAsync(ct).ConfigureAwait(false);
                return null;
            }
        }

        var relocatedPath = await _organizer.ExecuteMoveAsync(oldPath, newDest, ct)
            .ConfigureAwait(false);

        if (relocatedPath is null)
        {
            return null;
        }

        // The file is at its new place and the old one is empty: written down before the data store follows.
        if (steps is not null)
        {
            await steps.MovedAsync(relocatedPath, ct).ConfigureAwait(false);
        }

        await FinishMovedAssetAsync(assetId, mediaType, oldPath, relocatedPath, reason, ct).ConfigureAwait(false);

        if (steps is not null)
        {
            // Checked: the data store holds the path the file is really at, and the file is there with the size it had.
            var after = await _assetRepo.FindByIdAsync(assetId, ct).ConfigureAwait(false);
            var samePlace = string.Equals(oldPath, relocatedPath, StringComparison.OrdinalIgnoreCase);
            if (after is null
                || !string.Equals(after.FilePathRoot, relocatedPath, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(relocatedPath)
                || new FileInfo(relocatedPath).Length != sizeBefore
                || (!samePlace && File.Exists(oldPath)))
            {
                _logger.LogWarning("Library move: {Dest} for {Id} could not be checked after the move", relocatedPath, assetId);
                return null;
            }

            await steps.CheckedAsync(ct).ConfigureAwait(false);
        }

        return relocatedPath;
    }

    /// <summary>
    /// What follows a file reaching its new place: the stored path, the files that travel with it, the folders it left and the note in
    /// the activity history. Safe to repeat, so a move that was cut short can be finished from what is on disk.
    /// </summary>
    private async Task FinishMovedAssetAsync(
        Guid assetId, MediaType? mediaType, string oldPath, string relocatedPath, string reason, CancellationToken ct)
    {
        var oldFolder = Path.GetDirectoryName(oldPath) ?? string.Empty;

        // The data store and the files that travel with it follow the path the file really ended up at.
        await _assetRepo.UpdateFilePathAsync(assetId, relocatedPath, ct).ConfigureAwait(false);
        await MarkPresentedAsync(assetId, ct).ConfigureAwait(false);

        var keptBehind = MoveSidecarFiles(oldPath, relocatedPath);
        await LogKeptBehindAsync(assetId, mediaType, keptBehind, ct).ConfigureAwait(false);

        var sourceRoot = _libraryResolver?.ResolveSourceForPath(oldPath)?.Source.Path;
        CleanEmptyParents(oldFolder, sourceRoot ?? oldFolder);

        _logger.LogInformation(
            "Re-organized asset {Id} {Reason}: {Old} → {New}",
            assetId, reason, oldPath, relocatedPath);

        try
        {
            await _activityRepo.LogAsync(new SystemActivityEntry
            {
                ActionType = SystemActionType.PathUpdated,
                EntityId = assetId,
                EntityType = mediaType?.ToString() ?? "Unknown",
                Detail = $"Re-organized {reason}: {Path.GetFileName(relocatedPath)}",
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to log re-organization activity for {Id}", assetId);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private async Task MarkPresentedAsync(Guid assetId, CancellationToken ct)
    {
        // "Added" is announced by the readiness service when the Work becomes Ready, not when its file is filed.
        await _assetRepo
            .MarkPresentedAsync(assetId, DateTimeOffset.UtcNow, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Subtitles, notes, artwork, cue sheets, lyrics and playlists that share the media file's name travel with it
    /// (keeping a language suffix such as <c>.en.srt</c>). The folder-wide files (cover, poster, metadata.json, metadata.opf)
    /// travel only once no other media file is left in the old folder. Poster, fanart and logo pictures take the name the new
    /// folder expects: <c>poster.jpg</c> when the media file is alone in its folder, <c>{name}-poster.jpg</c> when the folder is
    /// shared (so a picture never becomes another file's folder-wide one). Nothing is ever overwritten or deleted: a file whose
    /// new name is already taken (even by an identical file) stays where it is. Anything else in the folder is left alone.
    /// </summary>
    private IReadOnlyList<SidecarMoveResult> MoveSidecarFiles(string oldMediaPath, string newMediaPath, ImportDestination? importDestination = null)
    {
        if (_sidecars is null
            || string.IsNullOrWhiteSpace(oldMediaPath)
            || string.IsNullOrWhiteSpace(newMediaPath)
            || string.Equals(oldMediaPath, newMediaPath, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        try
        {
            var results = _sidecars.Move(
                oldMediaPath, newMediaPath, (from, to) => CanMoveBetween(from, to, importDestination), mayDelete: null, includeArtwork: true, renameArtworkToFolder: true);
            var keptBehind = results
                .Where(result => result.Outcome is FolderOrganiseOutcomes.NotMoved or FolderOrganiseOutcomes.Failed)
                .ToList();
            foreach (var result in keptBehind)
            {
                _logger.LogInformation("Left {Sidecar} where it is: {Reason}", result.From, result.Detail);
            }

            return keptBehind;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Sidecar files beside {Path} were not moved", oldMediaPath);
            return [];
        }
    }

    /// <summary>
    /// A file that stays in the old folder (its new name was taken, even by an identical copy) keeps that folder from
    /// being tidied away, so say so in History where the user can find and clear it.
    /// </summary>
    private async Task LogKeptBehindAsync(
        Guid assetId, MediaType? mediaType, IReadOnlyList<SidecarMoveResult> keptBehind, CancellationToken ct)
    {
        if (keptBehind.Count == 0)
        {
            return;
        }

        var folder = Path.GetDirectoryName(keptBehind[0].From) ?? string.Empty;
        var names = string.Join(", ", keptBehind.Select(result => Path.GetFileName(result.From)).Take(3));
        try
        {
            await _activityRepo.LogAsync(new SystemActivityEntry
            {
                ActionType = SystemActionType.PathUpdated,
                EntityId = assetId,
                EntityType = mediaType?.ToString() ?? "MediaAsset",
                Detail = $"Kept folder: {keptBehind.Count} file(s) left in place ({names}) because the same name already exists in the new folder.",
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to log kept-folder history for {Id} ({Folder})", assetId, folder);
        }
    }

    private static IEnumerable<string> EnumeratePosterCandidates(string mediaPath)
    {
        var folder = Path.GetDirectoryName(mediaPath) ?? string.Empty;
        var basename = Path.GetFileNameWithoutExtension(mediaPath);

        return new[]
        {
            AssetPathService.GetMediaFilePosterPath(mediaPath),
            Path.Combine(folder, "poster.jpg"),
            Path.Combine(folder, $"{basename}-poster.jpg"),
        };
    }

    private static IEnumerable<string> EnumeratePosterThumbCandidates(string mediaPath)
    {
        var folder = Path.GetDirectoryName(mediaPath) ?? string.Empty;
        var basename = Path.GetFileNameWithoutExtension(mediaPath);

        return new[]
        {
            AssetPathService.GetMediaFileThumbPath(mediaPath),
            Path.Combine(folder, "poster-thumb.jpg"),
            Path.Combine(folder, $"{basename}-poster-thumb.jpg"),
        };
    }

    private void CleanStagingBakFiles(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return;
        }

        try
        {
            foreach (var bakFile in Directory.EnumerateFiles(folder, "*.tuvima.bak"))
            {
                if (!CanMutate(bakFile, SourceMutationKind.Delete, allowDelete: true))
                {
                    continue;
                }

                try { File.Delete(bakFile); }
                catch { /* best-effort cleanup */ }
            }
        }
        catch { /* best-effort cleanup */ }
    }

    private void CleanEmptyParents(string folder, string stopAt)
    {
        try
        {
            var dir = new DirectoryInfo(folder);
            while (dir is not null &&
                   dir.Exists &&
                   !string.Equals(dir.FullName.TrimEnd(Path.DirectorySeparatorChar),
                       stopAt.TrimEnd(Path.DirectorySeparatorChar),
                       StringComparison.OrdinalIgnoreCase))
            {
                if (dir.EnumerateFileSystemInfos().Any())
                {
                    break;
                }

                if (!CanMutate(dir.FullName, SourceMutationKind.Delete, allowDelete: true))
                {
                    break;
                }

                var parent = dir.Parent;
                dir.Delete();
                dir = parent;
            }
        }
        catch { /* best-effort cleanup */ }
    }

    /// <summary>
    /// True when both paths sit under <paramref name="root"/> on the same drive. A tidy-up inside a folder is a plain rename
    /// there; a path that would leave the folder (or the drive) is never a tidy-up.
    /// </summary>
    public static bool StaysInsideRoot(string currentPath, string newPath, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullCurrent = Path.GetFullPath(currentPath);
        var fullNew = Path.GetFullPath(newPath);

        return fullCurrent.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
            && fullNew.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetPathRoot(fullCurrent), Path.GetPathRoot(fullNew), StringComparison.OrdinalIgnoreCase);
    }

    private bool CanMoveBetween(string sourcePath, string destinationPath, ImportDestination? importDestination = null)
    {
        if (importDestination is not null)
        {
            return CanFileFromImport(sourcePath, destinationPath, importDestination);
        }

        var mutation = string.Equals(
            Path.GetDirectoryName(sourcePath),
            Path.GetDirectoryName(destinationPath),
            StringComparison.OrdinalIgnoreCase)
                ? SourceMutationKind.Rename
                : SourceMutationKind.Move;

        // A move inside one managed folder only needs that folder to allow moving and renaming. Receiving a file from
        // somewhere else is what "destination" permission is for.
        var sameSource = _libraryResolver?.ResolveSourceForPath(sourcePath) is { } from
            && _libraryResolver.ResolveSourceForPath(destinationPath) is { } to
            && string.Equals(from.Source.Id, to.Source.Id, StringComparison.OrdinalIgnoreCase);

        return CanMutate(sourcePath, mutation)
            && CanMutate(destinationPath, sameSource ? mutation : SourceMutationKind.UseAsDestination);
    }

    /// <summary>
    /// A file leaves the Import folder only into the one managed folder marked New media for its type. Both ends pass the
    /// source-policy gate: the import folder must allow the move, and the destination must be a managed destination.
    /// </summary>
    private bool CanFileFromImport(string sourcePath, string destinationPath, ImportDestination destination)
    {
        var allowed = ImportFolderGate.CanFile(
            _sourceMutationGate, _importFolders!, sourcePath, destinationPath, destination, out var reason);
        if (!allowed)
        {
            _logger.LogDebug(
                "Import filing denied for {Source} -> {Destination}: {Reason}", sourcePath, destinationPath, reason);
        }

        return allowed;
    }

    private bool CanMutate(
        string path,
        SourceMutationKind mutation,
        bool allowDelete = false)
    {
        if (_importFolders?.IsInImportFolder(path) == true)
        {
            // The import folder is not a library source; it has its own policy.
            var importPolicy = _importFolders.CreateImportPolicy(allowDelete);
            return importPolicy is not null
                && _sourceMutationGate.Evaluate(new SourceMutationRequest
                {
                    Source = importPolicy,
                    Mutation = mutation,
                    Path = path,
                }).Allowed;
        }

        var resolved = _libraryResolver?.ResolveSourceForPath(path);
        FileSourceMutationPolicy? policy = resolved is not null
            ? FileSourceMutationPolicyFactory.Create(
                resolved.Library,
                resolved.Source,
                allowDelete: allowDelete)
            : null;

        if (policy is null)
        {
            _logger.LogDebug(
                "Filesystem mutation denied because {Path} has no configured source policy",
                path);
            return false;
        }

        var decision = _sourceMutationGate.Evaluate(new SourceMutationRequest
        {
            Source = policy,
            Mutation = mutation,
            Path = path,
        });

        if (!decision.Allowed)
        {
            _logger.LogDebug(
                "Filesystem mutation {Mutation} denied for source {SourceId}: {Reason}",
                mutation, policy.SourceId, decision.Reason);
        }

        return decision.Allowed;
    }
}
