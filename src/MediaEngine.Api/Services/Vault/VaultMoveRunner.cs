using System.Globalization;
using System.Text.Json;
using MediaEngine.Api.Services.Libraries;
using MediaEngine.Contracts.Vault;
using MediaEngine.Domain.Contracts;
using MediaEngine.Ingestion.Contracts;

namespace MediaEngine.Api.Services.Vault;

/// <summary>
/// Carries out the moves <see cref="VaultMoveService"/> saved (Vault sections D36, D37, D40). Each item goes through saved steps, one after
/// another: <b>copy</b> beside its final place under a name no scan reads, <b>check</b> the copy byte for byte, <b>remove</b> the old title
/// through the same safety gate as any deletion (a Read-only folder keeps its original), <b>publish</b> the copy under its real name so the
/// destination folder's scan adds it, then <b>carry across</b> what each profile had done once the new title exists. Because the step is saved
/// before the next begins, a restart resumes where it stopped, and the old title is never removed before its copy has been checked. Nothing
/// here logs or returns a title, a path or a file name.
/// </summary>
public sealed class VaultMoveRunner(
    IVaultMoveRepository moves,
    IMovedItemStateCarry carry,
    LibraryItemDeletionService deletion,
    IAssetHasher hasher,
    TimeProvider clock,
    ILogger<VaultMoveRunner> logger)
{
    /// <summary>How long a copy may wait for the destination folder's scan before the job asks the administrator to look.</summary>
    internal static readonly TimeSpan ScanPatience = TimeSpan.FromHours(2);

    /// <summary>How long the new title may take to appear in full (with its record of the title) before progress is carried to the file alone.</summary>
    internal static readonly TimeSpan TitlePatience = TimeSpan.FromMinutes(10);

    /// <summary>Lets a test stop the run just before a step, as a crash would; null in the running Engine.</summary>
    internal Action<string>? BeforeStep { get; set; }

    /// <summary>One pass over every open job. Returns true while some job still has work.</summary>
    public async Task<bool> RunPassAsync(CancellationToken ct)
    {
        foreach (var job in await moves.ListOpenAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await RunJobAsync(job, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A job that cannot be carried on is handed to the administrator; its files are as the last saved step left them.
                logger.LogWarning("A library move could not carry on ({Type}); it is waiting for the administrator.", ex.GetType().Name);
                await moves.UpdateJobAsync(job.Id, VaultMoveStates.Attention, "The move stopped unexpectedly. Nothing was lost; it carries on after the problem is fixed.", clock.GetUtcNow(), ct)
                    .ConfigureAwait(false);
            }
        }

        return (await moves.ListOpenAsync(ct).ConfigureAwait(false)).Any(job => job.State != VaultMoveStates.Attention);
    }

    internal async Task RunJobAsync(VaultMoveJob job, CancellationToken ct)
    {
        if (job.State == VaultMoveStates.Queued)
        {
            await moves.UpdateJobAsync(job.Id, VaultMoveStates.Running, null, clock.GetUtcNow(), ct).ConfigureAwait(false);
        }

        var progressed = false;
        foreach (var item in await moves.GetItemsAsync(job.Id, ct).ConfigureAwait(false))
        {
            if (VaultMoveItemStates.IsFinished(item.State))
            {
                continue;
            }

            var advanced = await AdvanceAsync(job, item, ct).ConfigureAwait(false);
            progressed |= !string.Equals(advanced.State, item.State, StringComparison.Ordinal);
        }

        var items = await moves.GetItemsAsync(job.Id, ct).ConfigureAwait(false);
        var unfinished = items.Where(item => !VaultMoveItemStates.IsFinished(item.State)).ToList();
        var now = clock.GetUtcNow();
        if (unfinished.Count == 0)
        {
            var dto = VaultMoveService.ToDto(job, items);
            var (state, message) = Summarise(dto);
            await moves.UpdateJobAsync(job.Id, state, message, now, ct).ConfigureAwait(false);
            logger.LogDebug("A library move finished.");
            return;
        }

        // Copies that wait for the folder's scan keep the job running; anything else that stopped moving, or a scan that never came, needs the administrator.
        var onlyWaiting = unfinished.All(item => item.State == VaultMoveItemStates.Published);
        var waitedTooLong = onlyWaiting && unfinished.All(item => now - item.UpdatedAt > ScanPatience);
        if ((!onlyWaiting && !progressed) || waitedTooLong)
        {
            if (job.State != VaultMoveStates.Attention)
            {
                await moves.UpdateJobAsync(
                    job.Id,
                    VaultMoveStates.Attention,
                    onlyWaiting
                        ? "The copies are in place, but the folder's scan hasn't added them yet. They are added when the folder is scanned."
                        : "Some items couldn't go further. Nothing was lost; they carry on after the problem is fixed.",
                    now,
                    ct).ConfigureAwait(false);
            }
        }
        else if (job.State == VaultMoveStates.Attention)
        {
            await moves.UpdateJobAsync(job.Id, VaultMoveStates.Running, null, now, ct).ConfigureAwait(false);
        }
    }

    // ----- one item -------------------------------------------------------------------------------------------------------

    private async Task<VaultMoveItem> AdvanceAsync(VaultMoveJob job, VaultMoveItem item, CancellationToken ct)
    {
        while (!VaultMoveItemStates.IsFinished(item.State))
        {
            var before = item.State;
            try
            {
                BeforeStep?.Invoke(before);
                item = item.State switch
                {
                    VaultMoveItemStates.Planned => await CopyAsync(job, item, ct).ConfigureAwait(false),
                    VaultMoveItemStates.Copied => await RetireAsync(job, item, ct).ConfigureAwait(false),
                    VaultMoveItemStates.Retired => await PublishAsync(job, item, ct).ConfigureAwait(false),
                    VaultMoveItemStates.Published => await AdoptAsync(job, item, ct).ConfigureAwait(false),
                    _ => item,
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("A library move step could not finish ({Type}).", ex.GetType().Name);
                if (item.State == VaultMoveItemStates.Planned)
                {
                    // Nothing has been changed yet except a half-made copy, which is removed. The original and its title are untouched.
                    DeleteQuietly(StagingOf(job, item));
                    foreach (var sidecar in SidecarsOf(item))
                    {
                        DeleteQuietly(sidecar.Staging);
                    }

                    item = await SaveAsync(item with { State = VaultMoveItemStates.Failed, Reason = "The copy couldn't be made (no room, or no access). Nothing was changed." }, ct).ConfigureAwait(false);
                }
                else
                {
                    item = await SaveAsync(item with { Reason = "This step couldn't finish yet; it is tried again." }, ct).ConfigureAwait(false);
                }
            }

            if (string.Equals(item.State, before, StringComparison.Ordinal))
            {
                break;
            }
        }

        return item;
    }

    private async Task<VaultMoveItem> CopyAsync(VaultMoveJob job, VaultMoveItem item, CancellationToken ct)
    {
        if (!File.Exists(item.SourcePath))
        {
            return await SaveAsync(item with { State = VaultMoveItemStates.Skipped, Reason = "The file was no longer there." }, ct).ConfigureAwait(false);
        }

        var hash = (await hasher.ComputeAsync(item.SourcePath, ct).ConfigureAwait(false)).Hex;
        if (await moves.FindByHashAsync(job.DestinationLibraryId, hash, ct).ConfigureAwait(false) is not null)
        {
            return await SaveAsync(item with { State = VaultMoveItemStates.Skipped, Reason = "The destination already has this file, so it was left where it is." }, ct)
                .ConfigureAwait(false);
        }

        // Another file may have taken the name since the plan was made.
        var name = FreeName(job.DestinationFolder, item.FinalName);
        var staging = Path.Combine(job.DestinationFolder, name) + VaultMoveService.StagingSuffix;
        await CopyFileAsync(item.SourcePath, staging, ct).ConfigureAwait(false);
        if (!string.Equals((await hasher.ComputeAsync(staging, ct).ConfigureAwait(false)).Hex, hash, StringComparison.Ordinal))
        {
            DeleteQuietly(staging);
            return await SaveAsync(item with { State = VaultMoveItemStates.Failed, Reason = "The copy didn't match the original, so nothing was changed." }, ct).ConfigureAwait(false);
        }

        var sidecars = new List<VaultMoveSidecar>();
        foreach (var sidecar in SidecarsOf(item))
        {
            if (!File.Exists(sidecar.Source))
            {
                continue;
            }

            await CopyFileAsync(sidecar.Source, sidecar.Staging, ct).ConfigureAwait(false);
            var same = string.Equals(
                (await hasher.ComputeAsync(sidecar.Source, ct).ConfigureAwait(false)).Hex,
                (await hasher.ComputeAsync(sidecar.Staging, ct).ConfigureAwait(false)).Hex,
                StringComparison.Ordinal);
            if (!same)
            {
                DeleteQuietly(staging);
                foreach (var made in sidecars.Append(sidecar))
                {
                    DeleteQuietly(made.Staging);
                }

                return await SaveAsync(item with { State = VaultMoveItemStates.Failed, Reason = "A copy didn't match the original, so nothing was changed." }, ct).ConfigureAwait(false);
            }

            sidecars.Add(sidecar);
        }

        return await SaveAsync(
            item with
            {
                State = VaultMoveItemStates.Copied,
                ContentHash = hash,
                FinalName = name,
                FilesJson = sidecars.Count == 0 ? null : JsonSerializer.Serialize(sidecars),
                Reason = null,
            }, ct).ConfigureAwait(false);
    }

    private async Task<VaultMoveItem> RetireAsync(VaultMoveJob job, VaultMoveItem item, CancellationToken ct)
    {
        if (item.WorkId is not { } workId || item.AssetId is not { } assetId)
        {
            Discard(job, item);
            return await SaveAsync(item with { State = VaultMoveItemStates.Failed, Reason = "The item has no title record, so nothing was changed." }, ct).ConfigureAwait(false);
        }

        // The original goes only while its checked copy is still there and still matches. After a restart, or when a drive was unplugged, the copy may
        // be gone: then the item starts again from the copy step and the original is not touched.
        if (!await CopyIsIntactAsync(job, item, ct).ConfigureAwait(false))
        {
            return await SaveAsync(item with { State = VaultMoveItemStates.Planned, ContentHash = null, FilesJson = null }, ct).ConfigureAwait(false);
        }

        // What each profile had done with the old title is saved first, and only once, because removing the title removes it.
        if (item.SnapshotJson is null)
        {
            item = await SaveAsync(item with { SnapshotJson = await carry.CaptureAsync(workId, assetId, ct).ConfigureAwait(false) }, ct).ConfigureAwait(false);
        }

        var actor = new LibraryItemDeletionActor(null, job.CreatedByProfileId, "An administrator", VaultMove: true);
        var result = await deletion.RetireForVaultMoveAsync([workId], fromVault: job.Direction == VaultMoveDirections.Out, actor, ct).ConfigureAwait(false);
        if (!result.IsSuccess && result.Refusal!.Kind != LibraryItemDeletionRefusalKind.NotFound)
        {
            // The old title could not be taken out of its library (for example it became a group). The copy is removed and the original stays as it was.
            Discard(job, item);
            return await SaveAsync(item with { State = VaultMoveItemStates.Failed, Reason = "The item couldn't be taken out of its library, so nothing was changed." }, ct).ConfigureAwait(false);
        }

        var left = File.Exists(item.SourcePath);
        if (left && job.RemoveReadOnlyOriginals)
        {
            left = !await TryRemoveReadOnlyOriginalAsync(job, item, ct).ConfigureAwait(false);
        }

        return await SaveAsync(item with { State = VaultMoveItemStates.Retired, OriginalLeft = left, Reason = null }, ct).ConfigureAwait(false);
    }

    private async Task<bool> CopyIsIntactAsync(VaultMoveJob job, VaultMoveItem item, CancellationToken ct)
    {
        var finalPath = Path.Combine(job.DestinationFolder, item.FinalName);
        var staging = finalPath + VaultMoveService.StagingSuffix;
        // A copy already published (a crash after the rename) is checked at its final name.
        var present = File.Exists(staging) ? staging : File.Exists(finalPath) ? finalPath : null;
        if (present is null || item.ContentHash is null)
        {
            return false;
        }

        if (!string.Equals((await hasher.ComputeAsync(present, ct).ConfigureAwait(false)).Hex, item.ContentHash, StringComparison.Ordinal))
        {
            return false;
        }

        return SidecarsOf(item).All(sidecar => File.Exists(sidecar.Staging) || File.Exists(sidecar.Final));
    }

    /// <summary>
    /// The separate, explicit action for a Read-only folder (Vault sections D30): remove the original, and only after the copy has been checked
    /// against it once more. It touches only the files this item listed, inside the folder the administrator chose, and it never runs for a
    /// folder Library manages (those go through the ordinary safety gate).
    /// </summary>
    private async Task<bool> TryRemoveReadOnlyOriginalAsync(VaultMoveJob job, VaultMoveItem item, CancellationToken ct)
    {
        try
        {
            if (!InsideFolder(job.SourceFolder, item.SourcePath)
                || !string.Equals((await hasher.ComputeAsync(item.SourcePath, ct).ConfigureAwait(false)).Hex, item.ContentHash, StringComparison.Ordinal))
            {
                return false;
            }

            File.Delete(item.SourcePath);
            foreach (var sidecar in SidecarsOf(item))
            {
                if (InsideFolder(job.SourceFolder, sidecar.Source))
                {
                    DeleteQuietly(sidecar.Source);
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("An original in a Read-only folder couldn't be removed ({Type}); it stays in place.", ex.GetType().Name);
            return false;
        }
    }

    private async Task<VaultMoveItem> PublishAsync(VaultMoveJob job, VaultMoveItem item, CancellationToken ct)
    {
        var finalPath = Path.Combine(job.DestinationFolder, item.FinalName);
        var staging = finalPath + VaultMoveService.StagingSuffix;
        var sidecars = SidecarsOf(item);

        if (!File.Exists(staging) && !File.Exists(finalPath))
        {
            // The checked copy has gone, after the old title was removed. Say so plainly rather than pretend.
            return await SaveAsync(item with { State = VaultMoveItemStates.Failed, Reason = "The checked copy went missing after the old item was removed." }, ct).ConfigureAwait(false);
        }

        foreach (var sidecar in sidecars)
        {
            if (File.Exists(sidecar.Staging))
            {
                File.Move(sidecar.Staging, FreePath(sidecar.Final));
            }
        }

        var name = item.FinalName;
        if (File.Exists(staging))
        {
            name = FreeName(job.DestinationFolder, item.FinalName);
            File.Move(staging, Path.Combine(job.DestinationFolder, name));
        }

        return await SaveAsync(item with { State = VaultMoveItemStates.Published, FinalName = name, Reason = null }, ct).ConfigureAwait(false);
    }

    private async Task<VaultMoveItem> AdoptAsync(VaultMoveJob job, VaultMoveItem item, CancellationToken ct)
    {
        if (item.ContentHash is null)
        {
            return await SaveAsync(item with { State = VaultMoveItemStates.Done }, ct).ConfigureAwait(false);
        }

        var found = await moves.FindByHashAsync(job.DestinationLibraryId, item.ContentHash, ct).ConfigureAwait(false);
        if (found is null)
        {
            return item;
        }

        // The title record follows the file by a moment; progress is carried to the file alone only if the title never comes.
        if (found.Value.WorkId is null && clock.GetUtcNow() - item.UpdatedAt < TitlePatience)
        {
            return item;
        }

        if (item.SnapshotJson is { } snapshot)
        {
            await carry.RestoreAsync(snapshot, found.Value.WorkId, found.Value.AssetId, includeHistory: job.Direction != VaultMoveDirections.Out, ct: ct).ConfigureAwait(false);
        }

        return await SaveAsync(item with { State = VaultMoveItemStates.Done, NewAssetId = found.Value.AssetId, SnapshotJson = null, Reason = null }, ct).ConfigureAwait(false);
    }

    // ----- helpers ------------------------------------------------------------------------------------------------------

    private Task<VaultMoveItem> SaveAsync(VaultMoveItem item, CancellationToken ct)
    {
        var saved = item with { UpdatedAt = clock.GetUtcNow() };
        return moves.UpdateItemAsync(saved, ct).ContinueWith(_ => saved, ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    private static string StagingOf(VaultMoveJob job, VaultMoveItem item) => Path.Combine(job.DestinationFolder, item.FinalName) + VaultMoveService.StagingSuffix;

    private static List<VaultMoveSidecar> SidecarsOf(VaultMoveItem item) =>
        string.IsNullOrEmpty(item.FilesJson) ? [] : JsonSerializer.Deserialize<List<VaultMoveSidecar>>(item.FilesJson) ?? [];

    // Removes the checked copy and its sidecar copies; used when the original stays as it is.
    private static void Discard(VaultMoveJob job, VaultMoveItem item)
    {
        DeleteQuietly(StagingOf(job, item));
        foreach (var sidecar in SidecarsOf(item))
        {
            DeleteQuietly(sidecar.Staging);
        }
    }

    private static bool InsideFolder(string folder, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar, comparison);
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover partial copy under a name no scan reads is harmless; the next pass or the administrator can remove it.
            _ = ex;
        }
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken ct)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous);
        await input.CopyToAsync(output, 1 << 20, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    // A name that no file and no pending copy in the folder is using.
    private static string FreeName(string folder, string name)
    {
        var candidate = name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var n = 2; File.Exists(Path.Combine(folder, candidate)); n++)
        {
            candidate = $"{stem} ({n.ToString(CultureInfo.InvariantCulture)}){extension}";
        }

        return candidate;
    }

    private static string FreePath(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        return Path.Combine(folder, FreeName(folder, Path.GetFileName(path)));
    }

    private static (string State, string Message) Summarise(VaultMoveJobDto dto)
    {
        var parts = new List<string> { $"{Count(dto.Moved, "item")} moved" };
        if (dto.OriginalLeft > 0)
        {
            parts.Add($"{Count(dto.OriginalLeft, "original")} still in place");
        }

        if (dto.Skipped > 0)
        {
            parts.Add($"{Count(dto.Skipped, "item")} left where they were");
        }

        if (dto.Failed > 0)
        {
            parts.Add($"{Count(dto.Failed, "item")} couldn't be moved");
        }

        var needsLook = dto.OriginalLeft > 0 || dto.Failed > 0;
        return (needsLook ? VaultMoveStates.Attention : VaultMoveStates.Done, string.Join(". ", parts) + ".");
    }

    private static string Count(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count.ToString(CultureInfo.InvariantCulture)} {noun}s";
}
