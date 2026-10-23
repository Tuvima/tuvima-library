using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Services.Libraries;

/// <summary>
/// "Change what this folder is" (Library setup spec D40(c), D60, D61, D69): the honest preview behind the confirmation and the start of the
/// durable job that does it. The folder moves to a library of another kind in the same area; the library is chosen by id (a new named library
/// is created first through the normal library action). Nothing here changes a file: the plan is written and the job is queued, and
/// <see cref="FolderKindChangeRunner"/> does the work.
/// </summary>
public sealed class FolderKindChangeService
{
    private const string NotInLibraries = "This folder is not part of your libraries.";

    private readonly IConfigurationLoader _configuration;
    private readonly ILibraryKindSource _kindSource;
    private readonly IVaultLibraryRepository _vaultLibraries;
    private readonly IMediaAssetRepository _assets;
    private readonly IMediaOperationRepository _operations;
    private readonly IFolderOrganiseLogRepository _ledger;
    private readonly IVisibilityAfterMoveReadService _visibility;
    private readonly FolderJobService _jobs;
    private readonly IFolderKindChangeRepository _changes;
    private readonly IMediaOperationTracker _tracker;
    private readonly ILogger<FolderKindChangeService> _logger;

    public FolderKindChangeService(
        IConfigurationLoader configuration,
        ILibraryKindSource kindSource,
        IVaultLibraryRepository vaultLibraries,
        IMediaAssetRepository assets,
        IMediaOperationRepository operations,
        IFolderOrganiseLogRepository ledger,
        IVisibilityAfterMoveReadService visibility,
        FolderJobService jobs,
        IFolderKindChangeRepository changes,
        IMediaOperationTracker tracker,
        ILogger<FolderKindChangeService> logger)
    {
        _configuration = configuration;
        _kindSource = kindSource;
        _vaultLibraries = vaultLibraries;
        _assets = assets;
        _operations = operations;
        _ledger = ledger;
        _visibility = visibility;
        _jobs = jobs;
        _changes = changes;
        _tracker = tracker;
        _logger = logger;
    }

