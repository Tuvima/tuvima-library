using System.Globalization;
using System.Text.Json;
using MediaEngine.Api.Services.LocalAssets;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services;
using MediaEngine.Storage.Configuration;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Api.Services.View;

/// <summary>How an Empty Trash request was answered.</summary>
public enum ViewTrashEmptyStatus
{
    Ok,
    Unauthenticated,
    Forbidden,
    NotFound,

    /// <summary>The summary the person confirmed is out of date. Nothing was changed; <see cref="ViewTrashEmptyOutcome.FreshPreview"/> holds the new one.</summary>
    Conflict,
}

/// <summary>Either what Empty Trash did, the fresh summary to show instead, or the reason it was turned away.</summary>
public sealed record ViewTrashEmptyOutcome(
    ViewTrashEmptyStatus Status,
    ViewTrashEmptyResultDto? Result = null,
    ViewTrashEmptyPreviewDto? FreshPreview = null);

/// <summary>
/// Empty Trash: the only way a personal file leaves the disk. It recomputes the plan and acts only if the person confirmed exactly
/// that plan. Each item is all-or-nothing and goes in a fixed order: every file is checked again (path, size, safety gate), all of
/// them are parked in a hidden folder inside the owner's profile folder, the item's records are removed in one transaction, and only
/// then are the parked files and the item's thumbnails deleted. If anything fails before the records are gone, every file is put
/// back and the item stays in Trash with a plain reason. Only one Empty Trash (or recovery sweep) runs at a time.
/// </summary>
public sealed class ViewTrashDeletionService(
    ViewTrashDeletionPlanner planner,
    IViewPersonalSpaceRepository spaces,
    ViewStorageService storage,
    ISourceMutationPolicyGate gate,
    IViewTrashPurgeRepository purge,
    ViewTrashRecoveryService recovery,
    AssetPathService assetPaths,
    ISystemActivityRepository activity,
    ILogger<ViewTrashDeletionService> logger,
    IViewTrashFileMover? fileMover = null)
{
    private readonly IViewTrashFileMover _mover = fileMover ?? new FileSystemViewTrashFileMover();

    public const string ChangedMessage = "Something changed since you opened this. Here's the updated list.";
    public const string FileChangedReason = "This file changed since you opened this.";
    public const string FileInUseReason = "Couldn't delete: the file is in use.";
    public const string FileNotAllowedReason = "Couldn't delete: Library isn't allowed to change this file.";
    public const string NotPreparedReason = "Couldn't delete: the files couldn't be set aside safely.";
    public const string RecordsFailedReason = "Couldn't delete: the item couldn't be removed from the library. Its files were left where they were.";
    public const string SharedFileHeldReason = "Couldn't delete: it shares a file with an item that couldn't be deleted.";
    public const string UnexpectedReason = "Couldn't delete: something went wrong, so it was left in Trash.";
    public const string RestoredMeanwhileReason = "It was restored from Trash, so it was left alone.";

    /// <summary>
    /// Empties the signed-in person's own Trash if <paramref name="fingerprint"/> is the one the latest summary returned. A different
    /// fingerprint changes nothing and returns the fresh summary.
    /// </summary>
    public async Task<ViewTrashEmptyOutcome> EmptyAsync(
        RequestAuthority authority, string? fingerprint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authority);

        await ViewTrashStaging.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var planned = await planner.BuildPlanAsync(authority, ct).ConfigureAwait(false);
            if (planned.Status != ViewTrashPlanStatus.Ok || planned.Plan is null)
            {
                return new ViewTrashEmptyOutcome(planned.Status switch
                {
                    ViewTrashPlanStatus.Unauthenticated => ViewTrashEmptyStatus.Unauthenticated,
                    ViewTrashPlanStatus.Forbidden => ViewTrashEmptyStatus.Forbidden,
                    _ => ViewTrashEmptyStatus.NotFound,
                });
            }

            var plan = planned.Plan;
            if (string.IsNullOrEmpty(fingerprint)
                || !string.Equals(fingerprint, plan.Preview.Fingerprint, StringComparison.Ordinal))
            {
                return new ViewTrashEmptyOutcome(ViewTrashEmptyStatus.Conflict, FreshPreview: plan.Preview);
            }

            // From here the work is carried through even if the caller goes away: stopping half way would leave some items
            // gone and others untouched with no record of which.
            var result = await ExecuteAsync(plan).ConfigureAwait(false);
            return new ViewTrashEmptyOutcome(ViewTrashEmptyStatus.Ok, result);
        }
        finally
        {
            ViewTrashStaging.Gate.Release();
        }
    }

    private async Task<ViewTrashEmptyResultDto> ExecuteAsync(ViewTrashPlan plan)
    {
        var ct = CancellationToken.None;
        var failed = new List<ViewTrashEmptyFailureDto>();
        var deleted = 0;
        var alreadyMissing = 0;
        long bytesFreed = 0;

        var context = await PrepareAsync(plan, ct).ConfigureAwait(false);
        foreach (var group in GroupBySharedFiles(plan.Items.Where(item => item.Outcome == ViewTrashOutcomes.Delete)))
        {
            var outcomes = context is null
                ? group.ToDictionary(item => item.ItemId, _ => ItemOutcome.Failed(ViewTrashDeletionPlanner.StorageUnavailableReason))
                : await RemoveGroupGuardedAsync(plan, group, context, ct).ConfigureAwait(false);
            foreach (var item in group)
            {
                var outcome = outcomes[item.ItemId];
                if (outcome.Reason is null)
                {
                    deleted++;
                    bytesFreed += outcome.Bytes;
                    if (ViewTrashDeletionPlanner.IsAlreadyMissing(item))
                    {
                        alreadyMissing++;
                    }
                }
                else
                {
                    failed.Add(new ViewTrashEmptyFailureDto(item.ItemId, outcome.Reason));
                }
            }
        }

        var skipped = plan.Items.Count(item => item.Outcome != ViewTrashOutcomes.Delete);

        // Anything left parked (a file that was busy when its item was removed) is finished off now rather than at the next start.
        try
        {
            await recovery.SweepLockedAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The Trash recovery sweep after Empty Trash failed; it will run again at the next start");
        }

        if (deleted > 0)
        {
            await LogActivityAsync(plan.OwnerProfileId, deleted, bytesFreed, skipped + failed.Count).ConfigureAwait(false);
        }

        return new ViewTrashEmptyResultDto(deleted, bytesFreed, skipped, failed, alreadyMissing);
    }

    // ----- one group of items (usually one item; several only when they share a file on disk) ---------------------------------

    /// <summary>
    /// Items that use the same file on disk go as one group: that file is deleted only if every item using it can go, so none is ever
    /// left in Trash with a file already gone. Everything else is a group of one.
    /// </summary>
    private static List<List<ViewTrashPlannedItem>> GroupBySharedFiles(IEnumerable<ViewTrashPlannedItem> items)
    {
        var list = items.OrderBy(item => item.ItemId).ToList();
        var parent = Enumerable.Range(0, list.Count).ToArray();
        int Find(int index)
        {
            while (parent[index] != index)
            {
                parent[index] = parent[parent[index]];
                index = parent[index];
            }

            return index;
        }

        var owners = new Dictionary<string, int>(PathComparer);
        for (var index = 0; index < list.Count; index++)
        {
            foreach (var file in list[index].Files.Where(file => file.Action == ViewTrashFileAction.Delete))
            {
                if (owners.TryGetValue(file.ResolvedPath, out var other))
                {
                    parent[Find(index)] = Find(other);
                }
                else
                {
                    owners[file.ResolvedPath] = index;
                }
            }
        }

        return list.Select((item, index) => (item, root: Find(index)))
            .GroupBy(entry => entry.root)
            .OrderBy(group => group.Min(entry => entry.item.ItemId))
            .Select(group => group.Select(entry => entry.item).ToList())
            .ToList();
    }

    /// <summary>
    /// Runs one group and never lets an unexpected failure stop the others or the activity entry for what was already removed. Whatever
    /// state the group was left in, the sweep that follows settles its files: items that still exist get their files put back, items
    /// that are gone get their parked files deleted.
    /// </summary>
    private async Task<Dictionary<Guid, ItemOutcome>> RemoveGroupGuardedAsync(
        ViewTrashPlan plan, IReadOnlyList<ViewTrashPlannedItem> group, Context context, CancellationToken ct)
    {
        try
        {
            return await RemoveGroupAsync(plan, group, context, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            logger.LogError(exception, "Emptying a Trash stopped on an unexpected error for one item; the rest continue");
            var outcomes = new Dictionary<Guid, ItemOutcome>(group.Count);
            foreach (var item in group)
            {
                // If the item's records did go before the error, it is gone from Trash and counts as deleted.
                // If that cannot be read either, the item is reported as failed (it stays in Trash if it is still there) and the rest go on.
                bool stillThere;
                try
                {
                    stillThere = await purge.ItemExistsAsync(item.ItemId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception lookupFailure) when (IsRecoverable(lookupFailure))
                {
                    logger.LogError(lookupFailure, "Could not tell whether an item was removed after an unexpected error; it is reported as not deleted");
                    stillThere = true;
                }

                outcomes[item.ItemId] = stillThere
                    ? ItemOutcome.Failed(UnexpectedReason)
                    : ItemOutcome.Deleted(item.Files.Where(file => file.Action == ViewTrashFileAction.Delete).Sum(file => file.Bytes));
            }

            return outcomes;
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException or AccessViolationException);

    private async Task<Dictionary<Guid, ItemOutcome>> RemoveGroupAsync(
        ViewTrashPlan plan, IReadOnlyList<ViewTrashPlannedItem> group, Context context, CancellationToken ct)
    {
        Dictionary<Guid, ItemOutcome> FailAll(string reason, Guid? culprit)
        {
            // The item that caused the failure gets the real reason; the others only share a file with it.
            return group.ToDictionary(
                item => item.ItemId,
                item => ItemOutcome.Failed(group.Count == 1 || culprit is null || item.ItemId == culprit ? reason : SharedFileHeldReason));
        }

        // 1. Every file is checked again, right now. Nothing has moved yet, so a refusal here costs nothing.
        var files = new List<CheckedFile>();
        foreach (var byPath in group
                     .SelectMany(item => item.Files.Where(file => file.Action == ViewTrashFileAction.Delete)
                         .Select(file => (Item: item, File: file)))
                     .GroupBy(entry => entry.File.ResolvedPath, PathComparer))
        {
            var first = byPath.First();
            var verdict = Recheck(context, first.File, byPath.Max(entry => entry.File.Bytes));
            if (verdict.Reason is not null)
            {
                return FailAll(verdict.Reason, first.Item.ItemId);
            }

            files.Add(verdict.File!);
        }

        // 2. Park every file in the group's hidden staging folder (named for its first item).
        var itemFolder = ViewTrashStaging.ItemFolder(context.ProfileRoot, group.Min(item => item.ItemId));
        ViewTrashManifest? manifest = null;
        if (files.Count > 0)
        {
            var staged = Stage(context, group.Min(item => item.ItemId), itemFolder, files, out manifest);
            if (staged is not null)
            {
                return FailAll(staged, null);
            }
        }

        // 3. The records, all items in one transaction.
        var requests = group.Select(item => new ViewTrashPurgeRequest(
            item.ItemId,
            plan.PersonalSpaceId,
            plan.OwnerProfileId,
            item.Files.Select(file => file.SourceRowId).Distinct().ToList(),
            item.Files.Where(file => file.Action is ViewTrashFileAction.Delete or ViewTrashFileAction.AlreadyGone)
                .Select(file => file.SourceRowId).Distinct().ToList(),
            item.Files.Where(file => file.Action == ViewTrashFileAction.Delete).Select(file => file.FileId).Distinct().ToList())).ToList();
        ViewTrashPurgeResult purged;
        try
        {
            purged = await purge.PurgeItemsAsync(requests, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The transaction rolled back, so the items are still whole: their files go back where they were.
            logger.LogError(exception, "Removing trashed items' records failed; their files are being put back");
            PutBack(context, itemFolder, manifest);
            return FailAll(RecordsFailedReason, null);
        }

        if (purged.Outcome != ViewTrashPurgeOutcome.Removed)
        {
            PutBack(context, itemFolder, manifest);
            return FailAll(purged.Outcome switch
            {
                ViewTrashPurgeOutcome.NotInTrash => RestoredMeanwhileReason,
                ViewTrashPurgeOutcome.SharedWorkInFlight => ViewTrashDeletionPlanner.ContributionPendingReason,
                _ => FileChangedReason,
            }, purged.ItemId);
        }

        // 4. The items are gone from the library. Delete what was parked and the items' thumbnails; a file that is busy now is left
        // in staging and removed by the sweep that follows.
        if (manifest is not null)
        {
            ViewTrashStaging.DeleteStaged(itemFolder, manifest, logger);
        }

        foreach (var item in group)
        {
            DeleteDerivedFolder(item.ItemId);
        }

        return group.ToDictionary(
            item => item.ItemId,
            item => ItemOutcome.Deleted(item.Files.Where(file => file.Action == ViewTrashFileAction.Delete).Sum(file => file.Bytes)));
    }

    private (string? Reason, CheckedFile? File) Recheck(Context context, ViewTrashPlannedFile planned, long expectedBytes)
    {
        if (!context.Sources.TryGetValue(planned.SourceId, out var source))
        {
            return (ViewTrashDeletionPlanner.SourceMissingReason, null);
        }

        // The stored path is followed through every link again and must land exactly where the plan said.
        if (!ViewStorageService.TryResolveContainedPath(context.ProfileRoot, planned.StoredPath, out var resolved, out var reason))
        {
            return (reason, null);
        }

        if (!string.Equals(resolved.Path, planned.ResolvedPath, PathComparison))
        {
            return (FileChangedReason, null);
        }

        // The policy is rebuilt now and the gate asked at this moment; the plan's own policy is never trusted.
        var policy = ViewSourceMutationPolicyFactory.Create(context.Space, source, resolved.Root, context.StorageWritable);
        var verdict = gate.Evaluate(new SourceMutationRequest
        {
            Source = policy,
            Mutation = SourceMutationKind.Delete,
            Path = resolved.Path,
        });
        if (!verdict.Allowed)
        {
            logger.LogWarning("The source-file safety gate refused to let Empty Trash delete file {FileId}: {Reason}",
                planned.FileId, verdict.Reason);
            return (context.StorageWritable ? ViewTrashDeletionPlanner.NotAllowedReason : ViewTrashDeletionPlanner.StorageReadOnlyReason, null);
        }

        // The plan has no content hash, so size and kind are what notice a file that was swapped or replaced by a link.
        try
        {
            var info = new FileInfo(resolved.Path);
            if (Directory.Exists(resolved.Path))
            {
                return (ViewTrashDeletionPlanner.NotAFileReason, null);
            }

            if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0
                || info.Length != expectedBytes)
            {
                return (FileChangedReason, null);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "A file could not be inspected before Empty Trash; its item was left in Trash");
            return (FileInUseReason, null);
        }

        return (null, new CheckedFile(resolved.Path));
    }

    /// <summary>Parks every file; on any failure puts back the ones already moved. Returns the reason, or null when all are parked.</summary>
    private string? Stage(
        Context context, Guid itemId, string itemFolder, IReadOnlyList<CheckedFile> files, out ViewTrashManifest? manifest)
    {
        manifest = null;
        var entries = files
            .Select((file, index) => new ViewTrashManifestEntry(
                file.Path,
                $"{index.ToString("D4", CultureInfo.InvariantCulture)}_{Path.GetFileName(file.Path)}"))
            .ToList();
        var built = new ViewTrashManifest(itemId, entries);
        var moved = new List<ViewTrashManifestEntry>();

        // A staging folder that is already there belongs to an earlier, unfinished removal. It is never reused or overwritten; the
        // recovery sweep settles it, and this item stays in Trash until then.
        if (Directory.Exists(itemFolder))
        {
            logger.LogWarning("An item's Trash staging folder already exists, so the item was left in Trash for the recovery sweep");
            return NotPreparedReason;
        }

        try
        {
            ViewTrashStaging.CreateItemFolder(itemFolder, logger);
            // The folder must be a plain folder inside the profile folder, not a link someone put there.
            if (!ViewStorageService.TryResolveContainedPath(context.ProfileRoot, itemFolder, out var folder, out _)
                || !string.Equals(folder.Path, Path.GetFullPath(itemFolder).TrimEnd(Path.DirectorySeparatorChar), PathComparison))
            {
                ViewTrashStaging.RemoveItemFolder(itemFolder, logger);
                return NotPreparedReason;
            }

            // The manifest is written before the first file moves, so a crash at any point can be undone.
            ViewTrashStaging.WriteManifest(itemFolder, built);
            foreach (var entry in entries)
            {
                _mover.Move(entry.Original, Path.Combine(itemFolder, entry.Staged));
                moved.Add(entry);
            }
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Any failure while parking (a file in use, no permission, or something unexpected) puts every moved file back at once.
            logger.LogWarning(exception, "Empty Trash could not set an item's files aside; they are being put back");
            var back = ViewTrashStaging.MoveBack(context.ProfileRoot, itemFolder, new ViewTrashManifest(itemId, moved), logger);
            if (back)
            {
                ViewTrashStaging.RemoveItemFolder(itemFolder, logger);
            }

            return exception switch
            {
                UnauthorizedAccessException => FileNotAllowedReason,
                IOException => FileInUseReason,
                _ => NotPreparedReason,
            };
        }

        manifest = built;
        return null;
    }

    private void PutBack(Context context, string itemFolder, ViewTrashManifest? manifest)
    {
        if (manifest is null)
        {
            return;
        }

        if (ViewTrashStaging.MoveBack(context.ProfileRoot, itemFolder, manifest, logger))
        {
            ViewTrashStaging.RemoveItemFolder(itemFolder, logger);
        }
        else
        {
            // The manifest stays, so the recovery sweep puts the rest back (the item still exists).
            logger.LogError("Some of an item's files could not be put back right away; the recovery sweep will finish it");
        }
    }

    /// <summary>Deletes <c>derived/local-item/&lt;id&gt;</c> (thumbnails and previews) and nothing else.</summary>
    private void DeleteDerivedFolder(Guid itemId)
    {
        try
        {
            var sample = assetPaths.GetCentralDerivedPath("local-item", itemId, "thumbnail", "thumbnail.jpg");
            var folder = Path.GetDirectoryName(Path.GetDirectoryName(sample));
            var kindRoot = Path.Combine(assetPaths.DerivedRoot, "local-item");
            if (folder is null
                || !string.Equals(Path.GetFileName(folder), itemId.ToString("D"), StringComparison.OrdinalIgnoreCase)
                || !ViewStorageService.Contains(kindRoot, folder)
                || string.Equals(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(kindRoot).TrimEnd(Path.DirectorySeparatorChar), PathComparison)
                || !Directory.Exists(folder))
            {
                return;
            }

            var info = new DirectoryInfo(folder);
            if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                logger.LogWarning("An item's thumbnail folder is a link, so it was left alone");
                return;
            }

            Directory.Delete(folder, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Thumbnails are throwaway copies; the item is already gone, so a leftover folder costs nothing but space.
            logger.LogWarning(exception, "A removed item's thumbnails could not be deleted");
        }
    }

    // ----- setup and activity ------------------------------------------------------------------------------------------------

    private async Task<Context?> PrepareAsync(ViewTrashPlan plan, CancellationToken ct)
    {
        try
        {
            var space = await spaces.GetByOwnerAsync(plan.OwnerProfileId, ct).ConfigureAwait(false);
            if (space is null || space.Id != plan.PersonalSpaceId)
            {
                return null;
            }

            var sources = (await spaces.GetSourcesAsync(space.Id, ct).ConfigureAwait(false)).ToDictionary(source => source.Id);
            var root = storage.GetProfileRoot(space, requireWrite: false);
            return new Context(space, sources, root, storage.IsStorageWritable());
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException
            or ConfigValidationException or ArgumentException)
        {
            logger.LogWarning(exception, "View storage could not be resolved while emptying a Trash; nothing was deleted");
            return null;
        }
    }

    private async Task LogActivityAsync(Guid profileId, int deleted, long bytesFreed, int skipped)
    {
        // Counts and size only: personal media is private, so no file name, title or thumbnail is ever written here.
        var parts = new List<string>
        {
            $"{deleted.ToString("N0", CultureInfo.InvariantCulture)} {(deleted == 1 ? "item" : "items")}",
            $"{FormatBytes(bytesFreed)} freed",
        };
        if (skipped > 0)
        {
            parts.Add($"{skipped.ToString("N0", CultureInfo.InvariantCulture)} skipped");
        }

        try
        {
            await activity.LogAsync(new SystemActivityEntry
            {
                ActionType = SystemActionType.PersonalTrashEmptied,
                EntityType = "PersonalTrash",
                ProfileId = profileId,
                Detail = string.Join(", ", parts),
                ChangesJson = JsonSerializer.Serialize(new { items = deleted, bytes_freed = bytesFreed, skipped }),
            }).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Emptied a Trash ({Deleted} item(s)) but could not write the activity entry", deleted);
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes.ToString(CultureInfo.InvariantCulture)} B"
            : $"{value.ToString("0.#", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed record Context(
        ViewPersonalSpace Space, IReadOnlyDictionary<Guid, ViewSource> Sources, string ProfileRoot, bool StorageWritable);

    private sealed record CheckedFile(string Path);

    private readonly record struct ItemOutcome(string? Reason, long Bytes)
    {
        public static ItemOutcome Failed(string reason) => new(reason, 0);

        public static ItemOutcome Deleted(long bytes) => new(null, bytes);
    }
}
