using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion;

// Re-filing after a library move (Details overlay D19): in a Managed folder the file is always renamed and moved to fit its new
// place; As-is folders, read-only folders and protected files are never touched. It is the same re-file that tidies an asset after
// hydration (the same naming policy, the same mutation policy, the same file move, the same companion files and stored path), asked
// for explicitly and told where the item now sits. The preview of a move and the durable job that carries it out both call
// PlanRefileAsync, so what the person was shown is what the job decides.
public sealed partial class AutoOrganizeService
{
    /// <inheritdoc/>
    public async Task<AssetRefilePlan> PlanRefileAsync(Guid assetId, AssetRefilePlacement? placement, CancellationToken ct = default)
    {
        var asset = await _assetRepo.FindByIdAsync(assetId, ct).ConfigureAwait(false);
        if (asset is null)
        {
            return AssetRefilePlan.Of(AssetRefileDisposition.Unavailable, string.Empty, "The file is no longer in the library.");
        }

        var current = asset.FilePathRoot;
        if (string.IsNullOrWhiteSpace(current) || !File.Exists(current))
        {
            return AssetRefilePlan.Of(AssetRefileDisposition.Unavailable, current ?? string.Empty, "The file is not on disk.");
        }

        // A file waiting in the Import folder is filed by the import flow, never by a move in the library.
        var resolved = _importFolders?.IsInImportFolder(current) == true ? null : _libraryResolver?.ResolveSourceForPath(current);
        if (resolved is null)
        {
            return AssetRefilePlan.Of(AssetRefileDisposition.Unavailable, current, "The file is not in a library folder.");
        }

        // The folder decides before anything else: an As-is folder is only ever read, a read-only one is never written.
        if (!resolved.Source.IsManaged)
        {
            return AssetRefilePlan.Of(AssetRefileDisposition.AsIs, current, "The file is in a Read-only folder, so it is only ever read.");
        }

        if (!resolved.Source.IsWritable)
        {
            return AssetRefilePlan.Of(AssetRefileDisposition.ReadOnly, current, "The file is in a read-only folder.");
        }

        var (metadata, mediaType) = await LoadRefileMetadataAsync(assetId, placement, ct).ConfigureAwait(false);
        if (metadata is null)
        {
            return AssetRefilePlan.Of(AssetRefileDisposition.Unavailable, current, "Nothing is known about this file yet, so it has no name to take.");
        }

        var candidate = new IngestionCandidate
        {
            Path = current,
            EventType = FileEventType.Created,
            DetectedAt = DateTimeOffset.UtcNow,
            ReadyAt = DateTimeOffset.UtcNow,
            Metadata = metadata,
            DetectedMediaType = mediaType,
        };
        var relative = LibraryOrganizationPath.Calculate(_organizer, candidate, resolved.Library, _options.ResolveTemplate(mediaType?.ToString()));
        var target = Path.GetFullPath(Path.Combine(resolved.Source.Path, relative));
        if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
        {
            return AssetRefilePlan.Of(AssetRefileDisposition.AlreadyFits, current);
        }

        // The same checks tidying a filed asset makes: a usable name, the folder the file already lives in, then the shared policy.
        var gate = _gate.Evaluate(1.0, metadata, hasUserLock: false, mediaTypeNeedsReview: false, filingCategory: LibraryOrganizationPath.FilingCategory(resolved.Library, mediaType));
        if (!gate.CanOrganize)
        {
            return AssetRefilePlan.Of(AssetRefileDisposition.Unavailable, current, "The file has no proper name to take yet.");
        }

        if (!StaysInsideRoot(current, target, resolved.Source.Path))
        {
            return AssetRefilePlan.Of(AssetRefileDisposition.Unavailable, current, "The new name would leave the folder the file lives in.");
        }

        if (!CanMoveBetween(current, target))
        {
            return AssetRefilePlan.Of(AssetRefileDisposition.Protected, current, "The file is protected, so it is not touched.");
        }

        // Never onto a name another file already holds (two editions can share a standard name): the file stays as it is.
        if (File.Exists(target))
        {
            return AssetRefilePlan.Of(
                AssetRefileDisposition.Collision, current, "Another file already has the name this file would take.", target);
        }

        return AssetRefilePlan.Of(AssetRefileDisposition.Moves, current, targetPath: target);
    }

    /// <inheritdoc/>
    public Task<AssetRefileOutcome> RefileAsync(Guid assetId, CancellationToken ct = default) =>
        RefileCoreAsync(assetId, null, ct);