    /// <summary>What the change would do, worked out without changing anything.</summary>
    public async Task<FolderKindChangePreviewResponse> PreviewAsync(string sourceIdText, FolderKindChangeRequest request, CancellationToken ct)
    {
        // The preview may name no library: a library of the kind that does not exist yet, which the Dashboard creates only if the person goes on.
        var context = await ResolveAsync(sourceIdText, request, forPreview: true, ct).ConfigureAwait(false);
        await RefuseWhileBusyAsync(context.SourceId, ct).ConfigureAwait(false);

        var response = new FolderKindChangePreviewResponse
        {
            SourceId = context.SourceId.ToString("D"),
            FromKindLabel = context.FromKind.Label,
            ToKindKey = context.ToKind.Key,
            ToKindLabel = context.ToKind.Label,
            ToLibraryId = context.ToLibrary.Id,
            ToLibraryName = context.ToLibrary.Name,
            FileCount = context.Files.Count,
            FilesToRefile = context.FileMayMove ? context.Files.Count : 0,
            DropsDetails = context.TargetIsPlain && context.FromLooksUp,
            StartsFirstCheck = context.StartsFirstCheck,
            OtherAppsWarning = context.Source.IsManaged,
        };

        // A library that does not exist yet is hidden from nobody (Simpler access D2: every library in a person's areas is visible unless
        // unticked for them), and the kind is in the same area as the folder's, so nobody loses sight of anything.
        if (context.NewLibrary)
        {
            response.VisibilityLossChecked = true;
            return response;
        }

        // Who would stop seeing these files in the new library (Simpler access D13). A question that could not be answered is never an empty list.
        try
        {
            var answer = await _visibility.GetAsync(
                new VisibilityAfterMoveRequest(Guid.Parse(context.ToLibrary.Id), SourceId: context.Source.Id), ct).ConfigureAwait(false);
            if (answer.People is { } people)
            {
                response.VisibilityLoss = [.. people];
                response.VisibilityLossChecked = true;
            }
            else
            {
                response.VisibilityMessage = answer.Refusal ?? "Library couldn't work out who would lose sight of these files.";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Who would lose sight of the files of {Path} in {LibraryId} could not be worked out", context.Source.Path, context.ToLibrary.Id);
            response.VisibilityMessage = "Library couldn't work out who would lose sight of these files.";
        }

        return response;
    }

    /// <summary>
    /// Writes the plan and queues the job. A folder with a job open for it is refused, so there is never a second change at once.
    /// <paramref name="startedByProfileId"/> is the administrator's profile, named in the activity entry.
    /// </summary>
    public async Task<FolderJobStatusDto> StartAsync(string sourceIdText, FolderKindChangeRequest request, CancellationToken ct, Guid? startedByProfileId = null)
    {
        await LibraryMutationEndpoints.WriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var context = await ResolveAsync(sourceIdText, request, forPreview: false, ct).ConfigureAwait(false);
            await RefuseWhileBusyAsync(context.SourceId, ct).ConfigureAwait(false);

            var operationId = Guid.NewGuid();
            // The plan first: a job that is stopped half way carries on from it.
            await _changes.SavePlanAsync(new FolderKindChangePlan(
                operationId,
                context.SourceId,
                context.FromLibrary.Id,
                context.ToLibrary.Id,
                context.FromKind.Key,
                context.ToKind.Key,
                context.TargetMediaType.ToString(),
                context.TargetIsPlain,
                context.StartsFirstCheck,
                DateTimeOffset.UtcNow,
                StartedByProfileId: startedByProfileId,
                RepairsOperationId: context.RepairsOperationId), ct).ConfigureAwait(false);

            await _tracker.EnsureQueuedAsync(new MediaOperation
            {
                Id = operationId,
                OperationType = MediaOperationType.ChangeFolderKind,
                OperationKind = MediaOperationKind.Maintenance,
                EntityId = context.SourceId,
                EntityKind = "library_source",
                SourcePath = context.Source.Path,
                Status = MediaOperationStatus.Queued,
                Stage = MediaOperationStage.Queued,
                QueueName = MediaOperationQueue.FolderJobs,
                ItemsTotal = context.Files.Count,
                IdempotencyKey = $"{MediaOperationType.ChangeFolderKind}:{context.SourceId:D}:{operationId:N}",
            }, ct).ConfigureAwait(false);

            // The job is queued. Reading its status back may fail, but that never undoes the queued job.
            return await _jobs.GetAsync(operationId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The folder job was queued but could not be read back.");
        }
        finally
        {
            LibraryMutationEndpoints.WriteGate.Release();
        }
    }

    // ----- the checks both actions make ---------------------------------------------------------------------------------------------

    private async Task RefuseWhileBusyAsync(Guid sourceId, CancellationToken ct)
    {
        if (await _jobs.HasOpenJobAsync(sourceId, ct).ConfigureAwait(false))
        {
            throw new FolderJobException("This folder is being organised, merged or changed right now. Wait for that to finish first.", conflict: true);
        }
    }

    private async Task<ChangeContext> ResolveAsync(string sourceIdText, FolderKindChangeRequest request, bool forPreview, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Guid.TryParse(sourceIdText, out var sourceId) || sourceId == Guid.Empty)
        {
            throw new FolderJobException("Choose a folder from your libraries.");
        }

        var libraries = _configuration.LoadLibraries();
        var vaultIds = await _vaultLibraries.GetIdsAsync(ct).ConfigureAwait(false);
        var holder = libraries.Libraries.FirstOrDefault(library => library.Sources.Any(source =>
            Guid.TryParse(source.Id, out var id) && id == sourceId));

        // A folder of a Vault library answers exactly like a folder that is not in the libraries (Videos spec D17).
        if (holder is null || IsVault(holder, vaultIds))
        {
            throw new FolderJobException(NotInLibraries, notFound: true);
        }

        var source = holder.Sources.First(candidate => Guid.TryParse(candidate.Id, out var id) && id == sourceId);
        if (source.IsMergedAway)
        {
            throw new FolderJobException("This folder was merged into another one and is no longer a folder of its own.");
        }

        if (string.IsNullOrWhiteSpace(request.KindKey))
        {
            throw new FolderJobException("Choose what this folder should become.");
        }

        var kinds = _kindSource.GetKinds();
        var toKind = kinds.FirstOrDefault(kind => string.Equals(kind.Key, request.KindKey, StringComparison.Ordinal) && kind.Available)
            ?? throw new FolderJobException("That kind of folder does not exist.");
        if (string.Equals(toKind.Area, LibraryAreas.Vault, StringComparison.OrdinalIgnoreCase))
        {
            throw new FolderJobException("A folder cannot be changed into a Vault kind from here. The Vault has its own screens.");
        }

        var fromKind = kinds.FirstOrDefault(kind => string.Equals(kind.Key, holder.KindKey, StringComparison.Ordinal))
            ?? throw new FolderJobException("This folder's library does not have a kind that can be changed.");
        if (!string.Equals(fromKind.Area, toKind.Area, StringComparison.OrdinalIgnoreCase))
        {
            throw new FolderJobException(
                $"{toKind.Label} is not in the same area as {fromKind.Label}. A folder can only change to a kind in its own area.");
        }

        if (fromKind.Format is null || toKind.Format is null)
        {
            throw new FolderJobException("This kind of folder cannot be changed from here.");
        }

        var (toLibrary, newLibrary) = ChooseLibrary(libraries, vaultIds, toKind, request.LibraryId, forPreview);

        // An earlier change of this folder that was stopped (cancelled, or finished with files not changed) leaves files still in the old
        // library or marked failed. Starting the same kind again finishes them. Changing back to the kind the folder was is allowed too: the
        // files already changed go back, and the ones left behind are already there. Any other kind waits until they are done.
        var unfinished = await FolderKindChangeRunner.UnfinishedPathsAsync(_changes, _ledger, sourceId, ct).ConfigureAwait(false);
        var stopped = await StoppedChangeAsync(sourceId, holder, libraries, vaultIds, unfinished, ct).ConfigureAwait(false);
        var sameKind = string.Equals(toKind.Key, fromKind.Key, StringComparison.Ordinal);
        var finishing = stopped is not null && sameKind && ReferenceEquals(toLibrary, holder);
        var changingBack = stopped is not null && !finishing && !ReferenceEquals(stopped.From, holder)
            && string.Equals(toLibrary.Id, stopped.From.Id, StringComparison.OrdinalIgnoreCase);
        if (stopped is not null && !finishing && !changingBack)
        {
            var why = stopped.Refused.Count > 0
                ? " " + FolderKindChangeRunner.JoinSentences(stopped.Refused)
                : string.Empty;
            throw new FolderJobException(
                $"An earlier change of this folder was not finished.{why} Start it again as {fromKind.Label} first, or change it back to {KindLabelOf(kinds, stopped.From.KindKey)}.");
        }

        var fromLibrary = holder;
        if (finishing)
        {
            fromLibrary = stopped!.From;
            fromKind = kinds.FirstOrDefault(kind => string.Equals(kind.Key, stopped.From.KindKey, StringComparison.Ordinal)) ?? fromKind;
        }
        else if (sameKind && !changingBack)
        {
            throw new FolderJobException($"This folder already is {toKind.Label}.");
        }

        var repairs = finishing ? stopped : null;
        // The files still to change: those not registered in the new library yet, and those an earlier change of this folder left unfinished.
        var files = (await FolderFilesAsync(source, fromLibrary, toLibrary, ct).ConfigureAwait(false))
            .Where(file => !string.Equals(file.LibraryId, toLibrary.Id, StringComparison.OrdinalIgnoreCase) || unfinished.Contains(file.FilePath))
            .ToList();
        var mode = LibraryKindLookupPolicy.For(toLibrary, kinds).Mode;
        var fromMode = LibraryKindLookupPolicy.For(fromLibrary, kinds).Mode;
        return new ChangeContext(
            sourceId,
            source,
            fromLibrary,
            toLibrary,
            fromKind,
            toKind,
            toKind.FormatMediaType,
            TargetIsPlain: mode == LibraryLookupMode.Plain,
            FromLooksUp: fromMode != LibraryLookupMode.Plain,
            StartsFirstCheck: FolderFirstCheckService.NeedsFirstCheck(toLibrary, kinds, vaultIds),
            FileMayMove: source.IsManaged && source.IsWritable,
            Files: files,
            RepairsOperationId: repairs?.OperationId,
            NewLibrary: newLibrary);
    }

