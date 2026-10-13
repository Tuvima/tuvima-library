using System.Diagnostics;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion.Organise;

/// <summary>Which folder conversion to run: the operation that owns it and the folder it converts.</summary>
public sealed record FolderOrganiseJob(Guid OperationId, Guid SourceId);

/// <summary>What the person (or the settings) asked for while the job was running.</summary>
public enum FolderJobSignal
{
    Continue,
    Pause,
    Cancel,

    /// <summary>The folder was switched back to As-is (or removed): stop and change nothing more.</summary>
    SourceNotManaged,
}

public sealed record FolderOrganiseProgress(int Total, int Completed, int Failed, string? CurrentPath);

/// <summary>How the run was told about the outside world: control signals in, progress out.</summary>
public interface IFolderOrganiseHost
{
    /// <summary>Asked before every file. Anything other than <see cref="FolderJobSignal.Continue"/> ends the run after the current file.</summary>
    Task<FolderJobSignal> GetSignalAsync(CancellationToken ct);

    /// <summary>Told how far the run has got, at most about once a second.</summary>
    Task ReportProgressAsync(FolderOrganiseProgress progress, CancellationToken ct);
}

public enum FolderOrganiseStop
{
    /// <summary>Every file was looked at.</summary>
    Finished,
    Paused,
    Cancelled,
    SourceNotManaged,
    SourceMissing,
}

public sealed class FolderOrganiseRunResult
{
    public FolderOrganiseStop Stop { get; init; }

    public int Total { get; init; }

    public int Completed { get; init; }

    public int Failed { get; init; }

    /// <summary>How many log lines each outcome has, for the completion message.</summary>
    public IReadOnlyDictionary<string, int> Outcomes { get; init; } = new Dictionary<string, int>();

    public int Count(string outcome) => Outcomes.TryGetValue(outcome, out var count) ? count : 0;
}

/// <summary>Pacing the job uses. Tests shorten it; the defaults are the real behaviour.</summary>
public sealed class FolderOrganiseTiming
{
    /// <summary>The wait before the first retry of a file that was in use; it doubles each round.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How many times a file that was in use is tried again at the end of the pass.</summary>
    public int RetryRounds { get; init; } = 3;

