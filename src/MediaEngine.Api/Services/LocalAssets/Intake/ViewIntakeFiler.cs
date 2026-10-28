using System.Globalization;
using MediaEngine.Api.Services.Libraries;
using MediaEngine.Api.Services.View;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.LocalAssets.Intake;

/// <summary>
/// The one filer for photos and personal videos (Photo and video filing spec D2, D4, D5, D6, D9). Every way a file can arrive (copied
/// into the Import area by hand, a card reader, a phone sync, a browser upload) ends here: it is checked, dated from its metadata, checked
/// for an exact copy, moved to <c>Year/Year-Month-Day/original name</c> inside the person's library, and indexed. Each step is written to
/// the filing journal first, so a stop in the middle is finished or put back the next time Library starts.
/// </summary>
public sealed class ViewIntakeFiler(
    ViewIntakeAreas areas,
    IViewIntakeRepository intake,
    ViewLibraryService library,
    ViewStorageService storage,
    ILocalAssetRepository assets,
    IConfigurationLoader configuration,
    TimeProvider time,
    ILogger<ViewIntakeFiler> logger,
    ViewSharedTransferService? sharedTransfer = null,
    ViewIntakeActivity? activity = null,
    IViewTidyRepository? tidy = null)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly HashSet<string> TransientExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".part", ".partial", ".crdownload", ".download", ".uploading", ".!ut", ".filepart",
    };

    private static readonly HashSet<string> JunkNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Thumbs.db", "desktop.ini", ".DS_Store", "ehthumbs.db",
    };

    /// <summary>Free space at a path. Replaced in tests; the default reads the drive.</summary>
    internal Func<string, long?> FreeBytesAt { get; set; } = FreeSpaceCheck.FreeBytesAt;

    // ── Passes ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Looks at the Import and Rejected areas once: finishes anything the last run left half filed, files what has settled, clears
    /// out refused files past their time and notes strangers in the Rejected area.
    /// </summary>
    public async Task<IntakePassResult> FilePendingAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!areas.EnsureFolders())
            {
                return IntakePassResult.Empty;
            }

            var settings = configuration.LoadPhotoFiling();
            var results = new List<IntakeGroupResult>();
            await RecoverCoreAsync(results, ct).ConfigureAwait(false);
            await DropVanishedAsync(ct).ConfigureAwait(false);

            var remaining = Math.Max(1, settings.MaxFilesPerPass);
            foreach (var kind in new[] { ViewContentKinds.Photos, ViewContentKinds.PersonalVideos })
            {
                if (remaining <= 0 || areas.SectionPath(kind) is not { } section || !Directory.Exists(section))
                {
                    continue;
                }

                remaining -= await FileSectionAsync(kind, section, settings, remaining, results, ct).ConfigureAwait(false);
            }

            var strays = await CleanRejectedAsync(settings, ct).ConfigureAwait(false);
            await intake.PruneFinishedAsync(time.GetUtcNow().AddDays(-Math.Max(1, settings.RejectedRetentionDays)), ct).ConfigureAwait(false);
            var pass = new IntakePassResult(results);
            if (activity is not null)
            {
                if (results.Count > 0)
                {
                    await activity.RecordAsync(pass, ct).ConfigureAwait(false);
                }

                await activity.RecordStraysAsync(strays, ct).ConfigureAwait(false);
            }

            return pass;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Finishes or puts back files a stop left in the middle of filing. Run once when Library starts and on every pass.</summary>
    public async Task RecoverAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!areas.EnsureFolders())
            {
                return;
            }

            var results = new List<IntakeGroupResult>();
            await RecoverCoreAsync(results, ct).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task RecoverCoreAsync(List<IntakeGroupResult> results, CancellationToken ct)
    {
        var filing = await intake.GetByStateAsync([ViewIntakeStates.Filing], 5000, ct).ConfigureAwait(false);
        foreach (var batch in filing.GroupBy(row => row.GroupKey ?? row.Id.ToString("N")))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                results.Add(await ResumeAsync(batch.ToList(), ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "A half-filed group in the Import area could not be finished; it will be tried again.");
            }
        }
    }

    // A journalled group is finished the way it was going: the files that are still at their old path are moved to the recorded target,
    // then the group is indexed. A target that was never reached because the old file is gone too is dropped from the journal.
    private async Task<IntakeGroupResult> ResumeAsync(IReadOnlyList<ViewIntakeFile> rows, CancellationToken ct)
    {
        var primary = rows.FirstOrDefault(row => row.GroupRole is null or LocalAssetFileRoles.Primary) ?? rows[0];
        long bytes = 0;
        foreach (var row in rows)
        {
            if (row.TargetPath is null)
            {
                await intake.UpdateAsync(row.Id, ViewIntakeStates.Waiting, ct: ct).ConfigureAwait(false);
                continue;
            }

            if (!File.Exists(row.TargetPath) && File.Exists(row.CurrentPath))
            {
                MoveFile(row.CurrentPath, row.TargetPath);
            }

            if (File.Exists(row.TargetPath))
            {
                bytes += new FileInfo(row.TargetPath).Length;
            }
        }

        var filed = rows.Where(row => row.TargetPath is not null && File.Exists(row.TargetPath)).ToList();
        var destination = new IntakeDestination(primary.ScopeKind, primary.OwnerProfileId);
        if (filed.Count == 0 || primary.TargetPath is null || !File.Exists(primary.TargetPath))
        {
            foreach (var row in rows.Where(row => row.TargetPath is not null && !filed.Contains(row)))
            {
                await intake.DeleteAsync(row.Id, ct).ConfigureAwait(false);
            }

            return Result(IntakeOutcomes.Deferred, primary, destination, null, null, null, null, false, rows.Count, bytes);
        }

        var indexed = await IndexFiledAsync(primary, destination, filed, ct).ConfigureAwait(false);
        return Result(IntakeOutcomes.Filed, primary, destination, indexed.LibraryId, null, null, indexed.Result, false, filed.Count, bytes);
    }

    private async Task DropVanishedAsync(CancellationToken ct)
    {
        var waiting = await intake.GetByStateAsync([ViewIntakeStates.Waiting], 5000, ct).ConfigureAwait(false);
        foreach (var row in waiting.Where(row => !File.Exists(row.CurrentPath)))
        {
            await intake.DeleteAsync(row.Id, ct).ConfigureAwait(false);
        }
    }

    private async Task<int> FileSectionAsync(
        string kind, string section, PhotoFilingSettings settings, int budget,
        List<IntakeGroupResult> results, CancellationToken ct)
    {
        var taken = 0;
        var now = time.GetUtcNow();
        var settle = TimeSpan.FromSeconds(Math.Max(0, settings.SettleSeconds));
        foreach (var directory in EnumerateDirectories(section))
        {
            ct.ThrowIfCancellationRequested();
            var files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => !IsTransient(Path.GetFileName(path)))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (files.Count == 0)
            {
                continue;
            }

            var destination = await areas.ResolveDestinationAsync(files[0], ct).ConfigureAwait(false);
            var candidates = new List<ViewLibraryService.FileCandidate>();
            var others = new List<string>();
            foreach (var path in files)
            {
                if (ViewLibraryService.TryCreateCandidate(path) is { } candidate)
                {
                    candidates.Add(candidate);
                }
                else
                {
                    others.Add(path);
                }
            }

            foreach (var group in ViewLibraryService.BuildGroups(candidates))
            {
                if (taken >= budget)
                {
                    return taken;
                }

                var members = group.Files.Select(member => (member.Candidate.Path, member.Role)).ToList();
                if (!await IsSettledAsync(members, kind, destination, now, settle, ct).ConfigureAwait(false))
                {
                    continue;
                }

                taken += members.Count;
                results.Add(await FileGroupAsync(kind, destination, group, null, immediate: false, ct).ConfigureAwait(false));
            }

            // Companion files that belong to no group (a stray .xmp) and files Library cannot read are refused with a reason.
            foreach (var path in others.Concat(candidates
                         .Where(candidate => candidate.Extension == ".xmp"
                             && !ViewLibraryService.BuildGroups(candidates).Any(group => group.Files.Any(member => member.Candidate == candidate)))
                         .Select(candidate => candidate.Path)))
            {
                if (taken >= budget)
                {
                    return taken;
                }

                if (!await IsSettledAsync([(path, LocalAssetFileRoles.Primary)], kind, destination, now, settle, ct).ConfigureAwait(false))
                {
                    continue;
                }

                taken++;
                results.Add(await RejectLooseAsync(kind, destination, path, ViewIntakeReasons.UnsupportedType, ct).ConfigureAwait(false));
            }
        }

        return taken;
    }

    // A file is settled when the same size was seen on two passes at least the settle time apart and nothing else holds it open.
    private async Task<bool> IsSettledAsync(
        IReadOnlyList<(string Path, string Role)> members, string kind, IntakeDestination? destination,
        DateTimeOffset now, TimeSpan settle, CancellationToken ct)
    {
        var settled = true;
        var groupKey = GroupKeyOf(members[0].Path);
        foreach (var (path, role) in members)
        {
            long size;
            try
            {
                size = new FileInfo(path).Length;
            }
            catch (IOException)
            {
                settled = false;
                continue;
            }

            var row = await intake.AddAsync(new ViewIntakeFile(
                Guid.NewGuid(), kind, destination?.ScopeKind ?? IntakeDestination.Personal, destination?.OwnerProfileId, null,
                Path.GetFileName(path), path, null, null, groupKey, role, size, null, ViewIntakeStates.Waiting,
                null, null, now, now, null), ct).ConfigureAwait(false);
            if (row.ByteSize != size)
            {
                await intake.SetSizeAsync(row.Id, size, ct).ConfigureAwait(false);
                settled = false;
            }
            else if (settle > TimeSpan.Zero && now - row.UpdatedAt < settle)
            {
                settled = false;
            }
            else if (!CanOpenExclusively(path))
            {
                settled = false;
            }
        }

        return settled;
    }

    // ── One group ────────────────────────────────────────────────────────────

    /// <summary>
    /// Checks, dates, de-duplicates and files one group (the main file with its RAW, sidecar or Live Photo video). The outcome is the
    /// group's result; a group that cannot be filed yet (no room, the folder is offline) is left where it is.
    /// </summary>
    private async Task<IntakeGroupResult> FileGroupAsync(
        string kind, IntakeDestination? destination, ViewLibraryService.AssetGroup group, Guid? addedBy,
        bool immediate, CancellationToken ct)
    {
        var members = group.Files.Select(member => (Candidate: member.Candidate, member.Role)).ToList();
        var primaryPath = group.Primary.Path;
        var fileName = Path.GetFileName(primaryPath);
        var bytes = members.Sum(member => new FileInfo(member.Candidate.Path).Length);
        var contentKind = ViewKindAcceptance.OwnKindOf(group.Primary.Type.MediaKind);
        var effectiveDestination = destination ?? IntakeDestination.SharedLibrary;

        var rows = new List<ViewIntakeFile>();
        foreach (var member in members)
        {
            rows.Add(await RowForAsync(kind, destination, member.Candidate.Path, member.Role, addedBy, ct).ConfigureAwait(false));
        }

        // 1. Is it something Library can file, and is it whole?
        if (destination is null)
        {
            return await DeferAsync(rows, effectiveDestination, kind, bytes, ViewIntakeReasons.DestinationOffline, null, immediate, ct).ConfigureAwait(false);
        }

        if (contentKind is not (ViewContentKinds.Photos or ViewContentKinds.PersonalVideos))
        {
            return await RejectGroupAsync(kind, destination, rows, ViewIntakeReasons.UnsupportedType, ct).ConfigureAwait(false);
        }

        if (members.Any(member => !ViewFilingLayout.IsStorableName(Path.GetFileName(member.Candidate.Path))))
        {
            return await RejectGroupAsync(kind, destination, rows, ViewIntakeReasons.BadName, ct).ConfigureAwait(false);
        }

        foreach (var member in members)
        {
            if (new FileInfo(member.Candidate.Path).Length == 0 && member.Candidate.Extension != ".xmp")
            {
                return await RejectGroupAsync(kind, destination, rows, ViewIntakeReasons.Empty, ct).ConfigureAwait(false);
            }

            if (ViewFileIntegrity.Check(member.Candidate.Path, member.Candidate.Type.MediaKind) is { } problem)
            {
                return await RejectGroupAsync(kind, destination, rows, problem, ct).ConfigureAwait(false);
            }
        }

        // 2. Where does it go, and is there room?
        var stage = await ResolveStageAsync(destination, ct).ConfigureAwait(false);
        if (stage is null || !storage.IsStorageWritable())
        {
            return await DeferAsync(rows, destination, kind, bytes, ViewIntakeReasons.DestinationOffline, null, immediate, ct).ConfigureAwait(false);
        }

        if (tidy is not null && await tidy.IsLibraryBeingTidiedAsync(stage.Space.LibraryId, ct).ConfigureAwait(false))
        {
            // The folders are being renamed in place; new files wait so nothing lands in the middle of the run.
            return await DeferAsync(rows, destination, kind, bytes, ViewIntakeReasons.BeingTidied, null, immediate, ct).ConfigureAwait(false);
        }

        var space = FreeSpaceCheck.Check(members.Select(member => new FileInfo(member.Candidate.Path).Length), FreeBytesAt(stage.Root));
        if (!space.Fits)
        {
            return await DeferAsync(rows, destination, kind, bytes, ViewIntakeReasons.NotEnoughSpace, space.Message, immediate, ct).ConfigureAwait(false);
        }

        // 3. When was it taken, and is it already there?
        var date = await library.ReadCaptureDateAsync(group.Primary, ct).ConfigureAwait(false);
        var hash = await ViewLibraryService.HashAsync(primaryPath, ct).ConfigureAwait(false);
        // A file for the Shared Library is checked against the Shared Library, not against the staging person's own space.
        var forShared = destination.ScopeKind == IntakeDestination.Shared;
        var presence = forShared
            ? sharedTransfer?.FindInSharedLibrary(stage.Space.OwnerProfileId, hash, ct)
            : assets.FindPrimaryContent(IntakeDestination.Personal, stage.Space.LibraryId, stage.Space.Id, hash, ct);
        var duplicate = false;
        if (presence is not null)
        {
            if (presence.InTrash)
            {
                return await RejectGroupAsync(kind, destination, rows, ViewIntakeReasons.DuplicateInTrash, ct, hash).ConfigureAwait(false);
            }

            if (forShared || LibraryDuplicatePolicies.DeletesIncomingDuplicate(DuplicatePolicyFor(stage.Space.LibraryId)))
            {
                foreach (var row in rows)
                {
                    TryDelete(row.CurrentPath);
                    await intake.UpdateAsync(row.Id, ViewIntakeStates.Duplicate, contentHash: row.Id == rows[0].Id ? hash : null, ct: ct).ConfigureAwait(false);
                }

                return Result(IntakeOutcomes.Duplicate, rows[0], destination, stage.Space.LibraryId, null, null,
                    new LocalAssetUpsertResult(presence.ItemId, false, 0, 0), false, rows.Count, bytes);
            }

            duplicate = true;
        }

        // 4. File it: journal, move, index, finish.
        var settings = configuration.LoadPhotoFiling();
        var targetDirectory = Path.Combine(stage.Root, ViewFilingLayout.FolderFor(settings.LayoutPattern, date.Value));
        var names = ViewFilingLayout.NamesFor(targetDirectory,
            members.Select(member => Path.GetFileName(member.Candidate.Path)).ToList(), File.Exists);
        var targets = names.Select(name => Path.Combine(targetDirectory, name)).ToList();
        for (var i = 0; i < rows.Count; i++)
        {
            await intake.UpdateAsync(rows[i].Id, ViewIntakeStates.Filing, targetPath: targets[i],
                contentHash: i == 0 ? hash : null, ct: ct).ConfigureAwait(false);
        }

        Directory.CreateDirectory(targetDirectory);
        for (var i = 0; i < rows.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            MoveFile(rows[i].CurrentPath, targets[i]);
        }

        var filedRows = rows.Select((row, i) => row with { TargetPath = targets[i], State = ViewIntakeStates.Filing }).ToList();
        var indexed = await IndexFiledAsync(filedRows[0], destination, filedRows, ct).ConfigureAwait(false);
        if (indexed.Result is { ItemAdded: false, FilesAdded: 0, SourcesAdded: 0 } && !duplicate)
        {
            duplicate = true;
        }

        return Result(duplicate ? IntakeOutcomes.Duplicate : IntakeOutcomes.Filed, filedRows[0], destination, stage.Space.LibraryId,
            null, null, indexed.Result, false, filedRows.Count, bytes);
    }

    private async Task<(Guid? LibraryId, LocalAssetUpsertResult? Result)> IndexFiledAsync(
        ViewIntakeFile primary, IntakeDestination destination, IReadOnlyList<ViewIntakeFile> rows, CancellationToken ct)
    {
        LocalAssetUpsertResult? result = null;
        Guid? libraryId = null;
        var ownerId = destination.OwnerProfileId ?? (await StageOwnerAsync(ct).ConfigureAwait(false));
        if (ownerId is { } owner && primary.TargetPath is { } primaryPath)
        {
            try
            {
                var space = await storage.EnsurePersonalSpaceAsync(owner, ct).ConfigureAwait(false);
                libraryId = space.LibraryId;
                result = await library.IndexPathAsync(space.LibraryId, primaryPath, primary.AddedByProfileId ?? owner, ct).ConfigureAwait(false);
                if (destination.ScopeKind == IntakeDestination.Shared && sharedTransfer is not null && result is { } staged)
                {
                    await sharedTransfer.ExecuteAsync(staged.ItemId, primary.AddedByProfileId ?? owner, "timeline", null, null, ct)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The file is safely in the library folder; a library scan will index it, so the group is not left half filed.
                logger.LogWarning(ex, "Filed '{Name}' but could not index it yet; the next scan will.", primary.OriginalName);
            }
        }

        foreach (var row in rows)
        {
            await intake.UpdateAsync(row.Id, ViewIntakeStates.Filed, filedPath: row.TargetPath, ct: ct).ConfigureAwait(false);
        }

        return (libraryId, result);
    }

    private async Task<ViewIntakeFile> RowForAsync(
        string kind, IntakeDestination? destination, string path, string role, Guid? addedBy, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return await intake.AddAsync(new ViewIntakeFile(
            Guid.NewGuid(), kind, destination?.ScopeKind ?? IntakeDestination.Personal, destination?.OwnerProfileId, addedBy,
            Path.GetFileName(path), path, null, null, GroupKeyOf(path), role, new FileInfo(path).Length, null,
            ViewIntakeStates.Waiting, null, null, now, now, null), ct).ConfigureAwait(false);
    }

    private sealed record Stage(ViewPersonalSpace Space, string Root);

    // The folder a file is filed into: the person's own managed intake folder (for the Shared Library, the household administrator's,
    // from where the transfer to Shared moves it).
    private async Task<Stage?> ResolveStageAsync(IntakeDestination destination, CancellationToken ct)
    {
        var owner = destination.OwnerProfileId ?? await StageOwnerAsync(ct).ConfigureAwait(false);
        if (owner is not { } ownerId)
        {
            return null;
        }

        try
        {
            var space = await storage.EnsurePersonalSpaceAsync(ownerId, ct).ConfigureAwait(false);
            var source = await storage.EnsureManagedSourceAsync(
                space, "Browser uploads", ViewSourceType.BrowserUpload, ViewKindAcceptance.BrowserUploadSourceKey, ct).ConfigureAwait(false);
            return new Stage(space, storage.GetSourcePath(space, source));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning(ex, "The library folder for a profile could not be reached.");
            return null;
        }
    }

    private async Task<Guid?> StageOwnerAsync(CancellationToken ct)
    {
        var section = areas.SectionPath(ViewContentKinds.Photos);
        var owner = section is null ? null : await areas.ResolveDestinationAsync(Path.Combine(section, "x"), ct).ConfigureAwait(false);
        return owner?.OwnerProfileId;
    }

    private string DuplicatePolicyFor(Guid libraryId)
    {
        var libraries = configuration.LoadLibraries();
        var own = libraries.Libraries.FirstOrDefault(candidate => Guid.TryParse(candidate.Id, out var id) && id == libraryId);
        return LibraryFileHandlingResolver.ResolveDuplicatePolicy(
            libraries.FileHandling.DuplicatePolicy, own?.FileHandling.DuplicatePolicy, null);
    }

    // ── Refusals and deferrals ───────────────────────────────────────────────

    private async Task<IntakeGroupResult> DeferAsync(
        IReadOnlyList<ViewIntakeFile> rows, IntakeDestination destination, string kind, long bytes,
        string code, string? message, bool immediate, CancellationToken ct)
    {
        var sentence = message ?? ViewIntakeReasons.Sentence(code);
        foreach (var row in rows)
        {
            if (row.ReasonCode != code)
            {
                await intake.UpdateAsync(row.Id, ViewIntakeStates.Waiting, reasonCode: code, reasonText: sentence, ct: ct).ConfigureAwait(false);
            }
        }

        if (immediate)
        {
            // A refused request leaves nothing behind: the upload is not kept waiting.
            foreach (var row in rows)
            {
                TryDelete(row.CurrentPath);
                await intake.DeleteAsync(row.Id, ct).ConfigureAwait(false);
            }

            throw new IntakeRefusedException(code, sentence,
                code == ViewIntakeReasons.NotEnoughSpace ? StatusCodes.Status507InsufficientStorage : StatusCodes.Status503ServiceUnavailable);
        }

        return Result(IntakeOutcomes.Deferred, rows[0], destination, null, code, sentence, null, false, rows.Count, bytes);
    }

    private async Task<IntakeGroupResult> RejectGroupAsync(
        string kind, IntakeDestination destination, IReadOnlyList<ViewIntakeFile> rows, string code, CancellationToken ct,
        string? hash = null)
    {
        var sentence = ViewIntakeReasons.Sentence(code);
        var settings = configuration.LoadPhotoFiling();
        var expires = time.GetUtcNow().AddDays(Math.Max(1, settings.RejectedRetentionDays));
        var folder = areas.RejectedPath(kind) ?? throw new IOException("There is no Rejected area.");
        Directory.CreateDirectory(folder);
        var names = ViewFilingLayout.NamesFor(folder, rows.Select(row => Path.GetFileName(row.CurrentPath)).ToList(), File.Exists);
        long bytes = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var target = Path.Combine(folder, names[i]);
            bytes += File.Exists(rows[i].CurrentPath) ? new FileInfo(rows[i].CurrentPath).Length : 0;
            MoveFile(rows[i].CurrentPath, target);
            await intake.UpdateAsync(rows[i].Id, ViewIntakeStates.Rejected, currentPath: target, contentHash: i == 0 ? hash : null,
                reasonCode: code, reasonText: sentence, expiresAt: expires, ct: ct).ConfigureAwait(false);
        }

        return Result(IntakeOutcomes.Rejected, rows[0], destination, null, code, sentence, null,
            code == ViewIntakeReasons.DuplicateInTrash, rows.Count, bytes);
    }

    private async Task<IntakeGroupResult> RejectLooseAsync(
        string kind, IntakeDestination? destination, string path, string code, CancellationToken ct)
    {
        var row = await RowForAsync(kind, destination, path, LocalAssetFileRoles.Primary, null, ct).ConfigureAwait(false);
        return await RejectGroupAsync(kind, destination ?? IntakeDestination.SharedLibrary, [row], code, ct).ConfigureAwait(false);
    }

    private static IntakeGroupResult Result(
        string outcome, ViewIntakeFile row, IntakeDestination destination, Guid? libraryId, string? code, string? sentence,
        LocalAssetUpsertResult? upsert, bool inTrash, int fileCount, long bytes) =>
        new(outcome, row.OriginalName, row.ContentKind, destination, libraryId, code, sentence, upsert?.ItemId,
            upsert?.ItemAdded ?? false, upsert?.FilesAdded ?? 0, upsert?.SourcesAdded ?? 0, inTrash, fileCount, bytes);

    // ── Rejected area upkeep ─────────────────────────────────────────────────

    private async Task<int> CleanRejectedAsync(PhotoFilingSettings settings, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var newStrays = 0;
        foreach (var row in await intake.GetExpiredAsync(now, 500, ct).ConfigureAwait(false))
        {
            // Only a file really inside the Rejected area is ever deleted, whatever the stored path says.
            if (areas.IsInRejectedArea(row.CurrentPath))
            {
                TryDelete(row.CurrentPath);
            }

            await intake.DeleteAsync(row.Id, ct).ConfigureAwait(false);
        }

        // Rows whose file a person already took out of the Rejected area.
        foreach (var row in await intake.GetByStateAsync([ViewIntakeStates.Rejected, ViewIntakeStates.Stray], 2000, ct).ConfigureAwait(false))
        {
            if (!File.Exists(row.CurrentPath))
            {
                await intake.DeleteAsync(row.Id, ct).ConfigureAwait(false);
            }
        }

        // Files in the Rejected area that Library did not put there are noted, never moved or deleted.
        foreach (var kind in new[] { ViewContentKinds.Photos, ViewContentKinds.PersonalVideos })
        {
            if (areas.RejectedPath(kind) is not { } rejected || !Directory.Exists(rejected))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(rejected, "*", SearchOption.AllDirectories)
                         .Where(path => !IsTransient(Path.GetFileName(path))))
            {
                ct.ThrowIfCancellationRequested();
                var id = Guid.NewGuid();
                var row = await intake.AddAsync(new ViewIntakeFile(
                    id, kind, IntakeDestination.Personal, null, null, Path.GetFileName(path), path, null, null, null, null,
                    new FileInfo(path).Length, null, ViewIntakeStates.Stray, ViewIntakeReasons.Stray,
                    ViewIntakeReasons.Sentence(ViewIntakeReasons.Stray), now, now, null), ct).ConfigureAwait(false);
                if (row.Id == id)
                {
                    newStrays++;
                }
            }
        }

        return newStrays;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    internal static bool IsTransient(string fileName) =>
        string.IsNullOrEmpty(fileName)
        || fileName.StartsWith('.') || fileName.StartsWith('~')
        || JunkNames.Contains(fileName)
        || TransientExtensions.Contains(Path.GetExtension(fileName));

    private static IEnumerable<string> EnumerateDirectories(string section)
    {
        var pending = new Stack<string>();
        pending.Push(section);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            yield return directory;
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(directory)
                    .Where(child => !Path.GetFileName(child).StartsWith('.'))
                    .OrderByDescending(child => child, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                pending.Push(child);
            }
        }
    }

    private static string GroupKeyOf(string path) =>
        Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, Path.GetFileNameWithoutExtension(path)).ToLowerInvariant();

    private static bool CanOpenExclusively(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static void MoveFile(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Move(from, to, overwrite: false);
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "A file could not be removed.");
        }
    }

    // ── Browser uploads ──────────────────────────────────────────────────────

    /// <summary>
    /// Files one browser upload through the same filer. The file is written to a working folder first, so a half-received upload is never
    /// mistaken for a waiting one; it is refused with a reason when it is damaged or there is no room, and otherwise filed straight away.
    /// </summary>
    public async Task<IntakeGroupResult> FileUploadAsync(
        Guid ownerProfileId, string fileName, Stream content, Guid? addedByProfileId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(content);
        if (!configuration.LoadLibraries().PersonalLibraryPolicy.AllowBrowserUpload)
        {
            throw new InvalidOperationException("Browser upload is disabled by administrator policy.");
        }

        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName)
            || ViewLibraryService.TryCreateCandidate(safeName) is not { } named
            || ViewKindAcceptance.OwnKindOf(named.Type.MediaKind) is not (ViewContentKinds.Photos or ViewContentKinds.PersonalVideos))
        {
            throw new InvalidDataException("The uploaded file type is not supported by View.");
        }

        if (!areas.EnsureFolders())
        {
            throw new IntakeRefusedException(IntakeRefusedException.DestinationOfflineCode,
                ViewIntakeReasons.Sentence(ViewIntakeReasons.DestinationOffline), StatusCodes.Status503ServiceUnavailable);
        }

        var kind = ViewKindAcceptance.OwnKindOf(named.Type.MediaKind)!;
        var working = areas.UploadWorkingPath()!;
        var staged = Path.Combine(working, $"{Guid.NewGuid():N}.uploading");
        try
        {
            await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await content.CopyToAsync(output, ct).ConfigureAwait(false);
            }

            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var destination = IntakeDestination.Person(ownerProfileId);
                var folder = await areas.DestinationFolderAsync(kind, destination, ct).ConfigureAwait(false)
                    ?? throw new IOException("The person's Import folder could not be found.");
                var name = ViewFilingLayout.NamesFor(folder, [safeName], File.Exists)[0];
                var path = Path.Combine(folder, name);
                MoveFile(staged, path);
                var candidate = ViewLibraryService.TryCreateCandidate(path)!;
                var group = new ViewLibraryService.AssetGroup(candidate, [new ViewLibraryService.AssetMember(candidate, LocalAssetFileRoles.Primary)]);
                var result = await FileGroupAsync(kind, destination, group, addedByProfileId ?? ownerProfileId, immediate: true, ct).ConfigureAwait(false);
                if (activity is not null)
                {
                    await activity.RecordAsync(new IntakePassResult([result]), ct).ConfigureAwait(false);
                }

                return result;
            }
            finally
            {
                Gate.Release();
            }
        }
        finally
        {
            TryDelete(staged);
        }
    }
}