    // The library the folder goes into: the kind's own, or (for a kind that holds several named libraries) the one named by id.
    // A preview with no library id may be about a library that does not exist yet (the kind has none, or holds several named ones, so no
    // existing one can be meant); it gets a stand-in with the kind's own settings, which is never saved.
    private static (LibraryFolderConfig Library, bool IsNew) ChooseLibrary(
        LibrariesConfiguration libraries, IReadOnlySet<string> vaultIds, LibraryKindDefinition kind, string? libraryId, bool forPreview)
    {
        var ofKind = libraries.Libraries
            .Where(library => library.Kind == LibraryKinds.Catalogued
                && !IsVault(library, vaultIds)
                && string.Equals(library.KindKey, kind.Key, StringComparison.Ordinal))
            .ToList();
        if (!string.IsNullOrWhiteSpace(libraryId))
        {
            return (ofKind.FirstOrDefault(library => string.Equals(library.Id, libraryId, StringComparison.OrdinalIgnoreCase))
                ?? throw new FolderJobException(
                    libraries.Libraries.Any(library => string.Equals(library.Id, libraryId, StringComparison.OrdinalIgnoreCase) && !IsVault(library, vaultIds))
                        ? $"That library is not a {kind.Label} library."
                        : "That library does not exist."), false);
        }

        if (forPreview && (ofKind.Count == 0 || kind.AllowsMultiple))
        {
            return (new LibraryFolderConfig
            {
                Id = string.Empty,
                Name = kind.Label,
                Category = kind.Label,
                Kind = LibraryKinds.Catalogued,
                KindKey = kind.Key,
                Area = kind.Area,
                MetadataPolicy = LibraryMetadataPolicies.Enriched,
                MediaTypes = [kind.Format ?? string.Empty],
            }, true);
        }

        // The preview and the start mean the same by a missing library: a kind with one library means that library; a kind that holds several named
        // libraries means none in particular, so starting never quietly picks one (the preview calls it a new library).
        if (ofKind.Count == 1 && !kind.AllowsMultiple)
        {
            return (ofKind[0], false);
        }

        throw new FolderJobException(ofKind.Count == 0
            ? $"There is no {kind.Label} library yet. Create it first, then choose it."
            : $"Choose which {kind.Label} library the folder goes into.");
    }

