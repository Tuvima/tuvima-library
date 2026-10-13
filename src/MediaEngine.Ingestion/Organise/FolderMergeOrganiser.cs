using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion.Organise;

/// <summary>
/// Brings one file of a folder that is being merged into a managed folder to its standard name there: it decides where the
/// file belongs under the same naming, duplicate and tag rules as converting a folder, puts a byte-checked copy in the
/// target (never overwriting), points the data store at that copy, moves or copies its sidecars, and records every step in the
/// job's ledger so a stopped merge can be finished. In Copy mode the source is only ever read. In Move mode an original is
/// removed only after its copy has been checked byte for byte, and never when the check fails.
/// </summary>
public sealed class FolderMergeOrganiser
{
    // Tags written into the ledger intent so a restart knows what a half-finished transfer was.
    private const string TagTransfer = "transfer";
    private const string TagUnique = "unique";
    private const string TagCollision = "collision";
    private const string TagReview = "review";
    private const string TagDuplicate = "duplicate";

    private readonly FolderConversionOrganiser _naming;
    private readonly IMediaAssetRepository _assets;
    private readonly IReviewQueueRepository _reviews;
    private readonly ISystemActivityRepository _activity;
    private readonly IFolderOrganiseLogRepository _ledger;
    private readonly IAssetHasher _hasher;
    private readonly ISourceMutationPolicyGate _policyGate;
    private readonly MergeFileTransfer _transfer;
    private readonly SidecarMover _sidecars;
    private readonly IWriteBackOutcomeService? _writeBack;
    private readonly IFileHashCacheRepository? _hashCache;
    private readonly ILogger<FolderMergeOrganiser> _logger;

