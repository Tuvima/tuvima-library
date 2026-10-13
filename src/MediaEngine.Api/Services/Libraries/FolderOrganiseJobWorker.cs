using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Ingestion.Organise;

namespace MediaEngine.Api.Services.Libraries;

/// <summary>
/// Runs the folder jobs (organising a folder, merging a folder) on the <c>folder_jobs</c> queue, one at a time, from the
/// durable operation table. A job that was running when the Engine stopped is picked up again on the next start: its ledger
/// says which files are done.
/// </summary>
public sealed class FolderOrganiseJobWorker : BackgroundService
{
    private static readonly string[] JobTypes = [MediaOperationType.OrganiseFolder, MediaOperationType.MergeFolder];
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReconcileEvery = TimeSpan.FromSeconds(30);

    private readonly IMediaOperationRepository _operations;
    private readonly IMediaOperationTracker _tracker;
    private readonly FolderOrganiseRunner _runner;
    private readonly FolderMergeRunner _mergeRunner;
    private readonly FolderJobService _jobs;
    private readonly ILogger<FolderOrganiseJobWorker> _logger;
    private readonly string _workerName = Environment.MachineName + ":folder-jobs";

    public FolderOrganiseJobWorker(
        IMediaOperationRepository operations,
        IMediaOperationTracker tracker,
        FolderOrganiseRunner runner,
        FolderMergeRunner mergeRunner,
        FolderJobService jobs,
        ILogger<FolderOrganiseJobWorker> logger)
    {
        _operations = operations;
        _tracker = tracker;
        _runner = runner;
        _mergeRunner = mergeRunner;
        _jobs = jobs;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ReleaseAbandonedJobsAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not check for folder jobs left running by an earlier start.");
        }

