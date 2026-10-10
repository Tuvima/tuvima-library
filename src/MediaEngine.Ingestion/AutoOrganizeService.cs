using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
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
public sealed class AutoOrganizeService : IAutoOrganizeService
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
        IMediaAssetCoverageRepository? coverageRepo = null)
    {
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
    public async Task TryAutoOrganizeAsync(Guid assetId, CancellationToken ct = default, Guid? ingestionRunId = null)
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

        bool alreadyOrganized = !isInStaging
            && asset.FilePathRoot.StartsWith(
                libraryRoot, StringComparison.OrdinalIgnoreCase);

        if (alreadyOrganized)
        {
            await HandleAlreadyOrganizedAsync(asset, assetId, metadata, mediaType, libraryRoot, ct)
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
            resolvedRelativePath: relative);

        if (!gateResult.CanOrganize)
        {
            _logger.LogDebug(
                "Auto-organize blocked for {Id}: {Reason}", assetId, gateResult.BlockReason);
            return;
        }

        var destPath = Path.Combine(libraryRoot, relative);
        var stagingFolder = Path.GetDirectoryName(asset.FilePathRoot) ?? string.Empty;

        if (!CanMoveBetween(asset.FilePathRoot, destPath))
        {
            _logger.LogInformation(
                "Auto-organize blocked by source policy for {Id}: {Source} → {Dest}",
                assetId, asset.FilePathRoot, destPath);
            return;
        }

        bool moved = await _organizer.ExecuteMoveAsync(asset.FilePathRoot, destPath, ct)
            .ConfigureAwait(false);

        if (!moved)
        {
            _logger.LogWarning(
                "Auto-organize move failed for {Id}: {Source} → {Dest}",
                assetId, asset.FilePathRoot, destPath);
            return;
        }

        await _assetRepo.UpdateFilePathAsync(assetId, destPath, ct).ConfigureAwait(false);
        await MarkPresentedAndPublishAsync(assetId, metadata, mediaType, ct).ConfigureAwait(false);

        string sourcePath = asset.FilePathRoot;
        string editionFolder = Path.GetDirectoryName(destPath) ?? string.Empty;

        // Move companion artwork files from staging to the library edition folder.
        // CoverArtWorker writes poster.jpg next to the media file (even in staging) via
        // AssetPathService.GetMediaFilePosterPath, so companion files must always be moved
        // on promotion when storage policy enables local artwork mirrors.
        MoveCompanionFiles(sourcePath, destPath);

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
            assetId, asset.FilePathRoot, destPath);

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
                Detail = $"Promoted from staging: {Path.GetFileName(asset.FilePathRoot)} → {Path.GetRelativePath(libraryRoot, destPath)}",
                IngestionRunId = ingestionRunId,
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
                    destPath,
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
    // Already-organized path (QID updated after hydration)
    // -------------------------------------------------------------------------

    /// <summary>
    /// A combined TV file is named from the episodes recorded for it (filename match or a person's
    /// choice in the editor), never from the filename claim alone: a file waiting in Review, or one
    /// whose picker changed, must not be named for a range nobody confirmed. Only an unbroken run of
    /// recorded episodes becomes a range; anything else is named as a single episode.
    /// </summary>
    private async Task ApplyRecordedEpisodeRangeAsync(
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

    private async Task HandleAlreadyOrganizedAsync(
        Domain.Aggregates.MediaAsset asset, Guid assetId,
        Dictionary<string, string> metadata, MediaType? mediaType,
        string libraryRoot,
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
        var checkRelative = LibraryOrganizationPath.Calculate(_organizer, checkSynth, _libraryResolver?.ResolveById(asset.LibraryId ?? ""), checkTemplate);
        var newDest = Path.Combine(libraryRoot, checkRelative);

        if (string.Equals(asset.FilePathRoot, newDest, StringComparison.OrdinalIgnoreCase))
        {
            await MarkPresentedAndPublishAsync(assetId, metadata, mediaType, ct).ConfigureAwait(false);

            // Path unchanged — nothing to do.
            _logger.LogDebug(
                "Already organized at {Path} for {Id}",
                asset.FilePathRoot, assetId);
            return;
        }

        // Path changed (QID now available) — move file to new location.
        var oldPath = asset.FilePathRoot;
        var oldFolder = Path.GetDirectoryName(oldPath) ?? string.Empty;
        if (!CanMoveBetween(asset.FilePathRoot, newDest))
        {
            _logger.LogInformation(
                "Re-organization blocked by source policy for {Id}: {Source} → {Dest}",
                assetId, asset.FilePathRoot, newDest);
            return;
        }

        bool relocated = await _organizer.ExecuteMoveAsync(asset.FilePathRoot, newDest, ct)
            .ConfigureAwait(false);

        if (relocated)
        {
            await _assetRepo.UpdateFilePathAsync(assetId, newDest, ct).ConfigureAwait(false);
            await MarkPresentedAndPublishAsync(assetId, metadata, mediaType, ct).ConfigureAwait(false);

            MoveCompanionFiles(oldPath, newDest);

            var sourceRoot = _libraryResolver?.ResolveSourceForPath(oldPath)?.Source.Path;
            CleanEmptyParents(oldFolder, sourceRoot ?? oldFolder);

            _logger.LogInformation(
                "Re-organized asset {Id} after hydration (QID in path): {Old} → {New}",
                assetId, asset.FilePathRoot, newDest);

            try
            {
                await _activityRepo.LogAsync(new SystemActivityEntry
                {
                    ActionType = SystemActionType.PathUpdated,
                    EntityId = assetId,
                    EntityType = mediaType?.ToString() ?? "Unknown",
                    Detail = $"Re-organized after hydration: {Path.GetFileName(newDest)}",
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Failed to log re-organization activity for {Id}", assetId);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private async Task MarkPresentedAndPublishAsync(
        Guid assetId,
        IReadOnlyDictionary<string, string> metadata,
        MediaType? mediaType,
        CancellationToken ct)
    {
        var newlyPresented = await _assetRepo
            .MarkPresentedAsync(assetId, DateTimeOffset.UtcNow, ct)
            .ConfigureAwait(false);

        if (!newlyPresented)
        {
            return;
        }

        var title = metadata.GetValueOrDefault("title", "Unknown");
        WorkLineage? lineage = null;
        if (_workRepo is not null)
        {
            lineage = await _workRepo.GetLineageByAssetAsync(assetId, ct).ConfigureAwait(false);
        }

        var workId = lineage?.TargetForSelfScope ?? assetId;
        var collectionId = lineage is not null && lineage.TargetForParentScope != workId
            ? lineage.TargetForParentScope
            : (Guid?)null;

        try
        {
            await _publisher.PublishAsync(
                SignalREvents.MediaAdded,
                new MediaAddedEvent(
                    workId,
                    collectionId,
                    mediaType?.ToString() ?? "Unknown",
                    title,
                    assetId),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "MediaAdded event publish failed for {Id}; continuing", assetId);
        }
    }

    private void MoveCompanionFiles(string oldMediaPath, string newMediaPath)
    {
        if (string.IsNullOrWhiteSpace(oldMediaPath) || string.IsNullOrWhiteSpace(newMediaPath))
        {
            return;
        }

        var oldFolder = Path.GetDirectoryName(oldMediaPath) ?? string.Empty;
        var newFolder = Path.GetDirectoryName(newMediaPath) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(oldFolder) || string.IsNullOrWhiteSpace(newFolder))
        {
            return;
        }

        MoveScopedCompanionFiles(oldMediaPath, newMediaPath, "poster", ".jpg", ".png");
        MoveScopedCompanionFiles(oldMediaPath, newMediaPath, "fanart", ".jpg", ".png");
        MoveScopedCompanionFiles(oldMediaPath, newMediaPath, "logo", ".png", ".jpg");

        MoveCompanionCandidates(
            [Path.Combine(oldFolder, "cover.jpg"), Path.Combine(oldFolder, "cover.jpeg"), Path.Combine(oldFolder, "cover.png")],
            Path.Combine(newFolder, "cover.jpg"));
        MoveCompanionCandidates(
            [Path.Combine(oldFolder, "metadata.json")],
            Path.Combine(newFolder, "metadata.json"));
        MoveCompanionCandidates(
            [Path.Combine(oldFolder, "metadata.opf")],
            Path.Combine(newFolder, "metadata.opf"));
    }

    private void MoveCompanionCandidates(IEnumerable<string> sourceCandidates, string destinationPath)
    {
        var existingSources = sourceCandidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .ToList();

        if (existingSources.Count == 0 || string.IsNullOrWhiteSpace(destinationPath))
        {
            return;
        }

        foreach (var sourcePath in existingSources)
        {
            if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (!CanMoveBetween(sourcePath, destinationPath))
                {
                    continue;
                }

                AssetPathService.EnsureDirectory(destinationPath);
                if (!File.Exists(destinationPath))
                {
                    File.Move(sourcePath, destinationPath);
                }
                else if (CanMutate(sourcePath, SourceMutationKind.Delete, allowDelete: true))
                {
                    File.Delete(sourcePath);
                }
            }
            catch { /* best-effort */ }
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

    private void MoveScopedCompanionFiles(
        string oldMediaPath,
        string newMediaPath,
        string artKind,
        params string[] extensions)
    {
        if (string.IsNullOrWhiteSpace(oldMediaPath) || string.IsNullOrWhiteSpace(newMediaPath))
        {
            return;
        }

        var oldFolder = Path.GetDirectoryName(oldMediaPath) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(oldFolder) || !Directory.Exists(oldFolder))
        {
            return;
        }

        var oldBaseName = Path.GetFileNameWithoutExtension(oldMediaPath);
        foreach (var extension in extensions
                     .Where(ext => !string.IsNullOrWhiteSpace(ext))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var normalizedExtension = extension.StartsWith(".", StringComparison.Ordinal)
                ? extension
                : "." + extension;

            var sources = new List<string>
            {
                BuildScopedCompanionPath(oldMediaPath, artKind, normalizedExtension),
            };

            sources.AddRange(Directory.EnumerateFiles(oldFolder, $"{artKind}-*{normalizedExtension}"));

            if (!string.IsNullOrWhiteSpace(oldBaseName))
            {
                sources.AddRange(Directory.EnumerateFiles(oldFolder, $"{oldBaseName}-{artKind}-*{normalizedExtension}"));
            }

            foreach (var sourcePath in sources
                         .Where(path => !string.IsNullOrWhiteSpace(path))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var destinationPath = BuildScopedCompanionDestination(newMediaPath, oldBaseName, sourcePath);
                MoveCompanionCandidates([sourcePath], destinationPath);
            }
        }
    }

    private static string BuildScopedCompanionPath(string mediaPath, string artKind, string extension) =>
        artKind switch
        {
            "poster" => Path.ChangeExtension(AssetPathService.GetMediaFilePosterPath(mediaPath), extension),
            "poster-thumb" => Path.ChangeExtension(AssetPathService.GetMediaFileThumbPath(mediaPath), extension),
            "fanart" => Path.ChangeExtension(AssetPathService.GetMediaFileFanartPath(mediaPath), extension),
            "logo" => Path.ChangeExtension(AssetPathService.GetMediaFileLogoPath(mediaPath), extension),
            _ => throw new ArgumentOutOfRangeException(nameof(artKind), artKind, "Unsupported companion art kind."),
        };

    private static string BuildScopedCompanionDestination(
        string newMediaPath,
        string oldBaseName,
        string sourcePath)
    {
        var newFolder = Path.GetDirectoryName(newMediaPath) ?? string.Empty;
        var newBaseName = Path.GetFileNameWithoutExtension(newMediaPath);
        var sourceFileName = Path.GetFileName(sourcePath);
        var oldPrefix = string.IsNullOrWhiteSpace(oldBaseName)
            ? string.Empty
            : oldBaseName + "-";

        var scopedFileName = !string.IsNullOrWhiteSpace(oldPrefix)
            && sourceFileName.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase)
            ? sourceFileName[oldPrefix.Length..]
            : sourceFileName;

        if (AssetPathService.GetMediaFileArtScope(newMediaPath) == MediaFileArtScope.Dedicated)
        {
            return Path.Combine(newFolder, scopedFileName);
        }

        return Path.Combine(newFolder, $"{newBaseName}-{scopedFileName}");
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

    private bool CanMoveBetween(string sourcePath, string destinationPath)
    {
        var mutation = string.Equals(
            Path.GetDirectoryName(sourcePath),
            Path.GetDirectoryName(destinationPath),
            StringComparison.OrdinalIgnoreCase)
                ? SourceMutationKind.Rename
                : SourceMutationKind.Move;

        return CanMutate(sourcePath, mutation)
            && CanMutate(destinationPath, SourceMutationKind.UseAsDestination);
    }

    private bool CanMutate(
        string path,
        SourceMutationKind mutation,
        bool allowDelete = false)
    {
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
