using System.Diagnostics;
using System.Globalization;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services.FileWrites;
using MediaEngine.Api.Services.Libraries.Moves;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.Organise;
using MediaEngine.Providers.Contracts;

namespace MediaEngine.Api.Services.Libraries;

/// <summary>
/// Runs the "Change what this folder is" job (Library setup spec D40(c), D60 to D64, D69). The plan was written before anything changed
/// (<c>folder_kind_changes</c>), so a job stopped half way carries on from it:
/// <list type="number">
///   <item>Once: the folder moves to the target library in <c>libraries.json</c> (its management mode unchanged), its old first check ends, and a
///     kind that looks details up starts a new first check, so moves wait until it passes.</item>
///   <item>For each file, recorded in the job's ledger (<c>folder_organise_log</c>) so a restart never repeats a finished file: the file's library
///     becomes the target; its place in the Work graph is made again when the media type differs (the way ingestion places a new file, carrying
///     what people did with the title); a plain kind drops the details looked up for it, a kind that looks details up identifies it again; and in a
///     Managed folder its filing into the new kind's pattern is queued for the next batch (held while the first check runs).</item>
/// </list>
/// Nothing is moved on disk here: filing waits for the batch (D62) and a file stays playable until then (D63).
/// </summary>
public sealed class FolderKindChangeRunner
{
    private readonly IConfigurationLoader _configuration;
    private readonly ILibraryKindSource _kinds;
    private readonly IFolderKindChangeRepository _changes;
    private readonly IFolderOrganiseLogRepository _ledger;
    private readonly IMediaAssetRepository _assets;
    private readonly ICanonicalValueRepository _canonicals;
    private readonly IIdentityJobRepository _identityJobs;
    private readonly IUnmatchedAssetRepository _unmatched;
    private readonly IFilingQueue _filing;
    private readonly FolderFirstCheckService _firstChecks;
    private readonly IEntityRescorer _rescorer;
    private readonly IAssetRereader _rereader;
    private readonly ISystemActivityRepository _activity;
    private readonly MoveOwnedFileCleaner _ownedFiles;
    private readonly IIdentityPipelineSignal? _signal;
    private readonly FolderOrganiseTiming _timing;
    private readonly ILogger<FolderKindChangeRunner> _logger;

    public FolderKindChangeRunner(
        IConfigurationLoader configuration,
        ILibraryKindSource kinds,
        IFolderKindChangeRepository changes,
        IFolderOrganiseLogRepository ledger,
        IMediaAssetRepository assets,
        ICanonicalValueRepository canonicals,
        IIdentityJobRepository identityJobs,
        IUnmatchedAssetRepository unmatched,
        IFilingQueue filing,
        FolderFirstCheckService firstChecks,
        IEntityRescorer rescorer,
        IAssetRereader rereader,
        ISystemActivityRepository activity,
        MoveOwnedFileCleaner ownedFiles,
        ILogger<FolderKindChangeRunner> logger,
        IIdentityPipelineSignal? signal = null,
        FolderOrganiseTiming? timing = null)
    {
        _configuration = configuration;
        _kinds = kinds;
        _changes = changes;
        _ledger = ledger;
        _assets = assets;
        _canonicals = canonicals;
        _identityJobs = identityJobs;
        _unmatched = unmatched;
        _filing = filing;
        _firstChecks = firstChecks;
        _rescorer = rescorer;
        _rereader = rereader;
        _activity = activity;
        _ownedFiles = ownedFiles;
        _signal = signal;
        _timing = timing ?? new FolderOrganiseTiming();
        _logger = logger;
    }