        var nextReconcile = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow >= nextReconcile)
                {
                    nextReconcile = DateTimeOffset.UtcNow + ReconcileEvery;
                    await _jobs.ReconcileAsync(stoppingToken).ConfigureAwait(false);
                }

                var leased = await _operations.LeaseNextAsync(_workerName, JobTypes, 1, Lease, stoppingToken).ConfigureAwait(false);
                if (leased.Count == 0)
                {
                    await Task.Delay(IdlePoll, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await RunJobAsync(leased[0], stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The folder job worker hit an unexpected error and will try again.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Only this process runs folder jobs, so any job still marked leased or running when the Engine starts belongs to a
    /// run that no longer exists. It goes back in the queue and resumes from its ledger.
    /// </summary>
    private async Task ReleaseAbandonedJobsAsync(CancellationToken ct)
    {
        foreach (var operation in await _operations.GetQueueAsync(MediaOperationQueue.FolderJobs, 1000, ct).ConfigureAwait(false))
        {
            if (operation.OperationType is MediaOperationType.OrganiseFolder or MediaOperationType.MergeFolder
                && operation.Status is MediaOperationStatus.Leased or MediaOperationStatus.Running)
            {
                await _operations.InterruptIfRunningAsync(operation.Id, "The Engine restarted; resuming from where it stopped.", ct)
                    .ConfigureAwait(false);
                _logger.LogInformation("Folder job {OperationId} was running when the Engine stopped; it will resume.", operation.Id);
            }
        }
    }

    private async Task RunJobAsync(MediaOperation operation, CancellationToken ct)
    {
        if (operation.EntityId is not { } sourceId)
        {
            await _operations.MarkFailedTerminalAsync(operation.Id, "The job has no folder.", ct).ConfigureAwait(false);
            return;
        }

        var merging = operation.OperationType == MediaOperationType.MergeFolder;
        _logger.LogInformation("{Verb} folder {SourceId} (job {OperationId})", merging ? "Merging" : "Organising", sourceId, operation.Id);
        await _operations.UpdateStageAsync(operation.Id, MediaOperationStage.Organising, operation.ProgressPercent, ct).ConfigureAwait(false);
        var host = new JobHost(operation, _operations, _tracker);
        try
        {
            var result = merging
                ? await _mergeRunner.RunAsync(new FolderMergeJob(operation.Id, sourceId), host, ct).ConfigureAwait(false)
                : await _runner.RunAsync(new FolderOrganiseJob(operation.Id, sourceId), host, ct).ConfigureAwait(false);
            await SettleAsync(operation, sourceId, result, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The Engine is stopping. The ledger keeps the place; the job continues on the next start.
            // Only a job still running is released: one the person paused or cancelled meanwhile keeps that state.
            await _operations.InterruptIfRunningAsync(operation.Id, "The Engine stopped; the job resumes on the next start.", CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Verb} folder {SourceId} failed", merging ? "Merging" : "Organising", sourceId);
            await _operations.MarkFailedTerminalAsync(
                operation.Id,
                merging
                    ? "Merging stopped because of an unexpected error. What was already copied stays where it is. See the Engine log."
                    : "Organising stopped because of an unexpected error. See the Engine log.",
                ct).ConfigureAwait(false);
            await FinishAsync(operation, sourceId, completed: false, ct).ConfigureAwait(false);
        }
    }

    /// <summary>A job is over: the folder stops saying it is converting or merging.</summary>
    private Task FinishAsync(MediaOperation operation, Guid sourceId, bool completed, CancellationToken ct) =>
        operation.OperationType == MediaOperationType.MergeFolder
            ? _jobs.FinishMergeAsync(sourceId, operation.Id, completed, ct)
            : _jobs.FinishConversionAsync(sourceId, operation.Id, ct);

    private async Task SettleAsync(MediaOperation operation, Guid sourceId, FolderOrganiseRunResult result, CancellationToken ct)
    {
        var merging = operation.OperationType == MediaOperationType.MergeFolder;
        switch (result.Stop)
        {
            case FolderOrganiseStop.Finished:
                await _operations.RecordProgressAsync(
                    operation.Id, result.Total, result.Completed, result.Failed, 100, ct: ct).ConfigureAwait(false);
                await _tracker.MarkSucceededAsync(
                    operation.Id,
                    merging ? FolderJobService.MergeCompletionMessage(result) : FolderJobService.CompletionMessage(result),
                    ct: ct).ConfigureAwait(false);
                await FinishAsync(operation, sourceId, completed: true, ct).ConfigureAwait(false);
                break;

            case FolderOrganiseStop.Paused:
                // Pausing already set the status; nothing to settle.
                break;

            case FolderOrganiseStop.Cancelled:
                await FinishAsync(operation, sourceId, completed: false, ct).ConfigureAwait(false);
                break;

            case FolderOrganiseStop.TargetUnavailable:
                await _operations.MarkCancelledAsync(
                    operation.Id,
                    "The folder being merged into is no longer there or no longer managed, so Tuvima stopped. What was already copied stays where it is; the log lists what remains in the source folder.",
                    ct).ConfigureAwait(false);
                await FinishAsync(operation, sourceId, completed: false, ct).ConfigureAwait(false);
                break;

            case FolderOrganiseStop.SourceNotManaged:
                await _operations.MarkCancelledAsync(
                    operation.Id,
                    "The folder was switched to As-is, so Tuvima stopped organising it. Nothing was undone.",
                    ct).ConfigureAwait(false);
                break;

            default:
                await _operations.MarkFailedTerminalAsync(
                    operation.Id, "The folder is no longer part of your libraries.", ct).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>The runner's window onto the operation row: control in, progress out.</summary>
    private sealed class JobHost : IFolderOrganiseHost
    {
        private readonly MediaOperation _operation;
        private readonly IMediaOperationRepository _operations;
        private readonly IMediaOperationTracker _tracker;
        private int _lastTotal;

        public JobHost(MediaOperation operation, IMediaOperationRepository operations, IMediaOperationTracker tracker)
        {
            _operation = operation;
            _operations = operations;
            _tracker = tracker;
        }

        public async Task<FolderJobSignal> GetSignalAsync(CancellationToken ct)
        {
            var current = await _operations.GetByIdAsync(_operation.Id, ct).ConfigureAwait(false);
            return current?.Status switch
            {
                MediaOperationStatus.Leased or MediaOperationStatus.Running => FolderJobSignal.Continue,
                MediaOperationStatus.Cancelled => FolderJobSignal.Cancel,
                null => FolderJobSignal.Cancel,
                // Paused, or put back in the queue after being resumed too quickly: stop and let the queue decide.
                _ => FolderJobSignal.Pause,
            };
        }

        public async Task ReportProgressAsync(FolderOrganiseProgress progress, CancellationToken ct)
        {
            _lastTotal = progress.Total;
            var percent = progress.Total <= 0 ? 0 : (int)Math.Clamp(progress.Completed * 100L / progress.Total, 0, 99);
            await _tracker.ReportProgressAsync(
                _operation.Id, progress.Total, progress.Completed, progress.Failed, percent, Lease, ct).ConfigureAwait(false);
        }
    }
}