    /// <inheritdoc/>
    public Task<AssetRefileOutcome> RefileAsync(Guid assetId, IRefileStepRecorder steps, CancellationToken ct = default) =>
        RefileCoreAsync(assetId, steps, ct);

    private async Task<AssetRefileOutcome> RefileCoreAsync(Guid assetId, IRefileStepRecorder? steps, CancellationToken ct)
    {
        // The plan is made again from the library as it is now, and the policy is asked again: whatever the preview said, a file that
        // became protected or whose new name was taken since is left alone.
        var plan = await PlanRefileAsync(assetId, null, ct).ConfigureAwait(false);
        if (plan.Disposition == AssetRefileDisposition.AlreadyFits)
        {
            return new AssetRefileOutcome(AssetRefileOutcomeKind.AlreadyFits, plan.Disposition, plan.CurrentPath, null);
        }

        if (plan.Disposition != AssetRefileDisposition.Moves)
        {
            await RecordKeptAsync(assetId, plan, ct).ConfigureAwait(false);
            return new AssetRefileOutcome(AssetRefileOutcomeKind.Kept, plan.Disposition, plan.CurrentPath, plan.Reason);
        }

        var asset = await _assetRepo.FindByIdAsync(assetId, ct).ConfigureAwait(false);
        if (asset is null)
        {
            return new AssetRefileOutcome(AssetRefileOutcomeKind.Kept, AssetRefileDisposition.Unavailable, plan.CurrentPath, "The file is no longer in the library.");
        }

        // The step is written down before the disk is touched, so a stop from here on can always be finished or undone.
        if (steps is not null)
        {
            await steps.PlannedAsync(plan.CurrentPath, plan.TargetPath!, ct).ConfigureAwait(false);
        }

        var relocated = await MoveFiledAssetAsync(asset, assetId, null, plan.TargetPath!, "after a library move", ct, steps)
            .ConfigureAwait(false);
        if (relocated is null)
        {
            const string failed = "The file could not be renamed and moved (it may be in use). It will be tried again.";
            _logger.LogWarning("Library move: {Source} could not be moved to {Dest} for {Id}", plan.CurrentPath, plan.TargetPath, assetId);
            return new AssetRefileOutcome(AssetRefileOutcomeKind.Failed, AssetRefileDisposition.Moves, plan.CurrentPath, failed);
        }

        return new AssetRefileOutcome(AssetRefileOutcomeKind.Moved, AssetRefileDisposition.Moves, relocated, null);
    }

