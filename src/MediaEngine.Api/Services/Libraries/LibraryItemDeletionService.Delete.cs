using System.Globalization;
using System.Text.Json;
using MediaEngine.Contracts.Items;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Api.Services.Libraries;

/// <summary>Who confirmed a deletion: kept on the activity entry and on the excluded-file rows. The display name is a snapshot.</summary>
/// <param name="VaultMove">True when the titles are being moved to or out of the Vault: nothing then names a title, no file goes to the shared recycle bin, and the activity entry says only that an item was moved.</param>
public sealed record LibraryItemDeletionActor(Guid? AccountId, Guid? ProfileId, string DisplayName, bool VaultMove = false);

/// <summary>
/// The second half of Delete from library: acts on a plan the person confirmed. Order matters and is fixed: re-check the plan, stop
/// processing, delete files through the safety gate (checked again immediately before each one), remember the files that stayed,
/// remove the records in one transaction, then tidy up (Library's own files, empty groups, empty folders), log one activity entry
/// and tell open screens.
/// </summary>
public sealed partial class LibraryItemDeletionService
{
    public const string ChangedMessage = "Something changed since you opened this. Here's the updated list.";
    public const string FileInUseReason = "The file is in use";
    public const string FileNotAllowedReason = "Library isn't allowed to change this file";

    // Two deletions never overlap: the second one plans against what the first left, so it gets "not found" or a fresh preview.
    private readonly SemaphoreSlim _deleteLock = new(1, 1);

    /// <summary>
    /// Deletes the titles the person confirmed. <paramref name="fingerprint"/> is the one the preview returned; when the plan has
    /// changed since, nothing is touched and the refusal carries the fresh preview.
    /// </summary>
    public async Task<LibraryItemDeletionResult<LibraryItemDeletionResultDto>> DeleteAsync(
        IReadOnlyList<Guid>? workIds,
        string? fingerprint,
        LibraryItemDeletionActor actor,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);

        await _deleteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var planned = await BuildPlanAsync(workIds, ct).ConfigureAwait(false);
            if (!planned.IsSuccess)
            {
                return LibraryItemDeletionResult<LibraryItemDeletionResultDto>.Refused(planned.Refusal!);
            }

            var plan = planned.Value!;
            if (!string.Equals(fingerprint, plan.Preview.Fingerprint, StringComparison.Ordinal))
            {
                return LibraryItemDeletionResult<LibraryItemDeletionResultDto>.Refused(new LibraryItemDeletionRefusal(
                    LibraryItemDeletionRefusalKind.Changed, ChangedMessage, [], plan.Preview));
            }

