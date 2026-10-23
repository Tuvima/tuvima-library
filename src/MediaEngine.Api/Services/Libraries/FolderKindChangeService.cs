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
        _visibility = visibility;
        _jobs = jobs;
        _changes = changes;
        _tracker = tracker;
        _logger = logger;
    }

    /// <summary>What the change would do, worked out without changing anything.</summary>
    public async Task<FolderKindChangePreviewResponse> PreviewAsync(string sourceIdText, FolderKindChangeRequest request, CancellationToken ct)
    {
        var context = await ResolveAsync(sourceIdText, request, ct).ConfigureAwait(false);
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
            Moves = context.FileMayMove ? context.Files.Count : 0,
            DropsDetails = context.TargetIsPlain && context.FromLooksUp,
            StartsFirstCheck = context.StartsFirstCheck,
            OtherAppsWarning = context.Source.IsManaged,
        };

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

    /// <summary>Writes the plan and queues the job. A folder with a job open for it is refused, so there is never a second change at once.</summary>
    public async Task<FolderJobStatusDto> StartAsync(string sourceIdText, FolderKindChangeRequest request, CancellationToken ct)
    {
        await LibraryMutationEndpoints.WriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var context = await ResolveAsync(sourceIdText, request, ct).ConfigureAwait(false);
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
                DateTimeOffset.UtcNow), ct).ConfigureAwait(false);

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

    private async Task<ChangeContext> ResolveAsync(string sourceIdText, FolderKindChangeRequest request, CancellationToken ct)
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

        var toLibrary = ChooseLibrary(libraries, vaultIds, toKind, request.LibraryId);

        // The same kind is refused, unless an earlier change of this folder into this library was stopped half way: the folder is already in
        // the library but some of its files are still registered in the old one, and starting the change again finishes them.
        var fromLibrary = holder;
        if (string.Equals(toKind.Key, fromKind.Key, StringComparison.Ordinal))
        {
            var stopped = ReferenceEquals(toLibrary, holder) ? await StoppedChangeAsync(sourceId, holder, libraries, vaultIds, ct).ConfigureAwait(false) : null;
            if (stopped is null)
            {
                throw new FolderJobException($"This folder already is {toKind.Label}.");
            }

            fromLibrary = stopped;
            fromKind = kinds.FirstOrDefault(kind => string.Equals(kind.Key, stopped.KindKey, StringComparison.Ordinal)) ?? fromKind;
        }

        // The files still to change: those not registered in the new library yet.
        var files = (await FolderFilesAsync(source, fromLibrary, toLibrary, ct).ConfigureAwait(false))
            .Where(file => !string.Equals(file.LibraryId, toLibrary.Id, StringComparison.OrdinalIgnoreCase))
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
            Files: files);
    }

    // The library the folder goes into: the kind's own, or (for a kind that holds several named libraries) the one named by id.
    private static LibraryFolderConfig ChooseLibrary(
        LibrariesConfiguration libraries, IReadOnlySet<string> vaultIds, LibraryKindDefinition kind, string? libraryId)
    {
        var ofKind = libraries.Libraries
            .Where(library => library.Kind == LibraryKinds.Catalogued
                && !IsVault(library, vaultIds)
                && string.Equals(library.KindKey, kind.Key, StringComparison.Ordinal))
            .ToList();
        if (!string.IsNullOrWhiteSpace(libraryId))
        {
            return ofKind.FirstOrDefault(library => string.Equals(library.Id, libraryId, StringComparison.OrdinalIgnoreCase))
                ?? throw new FolderJobException(
                    libraries.Libraries.Any(library => string.Equals(library.Id, libraryId, StringComparison.OrdinalIgnoreCase) && !IsVault(library, vaultIds))
                        ? $"That library is not a {kind.Label} library."
                        : "That library does not exist.");
        }

        if (ofKind.Count == 1)
        {
            return ofKind[0];
        }

        throw new FolderJobException(ofKind.Count == 0
            ? $"There is no {kind.Label} library yet. Create it first, then choose it."
            : $"There is more than one {kind.Label} library. Choose which one the folder goes into.");
    }

    // The library the folder came from, when the latest change of this folder into this library was stopped (cancelled or failed) after the folder
    // moved and files are still registered in the old library; otherwise null.
    private async Task<LibraryFolderConfig?> StoppedChangeAsync(
        Guid sourceId, LibraryFolderConfig holder, LibrariesConfiguration libraries, IReadOnlySet<string> vaultIds, CancellationToken ct)
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
            string.Equals(library.Id, plan.FromLibraryId, StringComparison.OrdinalIgnoreCase) && !IsVault(library, vaultIds));
        if (old is null)
        {
            return null;
        }

        var strays = await FolderFilesAsync(
            holder.Sources.First(source => Guid.TryParse(source.Id, out var id) && id == sourceId), old, holder, ct).ConfigureAwait(false);
        return strays.Any(file => !string.Equals(file.LibraryId, holder.Id, StringComparison.OrdinalIgnoreCase)) ? old : null;
    }

    // The folder's files the data store holds that this change owns: registered in the library the folder came from, in the one it goes into,
    // or in none yet. A file registered in some other library (a folder inside this one that belongs to it) is that library's.
    private async Task<IReadOnlyList<MediaAssetFileRef>> FolderFilesAsync(
        LibrarySourceConfig source, LibraryFolderConfig from, LibraryFolderConfig to, CancellationToken ct) =>
        [.. (await _assets.ListFilesUnderAsync(source.Path, ct).ConfigureAwait(false))
            .Where(file => string.IsNullOrWhiteSpace(file.LibraryId)
                || string.Equals(file.LibraryId, from.Id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(file.LibraryId, to.Id, StringComparison.OrdinalIgnoreCase))];

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
        IReadOnlyList<MediaAssetFileRef> Files);
}
