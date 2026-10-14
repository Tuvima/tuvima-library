using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaEngine.Ingestion.Organise;

/// <summary>
/// Brings one catalogued file in a folder that is being converted to Managed to the type's standard name: it decides where
/// the file belongs, moves it (never overwriting), moves its sidecars, points the data store at the new path, writes the
/// History line and tags, and records every step in the job's ledger so an interrupted conversion can be finished.
/// </summary>
public sealed class FolderConversionOrganiser
{
    private const int NameRaceAttempts = 3;

    private readonly AutoOrganizeService _auto;
    private readonly IMediaAssetRepository _assets;
    private readonly ICanonicalValueRepository _canonicals;
    private readonly IFileOrganizer _organizer;
    private readonly IOrganizationGate _gate;
    private readonly IReviewQueueRepository _reviews;
    private readonly ISystemActivityRepository _activity;
    private readonly IFolderOrganiseLogRepository _ledger;
    private readonly IAssetHasher _hasher;
    private readonly ISourceMutationPolicyGate _policyGate;
    private readonly SidecarMover _sidecars;
    private readonly IOrganiseFileMover _mover;
    private readonly IOptionsMonitor<IngestionOptions> _options;
    private readonly IWriteBackOutcomeService? _writeBack;
    private readonly IFileHashCacheRepository? _hashCache;
    private readonly ILogger<FolderConversionOrganiser> _logger;

    public FolderConversionOrganiser(
        AutoOrganizeService auto,
        IMediaAssetRepository assets,
        ICanonicalValueRepository canonicals,
        IFileOrganizer organizer,
        IOrganizationGate gate,
        IReviewQueueRepository reviews,
        ISystemActivityRepository activity,
        IFolderOrganiseLogRepository ledger,
        IAssetHasher hasher,
        ISourceMutationPolicyGate policyGate,
        SidecarMover sidecars,
        IOrganiseFileMover mover,
        IOptionsMonitor<IngestionOptions> options,
        ILogger<FolderConversionOrganiser> logger,
        IWriteBackOutcomeService? writeBack = null,
        IFileHashCacheRepository? hashCache = null)
    {
        _auto = auto;
        _assets = assets;
        _canonicals = canonicals;
        _organizer = organizer;
        _gate = gate;
        _reviews = reviews;
        _activity = activity;
        _ledger = ledger;
        _hasher = hasher;
        _policyGate = policyGate;
        _sidecars = sidecars;
        _mover = mover;
        _options = options;
        _writeBack = writeBack;
        _hashCache = hashCache;
        _logger = logger;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Deciding (read-only; the preview and the job use the same rules)
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Works out what should happen to one catalogued file without touching the disk or the data store.
    /// </summary>
    /// <param name="plannedTargets">
    /// In a preview, the names earlier files in the same preview would take, so two files that want one name are reported
    /// as a collision. Null for the real job, which looks at the disk as it is.
    /// </param>
    public async Task<OrganiseDecision> DecideAsync(
        MediaAssetFileRef file,
        FolderConversionContext context,
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

        var name = await ResolveStandardNameAsync(file, current, context.LibraryEntry, ct).ConfigureAwait(false);
        if (name.Blocked is not null)
        {
            return name.Blocked;
        }

        var relative = name.RelativePath!;
        var target = Path.GetFullPath(Path.Combine(context.SourceRoot, relative));
        if (!PathSafety.IsContainedBy(target, context.SourceRoot))
        {
            return new OrganiseDecision(
                OrganiseDecisionKind.Denied, Reason: "The standard name would put the file outside this folder.");
        }

        if (string.Equals(current, target, StringComparison.Ordinal))
        {
            return new OrganiseDecision(OrganiseDecisionKind.AlreadyCorrect, Target: target);
        }

        var kind = IsSameFolder(current, target) ? SourceMutationKind.Rename : SourceMutationKind.Move;
        var denied = Deny(context, kind, current) ?? Deny(context, kind, target);
        if (denied is not null)
        {
            return new OrganiseDecision(OrganiseDecisionKind.Denied, Reason: denied);
        }

        // The same file with different letter case is a rename, not a collision and never a duplicate. On a drive that ignores
        // case (Windows, SMB and CIFS shares, exFAT, macOS) the new name "exists" because it is this very file, so the check is
        // by file identity on every system. Where the drive keeps both names as two files, they fall through to the checks below.
        if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase) && _mover.IsSameFile(current, target))
        {
            return new OrganiseDecision(OrganiseDecisionKind.Move, Target: target);
        }