    // The latest change of this folder into this library, when it was stopped (cancelled, or finished with files not changed) after the folder moved
    // and files are still registered in the old library or were logged as failed; otherwise null.
    private async Task<StoppedChange?> StoppedChangeAsync(
        Guid sourceId, LibraryFolderConfig holder, LibrariesConfiguration libraries, IReadOnlySet<string> vaultIds, HashSet<string> unfinished, CancellationToken ct)
    {
        var plan = await _changes.GetLatestPlanAsync(sourceId, ct).ConfigureAwait(false);
        if (plan is not { ConfigAppliedAt: not null } || !string.Equals(plan.ToLibraryId, holder.Id, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var operation = await _operations.GetByIdAsync(plan.OperationId, ct).ConfigureAwait(false);
        if (operation is null || operation.Status == MediaOperationStatus.Succeeded)
        {
            return null;
        }

        var old = libraries.Libraries.FirstOrDefault(library =>
            string.Equals(library.Id, plan.FromLibraryId, StringComparison.OrdinalIgnoreCase) && !IsVault(library, vaultIds)) ?? holder;
        var source = holder.Sources.First(candidate => Guid.TryParse(candidate.Id, out var id) && id == sourceId);
        var own = (await FolderFilesAsync(source, old, holder, ct).ConfigureAwait(false));
        var strays = own.Any(file => !string.Equals(file.LibraryId, holder.Id, StringComparison.OrdinalIgnoreCase));
        var unfinishedHere = own.Any(file => unfinished.Contains(file.FilePath));
        var refused = await FolderKindChangeRunner.RefusedDetailsAsync(_ledger, plan.OperationId, ct).ConfigureAwait(false);
        return strays || unfinishedHere ? new StoppedChange(old, plan.OperationId, refused) : null;
    }

    // The folder's files the data store holds that this change owns: registered in a library the folder has been in or out of (across all its
    // changes), or in none yet. A file registered in some other library (a folder inside this one that belongs to it) is that library's.
    private async Task<IReadOnlyList<MediaAssetFileRef>> FolderFilesAsync(
        LibrarySourceConfig source, LibraryFolderConfig from, LibraryFolderConfig to, CancellationToken ct)
    {
        var sourceId = Guid.TryParse(source.Id, out var id) ? id : Guid.Empty;
        var own = await FolderKindChangeRunner.OwnLibraryIdsAsync(_changes, sourceId, from.Id, to.Id, ct).ConfigureAwait(false);
        return [.. (await _assets.ListFilesUnderAsync(source.Path, ct).ConfigureAwait(false))
            .Where(file => string.IsNullOrWhiteSpace(file.LibraryId) || own.Contains(file.LibraryId))];
    }

    private static string KindLabelOf(IReadOnlyList<LibraryKindDefinition> kinds, string key) =>
        kinds.FirstOrDefault(kind => string.Equals(kind.Key, key, StringComparison.Ordinal))?.Label ?? key;

    private static bool IsVault(LibraryFolderConfig library, IReadOnlySet<string> vaultIds) =>
        vaultIds.Contains(library.Id) || string.Equals(library.Area, LibraryAreas.Vault, StringComparison.OrdinalIgnoreCase);

    private sealed record ChangeContext(
        Guid SourceId,
        LibrarySourceConfig Source,
        LibraryFolderConfig FromLibrary,
        LibraryFolderConfig ToLibrary,
        LibraryKindDefinition FromKind,
        LibraryKindDefinition ToKind,
        MediaType TargetMediaType,
        bool TargetIsPlain,
        bool FromLooksUp,
        bool StartsFirstCheck,
        bool FileMayMove,
        IReadOnlyList<MediaAssetFileRef> Files,
        Guid? RepairsOperationId,
        bool NewLibrary);

    private sealed record StoppedChange(LibraryFolderConfig From, Guid OperationId, IReadOnlyList<string> Refused);
}
