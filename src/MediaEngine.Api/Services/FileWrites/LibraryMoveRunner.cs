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
    ILogger<LibraryMoveRunner> logger)
{
    internal const string LostReason = "The file was not found at its old or new place after the Engine stopped mid-move.";

    /// <summary>What happened to a move that was asked for.</summary>
    public enum MoveResultKind
    {
        /// <summary>The file is where it should be (or was deliberately left alone) and its row is ready for the scheduled tag write.</summary>
        Done,

        /// <summary>The move did not happen this time; the file is at its old place and the move can be tried again.</summary>
        Retry,

        /// <summary>The file could not be found; the row has been marked failed.</summary>
        Lost,

        /// <summary>The move is part-way and could not be settled yet; its steps stay on the row and the next start looks again.</summary>
        Interrupted,
    }

    public readonly record struct MoveResult(MoveResultKind Kind, string? Reason = null);

    /// <summary>Renames and moves the file of a claimed library move, recording each step. Never throws except for a stop.</summary>
    public async Task<MoveResult> MoveAsync(MediaFileWriteIntent intent, CancellationToken ct)
    {
        string failure;
        try
        {
            var steps = new StepRecorder(intents, assets, hasher, intent);
            var filed = await organizer.RefileAsync(intent.AssetId, steps, ct);
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
            try
            {
                switch (await RecoverOneAsync(row, ct))
                {
                    case Recovery.Finished:
                        return new MoveResult(MoveResultKind.Done);
                    case Recovery.Lost:
                        return new MoveResult(MoveResultKind.Lost, LostReason);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "A library move for asset {AssetId} is part-way and could not be settled yet.", row.AssetId);
                return new MoveResult(MoveResultKind.Interrupted, ex.Message);
            }
        }

        return new MoveResult(MoveResultKind.Retry, failure);
    }

    // ----- recovery -----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Run once when the Engine starts, before the move lane: looks at the disk for every move that had started and finishes or undoes it.
    /// Returns how many were looked at.
    /// </summary>
    public async Task<int> RecoverInterruptedAsync(CancellationToken ct)
    {
        await intents.ReleaseMoveClaimsAsync(ct);
        var started = await intents.GetStartedMovesAsync(ct);
        foreach (var row in started)
        {
            try
            {
                await RecoverOneAsync(row, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The row keeps its steps, so the next start looks again.
                logger.LogWarning(ex, "A library move that was cut short for asset {AssetId} could not be recovered yet.", row.AssetId);
            }
        }
        return started.Count;
    }

    internal enum Recovery
    {
        Finished,
        WillRetry,
        Lost,
    }

    internal async Task<Recovery> RecoverOneAsync(MediaFileWriteIntent row, CancellationToken ct)
    {
        var source = row.MoveSourcePath;
        var target = row.MoveTargetPath;

        // Checked means the file was verified at its new place and only the final bookkeeping was left.
        if (row.MoveStep == "checked" && !string.IsNullOrWhiteSpace(target) && File.Exists(target))
        {
            await intents.CompleteMoveAsync(row.Id, row.Generation, TagsWanted(), ct);
            return Recovery.Finished;
        }

        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
        {
            // Nothing was written down about where the file was going, so nothing was done to it.
            await intents.ResetMoveAsync(row.Id, ct);
            return Recovery.WillRetry;
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
            // An interrupted copy across drives: keep the target only when it is a complete copy of the source.
            if (await SameContentAsync(row, source, target, ct))
            {
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

        if (atTarget)
        {
            await organizer.CompleteInterruptedMoveAsync(row.AssetId, source, target, ct);
            var asset = await assets.FindByIdAsync(row.AssetId, ct);
            if (asset is null || !string.Equals(asset.FilePathRoot, target, StringComparison.OrdinalIgnoreCase))
            {
                // The data store could not follow (the asset went away, or another file holds the hash): the file stays where it is
                // and the move is looked at again on the next start.
                throw new IOException("The stored path could not be brought up to date for a move that had already happened.");
            }

            await intents.RecordMoveCheckedAsync(row.Id, ct);
            await intents.CompleteMoveAsync(row.Id, row.Generation, TagsWanted(), ct);
            return Recovery.Finished;
        }

        if (atSource)
        {
            await intents.ResetMoveAsync(row.Id, ct);
            return Recovery.WillRetry;
        }

        logger.LogWarning("The file of asset {AssetId} is at neither {Source} nor {Target} after an interrupted move.", row.AssetId, source, target);
        await intents.FailMoveAsync(row.Id, "failed", LostReason, ct);
        return Recovery.Lost;
    }

    // A moved file's tags are written on the next run unless write-back is off, which drops that write (the move itself is filing).
    private bool TagsWanted() =>
        (configuration.LoadConfig<WriteBackConfiguration>(string.Empty, "writeback") ?? new WriteBackConfiguration())
        .AllowsTrigger(MediaFileWriteTriggers.LibraryMove);

    private async Task<bool> SameContentAsync(MediaFileWriteIntent row, string source, string target, CancellationToken ct)
    {
        var expected = row.MoveSourceHash;
        if (string.IsNullOrWhiteSpace(expected))
        {
            expected = (await hasher.ComputeAsync(source, ct)).Hex;
        }
        var actual = (await hasher.ComputeAsync(target, ct)).Hex;
        return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
    }

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
            await intents.RecordMovePlannedAsync(intent.Id, sourcePath, targetPath, hash, ct);
        }

        public Task MovedAsync(string actualPath, CancellationToken ct) => intents.RecordMoveMovedAsync(intent.Id, actualPath, ct);

        public Task CheckedAsync(CancellationToken ct) => intents.RecordMoveCheckedAsync(intent.Id, ct);
    }
}