    /// <inheritdoc/>
    public async Task<AssetRefileOutcome> FileOnScheduleAsync(Guid assetId, IRefileStepRecorder steps, CancellationToken ct = default)
    {
        var asset = await _assetRepo.FindByIdAsync(assetId, ct).ConfigureAwait(false);
        if (asset is null)
        {
            return new AssetRefileOutcome(
                AssetRefileOutcomeKind.Kept, AssetRefileDisposition.Unavailable, string.Empty, "The file is no longer in the library.");
        }

        if (!File.Exists(asset.FilePathRoot))
        {
            return KeptInPlace(asset, "The file is not on disk.");
        }

        // A file waiting in the Import folder is filed out of it by the import flow, which keeps its own journal for a stop part-way.
        if (_importFolders is not null && _importMover is not null && _importFolders.IsInImportFolder(asset.FilePathRoot))
        {
            await _importFilingGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var current = await _assetRepo.FindByIdAsync(assetId, ct).ConfigureAwait(false);
                if (current is not null
                    && File.Exists(current.FilePathRoot)
                    && _importFolders.IsInImportFolder(current.FilePathRoot))
                {
                    return await TryFileFromImportAsync(current, assetId, immediate: true, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                _importFilingGate.Release();
            }

            // Another pass filed it while this one waited.
            return await RefileCoreAsync(assetId, steps, ct).ConfigureAwait(false);
        }

        // A review opened since the move was queued still holds it back: the name would come from an unconfirmed match.
        var blocking = await FindBlockingReviewAsync(assetId, ct).ConfigureAwait(false);
        if (blocking is not null)
        {
            _logger.LogDebug("Filing held for {Id}: review '{Trigger}' must be resolved first", assetId, blocking.Trigger);
            return KeptInPlace(asset, "A review must be resolved first.");
        }

        return await RefileCoreAsync(assetId, steps, ct).ConfigureAwait(false);
    }

    // A file still waiting for a person to confirm what it is is not renamed in place. (A name clash or a merge arrival is not a
    // question about identity and does not hold it back.)
    private async Task<ReviewQueueEntry?> FindBlockingReviewAsync(Guid assetId, CancellationToken ct)
    {
        var pendingReviews = await _reviewRepo.GetPendingByEntityAsync(assetId, ct).ConfigureAwait(false);
        return pendingReviews.FirstOrDefault(review =>
            review.ReviewReadyAt is not null
            && !string.Equals(review.Trigger, nameof(ReviewTrigger.WritebackFailed), StringComparison.OrdinalIgnoreCase)
            && !string.Equals(review.Trigger, ReviewTrigger.NameCollision, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(review.Trigger, ReviewTrigger.FromMerge, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc/>
    public async Task CompleteInterruptedMoveAsync(Guid assetId, string sourcePath, string targetPath, CancellationToken ct = default)
    {
        var asset = await _assetRepo.FindByIdAsync(assetId, ct).ConfigureAwait(false);
        if (asset is null || !File.Exists(targetPath))
        {
            return;
        }

        await FinishMovedAssetAsync(assetId, null, sourcePath, targetPath, "after a library move was interrupted", ct).ConfigureAwait(false);
    }

    /// <summary>Says in the activity history and the logs why a file stayed where it is after a move. The library change stands.</summary>
    private async Task RecordKeptAsync(Guid assetId, AssetRefilePlan plan, CancellationToken ct)
    {
        _logger.LogInformation(
            "Library move: {Path} for {Id} stays where it is ({Disposition}): {Reason}",
            plan.CurrentPath, assetId, plan.Disposition, plan.Reason);
        try
        {
            await _activityRepo.LogAsync(new SystemActivityEntry
            {
                ActionType = SystemActionType.PathUpdated,
                EntityId = assetId,
                EntityType = "MediaAsset",
                Detail = $"Kept in place after a library move: {Path.GetFileName(plan.CurrentPath)} ({plan.Reason})",
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to log the kept-in-place note for {Id}", assetId);
        }
    }

    /// <summary>
    /// What the naming policy reads for the asset: its own values with the ones its Work, its edition and the place above it hold
    /// laid over them (the same ownership the file's tags are written from). With a placement, the values are the ones the item would
    /// have after the move: the place above is the new one and the position numbers are the new ones.
    /// </summary>
    private async Task<(Dictionary<string, string>? Metadata, MediaType? MediaType)> LoadRefileMetadataAsync(
        Guid assetId, AssetRefilePlacement? placement, CancellationToken ct)
    {
        var canonicals = await _canonicalRepo.GetByEntityAsync(assetId, ct).ConfigureAwait(false);
        if (canonicals.Count == 0)
        {
            return (null, null);
        }

        var metadata = canonicals.ToDictionary(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase);
        var lineage = _workRepo is null ? null : await _workRepo.GetLineageByAssetAsync(assetId, ct).ConfigureAwait(false);
        if (lineage is not null)
        {
            if (placement is not null)
            {
                lineage = lineage with
                {
                    WorkId = placement.WorkId,
                    ParentWorkId = placement.ParentWorkId,
                    RootParentWorkId = placement.ParentScopeWorkId,
                };
            }

            var entityIds = new[]
            {
                lineage.TargetForParentScope,
                lineage.TargetForSelfScope,
                lineage.EditionId,
                lineage.AssetId,
            }.Distinct().ToList();
            var values = await _canonicalRepo.GetByEntitiesAsync(entityIds, ct).ConfigureAwait(false);
            var keys = values.Values.SelectMany(list => list).Select(value => value.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var effective = EffectiveWritebackMetadataComposer.Compose(
                lineage, values, keys, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            foreach (var (key, value) in effective)
            {
                metadata[key] = value;
            }
        }

        if (placement is not null)
        {
            foreach (var (key, value) in placement.Numbers)
            {
                if (value is null)
                {
                    metadata.Remove(key);
                }
                else
                {
                    metadata[key] = value;
                }
            }
        }

        var mediaType = metadata.TryGetValue("media_type", out var mtStr)
            && Enum.TryParse<MediaType>(mtStr, ignoreCase: true, out var mt)
                ? mt
                : (MediaType?)null;
        await ApplyRecordedEpisodeRangeAsync(assetId, metadata, mediaType, ct).ConfigureAwait(false);
        return (metadata, mediaType);
    }
}
