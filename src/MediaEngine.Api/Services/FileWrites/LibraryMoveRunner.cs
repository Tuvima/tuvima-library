using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Storage;

namespace MediaEngine.Api.Services.FileWrites;

/// <summary>
/// Carries out one library move so that a stop at any moment leaves the file in exactly one place that Library knows about (batched
/// write-back D10). Each step is written on the file's to-do row before and after it happens:
/// <c>planned</c> (before the disk is touched), <c>moved</c> (the file is at its new place), <c>checked</c> (the data store holds the new
/// path and the file was verified there). After a stop, <see cref="RecoverInterruptedAsync"/> looks at the disk and finishes or undoes
/// the move; Library never deletes a file it cannot account for, and never leaves it in two places or none through its own action.
/// </summary>
public sealed class LibraryMoveRunner(
    MediaFileWriteIntentRepository intents,
    IAutoOrganizeService organizer,
    IMediaAssetRepository assets,
    IAssetHasher hasher,
    IConfigurationLoader configuration,
    ILogger<LibraryMoveRunner> logger,
    ILibraryFolderResolver? libraryResolver = null)
{
    internal const string LostReason = "The file was not found at its old or new place after the Engine stopped mid-move.";

    internal const string GaveUpReason = "The move was cut short and could not be settled after several tries, so it was given up.";

    internal const string OfflineReason = "A drive or folder holding this file is not available, so its move waits until it is back.";

    internal const string TookOverReason = "A person's own move took this file's row over, so it runs next, straight away.";

    /// <summary>How many starts or passes may find a part-way move unsettled (with its drives reachable) before it is given up.</summary>
    internal const int MaxRecoveryAttempts = 5;

    /// <summary>How long a move waits before looking again when a drive or folder it needs is not reachable.</summary>
    internal static readonly TimeSpan OfflineRetry = TimeSpan.FromMinutes(5);

    /// <summary>What happened to a move that was asked for.</summary>
    public enum MoveResultKind
    {
        /// <summary>The file is where it should be (or was deliberately left alone) and its row is ready for the scheduled tag write.</summary>
        Done,

        /// <summary>The move did not happen this time; the file is at its old place and the move can be tried again.</summary>
        Retry,

        /// <summary>The file could not be found; the row has been marked failed.</summary>
        Lost,

        /// <summary>The move is part-way and could not be settled yet; its steps stay on the row and it is looked at again later.</summary>
        Interrupted,

        /// <summary>
        /// An automatic filing move found its file in use (being played, or held by another program). The file was left exactly as it
        /// was and the move waits for the next run; this is not a failed attempt and nothing was written for the file.
        /// </summary>
        InUse,
    }

    /// <param name="Kind">What happened.</param>
    /// <param name="Reason">A plain reason when it did not go through.</param>
    /// <param name="Completed">True when the row's move was already completed (a stop was finished on the spot), so the caller must not complete it again.</param>
    public readonly record struct MoveResult(MoveResultKind Kind, string? Reason = null, bool Completed = false);

    /// <summary>Renames and moves the file of a claimed library move, recording each step. Never throws except for a stop.</summary>
    public async Task<MoveResult> MoveAsync(MediaFileWriteIntent intent, CancellationToken ct)
    {
        // A move asked for while the file's drive or folder is not reachable waits for it to come back rather than being dropped as
        // "not on disk", so the folders on disk end up matching Library once the drive returns (D10).
        var current = await assets.FindByIdAsync(intent.AssetId, ct);
        if (current is not null
            && !string.IsNullOrWhiteSpace(current.FilePathRoot)
            && !File.Exists(current.FilePathRoot)
            && !IsReachable(current.FilePathRoot))
        {
            await intents.DeferMoveAsync(intent.Id, OfflineRetry, countAttempt: false, OfflineReason, ct);
            return new MoveResult(MoveResultKind.Interrupted, OfflineReason);
        }

        // Library's own filing never takes a file that is in use: the probe opens it exclusively and lets go at once. The file is left
        // untouched, and so are its tags, which must be written at its final place.
        var filing = intent.MoveOrigin == MediaFileWriteIntentRepository.MoveOriginFiling;
        if (filing && current is not null && FileInUseProbe.IsInUse(current.FilePathRoot))
        {
            return await SetAsideAsync(intent, ct);
        }

        string failure;
        try
        {
            var steps = new StepRecorder(intents, assets, hasher, intent);
            var filed = filing
                ? await organizer.FileOnScheduleAsync(intent.AssetId, steps, ct)
                : await organizer.RefileAsync(intent.AssetId, steps, ct);
            if (filed.Kind != AssetRefileOutcomeKind.Failed)
            {
                return new MoveResult(MoveResultKind.Done);
            }
            failure = filed.Reason ?? "The file could not be renamed and moved.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A stop leaves the steps as recorded; the next start looks at the disk and finishes or undoes the move.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Renaming the file of asset {AssetId} after a library move failed.", intent.AssetId);
            failure = ex.Message;
        }

        // The move may have got part-way: look at the disk before deciding whether to try again.
        var row = await intents.GetByIdAsync(intent.Id, ct);
        if (row?.MoveStep is "planned" or "moved" or "checked")
        {
            var settled = await SettleAsync(row, ct);
            switch (settled.Kind)
            {
                case Recovery.Finished:
                    return new MoveResult(MoveResultKind.Done, Completed: true);
                case Recovery.Lost:
                    return new MoveResult(MoveResultKind.Lost, settled.Reason ?? LostReason);
                case Recovery.Waiting:
                    return new MoveResult(MoveResultKind.Interrupted, settled.Reason);
            }
        }

        // A sharing violation during the move is the same as being in use: the file is where it was, so it is set aside, not failed.
        if (filing)
        {
            var after = await assets.FindByIdAsync(intent.AssetId, ct);
            if (after is not null && FileInUseProbe.IsInUse(after.FilePathRoot))
            {
                return await SetAsideAsync(intent, ct);
            }
        }

        return new MoveResult(MoveResultKind.Retry, failure);
    }

    // The file was in use: the filing goes back to waiting for the next run. If a person's own move took the row over meanwhile, the
    // claim is handed over to that move instead, so the row is not left "writing" until its claim runs out.
    private async Task<MoveResult> SetAsideAsync(MediaFileWriteIntent intent, CancellationToken ct)
    {
        if (await intents.DeferFilingAsync(intent.Id, MediaFileWriteIntentRepository.InUseReason, ct))
        {
            return new MoveResult(MoveResultKind.InUse, MediaFileWriteIntentRepository.InUseReason);
        }

        await intents.ReleaseClaimAsync(intent.Id, ct);
        return new MoveResult(MoveResultKind.Interrupted, TookOverReason);
    }

    // ----- recovery -----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Run once when the Engine starts, before the move lane: looks at the disk for every move that had started and finishes or undoes it.
    /// Returns how many were looked at.
    /// </summary>
    public async Task<int> RecoverInterruptedAsync(CancellationToken ct)
    {
        await intents.ReleaseMoveClaimsAsync(ct);
        var started = await intents.GetStartedMovesAsync(onlyDue: false, ct: ct);
        foreach (var row in started)
        {
            await SettleAsync(row, ct);
        }
        return started.Count;
    }

    /// <summary>
    /// Called by the move lane on every pass: looks again at part-way moves whose next attempt time has come (a drive that was offline,
    /// a data store that could not follow). Returns how many were looked at.
    /// </summary>
    public async Task<int> RecoverDueAsync(CancellationToken ct)
    {
        var due = await intents.GetStartedMovesAsync(onlyDue: true, ct: ct);
        foreach (var row in due)
        {
            await SettleAsync(row, ct);
        }
        return due.Count;
    }

    internal enum Recovery
    {
        Finished,
        WillRetry,
        Lost,

        /// <summary>Not settled yet and left as it is on the row; looked at again later.</summary>
        Waiting,
    }

    internal readonly record struct Recovered(Recovery Kind, string? Reason = null);

    // Settles one part-way move. A failure leaves the steps on the row and counts towards the cap, after which the move is given up
    // with its paths kept and no longer hides a real deletion from the watcher.
    private async Task<Recovered> SettleAsync(MediaFileWriteIntent row, CancellationToken ct)
    {
        try
        {
            return await RecoverOneAsync(row, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "A library move for asset {AssetId} is part-way and could not be settled yet.", row.AssetId);
            var counted = await intents.DeferMoveAsync(
                row.Id, FileWriteBackoff.After(row.RecoveryAttempts + 1) ?? FileWriteBackoff.Longest, countAttempt: true, ex.Message, ct);
            if (counted >= MaxRecoveryAttempts)
            {
                logger.LogWarning("Giving up on the library move for asset {AssetId} after {Count} tries; it is on the failed list.", row.AssetId, counted);
                await intents.FailMoveAsync(row.Id, "failed", GaveUpReason + " " + ex.Message, ct: ct);
                return new Recovered(Recovery.Lost, GaveUpReason);
            }
            return new Recovered(Recovery.Waiting, ex.Message);
        }
    }

    internal async Task<Recovered> RecoverOneAsync(MediaFileWriteIntent row, CancellationToken ct)
    {
        var source = row.MoveSourcePath;
        var target = row.MoveTargetPath;

        // A move that was planned completes with the generation it was planned at, so a newer change is seen and planned again.
        var generation = row.MoveGeneration ?? row.Generation;

        // Checked means the file was verified at its new place and only the final bookkeeping was left.
        if (row.MoveStep == "checked" && !string.IsNullOrWhiteSpace(target) && File.Exists(target))
        {
            await intents.CompleteMoveAsync(row.Id, generation, TagsWanted(), ct);
            return new Recovered(Recovery.Finished);
        }

        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
        {
            // Nothing was written down about where the file was going, so nothing was done to it.
            await intents.ResetMoveAsync(row.Id, ct);
            return new Recovered(Recovery.WillRetry);
        }

        // A file that cannot be seen is not a file that is gone: with either drive or library folder away, nothing is decided.
        if (!IsReachable(source) || !IsReachable(target))
        {
            logger.LogWarning(
                "The library move for asset {AssetId} is waiting: {Source} or {Target} is on a drive or folder that is not available.",
                row.AssetId, source, target);
            await intents.DeferMoveAsync(row.Id, OfflineRetry, countAttempt: false, OfflineReason, ct);
            return new Recovered(Recovery.Waiting, OfflineReason);
        }

        var atSource = File.Exists(source);
        var atTarget = File.Exists(target);
        var samePlace = string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
        if (samePlace)
        {
            atTarget = false;
        }

        if (atSource && atTarget)
        {
            if (row.MoveStep != "planned")
            {
                // Once the file has moved the old place is empty; a file there is not Library's, so it is left alone.
                logger.LogWarning(
                    "A file is at {Source}, the old place of asset {AssetId}, whose move had already finished; it is not Library's and is left alone.",
                    source, row.AssetId);
            }
            else
            {
                // An interrupted copy across drives: the old copy goes only when both places hold exactly what was planned to move.
                var sourceHash = (await hasher.ComputeAsync(source, ct)).Hex;
                var expected = string.IsNullOrWhiteSpace(row.MoveSourceHash) ? sourceHash : row.MoveSourceHash;
                var targetHash = (await hasher.ComputeAsync(target, ct)).Hex;
                if (string.Equals(targetHash, expected, StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.Equals(sourceHash, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException("The old copy changed after the move was planned, so neither copy was removed.");
                    }

                    logger.LogInformation("Finishing an interrupted move of asset {AssetId}: removing the old copy {Source}.", row.AssetId, source);
                    File.Delete(source);
                    atSource = false;
                }
                else
                {
                    logger.LogInformation("Undoing an interrupted move of asset {AssetId}: removing the partial copy {Target}.", row.AssetId, target);
                    File.Delete(target);
                    atTarget = false;
                }
            }
        }

        if (atTarget)
        {
            await organizer.CompleteInterruptedMoveAsync(row.AssetId, source, target, ct);
            var asset = await assets.FindByIdAsync(row.AssetId, ct);
            if (asset is null || !string.Equals(asset.FilePathRoot, target, StringComparison.OrdinalIgnoreCase))
            {
                // The data store could not follow (the asset went away, or another file holds the hash): the file stays where it is
                // and the move is looked at again later.
                throw new IOException("The stored path could not be brought up to date for a move that had already happened.");
            }

            await intents.RecordMoveCheckedAsync(row.Id, ct);
            await intents.CompleteMoveAsync(row.Id, generation, TagsWanted(), ct);
            return new Recovered(Recovery.Finished);
        }

        if (atSource)
        {
            await intents.ResetMoveAsync(row.Id, ct);
            return new Recovered(Recovery.WillRetry);
        }

        // Both places and their library folders can be seen, and the file is in neither: it is lost. The paths stay on the row.
        logger.LogWarning("The file of asset {AssetId} is at neither {Source} nor {Target} after an interrupted move.", row.AssetId, source, target);
        await intents.FailMoveAsync(row.Id, "failed", LostReason, ct: ct);
        return new Recovered(Recovery.Lost, LostReason);
    }

    // The library folder a path belongs to must be there for the path to count as seen. A path no library claims counts when the
    // nearest folder above it that exists is not the top of a drive.
    private bool IsReachable(string path)
    {
        var root = libraryResolver?.ResolveSourcePath(path);
        if (!string.IsNullOrWhiteSpace(root))
        {
            return Directory.Exists(root);
        }

        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        while (!string.IsNullOrEmpty(folder))
        {
            if (Directory.Exists(folder))
            {
                return !string.Equals(Path.GetPathRoot(folder), folder, StringComparison.OrdinalIgnoreCase);
            }
            folder = Path.GetDirectoryName(folder);
        }
        return false;
    }

    // A moved file's tags are written on the next run unless write-back is off, which drops that write (the move itself is filing).
    private bool TagsWanted() =>
        (configuration.LoadConfig<WriteBackConfiguration>(string.Empty, "writeback") ?? new WriteBackConfiguration())
        .AllowsTrigger(MediaFileWriteTriggers.LibraryMove);

    /// <summary>Writes each step the organiser takes onto the file's row.</summary>
    private sealed class StepRecorder(
        MediaFileWriteIntentRepository intents,
        IMediaAssetRepository assets,
        IAssetHasher hasher,
        MediaFileWriteIntent intent) : IRefileStepRecorder
    {
        public async Task PlannedAsync(string sourcePath, string targetPath, CancellationToken ct)
        {
            // The asset's recorded fingerprint, or one worked out now, tells a complete copy from a partial one after a stop.
            var asset = await assets.FindByIdAsync(intent.AssetId, ct);
            var hash = string.IsNullOrWhiteSpace(asset?.ContentHash)
                ? (await hasher.ComputeAsync(sourcePath, ct)).Hex
                : asset.ContentHash;
            await intents.RecordMovePlannedAsync(intent.Id, sourcePath, targetPath, hash, intent.Generation, ct);
        }

        public Task MovedAsync(string actualPath, CancellationToken ct) => intents.RecordMoveMovedAsync(intent.Id, actualPath, ct);

        public Task NotMovedAsync(CancellationToken ct) => intents.ResetMoveAsync(intent.Id, ct);

        public Task CheckedAsync(CancellationToken ct) => intents.RecordMoveCheckedAsync(intent.Id, ct);
    }
}