            // From here the deletion is carried through even if the caller goes away: stopping half way would leave files gone
            // and titles still listed.
            var result = await ExecuteAsync(plan, actor).ConfigureAwait(false);
            return LibraryItemDeletionResult<LibraryItemDeletionResultDto>.Ok(result);
        }
        finally
        {
            _deleteLock.Release();
        }
    }

    /// <summary>
    /// Removes titles after a Move to Vault or Move out has copied and checked their files. The same steps and the same safety gate as
    /// <see cref="DeleteAsync"/>, with no preview to confirm (the move job holds the plan), and nothing that names a title. A file the gate
    /// will not let go of (a Read-only folder) stays where it is and is remembered so the next scan does not bring the title back.
    /// </summary>
    /// <param name="fromVault">True for Move out: the titles are Vault titles, which no other deletion can reach.</param>
    public async Task<LibraryItemDeletionResult<LibraryItemDeletionResultDto>> RetireForVaultMoveAsync(
        IReadOnlyList<Guid> workIds,
        bool fromVault,
        LibraryItemDeletionActor actor,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await _deleteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var planned = await BuildPlanAsync(workIds, ct, fromVault).ConfigureAwait(false);
            if (!planned.IsSuccess)
            {
                return LibraryItemDeletionResult<LibraryItemDeletionResultDto>.Refused(planned.Refusal!);
            }

            var result = await ExecuteAsync(planned.Value!, actor with { VaultMove = true }, announce: !fromVault).ConfigureAwait(false);
            return LibraryItemDeletionResult<LibraryItemDeletionResultDto>.Ok(result);
        }
        finally
        {
            _deleteLock.Release();
        }
    }

    // While a Vault move retires the old record, no log line may name a file, a folder or a title (Vault sections D37, D40).
    private static readonly AsyncLocal<bool> HideNames = new();

    private static string Shown(string? path) => HideNames.Value ? "a file" : Path.GetFileName(path) ?? string.Empty;

    private async Task<LibraryItemDeletionResultDto> ExecuteAsync(LibraryItemDeletionPlan plan, LibraryItemDeletionActor actor, bool announce = true)
    {
        HideNames.Value = actor.VaultMove;
        var ct = CancellationToken.None;

        // 3. No worker may pick the titles up while their files go.
        await repository.CancelProcessingAsync(plan.WorkIds, ct).ConfigureAwait(false);

        // 4. Files, each one through the gate again.
        var files = DeleteFiles(plan, actor.VaultMove);

        // 5. Media files that are still on disk are remembered so the next scan does not bring the title back.
        try
        {
            await RecordExclusionsAsync(plan, files, actor, actor.VaultMove).ConfigureAwait(false);
        }
        finally
        {
            // Scans remember the exclusions in memory; tell them these changed, even if only some were written.
            excludedFileFilter.Invalidate();
        }

        // 6. The records, in one transaction.
        LibraryItemDeletionDataResult data;
        try
        {
            data = await repository.DeleteWorksAsync(plan.WorkIds, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The files are already gone, so the titles are listed with missing files until this is retried; say so plainly.
            logger.LogError(
                ex,
                "Deleting from the library failed after {Deleted} file(s) were already removed from disk. These titles are still in the library with missing files: {WorkIds}",
                files.DeletedPaths.Count,
                actor.VaultMove
                    ? $"{plan.WorkIds.Count.ToString(CultureInfo.InvariantCulture)} item(s)"
                    : string.Join(", ", plan.WorkIds.Select(id => id.ToString("D", CultureInfo.InvariantCulture))));
            throw;
        }

        // 7. Files Tuvima wrote for the titles (pictures only they used, text tracks, derived files).
        RemoveTuvimaOwnedFiles(data, plan.WorkIds);

        // 8. Empty groups (shows, albums, series), then folders the file deletes left empty.
        try
        {
            await hierarchy.CleanupEmptyParentsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not tidy empty groups after deleting {Count} title(s) from the library", plan.WorkIds.Count);
        }

        RemoveEmptyFolders(files.DeletedPaths);

        var result = new LibraryItemDeletionResultDto
        {
            DeletedWorkIds = [.. plan.WorkIds],
            Titles = [.. plan.Preview.Items.Select(item => item.Title)],
            FilesDeleted = files.DeletedPaths.Count,
            FilesLeftOnDisk = files.LeftOnDisk + files.Failed.Count,
            FilesFailed = [.. files.Failed.Select(failed => new LibraryItemDeletionFailedFileDto
            {
                FileName = failed.FileName,
                FolderLabel = failed.FolderLabel,
                Reason = failed.Reason,
            })],
            BytesFreed = files.BytesFreed,
            CollectionsDeleted = [.. data.CollectionsDeleted.Select(collection => new LibraryItemDeletionRemovedCollectionDto
            {
                CollectionId = collection.Id,
                Name = collection.Name,
            })],
        };

        // 9. The one entry the server keeps: who deleted what.
        if (actor.VaultMove)
        {
            // Moving out of the Vault leaves no entry at all; the Vault's own move list is the record.
            if (announce)
            {
                await LogVaultMoveActivityAsync(plan, actor).ConfigureAwait(false);
            }
        }
        else
        {
            await LogActivityAsync(plan, actor, result).ConfigureAwait(false);
        }

        // 10. Open screens drop the titles. Vault titles are not announced to everyone; the Vault screens read the list again.
        if (announce)
        {
            await PublishRemovedAsync(plan.WorkIds, data.AssetIds).ConfigureAwait(false);
        }

        return result;
    }

    // ----- step 4: files ---------------------------------------------------------------------------------------------------

    private FileRun DeleteFiles(LibraryItemDeletionPlan plan, bool vaultMove)
    {
        var run = new FileRun();
        var titles = plan.Preview.Items.ToDictionary(item => item.WorkId, item => item.Title);

        // plan.Files lists each media file followed by its own sidecars.
        var currentMediaGone = false;
        foreach (var file in plan.Files)
        {
            if (!file.IsSidecar)
            {
                currentMediaGone = false;
            }

            if (file.Outcome != LibraryItemDeletionOutcomes.Delete)
            {
                if (file.Outcome != LibraryItemDeletionOutcomes.KeepMissing)
                {
                    run.LeftOnDisk++;
                }

                if (!file.IsSidecar)
                {
                    run.KeptMedia.Add(new KeptMedia(file, ReasonFor(file.Outcome)));
                }

                continue;
            }

            // A sidecar goes with its media file; when the media file could not be removed, its sidecars stay beside it.
            if (file.IsSidecar && !currentMediaGone)
            {
                run.LeftOnDisk++;
                continue;
            }

            var attempt = TryDelete(file, titles.GetValueOrDefault(file.WorkId), vaultMove);
            switch (attempt.Kind)
            {
                case DeleteAttemptKind.Deleted:
                    run.DeletedPaths.Add(file.FullPath);
                    run.BytesFreed += attempt.Bytes;
                    currentMediaGone |= !file.IsSidecar;
                    break;

                case DeleteAttemptKind.AlreadyGone:
                    // Nothing to remove and nothing freed; its sidecars still go.
                    currentMediaGone |= !file.IsSidecar;
                    break;

                default:
                    run.Failed.Add(new FailedFile(file, Path.GetFileName(file.FullPath), attempt.FolderLabel, attempt.Reason));
                    if (!file.IsSidecar)
                    {
                        run.KeptMedia.Add(new KeptMedia(file, ExcludedSourceFileReasons.DeleteFailed));
                    }

                    break;
            }
        }

        return run;
    }

    /// <summary>
    /// Deletes one file. The source is resolved again and the policy rebuilt now, and the gate is asked at this moment: the plan's own
    /// policy is never trusted, because a folder can be switched to as-is, or a merge can list the file, between the plan and here.
    /// </summary>
    private DeleteAttempt TryDelete(LibraryItemDeletionPlannedFile file, string? title, bool vaultMove)
    {
        var path = file.FullPath;
        var resolved = string.IsNullOrWhiteSpace(path) ? null : folders.ResolveSourceForPath(path);
        var label = LabelFor(resolved, path);

        var decision = Evaluate(resolved, path, null);
        if (decision.Outcome != LibraryItemDeletionOutcomes.Delete)
        {
            logger.LogWarning(
                "The safety check refused deleting {FileName} on confirmation: {Reason}",
                Shown(path),
                HideNames.Value ? "not allowed" : decision.Reason);
            return DeleteAttempt.Failed(label, FileNotAllowedReason);
        }

        try
        {
            if (!File.Exists(path))
            {
                return DeleteAttempt.Gone;
            }

            // A library type with its recycle bin on moves the file there instead; nothing is freed until the bin is emptied.
            // A moved title's file is already safe in its new place, and the shared bin would show its name, so a move skips the bin.
            if (resolved?.Library.RecycleBin == true && !vaultMove)
            {
                Bin.MoveIn(path, title, resolved.Library.Id);
                return DeleteAttempt.WasDeleted(0);
            }

            var size = LengthOf(path) ?? 0;
            File.Delete(path);
            return DeleteAttempt.WasDeleted(size);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Removed by something else a moment ago: the result is the one wanted.
            _ = ex;
            return DeleteAttempt.Gone;
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(HideNames.Value ? null : ex, "Not allowed to delete {FileName}; it stays on disk", Shown(path));
            return DeleteAttempt.Failed(label, FileNotAllowedReason);
        }
        catch (IOException ex)
        {
            logger.LogWarning(HideNames.Value ? null : ex, "Could not delete {FileName}; it stays on disk", Shown(path));
            return DeleteAttempt.Failed(label, FileInUseReason);
        }
    }

    private static string ReasonFor(string outcome) => outcome == LibraryItemDeletionOutcomes.KeepAsIs
        ? ExcludedSourceFileReasons.AsIs
        : ExcludedSourceFileReasons.Protected;

    // ----- step 5: exclusions ----------------------------------------------------------------------------------------------

    private async Task RecordExclusionsAsync(LibraryItemDeletionPlan plan, FileRun run, LibraryItemDeletionActor actor, bool vaultMove)
    {
        var hashes = plan.Works.SelectMany(work => work.Assets)
            .GroupBy(asset => asset.AssetId)
            .ToDictionary(group => group.Key, group => group.First().ContentHash);
        var titles = plan.Preview.Items.ToDictionary(item => item.WorkId, item => item.Title);

        foreach (var kept in run.KeptMedia)
        {
            var file = kept.File;

            // A file another title still uses stays that title's file, so it is not excluded. A file outside every library
            // folder has no source to remember it under, and no scan reads it.
            if (file.Reason == UsedElsewhereReason || file.Policy is null)
            {
                continue;
            }

            // A file that is gone (or went in the meantime) has nothing to skip.
            if (!File.Exists(file.FullPath))
            {
                continue;
            }

            var relative = ExcludedSourceFilePaths.RelativePathOf(file.Policy.RootPath, file.FullPath);
            if (relative is null)
            {
                logger.LogWarning("Not remembering {FileName} as excluded: it is not inside its source folder", Shown(file.FullPath));
                continue;
            }

            long? size = null;
            DateTimeOffset? lastWrite = null;
            try
            {
                var info = new FileInfo(file.FullPath);
                size = info.Length;
                lastWrite = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The facts are a convenience for noticing a changed file; the exclusion itself is still recorded.
                _ = ex;
            }

            var hash = file.AssetId is { } assetId && hashes.TryGetValue(assetId, out var known) && !string.IsNullOrWhiteSpace(known)
                ? known
                : null;
            await excludedFiles.AddOrReplaceAsync(new ExcludedSourceFile(
                Guid.NewGuid(),
                file.Policy.LibraryId,
                file.Policy.SourceId,
                relative,
                hash,
                size,
                lastWrite,
                vaultMove ? null : titles.GetValueOrDefault(file.WorkId),
                DateTimeOffset.UtcNow,
                actor.AccountId,
                actor.DisplayName,
                kept.Reason)).ConfigureAwait(false);
        }
    }

    // ----- steps 7 and 8: tidying ------------------------------------------------------------------------------------------

    /// <summary>
    /// Removes the files Tuvima wrote for the deleted titles, and only those inside Tuvima's own data folder. A picture exported beside a
    /// media file is not under that folder; it travels with the media file as a sidecar instead.
    /// </summary>
    private void RemoveTuvimaOwnedFiles(LibraryItemDeletionDataResult data, IReadOnlyList<Guid> workIds)
    {
        var assetsRoot = Path.TrimEndingDirectorySeparator(NormalizePath(assetPaths.AssetsRoot));

        foreach (var path in data.TuvimaOwnedFiles)
        {
            var full = NormalizePath(path);
            if (ExcludedSourceFilePaths.RelativePathOf(assetsRoot, full) is null)
            {
                continue;
            }

            try
            {
                File.Delete(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(HideNames.Value ? null : ex, "Could not remove Library's own file {FileName}", Shown(full));
                continue;
            }

            RemoveEmptyTuvimaFolders(Path.GetDirectoryName(full), assetsRoot);
        }

        // Folders keyed by a deleted id: derived files by owner, text tracks by media asset.
        var ids = workIds.Concat(data.EditionIds).Concat(data.AssetIds).Distinct().ToList();
        foreach (var kindFolder in SafeSubfolders(assetPaths.DerivedRoot))
        {
            foreach (var id in ids)
            {
                RemoveOwnedFolder(
                    Path.Combine(kindFolder, id.ToString("D", CultureInfo.InvariantCulture)), assetPaths.DerivedRoot, data.StillReferencedFiles);
            }
        }

        foreach (var kindFolder in SafeSubfolders(assetPaths.TextTracksRoot))
        {
            foreach (var id in data.AssetIds)
            {
                RemoveOwnedFolder(
                    Path.Combine(kindFolder, id.ToString("D", CultureInfo.InvariantCulture)), assetPaths.TextTracksRoot, data.StillReferencedFiles);
            }
        }
    }

    /// <summary>
    /// Removes a folder Tuvima keyed by a deleted id, with everything in it, unless a remaining row still points at a file inside it:
    /// then the whole folder is left, because it is not entirely the deleted title's.
    /// </summary>
    private void RemoveOwnedFolder(string folder, string root, IReadOnlyCollection<string> stillReferenced)
    {
        try
        {
            var normalizedFolder = NormalizePath(folder);
            if (stillReferenced.Any(path => ExcludedSourceFilePaths.RelativePathOf(normalizedFolder, path) is not null))
            {
                logger.LogInformation("Leaving {Folder}: a remaining record still uses a file in it", Shown(folder));
                return;
            }

            if (!Directory.Exists(folder)
                || ExcludedSourceFilePaths.RelativePathOf(NormalizePath(root), NormalizePath(folder)) is null
                || IsReparsePoint(folder))
            {
                return;
            }

            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(HideNames.Value ? null : ex, "Could not remove Library's own folder {Folder}", Shown(folder));
        }
    }

    private static bool IsReparsePoint(string directory) =>
        new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint);

    private static IEnumerable<string> SafeSubfolders(string root)
    {
        try
        {
            return Directory.Exists(root) ? Directory.GetDirectories(root) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be listed has nothing Tuvima can remove from it now; the asset-store sweep covers it later.
            _ = ex;
            return [];
        }
    }

    /// <summary>Removes folders emptied by deleting a file, up to (never including) a direct child of the data folder.</summary>
    private void RemoveEmptyTuvimaFolders(string? start, string assetsRoot)
    {
        var current = start;
        while (!string.IsNullOrEmpty(current))
        {
            var normalized = Path.TrimEndingDirectorySeparator(NormalizePath(current));
            var relative = ExcludedSourceFilePaths.RelativePathOf(assetsRoot, normalized);

            // Stop at the data folder itself and at its own top-level folders (artwork, derived, ...), which are never removed.
            if (relative is null || !relative.Contains('/', StringComparison.Ordinal))
            {
                return;
            }

            try
            {
                if (!Directory.Exists(normalized))
                {
                    current = Path.GetDirectoryName(normalized);
                    continue;
                }

                // A junction or symlink is somebody's arrangement, not ours to remove.
                if (IsReparsePoint(normalized) || Directory.EnumerateFileSystemEntries(normalized).Any())
                {
                    return;
                }

                Directory.Delete(normalized);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(HideNames.Value ? null : ex, "Could not remove the empty folder {Folder}", Shown(normalized));
                return;
            }

            current = Path.GetDirectoryName(normalized);
        }
    }

    /// <summary>
    /// Removes folders the file deletes left empty, walking up from each deleted file to (never including) its source folder. A
    /// folder goes only when it is empty and the safety gate permits deleting it for that source, asked at this moment.
    /// </summary>
    private void RemoveEmptyFolders(IReadOnlyList<string> deletedFiles)
    {
        foreach (var file in deletedFiles)
        {
            var current = Path.GetDirectoryName(file);
            while (!string.IsNullOrEmpty(current))
            {
                var directory = Path.TrimEndingDirectorySeparator(NormalizePath(current));
                var resolved = folders.ResolveSourceForPath(directory);
                if (resolved is null)
                {
                    break;
                }

                // The source folder itself, and anything outside it, is never removed.
                var sourceRoot = Path.TrimEndingDirectorySeparator(NormalizePath(resolved.Source.Path));
                if (ExcludedSourceFilePaths.RelativePathOf(sourceRoot, directory) is null)
                {
                    break;
                }

                try
                {
                    if (Directory.Exists(directory))
                    {
                        // A junction or symlink is somebody's arrangement, not ours to remove, and the walk goes no further up through it.
                        if (IsReparsePoint(directory) || Directory.EnumerateFileSystemEntries(directory).Any())
                        {
                            break;
                        }

                        if (Evaluate(resolved, directory, null).Outcome != LibraryItemDeletionOutcomes.Delete)
                        {
                            break;
                        }

                        Directory.Delete(directory);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(HideNames.Value ? null : ex, "Could not remove the empty folder {Folder}", Shown(directory));
                    break;
                }

                current = Path.GetDirectoryName(directory);
            }
        }
    }

    // ----- steps 9 and 10: the record and the live update ------------------------------------------------------------------

    private async Task LogActivityAsync(LibraryItemDeletionPlan plan, LibraryItemDeletionActor actor, LibraryItemDeletionResultDto result)
    {
        var titles = DescribeTitles(plan);
        var parts = new List<string>
        {
            result.FilesDeleted == 0
                ? "no files were deleted from disk"
                : result.BytesFreed == 0
                    ? $"{Count(result.FilesDeleted, "file")} moved out of the library"
                    : $"{Count(result.FilesDeleted, "file")} deleted from disk ({FormatBytes(result.BytesFreed)} freed)",
        };
        if (result.FilesLeftOnDisk > 0)
        {
            parts.Add($"{Count(result.FilesLeftOnDisk, "file")} left on disk");
        }

        var who = string.IsNullOrWhiteSpace(actor.DisplayName) ? "An administrator" : actor.DisplayName.Trim();
        var entry = new SystemActivityEntry
        {
            ActionType = SystemActionType.MediaRemoved,
            CollectionName = titles,
            // The titles are gone, so the entry points at no entity; it stands on its own.
            EntityType = "Work",
            ProfileId = actor.ProfileId,
            Detail = $"{who} deleted {titles} from the library: {string.Join(", ", parts)}.",
            ChangesJson = JsonSerializer.Serialize(new
            {
                titles = plan.WorkIds.Count,
                files_deleted = result.FilesDeleted,
                bytes_freed = result.BytesFreed,
                files_left_on_disk = result.FilesLeftOnDisk,
                actor = who,
            }),
        };

        try
        {
            await activity.LogAsync(entry).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Deleted {Count} title(s) from the library but could not write the activity entry", plan.WorkIds.Count);
        }
    }

    // A move names no title, id or path in anything a profile without the Vault can read (Vault sections D40); the full record is the Vault's own move list.
    private async Task LogVaultMoveActivityAsync(LibraryItemDeletionPlan plan, LibraryItemDeletionActor actor)
    {
        var entry = new SystemActivityEntry
        {
            ActionType = SystemActionType.MediaRemoved,
            EntityType = "Work",
            ProfileId = actor.ProfileId,
            Detail = "An item was moved to the Vault.",
        };

        try
        {
            await activity.LogAsync(entry).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Moved {Count} item(s) but could not write the activity entry", plan.WorkIds.Count);
        }
    }

    private async Task PublishRemovedAsync(IReadOnlyList<Guid> workIds, IReadOnlyList<Guid> assetIds)
    {
        try
        {
            await events.PublishAsync(
                SignalREvents.LibraryItemsRemoved,
                new LibraryItemsRemovedEvent([.. workIds], [.. assetIds])).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Deleted {Count} title(s) from the library but could not tell open screens", workIds.Count);
        }
    }

    /// <summary>The one title's name, or a count in the right words ("12 songs").</summary>
    private static string DescribeTitles(LibraryItemDeletionPlan plan)
    {
        if (plan.Preview.Items.Count == 1)
        {
            return $"\"{plan.Preview.Items[0].Title}\"";
        }

        var types = plan.Works.Select(work => work.MediaType).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var noun = types.Count == 1
            ? types[0].ToLowerInvariant() switch
            {
                "music" => "songs",
                "tv" => "episodes",
                "movies" => "movies",
                "books" => "books",
                "audiobooks" => "audiobooks",
                "comics" => "comics",
                _ => "items",
            }
            : "items";
        return $"{plan.Preview.Items.Count.ToString(CultureInfo.InvariantCulture)} {noun}";
    }

    private static string Count(int count, string noun) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {noun}{(count == 1 ? string.Empty : "s")}";

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

    // ----- working types ---------------------------------------------------------------------------------------------------

    private sealed class FileRun
    {
        public List<string> DeletedPaths { get; } = [];
        public List<FailedFile> Failed { get; } = [];
        public List<KeptMedia> KeptMedia { get; } = [];
        public long BytesFreed { get; set; }

        /// <summary>Files (media or sidecar) that were never deleted and are still on disk, not counting the ones that failed.</summary>
        public int LeftOnDisk { get; set; }
    }

    private sealed record FailedFile(LibraryItemDeletionPlannedFile File, string FileName, string FolderLabel, string Reason);

    private sealed record KeptMedia(LibraryItemDeletionPlannedFile File, string Reason);

    private enum DeleteAttemptKind
    {
        Deleted,
        AlreadyGone,
        Failed,
    }

    private readonly record struct DeleteAttempt(DeleteAttemptKind Kind, long Bytes, string FolderLabel, string Reason)
    {
        public static DeleteAttempt Gone => new(DeleteAttemptKind.AlreadyGone, 0, string.Empty, string.Empty);

        public static DeleteAttempt WasDeleted(long bytes) => new(DeleteAttemptKind.Deleted, bytes, string.Empty, string.Empty);

        public static DeleteAttempt Failed(string folderLabel, string reason) => new(DeleteAttemptKind.Failed, 0, folderLabel, reason);
    }
}