    public FolderMergeOrganiser(
        FolderConversionOrganiser naming,
        IMediaAssetRepository assets,
        IReviewQueueRepository reviews,
        ISystemActivityRepository activity,
        IFolderOrganiseLogRepository ledger,
        IAssetHasher hasher,
        ISourceMutationPolicyGate policyGate,
        MergeFileTransfer transfer,
        SidecarMover sidecars,
        ILogger<FolderMergeOrganiser> logger,
        IWriteBackOutcomeService? writeBack = null,
        IFileHashCacheRepository? hashCache = null)
    {
        _naming = naming;
        _assets = assets;
        _reviews = reviews;
        _activity = activity;
        _ledger = ledger;
        _hasher = hasher;
        _policyGate = policyGate;
        _transfer = transfer;
        _sidecars = sidecars;
        _writeBack = writeBack;
        _hashCache = hashCache;
        _logger = logger;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Deciding (read-only; the preview and the job use the same rules)
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Works out what should happen to one catalogued file without touching the disk or the data store. A
    /// <see cref="OrganiseDecisionKind.Move"/> is a transfer to <see cref="OrganiseDecision.Target"/>; a
    /// <see cref="OrganiseDecisionKind.NeedsReview"/> is a file that cannot be named yet and goes to the From merge folder.
    /// </summary>
    /// <param name="plannedTargets">In a preview, the names earlier files would take, so two files that want one name are reported as a collision.</param>
    public async Task<OrganiseDecision> DecideAsync(
        MediaAssetFileRef file,
        FolderMergeContext context,
        ISet<string>? plannedTargets,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(context);

        var current = Path.GetFullPath(file.FilePath);
        if (!File.Exists(current))
        {
            return new OrganiseDecision(OrganiseDecisionKind.Skip, Reason: "The file is not there any more.");
        }

        var name = await _naming.ResolveStandardNameAsync(file, current, context.LibraryEntry, ct).ConfigureAwait(false);
        if (name.Blocked is not null)
        {
            // Not identified yet, waiting in Review, or no confirmed title: it still comes along, to the From merge folder.
            var holding = HoldingTarget(context, current, plannedTargets);
            var held = DenyTarget(context, holding) ?? DenySourceForMove(context, current);
            return held is not null
                ? new OrganiseDecision(OrganiseDecisionKind.Denied, Reason: held)
                : new OrganiseDecision(OrganiseDecisionKind.NeedsReview, Target: holding, Reason: name.Blocked.Reason);
        }

        var target = Path.GetFullPath(Path.Combine(context.TargetRoot, name.RelativePath!));
        if (!PathSafety.IsContainedBy(target, context.TargetRoot))
        {
            return new OrganiseDecision(OrganiseDecisionKind.Denied, Reason: "The standard name would put the file outside the target folder.");
        }

        var denied = DenyTarget(context, target) ?? DenySourceForMove(context, current);
        if (denied is not null)
        {
            return new OrganiseDecision(OrganiseDecisionKind.Denied, Reason: denied);
        }

        var occupiedByDisk = File.Exists(target);
        var occupiedByPlan = plannedTargets?.Contains(target) == true;
        if (!occupiedByDisk && !occupiedByPlan)
        {
            return new OrganiseDecision(OrganiseDecisionKind.Move, Target: target);
        }

        if (occupiedByDisk && !occupiedByPlan && await _transfer.SameContentAsync(current, target, ct).ConfigureAwait(false))
        {
            return new OrganiseDecision(OrganiseDecisionKind.Duplicate, Target: target, Occupant: target);
        }

        return new OrganiseDecision(
            OrganiseDecisionKind.Collision,
            Target: FolderConversionOrganiser.UniqueTarget(target, plannedTargets),
            Reason: "Two different files want the same name.",
            Occupant: target);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Applying
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Carries out a decision for one file and records it in the ledger. A file that cannot be read or written yet comes back as
    /// <see cref="ConversionOutcome.Locked"/> with the ledger left on its intent, so the job can try it again at the end of the pass.
    /// </summary>
    public async Task<ConversionOutcome> ApplyAsync(
        MediaAssetFileRef file,
        OrganiseDecision decision,
        FolderMergeContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(context);

        var current = Path.GetFullPath(file.FilePath);
        switch (decision.Kind)
        {
            case OrganiseDecisionKind.Skip:
                return await FinishAsync(context, current, current, FolderOrganiseOutcomes.Skipped, decision.Reason, ct).ConfigureAwait(false);

            case OrganiseDecisionKind.Denied:
                return await FinishAsync(context, current, current, FolderOrganiseOutcomes.NotMoved, decision.Reason, ct).ConfigureAwait(false);

            case OrganiseDecisionKind.AlreadyCorrect:
                return await FinishAsync(context, current, current, FolderOrganiseOutcomes.AlreadyCorrect, null, ct).ConfigureAwait(false);

            case OrganiseDecisionKind.Duplicate when LibraryDuplicatePolicies.DeletesIncomingDuplicate(context.DuplicatePolicy):
                return await HandleDuplicateAsync(file, decision.Occupant!, context, ct).ConfigureAwait(false);

            case OrganiseDecisionKind.Duplicate:
                // Keep both: this copy keeps its own numbered name beside the one already there.
                return await TransferAsync(
                    file, FolderConversionOrganiser.UniqueTarget(decision.Target!, null), context, TagUnique,
                    "An identical copy already has that name, so this one was kept under a numbered name.", ct).ConfigureAwait(false);

            case OrganiseDecisionKind.Collision:
                return await TransferAsync(
                    file, decision.Target!, context, TagCollision,
                    $"A different file already has the standard name ({Path.GetFileName(decision.Occupant)}), so this one was kept under a numbered name.", ct)
                    .ConfigureAwait(false);

            case OrganiseDecisionKind.NeedsReview:
                return await TransferAsync(
                    file, decision.Target!, context, TagReview,
                    $"Could not be named yet ({TrimStop(decision.Reason)}), so it was put in the From merge folder and waits in Review.", ct)
                    .ConfigureAwait(false);

            default:
                return await TransferAsync(file, decision.Target!, context, TagTransfer, null, ct).ConfigureAwait(false);
        }
    }

    private async Task<ConversionOutcome> TransferAsync(
        MediaAssetFileRef file,
        string target,
        FolderMergeContext context,
        string tag,
        string? note,
        CancellationToken ct)
    {
        var current = Path.GetFullPath(file.FilePath);

        // The intent is written first, so a stop between the copy and the data store update is finished on restart.
        await WriteLedgerAsync(context, current, target, FolderOrganiseOutcomes.Moving, Intent(tag, note), ct).ConfigureAwait(false);
        var copy = await _transfer.CopyVerifiedAsync(current, target, ct).ConfigureAwait(false);
        if (copy.FinalPath is null)
        {
            return new ConversionOutcome(
                FolderOrganiseOutcomes.Failed, current, copy.Failure ?? "The file could not be copied yet.", Locked: true);
        }

        var unique = copy.UniqueName || tag is TagUnique or TagCollision;
        return await CompleteTransferAsync(file, current, copy.FinalPath, tag, note, unique, context, ct).ConfigureAwait(false);
    }

    /// <summary>Everything that follows a verified copy; also what a restart repeats for a transfer that was cut short.</summary>
    private async Task<ConversionOutcome> CompleteTransferAsync(
        MediaAssetFileRef file,
        string oldPath,
        string newPath,
        string tag,
        string? note,
        bool unique,
        FolderMergeContext context,
        CancellationToken ct)
    {
        // The data store learns about the verified copy before the original is touched, so an item is never left pointing at
        // a file that is about to go.
        var repointed = await RepointAsync(file.AssetId, newPath, ct).ConfigureAwait(false);
        if (repointed && context.IsMove && _hashCache is not null)
        {
            await _hashCache.MoveAsync(oldPath, newPath, ct).ConfigureAwait(false);
        }

        if (repointed)
        {
            if (tag == TagCollision)
            {
                await OpenReviewAsync(file.AssetId, newPath, ReviewTrigger.NameCollision, ReviewRootCause.ConflictingEvidence, note, ct).ConfigureAwait(false);
            }
            else if (tag == TagReview)
            {
                await OpenFromMergeReviewAsync(file.AssetId, newPath, note, ct).ConfigureAwait(false);
            }
        }

        await TransferSidecarsAsync(oldPath, newPath, context, ct).ConfigureAwait(false);

        var detail = note;
        var removedOriginal = false;
        if (context.IsMove)
        {
            // The original is only removed once the data store points at the checked copy, so no item is left pointing at nothing.
            var denied = repointed
                ? DenySource(context, SourceMutationKind.Delete, oldPath)
                : "Copied, but the library already holds another item at that place, so the original was not removed.";
            if (denied is null && await _transfer.DeleteOriginalIfCopyMatchesAsync(oldPath, newPath, ct).ConfigureAwait(false))
            {
                removedOriginal = true;
                if (_hashCache is not null)
                {
                    await _hashCache.DeleteAsync(oldPath, ct).ConfigureAwait(false);
                }

                RemoveEmptySourceFolders(Path.GetDirectoryName(oldPath)!, context);
            }
            else
            {
                detail = JoinNotes(note, denied ?? "Copied and checked, but the original could not be removed, so it is still in the source folder.");
            }
        }

        await WriteTagsAsync(file.AssetId, context, ct).ConfigureAwait(false);
        await LogHistoryAsync(file.AssetId, oldPath, newPath, context, ct).ConfigureAwait(false);

        var outcome = tag == TagReview
            ? FolderOrganiseOutcomes.MergedForReview
            : context.IsMove && removedOriginal
                ? unique ? FolderOrganiseOutcomes.MovedWithUniqueName : FolderOrganiseOutcomes.Moved
                : unique ? FolderOrganiseOutcomes.CopiedWithUniqueName : FolderOrganiseOutcomes.Copied;
        return await FinishAsync(context, oldPath, newPath, outcome, detail, ct).ConfigureAwait(false);
    }

    /// <summary>An exact copy already has the standard name in the target (and the global rule is Keep one copy).</summary>
    private async Task<ConversionOutcome> HandleDuplicateAsync(
        MediaAssetFileRef file, string kept, FolderMergeContext context, CancellationToken ct)
    {
        var current = Path.GetFullPath(file.FilePath);

        // The copy that stays must still be there and still identical before anything is decided about this one.
        if (!await _transfer.SameContentAsync(current, kept, ct).ConfigureAwait(false))
        {
            return await FinishAsync(
                context, current, current, FolderOrganiseOutcomes.NotMoved,
                "The copy in the target could not be confirmed, so nothing was changed.", ct).ConfigureAwait(false);
        }

        if (!context.IsMove)
        {
            await RepointAsync(file.AssetId, kept, ct).ConfigureAwait(false);
            await TransferSidecarsAsync(current, kept, context, ct).ConfigureAwait(false);
            await LogHistoryAsync(file.AssetId, current, kept, context, ct, "Merge found an identical copy already in the target").ConfigureAwait(false);
            return await FinishAsync(
                context, current, kept, FolderOrganiseOutcomes.DuplicateSkipped,
                "An identical copy already has the standard name in the target, so nothing was copied. The original was left alone.", ct)
                .ConfigureAwait(false);
        }

        var denied = DenySource(context, SourceMutationKind.Delete, current);
        if (denied is not null)
        {
            return await FinishAsync(context, current, current, FolderOrganiseOutcomes.NotMoved, denied, ct).ConfigureAwait(false);
        }

        await WriteLedgerAsync(context, current, kept, FolderOrganiseOutcomes.Moving, Intent(TagDuplicate, "Removing an exact duplicate."), ct)
            .ConfigureAwait(false);
        return await CompleteDuplicateMoveAsync(file, current, kept, context, ct).ConfigureAwait(false);
    }

    private async Task<ConversionOutcome> CompleteDuplicateMoveAsync(
        MediaAssetFileRef file, string current, string kept, FolderMergeContext context, CancellationToken ct)
    {
        if (!await RepointAsync(file.AssetId, kept, ct).ConfigureAwait(false))
        {
            return await FinishAsync(
                context, current, current, FolderOrganiseOutcomes.NotMoved,
                "Another library item already holds the copy in the target, so this original was left where it is.", ct).ConfigureAwait(false);
        }

        if (!await _transfer.DeleteOriginalIfCopyMatchesAsync(current, kept, ct).ConfigureAwait(false))
        {
            return new ConversionOutcome(FolderOrganiseOutcomes.Failed, current, "The duplicate could not be removed yet.", Locked: true);
        }

        if (_hashCache is not null)
        {
            await _hashCache.DeleteAsync(current, ct).ConfigureAwait(false);
        }

        await TransferSidecarsAsync(current, kept, context, ct).ConfigureAwait(false);
        await LogHistoryAsync(file.AssetId, current, kept, context, ct, "Merge removed an exact duplicate copy").ConfigureAwait(false);
        RemoveEmptySourceFolders(Path.GetDirectoryName(current)!, context);
        return await FinishAsync(
            context, current, kept, FolderOrganiseOutcomes.DuplicateRemoved,
            "An exact copy was already at the standard name in the target, so this copy was removed.", ct).ConfigureAwait(false);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Recovery
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Settles the data store for transfers a stopped job did make, when the merge can no longer carry on so nothing more may be
    /// changed on disk: a file already in place gets its path updated and its ledger line closed; anything else is left.
    /// </summary>
    public async Task RecoverPathOnlyAsync(FolderOrganiseLogEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrEmpty(entry.NewPath) || !File.Exists(entry.NewPath))
        {
            return;
        }

        if (File.Exists(entry.OldPath) && !await _transfer.SameContentAsync(entry.OldPath, entry.NewPath, ct).ConfigureAwait(false))
        {
            return;
        }

        var asset = await _assets.FindByPathRootAsync(entry.OldPath, ct).ConfigureAwait(false);
        if (asset is not null)
        {
            await RepointAsync(asset.Id, entry.NewPath, ct).ConfigureAwait(false);
        }

        await _ledger.UpsertAsync(
            new FolderOrganiseLogEntry(
                entry.OperationId, entry.SourceId, entry.OldPath, entry.NewPath, FolderOrganiseOutcomes.Copied,
                DateTimeOffset.UtcNow, "Finished after the merge was stopped; nothing else was changed."),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Finishes what a stopped job left half done: for each transfer that was started but never recorded as finished, completes
    /// it when a verified copy is already in place, or retires the intent when nothing usable was put there.
    /// </summary>
    public async Task RecoverAsync(FolderOrganiseLogEntry entry, FolderMergeContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(context);

        var (tag, note) = ParseIntent(entry.Detail);
        var oldExists = File.Exists(entry.OldPath);
        var newExists = !string.IsNullOrEmpty(entry.NewPath) && File.Exists(entry.NewPath);

        if (!newExists)
        {
            // Nothing usable was put in place: clear any half-written temporary copy and look at the file again from scratch.
            if (!string.IsNullOrEmpty(entry.NewPath) && Path.GetDirectoryName(entry.NewPath) is { Length: > 0 } folder)
            {
                _transfer.RemoveLeftoverParts(folder);
            }

            await _ledger.DeleteAsync(entry.OperationId, entry.OldPath, ct).ConfigureAwait(false);
            return;
        }

        // The copy only ever gets its final name after it was checked, so a file at that name with the original's bytes is a
        // verified copy. If the original is still there, confirm that now; a different file is simply not ours.
        if (oldExists && !await _transfer.SameContentAsync(entry.OldPath, entry.NewPath, ct).ConfigureAwait(false))
        {
            await _ledger.DeleteAsync(entry.OperationId, entry.OldPath, ct).ConfigureAwait(false);
            return;
        }

        var asset = await _assets.FindByPathRootAsync(entry.OldPath, ct).ConfigureAwait(false)
            ?? await _assets.FindByPathRootAsync(entry.NewPath, ct).ConfigureAwait(false);
        if (asset is null)
        {
            if (oldExists)
            {
                // Not in the library (it was handled as a file to be identified): look at it again.
                await _ledger.DeleteAsync(entry.OperationId, entry.OldPath, ct).ConfigureAwait(false);
                return;
            }

            await FinishAsync(context, entry.OldPath, entry.NewPath, FolderOrganiseOutcomes.Skipped,
                "The file is no longer in the library.", ct).ConfigureAwait(false);
            return;
        }

        var reference = new MediaAssetFileRef(asset.Id, entry.OldPath, asset.ContentHash, asset.LibraryId);
        if (tag == TagDuplicate)
        {
            if (!context.IsMove)
            {
                await RepointAsync(asset.Id, entry.NewPath, ct).ConfigureAwait(false);
                await FinishAsync(context, entry.OldPath, entry.NewPath, FolderOrganiseOutcomes.DuplicateSkipped, note, ct).ConfigureAwait(false);
                return;
            }

            await CompleteDuplicateMoveAsync(reference, entry.OldPath, entry.NewPath, context, ct).ConfigureAwait(false);
            return;
        }

        await CompleteTransferAsync(
            reference, entry.OldPath, entry.NewPath, tag, note, unique: tag is TagUnique or TagCollision, context, ct).ConfigureAwait(false);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Files on disk that are not in the library yet
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Looks at a media file Tuvima has not catalogued and says whether it is an exact copy of something the library already
    /// holds. Reads the file (or its cached fingerprint) but changes nothing.
    /// </summary>
    public async Task<UncataloguedClassification> ClassifyUncataloguedAsync(
        string path, FolderMergeContext context, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            return new UncataloguedClassification(false, null, "The file is not there any more.");
        }

        string hash;
        try
        {
            hash = (await _hasher.ComputeAsync(full, ct).ConfigureAwait(false)).Hex;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new UncataloguedClassification(false, null, ex.Message, Locked: true);
        }

        var existing = await _assets.FindByHashAsync(hash, ct).ConfigureAwait(false);
        if (existing is not null && !PathSafety.Comparer.Equals(Path.GetFullPath(existing.FilePathRoot), full))
        {
            return new UncataloguedClassification(true, Path.GetFullPath(existing.FilePathRoot), "An exact copy of a file the library already holds.");
        }

        return new UncataloguedClassification(false, null, "Not in the library yet. It goes to the From merge folder and is identified there.");
    }

    /// <summary>
    /// A media file in the source that Tuvima has not catalogued. An exact copy of something already held is handled by the
    /// duplicate rule; anything else goes to the target's From merge folder and is handed to the normal identification
    /// pipeline, which names it when it is identified.
    /// </summary>
    public async Task<ConversionOutcome> HandleUncataloguedAsync(
        string path, FolderMergeContext context, IFileIntake? intake, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        var classification = await ClassifyUncataloguedAsync(full, context, ct).ConfigureAwait(false);
        if (classification.Locked)
        {
            return new ConversionOutcome(FolderOrganiseOutcomes.Failed, full, classification.Reason, Locked: true);
        }

        if (!File.Exists(full))
        {
            return await FinishAsync(context, full, full, FolderOrganiseOutcomes.Skipped, classification.Reason, ct).ConfigureAwait(false);
        }

        if (classification.Duplicate && LibraryDuplicatePolicies.DeletesIncomingDuplicate(context.DuplicatePolicy))
        {
            var kept = classification.KeptPath!;
            if (!await _transfer.SameContentAsync(full, kept, ct).ConfigureAwait(false))
            {
                return await FinishAsync(context, full, full, FolderOrganiseOutcomes.NotMoved,
                    "The copy the library holds could not be confirmed, so nothing was changed.", ct).ConfigureAwait(false);
            }

            if (!context.IsMove)
            {
                return await FinishAsync(context, full, kept, FolderOrganiseOutcomes.DuplicateSkipped,
                    "An exact copy is already in the library, so nothing was copied. The original was left alone.", ct).ConfigureAwait(false);
            }

            var denied = DenySource(context, SourceMutationKind.Delete, full);
            if (denied is not null)
            {
                return await FinishAsync(context, full, full, FolderOrganiseOutcomes.NotMoved, denied, ct).ConfigureAwait(false);
            }

            if (!await _transfer.DeleteOriginalIfCopyMatchesAsync(full, kept, ct).ConfigureAwait(false))
            {
                return new ConversionOutcome(FolderOrganiseOutcomes.Failed, full, "The duplicate could not be removed yet.", Locked: true);
            }

            if (_hashCache is not null)
            {
                await _hashCache.DeleteAsync(full, ct).ConfigureAwait(false);
            }

            RemoveEmptySourceFolders(Path.GetDirectoryName(full)!, context);
            return await FinishAsync(context, full, kept, FolderOrganiseOutcomes.DuplicateRemoved,
                "An exact copy was already in the library, so this copy was removed.", ct).ConfigureAwait(false);
        }

        // Anything else comes along to the From merge folder, to be identified and named there.
        var holding = HoldingTarget(context, full, plannedTargets: null);
        var blocked = DenyTarget(context, holding) ?? DenySourceForMove(context, full);
        if (blocked is not null)
        {
            return await FinishAsync(context, full, full, FolderOrganiseOutcomes.NotMoved, blocked, ct).ConfigureAwait(false);
        }

        const string note = "Not in the library yet, so it was put in the From merge folder and sent to be identified.";
        await WriteLedgerAsync(context, full, holding, FolderOrganiseOutcomes.Moving, Intent(TagReview, note), ct).ConfigureAwait(false);
        var copy = await _transfer.CopyVerifiedAsync(full, holding, ct).ConfigureAwait(false);
        if (copy.FinalPath is null)
        {
            return new ConversionOutcome(FolderOrganiseOutcomes.Failed, full, copy.Failure ?? "The file could not be copied yet.", Locked: true);
        }

        await TransferSidecarsAsync(full, copy.FinalPath, context, ct).ConfigureAwait(false);
        if (context.IsMove && DenySource(context, SourceMutationKind.Delete, full) is null
            && await _transfer.DeleteOriginalIfCopyMatchesAsync(full, copy.FinalPath, ct).ConfigureAwait(false))
        {
            RemoveEmptySourceFolders(Path.GetDirectoryName(full)!, context);
        }

        if (intake is not null)
        {
            await intake.EnqueueAsync(copy.FinalPath, context.LibraryId, context.Target.Id, ct).ConfigureAwait(false);
        }

        return await FinishAsync(context, full, copy.FinalPath, FolderOrganiseOutcomes.MergedForReview, note, ct).ConfigureAwait(false);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────────

    private static string HoldingTarget(FolderMergeContext context, string sourceFile, ISet<string>? plannedTargets)
    {
        var holding = context.HoldingPathFor(sourceFile);
        return File.Exists(holding) || plannedTargets?.Contains(holding) == true
            ? FolderConversionOrganiser.UniqueTarget(holding, plannedTargets)
            : holding;
    }

    /// <summary>Why the target's settings do not allow a file to be created at <paramref name="path"/>, or null when they do.</summary>
    private string? DenyTarget(FolderMergeContext context, string path)
    {
        var decision = _policyGate.Evaluate(new SourceMutationRequest
        {
            Source = context.TargetPolicy,
            Mutation = SourceMutationKind.UseAsDestination,
            Path = path,
        });
        return decision.Allowed ? null : decision.Reason ?? "The target folder's settings do not allow this.";
    }

    /// <summary>A move also needs the source folder's settings to allow moving out of it and removing the original.</summary>
    private string? DenySourceForMove(FolderMergeContext context, string path) =>
        !context.IsMove
            ? null
            : DenySource(context, SourceMutationKind.Move, path) ?? DenySource(context, SourceMutationKind.Delete, path);

    private string? DenySource(FolderMergeContext context, SourceMutationKind kind, string path)
    {
        var decision = _policyGate.Evaluate(new SourceMutationRequest
        {
            Source = context.SourcePolicy,
            Mutation = kind,
            Path = path,
        });
        return decision.Allowed ? null : decision.Reason ?? "The source folder's settings do not allow that.";
    }

    /// <summary>Points the data store at the verified copy, unless a different item already holds that path.</summary>
    private async Task<bool> RepointAsync(Guid assetId, string newPath, CancellationToken ct)
    {
        var holder = await _assets.FindByPathRootAsync(newPath, ct).ConfigureAwait(false);
        if (holder is not null && holder.Id != assetId)
        {
            _logger.LogInformation("Asset {AssetId} stays where it is: another item already holds {Path}", assetId, newPath);
            return false;
        }

        await _assets.UpdateFilePathAsync(assetId, newPath, ct).ConfigureAwait(false);
        return true;
    }

    private async Task TransferSidecarsAsync(string oldPath, string newPath, FolderMergeContext context, CancellationToken ct)
    {
        var results = _sidecars.Transfer(
            oldPath,
            newPath,
            context.IsMove,
            mayWrite: target => DenyTarget(context, target) is null,
            mayRemove: context.IsMove ? source => DenySource(context, SourceMutationKind.Delete, source) is null : null);
        foreach (var result in results)
        {
            await WriteLedgerAsync(context, result.From, result.To, result.Outcome, result.Detail, ct).ConfigureAwait(false);
        }
    }

    private Task WriteLedgerAsync(
        FolderMergeContext context, string oldPath, string newPath, string outcome, string? detail, CancellationToken ct) =>
        _ledger.UpsertAsync(
            new FolderOrganiseLogEntry(context.OperationId, context.SourceId, oldPath, newPath, outcome, DateTimeOffset.UtcNow, detail),
            ct);

    private async Task<ConversionOutcome> FinishAsync(
        FolderMergeContext context, string oldPath, string newPath, string outcome, string? detail, CancellationToken ct)
    {
        await WriteLedgerAsync(context, oldPath, newPath, outcome, detail, ct).ConfigureAwait(false);
        return new ConversionOutcome(outcome, newPath, detail);
    }

    private async Task LogHistoryAsync(
        Guid assetId, string oldPath, string newPath, FolderMergeContext context, CancellationToken ct, string? lead = null)
    {
        try
        {
            await _activity.LogAsync(new SystemActivityEntry
            {
                ActionType = SystemActionType.PathUpdated,
                EntityId = assetId,
                EntityType = "MediaAsset",
                Detail = $"{lead ?? (context.IsMove ? "Moved by a folder merge" : "Copied by a folder merge")}: "
                    + $"{Path.GetRelativePath(context.SourceRoot, oldPath)} -> {Path.GetRelativePath(context.TargetRoot, newPath)}",
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // History is a convenience copy of the ledger; a failure to write it must not undo a copy that happened.
            _logger.LogDebug(ex, "Could not write the History line for asset {AssetId}", assetId);
        }
    }

    private async Task OpenFromMergeReviewAsync(Guid assetId, string newPath, string? note, CancellationToken ct)
    {
        // An item that is already waiting in Review keeps that item; the From merge badge is for files nothing else flagged.
        var pending = await _reviews.GetPendingByEntityAsync(assetId, ct).ConfigureAwait(false);
        if (pending.Count > 0)
        {
            return;
        }

        await OpenReviewAsync(assetId, newPath, ReviewTrigger.FromMerge, ReviewRootCause.InsufficientEvidence, note, ct).ConfigureAwait(false);
    }

    private async Task OpenReviewAsync(
        Guid assetId, string newPath, string trigger, ReviewRootCause rootCause, string? note, CancellationToken ct)
    {
        try
        {
            await _reviews.InsertAsync(new ReviewQueueEntry
            {
                Id = Guid.NewGuid(),
                EntityId = assetId,
                EntityType = "MediaAsset",
                Trigger = trigger,
                Detail = note ?? "This file came from a folder merge and needs a person to confirm what it is.",
                CreatedAt = DateTimeOffset.UtcNow,
                ReviewReadyAt = DateTimeOffset.UtcNow,
                AutomationCompletedAt = DateTimeOffset.UtcNow,
                RootCause = rootCause.ToString(),
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not open the {Trigger} review for {Path}", trigger, newPath);
        }
    }

    private async Task WriteTagsAsync(Guid assetId, FolderMergeContext context, CancellationToken ct)
    {
        if (!context.WriteTags || _writeBack is null)
        {
            return;
        }

        try
        {
            // The asset now points at the copy in the target, so tags are only ever written there, never into the original.
            await _writeBack.WriteMetadataWithOutcomeAsync(assetId, "auto_match", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A file whose tags could not be written keeps its new place; the re-tag sweep raises a Review item for it.
            _logger.LogDebug(ex, "Tags could not be written for asset {AssetId} after it was merged", assetId);
        }
    }

    /// <summary>After a Move: removes a source subfolder once nothing is left in it, then its emptied parents. The source root stays.</summary>
    private void RemoveEmptySourceFolders(string folder, FolderMergeContext context)
    {
        if (!context.IsMove)
        {
            return;
        }

        try
        {
            var directory = new DirectoryInfo(folder);
            while (directory.Exists
                   && directory.Parent is not null
                   && PathSafety.IsContainedBy(directory.FullName, context.SourceRoot)
                   && !PathSafety.Comparer.Equals(Path.TrimEndingDirectorySeparator(directory.FullName), context.SourceRoot))
            {
                if (directory.EnumerateFileSystemInfos().Any())
                {
                    return;
                }

                if (DenySource(context, SourceMutationKind.Delete, directory.FullName) is not null)
                {
                    return;
                }

                var parent = directory.Parent;
                directory.Delete();
                directory = parent;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be removed now is simply left; it is not an error for the file that moved.
            _logger.LogDebug(ex, "Could not remove the empty folder {Folder}", folder);
        }
    }

    private static string Intent(string tag, string? note) => $"{tag}:{note}";

    private static (string Tag, string? Note) ParseIntent(string? detail)
    {
        if (string.IsNullOrEmpty(detail))
        {
            return (TagTransfer, null);
        }

        var cut = detail.IndexOf(':', StringComparison.Ordinal);
        if (cut <= 0)
        {
            return (TagTransfer, detail);
        }

        var tag = detail[..cut];
        var note = detail[(cut + 1)..];
        return tag is TagTransfer or TagUnique or TagCollision or TagReview or TagDuplicate
            ? (tag, string.IsNullOrEmpty(note) ? null : note)
            : (TagTransfer, detail);
    }

    private static string JoinNotes(string? first, string second) => string.IsNullOrEmpty(first) ? second : $"{first} {second}";

    private static string TrimStop(string? reason) => (reason ?? "no confirmed identity yet").TrimEnd('.');
}