        var occupiedByDisk = File.Exists(target);
        if (occupiedByDisk && _mover.IsSameFile(current, target))
        {
            // Another name for this same file (a link, or a folder reached two ways): nothing here is a copy to remove.
            return new OrganiseDecision(
                OrganiseDecisionKind.Skip, Reason: "That name already leads to this same file, so nothing was changed.");
        }

        var occupiedByPlan = plannedTargets?.Contains(target) == true;
        if (!occupiedByDisk && !occupiedByPlan)
        {
            return new OrganiseDecision(OrganiseDecisionKind.Move, Target: target);
        }

        if (occupiedByDisk && !occupiedByPlan && await IsIdenticalAsync(file, target, ct).ConfigureAwait(false))
        {
            return new OrganiseDecision(OrganiseDecisionKind.Duplicate, Target: target, Occupant: target);
        }

        return new OrganiseDecision(
            OrganiseDecisionKind.Collision,
            Target: UniqueTarget(target, plannedTargets),
            Reason: "Two different files want the same name.",
            Occupant: target);
    }

    /// <summary>
    /// The type's standard relative path for a catalogued file (under the library's naming rules), or the reason it cannot be
    /// named yet: not identified, waiting in Review, no confirmed title, or a name the organisation gate refuses. Shared by the
    /// organising job and the merge job, so both name files by exactly the same rules.
    /// </summary>
    public async Task<StandardName> ResolveStandardNameAsync(
        MediaAssetFileRef file, string currentPath, LibraryFolderEntry library, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(library);

        var canonicals = await _canonicals.GetByEntityAsync(file.AssetId, ct).ConfigureAwait(false);
        if (canonicals.Count == 0)
        {
            return new StandardName(null, new OrganiseDecision(
                OrganiseDecisionKind.Skip, Reason: "Not identified yet. Tuvima names it once it has been identified."));
        }

        var metadata = canonicals.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        var mediaType = metadata.TryGetValue("media_type", out var typeText)
            && Enum.TryParse<MediaType>(typeText, ignoreCase: true, out var parsed)
                ? parsed
                : (MediaType?)null;
        await _auto.ApplyRecordedEpisodeRangeAsync(file.AssetId, metadata, mediaType, ct).ConfigureAwait(false);

        var pending = await _reviews.GetPendingByEntityAsync(file.AssetId, ct).ConfigureAwait(false);
        var blocking = pending.FirstOrDefault(review =>
            review.ReviewReadyAt is not null
            && !string.Equals(review.Trigger, ReviewTrigger.WritebackFailed, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(review.Trigger, ReviewTrigger.NameCollision, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(review.Trigger, ReviewTrigger.FromMerge, StringComparison.OrdinalIgnoreCase));
        if (blocking is not null)
        {
            return new StandardName(null, new OrganiseDecision(
                OrganiseDecisionKind.NeedsReview, Reason: "It is waiting in Review for a person to confirm what it is."));
        }

        var title = metadata.GetValueOrDefault("title");
        if (string.IsNullOrWhiteSpace(title) || title is "Untitled" or "Unknown")
        {
            return new StandardName(null, new OrganiseDecision(
                OrganiseDecisionKind.NeedsReview, Reason: "It has no confirmed title yet."));
        }

        var candidate = new IngestionCandidate
        {
            Path = currentPath,
            EventType = FileEventType.Created,
            DetectedAt = DateTimeOffset.UtcNow,
            ReadyAt = DateTimeOffset.UtcNow,
            Metadata = metadata,
            DetectedMediaType = mediaType,
        };
        var template = _options.CurrentValue.ResolveTemplate(mediaType?.ToString());
        var relative = LibraryOrganizationPath.Calculate(_organizer, candidate, library, template);
        var gate = _gate.Evaluate(
            overallConfidence: 1.0,
            canonicalValues: metadata,
            hasUserLock: false,
            mediaTypeNeedsReview: false,
            resolvedRelativePath: relative);
        return gate.CanOrganize
            ? new StandardName(relative, null)
            : new StandardName(null, new OrganiseDecision(OrganiseDecisionKind.NeedsReview, Reason: gate.BlockReason));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Applying
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Carries out a decision for one file and records it in the ledger. A file that cannot be touched yet (it is open for
    /// playback, say) comes back as <see cref="ConversionOutcome.Locked"/> with the ledger left on its intent, so the job
    /// can try it again at the end of the pass.
    /// </summary>
    public async Task<ConversionOutcome> ApplyAsync(
        MediaAssetFileRef file,
        OrganiseDecision decision,
        FolderConversionContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(context);

        var current = Path.GetFullPath(file.FilePath);
        switch (decision.Kind)
        {
            case OrganiseDecisionKind.AlreadyCorrect:
                return await FinishAsync(context, current, current, FolderOrganiseOutcomes.AlreadyCorrect, null, ct).ConfigureAwait(false);

            case OrganiseDecisionKind.NeedsReview:
                return await FinishAsync(context, current, current, FolderOrganiseOutcomes.NeedsReview, decision.Reason, ct).ConfigureAwait(false);

            case OrganiseDecisionKind.Skip:
                return await FinishAsync(context, current, current, FolderOrganiseOutcomes.Skipped, decision.Reason, ct).ConfigureAwait(false);

            case OrganiseDecisionKind.Denied:
                return await FinishAsync(context, current, current, FolderOrganiseOutcomes.NotMoved, decision.Reason, ct).ConfigureAwait(false);

            case OrganiseDecisionKind.Duplicate
                when LibraryDuplicatePolicies.DeletesIncomingDuplicate(context.DuplicatePolicy):
                return await RemoveDuplicateAsync(file, decision, context, ct).ConfigureAwait(false);

            case OrganiseDecisionKind.Duplicate:
                // Keep both: this copy keeps its own numbered name beside the one already there.
                return await MoveAsync(
                    file, UniqueTarget(decision.Target!, null), context,
                    "An identical copy already has that name, so this one was kept under a numbered name.",
                    openReview: false, ct).ConfigureAwait(false);

            case OrganiseDecisionKind.Collision:
                return await MoveAsync(
                    file, decision.Target!, context,
                    $"A different file already has the standard name ({Path.GetFileName(decision.Occupant)}), so this one was kept under a numbered name.",
                    openReview: true, ct).ConfigureAwait(false);

            default:
                return await MoveAsync(file, decision.Target!, context, null, openReview: false, ct).ConfigureAwait(false);
        }
    }

    private async Task<ConversionOutcome> MoveAsync(
        MediaAssetFileRef file,
        string target,
        FolderConversionContext context,
        string? note,
        bool openReview,
        CancellationToken ct)
    {
        var current = Path.GetFullPath(file.FilePath);
        var uniqueName = note is not null;

        for (var attempt = 1; attempt <= NameRaceAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // The intent is written first, so a stop between the rename and the data store update is finished on restart.
            await WriteLedgerAsync(context, current, target, FolderOrganiseOutcomes.Moving, note, ct).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                // A name that differs only in letter case is one rename on every drive (it is the same file).
                CaseSafeRename.Move(_mover, current, target, _logger);
            }
            catch (IOException ex) when (File.Exists(target)
                && !string.Equals(current, target, StringComparison.OrdinalIgnoreCase)
                && attempt < NameRaceAttempts)
            {
                // Someone took the name a moment ago: pick the next free numbered name and try again.
                _logger.LogDebug(ex, "The name {Target} was taken; choosing another for {Current}", target, current);
                target = UniqueTarget(target, null);
                uniqueName = true;
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not move {Current} to {Target} yet", current, target);
                return new ConversionOutcome(FolderOrganiseOutcomes.Failed, current, ex.Message, Locked: true);
            }

            break;
        }

        return await CompleteMoveAsync(file, current, target, context, uniqueName, note, openReview, ct).ConfigureAwait(false);
    }

    /// <summary>Everything that follows a successful rename; also what a restart repeats for a move that was cut short.</summary>
    private async Task<ConversionOutcome> CompleteMoveAsync(
        MediaAssetFileRef file,
        string oldPath,
        string newPath,
        FolderConversionContext context,
        bool uniqueName,
        string? note,
        bool openReview,
        CancellationToken ct)
    {
        if (_hashCache is not null)
        {
            await _hashCache.MoveAsync(oldPath, newPath, ct).ConfigureAwait(false);
        }

        await _assets.UpdateFilePathAsync(file.AssetId, newPath, ct).ConfigureAwait(false);

        if (openReview)
        {
            await OpenCollisionReviewAsync(file.AssetId, newPath, note, ct).ConfigureAwait(false);
        }

        await LogHistoryAsync(file.AssetId, oldPath, newPath, context, ct).ConfigureAwait(false);
        await MoveCompanionsAsync(oldPath, newPath, context, ct).ConfigureAwait(false);
        await WriteTagsAsync(file.AssetId, context, ct).ConfigureAwait(false);
        RemoveEmptyFolders(Path.GetDirectoryName(oldPath)!, context);

        return await FinishAsync(
            context,
            oldPath,
            newPath,
            uniqueName ? FolderOrganiseOutcomes.MovedWithUniqueName : FolderOrganiseOutcomes.Moved,
            note,
            ct).ConfigureAwait(false);
    }

    private async Task<ConversionOutcome> RemoveDuplicateAsync(
        MediaAssetFileRef file,
        OrganiseDecision decision,
        FolderConversionContext context,
        CancellationToken ct)
    {
        var current = Path.GetFullPath(file.FilePath);
        var kept = decision.Occupant!;

        var denied = Deny(context, SourceMutationKind.Delete, current, deleting: true);
        if (denied is not null)
        {
            return await FinishAsync(context, current, current, FolderOrganiseOutcomes.NotMoved, denied, ct).ConfigureAwait(false);
        }

        // Two names for one file are not a copy: removing one would remove the only file.
        if (_mover.IsSameFile(current, kept))
        {
            return await FinishAsync(
                context, current, current, FolderOrganiseOutcomes.NotMoved,
                "Both names lead to the same file, so nothing was removed.", ct).ConfigureAwait(false);
        }

        // The copy that stays must still be there and still identical before the other is removed.
        if (!File.Exists(kept) || !await IsIdenticalAsync(file, kept, ct).ConfigureAwait(false))
        {
            return await FinishAsync(
                context, current, current, FolderOrganiseOutcomes.NotMoved,
                "The copy that should stay could not be confirmed, so nothing was removed.", ct).ConfigureAwait(false);
        }

        await WriteLedgerAsync(context, current, kept, FolderOrganiseOutcomes.Moving, "Removing an exact duplicate.", ct).ConfigureAwait(false);
        try
        {
            File.Delete(current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove the duplicate {Current} yet", current);
            return new ConversionOutcome(FolderOrganiseOutcomes.Failed, current, ex.Message, Locked: true);
        }

        if (_hashCache is not null)
        {
            await _hashCache.DeleteAsync(current, ct).ConfigureAwait(false);
        }

        // The data store keeps its one record for this content and now points it at the copy that stayed.
        await _assets.UpdateFilePathAsync(file.AssetId, kept, ct).ConfigureAwait(false);
        await LogHistoryAsync(file.AssetId, current, kept, context, ct, "Removed an exact duplicate copy");
        await MoveCompanionsAsync(current, kept, context, ct, mayDropIdentical: true).ConfigureAwait(false);
        RemoveEmptyFolders(Path.GetDirectoryName(current)!, context);

        return await FinishAsync(
            context, current, kept, FolderOrganiseOutcomes.DuplicateRemoved,
            "An exact copy was already at the standard name, so this copy was removed.", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Settles the data store for moves a stopped job did make, when the folder is no longer Managed so nothing more may be
    /// changed on disk: a file already at its new name gets its path updated and its ledger line closed; anything else is left.
    /// </summary>
    public async Task RecoverPathOnlyAsync(FolderOrganiseLogEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (File.Exists(entry.OldPath) || string.IsNullOrEmpty(entry.NewPath) || !File.Exists(entry.NewPath))
        {
            return;
        }

        var asset = await _assets.FindByPathRootAsync(entry.OldPath, ct).ConfigureAwait(false);
        if (asset is not null)
        {
            await _assets.UpdateFilePathAsync(asset.Id, entry.NewPath, ct).ConfigureAwait(false);
        }

        await _ledger.UpsertAsync(
            new FolderOrganiseLogEntry(
                entry.OperationId, entry.SourceId, entry.OldPath, entry.NewPath, FolderOrganiseOutcomes.Moved,
                DateTimeOffset.UtcNow, "Finished after the folder was switched to As-is; nothing else was changed."),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Finishes what a stopped job left half done: for each move that was started but never recorded as finished, completes
    /// it when the file is already at its new name, or retires the intent when nothing moved.
    /// </summary>
    public async Task RecoverAsync(FolderOrganiseLogEntry entry, FolderConversionContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(context);

        var oldExists = File.Exists(entry.OldPath);
        var newExists = !string.IsNullOrEmpty(entry.NewPath) && File.Exists(entry.NewPath);

        // A letter-case rename stopped between its two steps leaves the file under its temporary name: finish the second step.
        if (!oldExists && !newExists && !string.IsNullOrEmpty(entry.NewPath)
            && CaseSafeRename.IsCaseOnlyChange(entry.OldPath, entry.NewPath)
            && File.Exists(CaseSafeRename.TemporaryName(entry.OldPath)))
        {
            try
            {
                _mover.Move(CaseSafeRename.TemporaryName(entry.OldPath), entry.NewPath);
                newExists = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not finish the interrupted rename of {Old} to {New} yet", entry.OldPath, entry.NewPath);
                return;
            }
        }

        if (!oldExists && newExists)
        {
            var asset = await _assets.FindByPathRootAsync(entry.OldPath, ct).ConfigureAwait(false)
                ?? await _assets.FindByPathRootAsync(entry.NewPath, ct).ConfigureAwait(false);
            if (asset is null)
            {
                await FinishAsync(context, entry.OldPath, entry.NewPath, FolderOrganiseOutcomes.Skipped,
                    "The file is no longer in the library.", ct).ConfigureAwait(false);
                return;
            }

            var reference = new MediaAssetFileRef(asset.Id, entry.OldPath, asset.ContentHash, asset.LibraryId);
            var isDuplicate = entry.Detail?.StartsWith("Removing an exact duplicate", StringComparison.Ordinal) == true;
            if (isDuplicate)
            {
                await _assets.UpdateFilePathAsync(asset.Id, entry.NewPath, ct).ConfigureAwait(false);
                await MoveCompanionsAsync(entry.OldPath, entry.NewPath, context, ct, mayDropIdentical: true).ConfigureAwait(false);
                RemoveEmptyFolders(Path.GetDirectoryName(entry.OldPath)!, context);
                await FinishAsync(context, entry.OldPath, entry.NewPath, FolderOrganiseOutcomes.DuplicateRemoved,
                    "An exact copy was already at the standard name, so this copy was removed.", ct).ConfigureAwait(false);
                return;
            }

            var unique = entry.Detail is { Length: > 0 }
                && !entry.Detail.StartsWith("Removing", StringComparison.Ordinal);
            var collision = entry.Detail?.StartsWith("A different file already has", StringComparison.Ordinal) == true;
            await CompleteMoveAsync(reference, entry.OldPath, entry.NewPath, context, unique, entry.Detail,
                openReview: collision, ct).ConfigureAwait(false);
            return;
        }

        if (!oldExists && !newExists)
        {
            await FinishAsync(context, entry.OldPath, entry.NewPath, FolderOrganiseOutcomes.Skipped,
                "The file was no longer there when the job resumed.", ct).ConfigureAwait(false);
            return;
        }

        // Nothing moved (or both names exist): forget the intent so the file is looked at again from scratch.
        await _ledger.DeleteAsync(entry.OperationId, entry.OldPath, ct).ConfigureAwait(false);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Files on disk that are not in the library yet
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Looks at a media file Tuvima has not catalogued and says whether it is an exact copy of something the library already
    /// holds. Reads the file (or its cached fingerprint) but changes nothing.
    /// </summary>
    public async Task<UncataloguedClassification> ClassifyUncataloguedAsync(
        string path, FolderConversionContext context, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            return new UncataloguedClassification(false, null, "The file is not there any more.");
        }

        string hash;
        try
        {
            hash = await HashAsync(full, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new UncataloguedClassification(false, null, ex.Message, Locked: true);
        }

        var existing = await _assets.FindByHashAsync(hash, ct).ConfigureAwait(false);
        if (existing is not null && !string.Equals(Path.GetFullPath(existing.FilePathRoot), full, StringComparison.OrdinalIgnoreCase))
        {
            return new UncataloguedClassification(true, Path.GetFullPath(existing.FilePathRoot), "An exact copy of a file the library already holds.");
        }

        return new UncataloguedClassification(false, null, "Not in the library yet. It is identified first and named when that finishes.");
    }

    /// <summary>
    /// A media file in the folder that Tuvima has not catalogued. An exact copy of something already held is handled by the
    /// duplicate rule; anything else is handed to the normal identification pipeline, which names it when it is identified.
    /// </summary>
    public async Task<ConversionOutcome> HandleUncataloguedAsync(
        string path,
        FolderConversionContext context,
        IFileIntake? intake,
        CancellationToken ct = default)
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

        if (classification.Duplicate)
        {
            if (!LibraryDuplicatePolicies.DeletesIncomingDuplicate(context.DuplicatePolicy))
            {
                return await FinishAsync(context, full, full, FolderOrganiseOutcomes.NotMoved,
                    "An exact copy is held elsewhere; kept because the \"same file twice\" setting is Keep both.", ct).ConfigureAwait(false);
            }

            var kept = classification.KeptPath!;
            var denied = Deny(context, SourceMutationKind.Delete, full, deleting: true);
            if (denied is not null)
            {
                return await FinishAsync(context, full, full, FolderOrganiseOutcomes.NotMoved, denied, ct).ConfigureAwait(false);
            }

            if (!File.Exists(kept)
                || _mover.IsSameFile(full, kept)
                || new FileInfo(kept).Length != new FileInfo(full).Length
                || !string.Equals(await HashAsync(kept, ct).ConfigureAwait(false), await HashAsync(full, ct).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
            {
                return await FinishAsync(context, full, full, FolderOrganiseOutcomes.NotMoved,
                    "The copy that should stay could not be confirmed, so nothing was removed.", ct).ConfigureAwait(false);
            }

            try
            {
                File.Delete(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new ConversionOutcome(FolderOrganiseOutcomes.Failed, full, ex.Message, Locked: true);
            }

            if (_hashCache is not null)
            {
                await _hashCache.DeleteAsync(full, ct).ConfigureAwait(false);
            }

            RemoveEmptyFolders(Path.GetDirectoryName(full)!, context);
            return await FinishAsync(context, full, kept, FolderOrganiseOutcomes.DuplicateRemoved,
                "An exact copy was already in the library, so this copy was removed.", ct).ConfigureAwait(false);
        }

        if (intake is null)
        {
            return await FinishAsync(context, full, full, FolderOrganiseOutcomes.Skipped,
                "Not in the library yet. Normal scans identify and name it.", ct).ConfigureAwait(false);
        }

        await intake.EnqueueAsync(full, context.LibraryId, context.Source.Id, ct).ConfigureAwait(false);
        return await FinishAsync(context, full, full, FolderOrganiseOutcomes.QueuedForIdentification,
            "Not in the library yet. It was sent to be identified and is named when that finishes.", ct).ConfigureAwait(false);
    }


    // ─────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The first <c>Name (2).ext</c>, <c>Name (3).ext</c>... that neither exists on disk nor is planned.</summary>
    public static string UniqueTarget(string target, ISet<string>? plannedTargets)
    {
        var folder = Path.GetDirectoryName(target) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(target);
        var extension = Path.GetExtension(target);
        for (var number = 2; number < 10_000; number++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({number}){extension}");
            if (!File.Exists(candidate) && plannedTargets?.Contains(candidate) != true)
            {
                return candidate;
            }
        }

        return Path.Combine(folder, $"{stem} ({Guid.NewGuid():N}){extension}");
    }

    private string? Deny(FolderConversionContext context, SourceMutationKind kind, string path, bool deleting = false)
    {
        var decision = _policyGate.Evaluate(new SourceMutationRequest
        {
            Source = deleting ? context.DeletePolicy : context.Policy,
            Mutation = kind,
            Path = path,
        });
        return decision.Allowed ? null : decision.Reason ?? "This folder's settings do not allow that.";
    }

    private static bool IsSameFolder(string left, string right) =>
        string.Equals(Path.GetDirectoryName(left), Path.GetDirectoryName(right), StringComparison.OrdinalIgnoreCase);

    private async Task<bool> IsIdenticalAsync(MediaAssetFileRef file, string other, CancellationToken ct)
    {
        try
        {
            var current = Path.GetFullPath(file.FilePath);
            if (!File.Exists(other) || new FileInfo(other).Length != new FileInfo(current).Length)
            {
                return false;
            }

            // Both are read fresh (the cache only answers for a file whose size and time are unchanged): a file edited outside
            // Tuvima must never be removed because of an old stored fingerprint.
            var mine = await HashAsync(current, ct).ConfigureAwait(false);
            var theirs = await HashAsync(other, ct).ConfigureAwait(false);
            return string.Equals(mine, theirs, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A copy that cannot be read is not treated as identical, so nothing is ever removed on a guess.
            _ = ex;
            return false;
        }
    }

    private async Task<string> HashAsync(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        if (_hashCache is not null
            && await _hashCache.TryGetAsync(path, info.Length, modified, ct).ConfigureAwait(false) is { } cached)
        {
            return cached;
        }

        var result = await _hasher.ComputeAsync(path, ct).ConfigureAwait(false);
        if (_hashCache is not null)
        {
            await _hashCache.UpsertAsync(path, info.Length, modified, result.Hex, ct).ConfigureAwait(false);
        }

        return result.Hex;
    }

    private Task WriteLedgerAsync(
        FolderConversionContext context, string oldPath, string newPath, string outcome, string? detail, CancellationToken ct) =>
        _ledger.UpsertAsync(
            new FolderOrganiseLogEntry(context.OperationId, context.SourceId, oldPath, newPath, outcome, DateTimeOffset.UtcNow, detail),
            ct);

    private async Task<ConversionOutcome> FinishAsync(
        FolderConversionContext context, string oldPath, string newPath, string outcome, string? detail, CancellationToken ct)
    {
        await WriteLedgerAsync(context, oldPath, newPath, outcome, detail, ct).ConfigureAwait(false);
        return new ConversionOutcome(outcome, newPath, detail);
    }

    private async Task LogHistoryAsync(
        Guid assetId, string oldPath, string newPath, FolderConversionContext context, CancellationToken ct, string? lead = null)
    {
        try
        {
            await _activity.LogAsync(new SystemActivityEntry
            {
                ActionType = SystemActionType.PathUpdated,
                EntityId = assetId,
                EntityType = "MediaAsset",
                Detail = $"{lead ?? "Reorganised when the folder became Managed"}: "
                    + $"{Path.GetRelativePath(context.SourceRoot, oldPath)} -> {Path.GetRelativePath(context.SourceRoot, newPath)}",
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // History is a convenience copy of the ledger; a failure to write it must not undo a move that happened.
            _logger.LogDebug(ex, "Could not write the History line for asset {AssetId}", assetId);
        }
    }

    private async Task OpenCollisionReviewAsync(Guid assetId, string newPath, string? note, CancellationToken ct)
    {
        try
        {
            await _reviews.InsertAsync(new ReviewQueueEntry
            {
                Id = Guid.NewGuid(),
                EntityId = assetId,
                EntityType = "MediaAsset",
                Trigger = ReviewTrigger.NameCollision,
                Detail = note ?? "Two different files want the same name.",
                CreatedAt = DateTimeOffset.UtcNow,
                ReviewReadyAt = DateTimeOffset.UtcNow,
                AutomationCompletedAt = DateTimeOffset.UtcNow,
                RootCause = nameof(ReviewRootCause.ConflictingEvidence),
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not open the name-collision review for {Path}", newPath);
        }
    }

    private async Task MoveCompanionsAsync(
        string oldPath, string newPath, FolderConversionContext context, CancellationToken ct, bool mayDropIdentical = false)
    {
        // Artwork goes through the same careful mover as sidecars: nothing is overwritten, nothing is removed unless it is a
        // byte-identical copy of a file already there, and every move is in the ledger.
        var results = _sidecars.Move(
            oldPath,
            newPath,
            mayMove: (from, to) => Deny(context, IsSameFolder(from, to) ? SourceMutationKind.Rename : SourceMutationKind.Move, from) is null
                && Deny(context, IsSameFolder(from, to) ? SourceMutationKind.Rename : SourceMutationKind.Move, to) is null,
            mayDelete: mayDropIdentical ? from => Deny(context, SourceMutationKind.Delete, from, deleting: true) is null : null,
            includeArtwork: true);
        foreach (var result in results)
        {
            await WriteLedgerAsync(context, result.From, result.To, result.Outcome, result.Detail, ct).ConfigureAwait(false);
        }
    }

    private async Task WriteTagsAsync(Guid assetId, FolderConversionContext context, CancellationToken ct)
    {
        if (!context.WriteTags || _writeBack is null)
        {
            return;
        }

        try
        {
            await _writeBack.WriteMetadataWithOutcomeAsync(assetId, "auto_match", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A file whose tags could not be written keeps its new name; the re-tag sweep raises a Review item for it.
            _logger.LogDebug(ex, "Tags could not be written for asset {AssetId} after it moved", assetId);
        }
    }

    /// <summary>Removes an old folder once nothing is left in it, then its emptied parents, never the folder being converted.</summary>
    private void RemoveEmptyFolders(string folder, FolderConversionContext context)
    {
        try
        {
            var directory = new DirectoryInfo(folder);
            while (directory.Exists
                   && directory.Parent is not null
                   && PathSafety.IsContainedBy(directory.FullName, context.SourceRoot)
                   && !string.Equals(
                       Path.TrimEndingDirectorySeparator(directory.FullName), context.SourceRoot, PathComparison))
            {
                if (directory.EnumerateFileSystemInfos().Any())
                {
                    return;
                }

                if (Deny(context, SourceMutationKind.Delete, directory.FullName, deleting: true) is not null)
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

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