    public async Task<FolderOrganiseRunResult> RunAsync(
        Guid operationId, Guid sourceId, IFolderOrganiseHost host, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var plan = await _changes.GetPlanAsync(operationId, ct).ConfigureAwait(false);
        if (plan is null || plan.SourceId != sourceId)
        {
            return new FolderOrganiseRunResult { Stop = FolderOrganiseStop.SourceMissing };
        }

        // 1. The folder moves to its new library, once.
        if (plan.ConfigAppliedAt is null && !await ApplyFolderMoveAsync(plan, ct).ConfigureAwait(false))
        {
            return new FolderOrganiseRunResult { Stop = FolderOrganiseStop.SourceMissing };
        }

        var libraries = _configuration.LoadLibraries();
        var source = FindSource(libraries, sourceId);
        var target = libraries.Libraries.FirstOrDefault(library => string.Equals(library.Id, plan.ToLibraryId, StringComparison.OrdinalIgnoreCase));
        if (source is null || target is null)
        {
            return new FolderOrganiseRunResult { Stop = FolderOrganiseStop.SourceMissing };
        }

        var mode = LibraryKindLookupPolicy.For(target, _kinds.GetKinds()).Mode;
        var fileMayMove = source.IsManaged && source.IsWritable;
        var mediaType = Enum.Parse<MediaType>(plan.TargetMediaType, ignoreCase: true);

        // 2. The folder's files, minus those a stopped run already finished. A file logged as failed is not finished: it is tried again.
        var failedBefore = plan.RepairsOperationId is { } repairing
            ? await FailedPathsAsync(_ledger, repairing, ct).ConfigureAwait(false)
            : [];
        var files = (await _assets.ListFilesUnderAsync(source.Path, ct).ConfigureAwait(false))
            .Where(file => string.IsNullOrWhiteSpace(file.LibraryId)
                || string.Equals(file.LibraryId, plan.FromLibraryId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(file.LibraryId, plan.ToLibraryId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var failedHere = await FailedPathsAsync(_ledger, operationId, ct).ConfigureAwait(false);
        var finished = await _ledger.GetFinishedPathsAsync(operationId, ct).ConfigureAwait(false);
        finished.ExceptWith(failedHere);
        var unfinished = (await _ledger.ListUnfinishedAsync(operationId, ct).ConfigureAwait(false)).Select(entry => entry.OldPath).ToHashSet(StringComparer.Ordinal);
        // The files this job owns: those not in the new library yet, those an earlier change of the folder could not change, those it already did,
        // and those it was part way through when it stopped. A file already changed by an earlier, stopped change is not done a second time.
        files = [.. files.Where(file => finished.Contains(file.FilePath)
            || unfinished.Contains(file.FilePath)
            || failedBefore.Contains(file.FilePath)
            || failedHere.Contains(file.FilePath)
            || !string.Equals(file.LibraryId, plan.ToLibraryId, StringComparison.OrdinalIgnoreCase))];
        var work = files.Where(file => !finished.Contains(file.FilePath)).ToList();
        var total = files.Count;
        var completed = Math.Max(0, total - work.Count);
        var failed = 0;

        await host.ReportProgressAsync(new FolderOrganiseProgress(total, completed, failed, null), ct).ConfigureAwait(false);

        var progress = Stopwatch.StartNew();

        // 3. The pass.
        foreach (var file in work)
        {
            ct.ThrowIfCancellationRequested();
            var signal = await host.GetSignalAsync(ct).ConfigureAwait(false);
            if (signal != FolderJobSignal.Continue)
            {
                return Stopped(signal, total, completed, failed);
            }

            // The intent is written before the file is touched; the steps are safe to repeat, so a restart simply does the file again.
            // When an earlier attempt was cut short, when it began is kept so its identity job is not queued a second time.
            var earlier = await _ledger.GetAsync(operationId, file.FilePath, ct).ConfigureAwait(false);
            var earlierAttemptAt = earlier is { Outcome: FolderOrganiseOutcomes.Moving } ? earlier.At : (DateTimeOffset?)null;
            await _ledger.UpsertAsync(
                new FolderOrganiseLogEntry(operationId, sourceId, file.FilePath, file.FilePath, FolderOrganiseOutcomes.Moving, DateTimeOffset.UtcNow),
                ct).ConfigureAwait(false);
            string outcome;
            string detail;
            try
            {
                detail = await ChangeFileAsync(plan, target, mode, mediaType, fileMayMove, sourceId, file, earlierAttemptAt, ct).ConfigureAwait(false);
                outcome = FolderOrganiseOutcomes.KindChanged;
            }
            catch (UserDetailsClashException ex)
            {
                // Not an error to retry: the file is left exactly as it was and the job reports it.
                outcome = FolderOrganiseOutcomes.KindChangeRefused;
                detail = ex.Message;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Changing what {Path} is failed; the rest of the folder carries on", file.FilePath);
                outcome = FolderOrganiseOutcomes.Failed;
                detail = $"This file could not be changed: {ex.Message}";
            }

            await _ledger.UpsertAsync(
                new FolderOrganiseLogEntry(operationId, sourceId, file.FilePath, file.FilePath, outcome, DateTimeOffset.UtcNow, detail),
                ct).ConfigureAwait(false);
            completed++;
            if (outcome is FolderOrganiseOutcomes.Failed or FolderOrganiseOutcomes.KindChangeRefused)
            {
                failed++;
            }

            if (progress.Elapsed >= _timing.ProgressInterval)
            {
                progress.Restart();
                await host.ReportProgressAsync(new FolderOrganiseProgress(total, completed, failed, file.FilePath), ct).ConfigureAwait(false);
            }
        }

        var outcomes = await _ledger.CountByOutcomeAsync(operationId, ct).ConfigureAwait(false);
        await LogActivityAsync(plan, source, libraries, outcomes, ct).ConfigureAwait(false);
        return new FolderOrganiseRunResult
        {
            Stop = FolderOrganiseStop.Finished,
            Total = total,
            Completed = completed,
            Failed = outcomes.GetValueOrDefault(FolderOrganiseOutcomes.Failed) + outcomes.GetValueOrDefault(FolderOrganiseOutcomes.KindChangeRefused),
            Outcomes = outcomes,
        };
    }

    /// <summary>The sentence shown when the job finishes: how many files changed, and how many could not be.</summary>
    public async Task<string> CompletionMessageAsync(Guid operationId, FolderOrganiseRunResult result, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var plan = await _changes.GetPlanAsync(operationId, ct).ConfigureAwait(false);
        var kinds = _kinds.GetKinds();
        var changed = result.Count(FolderOrganiseOutcomes.KindChanged);
        var summary = $"{changed.ToString("N0", CultureInfo.InvariantCulture)} {(changed == 1 ? "file" : "files")} changed"
            + (plan is null ? string.Empty : $" from {KindLabel(kinds, plan.FromKindKey)} to {KindLabel(kinds, plan.ToKindKey)}");
        if (result.Failed > 0)
        {
            var left = result.Failed.ToString("N0", CultureInfo.InvariantCulture);
            var were = result.Failed == 1 ? "file was" : "files were";
            var refused = result.Count(FolderOrganiseOutcomes.KindChangeRefused);
            if (refused == result.Failed)
            {
                return $"Finished, but {left} {were} not changed: {(refused == 1 ? "it and an existing title both have" : "they and an existing title both have")} your own artwork or details. ({summary}.)";
            }

            var clash = refused > 0 ? $" {refused.ToString("N0", CultureInfo.InvariantCulture)} of them and an existing title both have your own artwork or details." : string.Empty;
            return $"Finished, but {left} {were} not changed ({summary}).{clash} See the log, then start the change again to retry {(result.Failed == 1 ? "it" : "them")}.";
        }

        return summary + ".";
    }

    // ----- step 1 ------------------------------------------------------------------------------------------------------------------

    private async Task<bool> ApplyFolderMoveAsync(FolderKindChangePlan plan, CancellationToken ct)
    {
        await LibraryMutationEndpoints.WriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var libraries = _configuration.LoadLibraries();
            var source = FindSource(libraries, plan.SourceId);
            var target = libraries.Libraries.FirstOrDefault(library => string.Equals(library.Id, plan.ToLibraryId, StringComparison.OrdinalIgnoreCase));
            if (source is null || target is null)
            {
                return false;
            }

            // Finishing an earlier change: the folder is already in its library and its checks are settled, so only the files are done again.
            if (plan.RepairsOperationId is null)
            {
                var current = libraries.Libraries.First(library => library.Sources.Contains(source));
                if (!ReferenceEquals(current, target))
                {
                    current.Sources.Remove(source);
                    target.Sources.Add(source);
                    try
                    {
                        _configuration.SaveLibraries(libraries);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        // The folder is still where it was, with its first check and the moves held for it: nothing was touched.
                        _logger.LogError(ex, "The folder {Path} could not be moved to library {LibraryId}", source.Path, target.Id);
                        throw;
                    }
                }

                // Its old check (and the moves held for it) ends only once the folder is saved in its new library. A repeat after a stop in between
                // simply ends it (there is nothing yet to lose: no file has been changed) and starts the new one.
                await _firstChecks.EndBecauseKindChangedAsync(plan.SourceId.ToString("D"), ct).ConfigureAwait(false);
                if (plan.StartsFirstCheck)
                {
                    await _firstChecks.StartForKindChangeAsync(target.Id, source, ct).ConfigureAwait(false);
                }

            }

            await _changes.MarkConfigAppliedAsync(plan.OperationId, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            LibraryMutationEndpoints.WriteGate.Release();
        }
    }

    // ----- step 2: one file --------------------------------------------------------------------------------------------------------

    private async Task<string> ChangeFileAsync(
        FolderKindChangePlan plan,
        LibraryFolderConfig target,
        LibraryLookupMode mode,
        MediaType mediaType,
        bool fileMayMove,
        Guid sourceId,
        MediaAssetFileRef file,
        DateTimeOffset? earlierAttemptAt,
        CancellationToken ct)
    {
        var placement = await _changes.GetPlacementAsync(file.AssetId, ct).ConfigureAwait(false);
        if (placement is null)
        {
            return "The file is no longer in the library, so nothing was changed.";
        }

        var notes = new List<string>();

        // Its place in the Work graph, made the way ingestion places a new file, when the media type differs.
        if (!string.Equals(placement.WorkMediaType, mediaType.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            var hints = (await _canonicals.GetByEntityAsync(file.AssetId, ct).ConfigureAwait(false))
                .ToDictionary(value => value.Key, value => value.Value, StringComparer.OrdinalIgnoreCase);
            var replaced = await _changes.ReplaceUnderMediaTypeAsync(file.AssetId, mediaType, hints, file.FilePath, ct).ConfigureAwait(false);
            _ownedFiles.Remove(replaced.OwnedFilesToRemove);
            if (replaced.Replaced)
            {
                // The details a person set on the old title came along; the new title's values are worked out from them.
                await _rescorer.RescoreAsync(replaced.WorkId, ct).ConfigureAwait(false);
                notes.Add($"placed as {mediaType}");
            }
        }

        var queuedIdentity = false;
        switch (mode)
        {
            case LibraryLookupMode.Plain:
                // A plain kind shows a file as it is (D69): the details looked up for it go, and the cascade is worked out again from the
                // claims the file itself and the person left.
                var dropped = await DropDetailsAsync(file.AssetId, ct).ConfigureAwait(false);
                notes.Add(dropped > 0 ? "looked-up details dropped" : "no looked-up details to drop");
                break;

            case LibraryLookupMode.YouTube:
                // The YouTube lookup runs on the video ID when the file is read again, under the library it is now in.
                var reread = await _rereader.RereadAsync(file.AssetId, ct).ConfigureAwait(false);
                notes.Add(reread.Refreshed ? "looked up again on YouTube" : $"could not be read again ({reread.Status})");
                break;

            default:
                // The normal identification, as the Unmatched retry queues it: a new job for the file with the new media type. A job an
                // earlier, cut-short attempt already queued is not queued again.
                var latest = await _identityJobs.GetByEntityAsync(file.AssetId, ct).ConfigureAwait(false);
                if (earlierAttemptAt is { } since && latest is not null && latest.CreatedAt >= since)
                {
                    notes.Add("already queued to be identified again");
                    break;
                }

                await _identityJobs.CreateAsync(new IdentityJob
                {
                    EntityId = file.AssetId,
                    EntityType = EntityType.MediaAsset.ToString(),
                    MediaType = mediaType.ToString(),
                    Pass = "Quick",
                }, ct).ConfigureAwait(false);
                queuedIdentity = true;
                notes.Add("queued to be identified again");
                break;
        }

        // Its filing into the new kind's pattern waits for the batch; a Read-only folder is never touched (D60, D62).
        if (fileMayMove)
        {
            var held = _firstChecks.IsHeld(sourceId.ToString("D"));
            await _filing.RequestFilingAsync(file.AssetId, held, ct).ConfigureAwait(false);
            notes.Add(held ? "filing held until the first check passes" : "filing waits for the next batch");
        }

        // The file joins the new library last: until every step above has worked, a file that failed is still in the old library, never
        // half changed, and the job (or a new start) does it again.
        await _assets.SetLibraryIdAsync(file.AssetId, target.Id, ct).ConfigureAwait(false);
        if (queuedIdentity)
        {
            // Told only now, so the lookup never sees the file in the library it has left.
            _signal?.Signal(IdentityPipelineSignalKind.Retail);
        }

        if (mode == LibraryLookupMode.Plain)
        {
            // A lookup that was already running for the file may have written details while the library changed; they go too.
            await DropDetailsAsync(file.AssetId, ct).ConfigureAwait(false);
            // A plain kind never has Unmatched files (D38).
            await _unmatched.ClearAsync(file.AssetId, ct).ConfigureAwait(false);
        }

        return $"Now in {target.Name}: {string.Join("; ", notes)}.";
    }

    // Drops the details looked up for the file and works out the values of what is left again. Returns how many claims went.
    private async Task<int> DropDetailsAsync(Guid assetId, CancellationToken ct)
    {
        var dropped = await _changes.DropProviderDetailsAsync(assetId, ct).ConfigureAwait(false);
        foreach (var entityId in dropped.EntityIds)
        {
            await _rescorer.RescoreAsync(entityId, ct).ConfigureAwait(false);
        }

        return dropped.ClaimsDropped;
    }

    /// <summary>The files whose latest line in the job's ledger says they could not be changed.</summary>
    internal static async Task<HashSet<string>> FailedPathsAsync(IFolderOrganiseLogRepository ledger, Guid operationId, CancellationToken ct)
    {
        const int Page = 1000;
        var failed = new HashSet<string>(StringComparer.Ordinal);
        for (var offset = 0; ; offset += Page)
        {
            var entries = await ledger.ListAsync(operationId, offset, Page, ct).ConfigureAwait(false);
            foreach (var entry in entries.Where(entry => entry.Outcome == FolderOrganiseOutcomes.Failed))
            {
                failed.Add(entry.OldPath);
            }

            if (entries.Count < Page)
            {
                return failed;
            }
        }
    }

    // ----- helpers -----------------------------------------------------------------------------------------------------------------

    private async Task LogActivityAsync(
        FolderKindChangePlan plan,
        LibrarySourceConfig source,
        LibrariesConfiguration libraries,
        IReadOnlyDictionary<string, int> outcomes,
        CancellationToken ct)
    {
        var kinds = _kinds.GetKinds();
        var changed = outcomes.GetValueOrDefault(FolderOrganiseOutcomes.KindChanged);
        var folder = FolderJobService.FolderName(libraries, plan.SourceId);
        var from = KindLabel(kinds, plan.FromKindKey);
        var to = KindLabel(kinds, plan.ToKindKey);
        await _activity.LogAsync(new SystemActivityEntry
        {
            ActionType = SystemActionType.FolderKindChanged,
            CollectionName = folder,
            EntityId = plan.SourceId,
            EntityType = "LibrarySource",
            ProfileId = plan.StartedByProfileId,
            ChangesJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                from_kind = plan.FromKindKey,
                to_kind = plan.ToKindKey,
                to_library_id = plan.ToLibraryId,
                files = changed,
                path = source.Path,
            }),
            Detail = $"{folder} changed from {from} to {to}: {changed.ToString("N0", CultureInfo.InvariantCulture)} {(changed == 1 ? "file" : "files")}",
        }, ct).ConfigureAwait(false);
    }

    private static FolderOrganiseRunResult Stopped(FolderJobSignal signal, int total, int completed, int failed) => new()
    {
        Stop = signal switch
        {
            FolderJobSignal.Cancel => FolderOrganiseStop.Cancelled,
            _ => FolderOrganiseStop.Paused,
        },
        Total = total,
        Completed = completed,
        Failed = failed,
    };

    private static string KindLabel(IReadOnlyList<LibraryKindDefinition> kinds, string key) =>
        kinds.FirstOrDefault(kind => string.Equals(kind.Key, key, StringComparison.Ordinal))?.Label ?? key;

    private static LibrarySourceConfig? FindSource(LibrariesConfiguration libraries, Guid sourceId) =>
        libraries.Libraries.SelectMany(library => library.Sources)
            .FirstOrDefault(source => Guid.TryParse(source.Id, out var id) && id == sourceId);
}