    /// <summary>The least time between two progress reports.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How often the saved folder settings are read again, so switching a folder to As-is stops the work.</summary>
    public TimeSpan SettingsRefresh { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Runs the organising job over one folder: finishes anything a stopped run left half done, walks the media files in a
/// stable order, converts each one, retries files that were in use at the end of the pass, and leaves a ledger entry for
/// everything it looked at. It deletes nothing except exact duplicate copies and folders it has emptied.
/// </summary>
public sealed class FolderOrganiseRunner
{
    private const int LeftoverListLimit = 5000;

    private readonly IConfigurationLoader _configuration;
    private readonly IMediaAssetRepository _assets;
    private readonly IFolderOrganiseLogRepository _ledger;
    private readonly FolderConversionOrganiser _organiser;
    private readonly IMediaTypeExtensionCatalog? _extensions;
    private readonly IFileIntake? _intake;
    private readonly FolderOrganiseTiming _timing;
    private readonly ILogger<FolderOrganiseRunner> _logger;

    public FolderOrganiseRunner(
        IConfigurationLoader configuration,
        IMediaAssetRepository assets,
        IFolderOrganiseLogRepository ledger,
        FolderConversionOrganiser organiser,
        ILogger<FolderOrganiseRunner> logger,
        IMediaTypeExtensionCatalog? extensions = null,
        IFileIntake? intake = null,
        FolderOrganiseTiming? timing = null)
    {
        _configuration = configuration;
        _assets = assets;
        _ledger = ledger;
        _organiser = organiser;
        _extensions = extensions;
        _intake = intake;
        _timing = timing ?? new FolderOrganiseTiming();
        _logger = logger;
    }

    public async Task<FolderOrganiseRunResult> RunAsync(
        FolderOrganiseJob job,
        IFolderOrganiseHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(host);

        var context = Resolve(job, out var unavailable);
        if (context is null)
        {
            return new FolderOrganiseRunResult
            {
                Stop = unavailable == FolderConversionUnavailable.NotManaged
                    ? FolderOrganiseStop.SourceNotManaged
                    : FolderOrganiseStop.SourceMissing,
            };
        }

        // 1. Finish what a stopped run left half done.
        foreach (var unfinished in await _ledger.ListUnfinishedAsync(job.OperationId, ct).ConfigureAwait(false))
        {
            await _organiser.RecoverAsync(unfinished, context, ct).ConfigureAwait(false);
        }

        // 2. See what the folder holds now: files on disk, and which of them the library already knows.
        var disk = FolderMediaEnumerator.Enumerate(context.SourceRoot, context.Source.IncludeSubdirectories, MediaExtensions);
        var catalogued = await CataloguedByPathAsync(context, ct).ConfigureAwait(false);
        var finished = await _ledger.GetFinishedPathsAsync(job.OperationId, ct).ConfigureAwait(false);
        var work = disk.Where(path => !finished.Contains(path)).ToList();

        var total = disk.Count;
        var completed = Math.Max(0, total - work.Count);
        var failedBefore = (await _ledger.CountByOutcomeAsync(job.OperationId, ct).ConfigureAwait(false))
            .GetValueOrDefault(FolderOrganiseOutcomes.Failed);
        var failed = failedBefore;
        var progress = Stopwatch.StartNew();
        var lastRefresh = Stopwatch.StartNew();
        var deferred = new List<string>();

        await host.ReportProgressAsync(new FolderOrganiseProgress(total, completed, failed, null), ct).ConfigureAwait(false);

        // 3. The pass.
        foreach (var path in work)
        {
            ct.ThrowIfCancellationRequested();

            var signal = await host.GetSignalAsync(ct).ConfigureAwait(false);
            if (signal != FolderJobSignal.Continue)
            {
                return await StopAsync(job, host, signal, total, completed, failed, ct).ConfigureAwait(false);
            }

            if (lastRefresh.Elapsed >= _timing.SettingsRefresh)
            {
                lastRefresh.Restart();
                var refreshed = Resolve(job, out _);
                if (refreshed is null)
                {
                    return await StopAsync(job, host, FolderJobSignal.SourceNotManaged, total, completed, failed, ct).ConfigureAwait(false);
                }

                context = refreshed;
            }

            var outcome = await ProcessAsync(path, catalogued, context, ct).ConfigureAwait(false);
            if (outcome.Locked)
            {
                deferred.Add(path);
                continue;
            }

            completed++;
            if (outcome.Outcome == FolderOrganiseOutcomes.Failed)
            {
                failed++;
            }

            if (progress.Elapsed >= _timing.ProgressInterval)
            {
                progress.Restart();
                await host.ReportProgressAsync(new FolderOrganiseProgress(total, completed, failed, path), ct).ConfigureAwait(false);
            }
        }

        // 4. Files that were in use (open for playback, say) are tried again, a little later each round.
        for (var round = 1; round <= _timing.RetryRounds && deferred.Count > 0; round++)
        {
            await Task.Delay(TimeSpan.FromTicks(_timing.RetryDelay.Ticks * (1L << (round - 1))), ct).ConfigureAwait(false);

            var stillLocked = new List<string>();
            foreach (var path in deferred)
            {
                var signal = await host.GetSignalAsync(ct).ConfigureAwait(false);
                if (signal != FolderJobSignal.Continue)
                {
                    return await StopAsync(job, host, signal, total, completed, failed, ct).ConfigureAwait(false);
                }

                var outcome = await ProcessAsync(path, catalogued, context, ct).ConfigureAwait(false);
                if (outcome.Locked)
                {
                    stillLocked.Add(path);
                    continue;
                }

                completed++;
                if (outcome.Outcome == FolderOrganiseOutcomes.Failed)
                {
                    failed++;
                }
            }

            deferred = stillLocked;
            await host.ReportProgressAsync(new FolderOrganiseProgress(total, completed, failed, null), ct).ConfigureAwait(false);
        }

        foreach (var path in deferred)
        {
            await _ledger.UpsertAsync(
                new FolderOrganiseLogEntry(
                    job.OperationId, job.SourceId, path, path, FolderOrganiseOutcomes.Failed, DateTimeOffset.UtcNow,
                    "The file was in use or could not be changed, even after trying again."),
                ct).ConfigureAwait(false);
            completed++;
            failed++;
        }

        // 5. Files the job does not move are listed, so nothing is left unexplained.
        await ListLeftoversAsync(job, context, ct).ConfigureAwait(false);

        await host.ReportProgressAsync(new FolderOrganiseProgress(total, completed, failed, null), ct).ConfigureAwait(false);
        return new FolderOrganiseRunResult
        {
            Stop = FolderOrganiseStop.Finished,
            Total = total,
            Completed = completed,
            Failed = failed,
            Outcomes = await _ledger.CountByOutcomeAsync(job.OperationId, ct).ConfigureAwait(false),
        };
    }

    private IReadOnlySet<string> MediaExtensions =>
        _extensions?.GetAllMediaExtensions() ?? FolderMediaEnumerator.DefaultMediaExtensions;

    private FolderConversionContext? Resolve(FolderOrganiseJob job, out FolderConversionUnavailable unavailable) =>
        FolderConversionContext.TryCreate(_configuration.LoadLibraries(), job.OperationId, job.SourceId, out unavailable);

    private async Task<ConversionOutcome> ProcessAsync(
        string path,
        IReadOnlyDictionary<string, MediaAssetFileRef> catalogued,
        FolderConversionContext context,
        CancellationToken ct)
    {
        if (!catalogued.TryGetValue(path, out var file))
        {
            return await _organiser.HandleUncataloguedAsync(path, context, _intake, ct).ConfigureAwait(false);
        }

        var decision = await _organiser.DecideAsync(file, context, plannedTargets: null, ct).ConfigureAwait(false);
        return await _organiser.ApplyAsync(file, decision, context, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyDictionary<string, MediaAssetFileRef>> CataloguedByPathAsync(
        FolderConversionContext context, CancellationToken ct)
    {
        var map = new Dictionary<string, MediaAssetFileRef>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in await _assets.ListFilesUnderAsync(context.SourceRoot, ct).ConfigureAwait(false))
        {
            map.TryAdd(Path.GetFullPath(file.FilePath), file);
        }

        return map;
    }

    private async Task<FolderOrganiseRunResult> StopAsync(
        FolderOrganiseJob job,
        IFolderOrganiseHost host,
        FolderJobSignal signal,
        int total,
        int completed,
        int failed,
        CancellationToken ct)
    {
        await host.ReportProgressAsync(new FolderOrganiseProgress(total, completed, failed, null), ct).ConfigureAwait(false);
        return new FolderOrganiseRunResult
        {
            Stop = signal switch
            {
                FolderJobSignal.Pause => FolderOrganiseStop.Paused,
                FolderJobSignal.Cancel => FolderOrganiseStop.Cancelled,
                _ => FolderOrganiseStop.SourceNotManaged,
            },
            Total = total,
            Completed = completed,
            Failed = failed,
            Outcomes = await _ledger.CountByOutcomeAsync(job.OperationId, ct).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// In folders that files moved out of, lists what is still there (notes, a stray picture, anything the job does not
    /// know how to move) as "not moved", and leaves those folders in place.
    /// </summary>
    private async Task ListLeftoversAsync(FolderOrganiseJob job, FolderConversionContext context, CancellationToken ct)
    {
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;
        const int pageSize = 1000;
        while (true)
        {
            var page = await _ledger.ListAsync(job.OperationId, offset, pageSize, ct).ConfigureAwait(false);
            foreach (var entry in page.Where(item => item.Outcome is FolderOrganiseOutcomes.Moved
                         or FolderOrganiseOutcomes.MovedWithUniqueName
                         or FolderOrganiseOutcomes.DuplicateRemoved
                         or FolderOrganiseOutcomes.SidecarMoved))
            {
                if (Path.GetDirectoryName(entry.OldPath) is { Length: > 0 } folder)
                {
                    touched.Add(folder);
                }
            }

            if (page.Count < pageSize)
            {
                break;
            }

            offset += pageSize;
        }

        var finished = await _ledger.GetFinishedPathsAsync(job.OperationId, ct).ConfigureAwait(false);
        var listed = 0;
        foreach (var folder in touched.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(folder) || !PathSafety.IsContainedBy(folder, context.SourceRoot))
            {
                continue;
            }

            string[] left;
            try
            {
                left = Directory.GetFiles(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not list what is left in {Folder}", folder);
                continue;
            }

            foreach (var file in left.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (finished.Contains(file) || listed >= LeftoverListLimit)
                {
                    continue;
                }

                await _ledger.UpsertAsync(
                    new FolderOrganiseLogEntry(
                        job.OperationId, job.SourceId, file, file, FolderOrganiseOutcomes.NotMoved, DateTimeOffset.UtcNow,
                        "Left where it was: Tuvima does not know how to move this kind of file."),
                    ct).ConfigureAwait(false);
                listed++;
            }
        }
    }
}
