using System.Globalization;
using System.Text;
using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services.Settings;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Ingestion.Organise;

namespace MediaEngine.Api.Services.Libraries;

/// <summary>A folder job request that cannot be honoured, with a plain-English reason.</summary>
public sealed class FolderJobException(string message, bool notFound = false, bool conflict = false) : Exception(message)
{
    public bool NotFound { get; } = notFound;

    public bool Conflict { get; } = conflict;
}

/// <summary>
/// Starts, controls and reports on the long folder jobs (converting a folder to Managed today, merging folders next). A
/// job is one durable <c>media_operations</c> row on the <c>folder_jobs</c> queue, so it survives a restart and the
/// Dashboard sees it like any other background work. The files themselves are moved by <see cref="FolderOrganiseRunner"/>.
/// </summary>
public sealed class FolderJobService
{
    private const string OrganiseFolder = MediaOperationType.OrganiseFolder;
    private const int DefaultLogPage = 200;
    private const int MaxLogPage = 1000;
    private static readonly TimeSpan RecentJobWindow = TimeSpan.FromDays(7);

    private readonly IConfigurationLoader _configuration;
    private readonly IMediaOperationRepository _operations;
    private readonly IMediaOperationTracker _tracker;
    private readonly IFolderOrganiseLogRepository _ledger;
    private readonly FolderConversionPreviewer _previewer;
    private readonly ServerFolderBrowserService _folders;
    private readonly ILogger<FolderJobService> _logger;

    public FolderJobService(
        IConfigurationLoader configuration,
        IMediaOperationRepository operations,
        IMediaOperationTracker tracker,
        IFolderOrganiseLogRepository ledger,
        FolderConversionPreviewer previewer,
        ServerFolderBrowserService folders,
        ILogger<FolderJobService> logger)
    {
        _configuration = configuration;
        _operations = operations;
        _tracker = tracker;
        _ledger = ledger;
        _previewer = previewer;
        _folders = folders;
        _logger = logger;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Preview
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The honest numbers behind the confirmation: a quick count of the folder's media files and a time estimate, or the
    /// full dry run of what converting would do. Nothing is changed.
    /// </summary>
    public async Task<FolderConversionPreviewResponse> PreviewAsync(FolderConversionPreviewRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sourceId = ParseSourceId(request.SourceId);
        var libraries = _configuration.LoadLibraries();
        var library = FindLibrary(libraries, sourceId)
            ?? throw new FolderJobException("This folder is not part of your libraries.", notFound: true);
        var measured = await MeasuredSecondsPerFileAsync(ct).ConfigureAwait(false);

        if (request.Quick)
        {
            var count = await _previewer.CountAsync(sourceId, ct).ConfigureAwait(false)
                ?? throw new FolderJobException("This folder could not be read.", notFound: true);
            return new FolderConversionPreviewResponse
            {
                SourceId = request.SourceId,
                TotalFiles = count.Total,
                VideoFiles = count.Video,
                Examined = 0,
                Complete = false,
                DuplicatePolicy = libraries.FileHandling.DuplicatePolicy,
                EstimatedSeconds = EstimateSeconds(count.Video, count.Total - count.Video, measured),
            };
        }

        var preview = await _previewer.PreviewAsync(sourceId, Math.Max(0, request.FileLimit), ct).ConfigureAwait(false)
            ?? throw new FolderJobException("This folder could not be read.", notFound: true);
        _ = library;
        return new FolderConversionPreviewResponse
        {
            SourceId = request.SourceId,
            TotalFiles = preview.TotalFiles,
            VideoFiles = preview.VideoFiles,
            Examined = preview.Examined,
            Complete = preview.Complete,
            ToRename = preview.ToRename,
            AlreadyCorrect = preview.AlreadyCorrect,
            DuplicatesToRemove = preview.DuplicatesToRemove,
            NeedsReview = preview.NeedsReview,
            NameCollisions = preview.NameCollisions,
            Skipped = preview.Skipped,
            SkipReasons = preview.SkipReasons.Select(group => new FolderConversionSkipDto { Reason = group.Reason, Count = group.Count }).ToList(),
            Samples = preview.Samples.Select(sample => new FolderConversionSampleDto { OldPath = sample.OldPath, NewPath = sample.NewPath }).ToList(),
            DuplicatePolicy = preview.DuplicatePolicy,
            EstimatedSeconds = EstimateSeconds(preview.VideoFiles, preview.TotalFiles - preview.VideoFiles, measured),
        };
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Starting
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Makes the folder Managed and queues the job that organises the files already in it. Safe to call twice: a folder that
    /// already has a job in progress gets that job back instead of a second one.
    /// </summary>
    public async Task<FolderJobStatusDto> StartConversionAsync(string sourceIdText, CancellationToken ct)
    {
        var sourceId = ParseSourceId(sourceIdText);
        await LibraryMutationEndpoints.WriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var libraries = _configuration.LoadLibraries();
            var library = FindLibrary(libraries, sourceId)
                ?? throw new FolderJobException("This folder is not part of your libraries.", notFound: true);
            var source = library.Sources.First(candidate => Guid.TryParse(candidate.Id, out var id) && id == sourceId);

            var running = await FindOpenOperationAsync(sourceId, ct).ConfigureAwait(false);
            if (running is not null)
            {
                return await ToStatusAsync(running, libraries, ct).ConfigureAwait(false);
            }

            var proposed = Clone(libraries);
            var proposedSource = FindSource(proposed, sourceId)!;
            proposedSource.ManagementMode = LibrarySourceManagementModes.ManagedByTuvima;
            proposedSource.AccessMode = LibrarySourceAccessModes.Writable;
            var check = _folders.Validate(new ValidateServerFolderRequest
            {
                ManualPath = source.Path,
                CurrentSourceId = source.Id,
                SelectionMode = ServerFolderSelectionModes.ManagedLibrary,
            }, proposed);
            if (!check.CanSelect)
            {
                throw new FolderJobException(
                    check.Issues.FirstOrDefault(issue => issue.Severity == "error")?.Message
                    ?? "Tuvima cannot write to this folder, so it cannot manage it.");
            }

            var count = await _previewer.CountAsync(sourceId, ct).ConfigureAwait(false);
            var operationId = Guid.NewGuid();
            var wasManaged = source.IsManaged;
            var previousState = (source.ConversionState, source.ConversionOperationId);

            source.ManagementMode = LibrarySourceManagementModes.ManagedByTuvima;
            source.AccessMode = LibrarySourceAccessModes.Writable;
            source.ConversionState = LibrarySourceConversionStates.Converting;
            source.ConversionOperationId = operationId.ToString("D");
            try
            {
                _configuration.SaveLibraries(libraries);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new FolderJobException($"The folder settings could not be saved: {ex.Message}");
            }

            try
            {
                var operation = await _tracker.EnsureQueuedAsync(new MediaOperation
                {
                    Id = operationId,
                    OperationType = OrganiseFolder,
                    OperationKind = MediaOperationKind.Maintenance,
                    EntityId = sourceId,
                    EntityKind = "library_source",
                    SourcePath = source.Path,
                    Status = MediaOperationStatus.Queued,
                    Stage = MediaOperationStage.Queued,
                    QueueName = MediaOperationQueue.FolderJobs,
                    ItemsTotal = count?.Total ?? 0,
                    IdempotencyKey = $"{OrganiseFolder}:{sourceId:D}:{operationId:N}",
                }, ct).ConfigureAwait(false);
                return await ToStatusAsync(operation, libraries, ct).ConfigureAwait(false);
            }
            catch
            {
                // No job exists, so the folder must not be left saying it is converting.
                var current = _configuration.LoadLibraries();
                if (FindSource(current, sourceId) is { } saved)
                {
                    saved.ConversionState = previousState.ConversionState;
                    saved.ConversionOperationId = previousState.ConversionOperationId;
                    if (!wasManaged)
                    {
                        saved.ManagementMode = LibrarySourceManagementModes.ExistingLibrary;
                    }

                    _configuration.SaveLibraries(current);
                }

                throw;
            }
        }
        finally
        {
            LibraryMutationEndpoints.WriteGate.Release();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Controlling
    // ─────────────────────────────────────────────────────────────────────────────

    public async Task<FolderJobStatusDto> ControlAsync(Guid operationId, string action, CancellationToken ct)
    {
        var operation = await RequireJobAsync(operationId, ct).ConfigureAwait(false);
        switch (action?.Trim().ToLowerInvariant())
        {
            case "pause":
                if (!await _operations.PauseAsync(operationId, ct).ConfigureAwait(false))
                {
                    throw new FolderJobException("This job has already finished or is already paused.", conflict: true);
                }

                break;

            case "resume":
                if (!await _operations.ResumePausedAsync(operationId, ct).ConfigureAwait(false))
                {
                    throw new FolderJobException("This job is not paused.", conflict: true);
                }

                break;

            case "cancel":
                if (IsFinished(operation.Status))
                {
                    throw new FolderJobException("This job has already finished.", conflict: true);
                }

                await _operations.MarkCancelledAsync(operationId, StoppedMessage(operation), ct).ConfigureAwait(false);
                // A job nobody is running (paused, waiting) has no worker to tidy up after it.
                if (operation.Status is not (MediaOperationStatus.Leased or MediaOperationStatus.Running)
                    && operation.EntityId is { } sourceId)
                {
                    await FinishConversionAsync(sourceId, operationId, ct).ConfigureAwait(false);
                }

                break;

            default:
                throw new FolderJobException("The action must be pause, resume or cancel.");
        }

        await PublishAsync(operationId, ct).ConfigureAwait(false);
        var updated = await _operations.GetByIdAsync(operationId, ct).ConfigureAwait(false) ?? operation;
        return await ToStatusAsync(updated, _configuration.LoadLibraries(), ct).ConfigureAwait(false);
    }

    /// <summary>Stops every open folder job whose folder is no longer managed. Nothing on disk is changed.</summary>
    public async Task StopJobsForUnmanagedFoldersAsync(LibrariesConfiguration libraries, CancellationToken ct)
    {
        foreach (var operation in await OpenOperationsAsync(ct).ConfigureAwait(false))
        {
            if (operation.EntityId is not { } sourceId)
            {
                continue;
            }

            var source = FindSource(libraries, sourceId);
            if (source is { IsManaged: true })
            {
                continue;
            }

            _logger.LogInformation("Stopping folder job {OperationId}: its folder is no longer managed", operation.Id);
            await _operations.MarkCancelledAsync(
                operation.Id, "The folder was switched to As-is, so Tuvima stopped organising it. Nothing was undone.", ct)
                .ConfigureAwait(false);
            await PublishAsync(operation.Id, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The folder's settings are saved from the Dashboard, which only knows the conversion state it last read. The state
    /// belongs to the Engine, so a saved library keeps whatever the Engine has for each folder it already knew.
    /// </summary>
    public static void KeepEngineOwnedConversionState(LibrariesConfiguration current, LibrariesConfiguration proposed)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(proposed);

        var known = current.Libraries.SelectMany(library => library.Sources)
            .GroupBy(source => source.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var source in proposed.Libraries.SelectMany(library => library.Sources))
        {
            if (source.IsManaged && known.TryGetValue(source.Id, out var existing))
            {
                source.ConversionState = existing.ConversionState;
                source.ConversionOperationId = existing.ConversionOperationId;
            }
            else
            {
                source.ConversionState = LibrarySourceConversionStates.None;
                source.ConversionOperationId = null;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Finishing (called by the worker)
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>A conversion is over (finished, cancelled or failed): the folder is simply Managed from here on.</summary>
    public async Task FinishConversionAsync(Guid sourceId, Guid operationId, CancellationToken ct)
    {
        // Only a job that is over settles its folder. A job that has not even been created yet (a settings save in the middle
        // of starting one) must not flip the folder to converted early.
        var job = await _operations.GetByIdAsync(operationId, ct).ConfigureAwait(false);
        if (job is not null && !IsFinished(job.Status))
        {
            return;
        }

        await LibraryMutationEndpoints.WriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var libraries = _configuration.LoadLibraries();
            var source = FindSource(libraries, sourceId);
            if (source is null
                || !source.IsManaged
                || source.ConversionState != LibrarySourceConversionStates.Converting
                || !string.Equals(source.ConversionOperationId, operationId.ToString("D"), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            source.ConversionState = LibrarySourceConversionStates.Converted;
            _configuration.SaveLibraries(libraries);
        }
        finally
        {
            LibraryMutationEndpoints.WriteGate.Release();
        }
    }

    /// <summary>
    /// Settles any folder still marked as converting whose job is over (cancelled from somewhere else, or lost), so the
    /// row never says "Converting" for a job that is not running.
    /// </summary>
    public async Task ReconcileAsync(CancellationToken ct)
    {
        var libraries = _configuration.LoadLibraries();
        var stale = new List<(Guid SourceId, Guid OperationId)>();
        foreach (var source in libraries.Libraries.SelectMany(library => library.Sources)
                     .Where(source => source.ConversionState == LibrarySourceConversionStates.Converting))
        {
            if (!Guid.TryParse(source.Id, out var sourceId) || !Guid.TryParse(source.ConversionOperationId, out var operationId))
            {
                continue;
            }

            var operation = await _operations.GetByIdAsync(operationId, ct).ConfigureAwait(false);
            if (operation is null || IsFinished(operation.Status))
            {
                stale.Add((sourceId, operationId));
            }
        }

        foreach (var (sourceId, operationId) in stale)
        {
            await FinishConversionAsync(sourceId, operationId, ct).ConfigureAwait(false);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Reporting
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The open jobs and, for each folder, its most recent finished job from the last week.</summary>
    public async Task<IReadOnlyList<FolderJobStatusDto>> ListAsync(CancellationToken ct)
    {
        var libraries = _configuration.LoadLibraries();
        var queue = await JobsAsync(ct).ConfigureAwait(false);
        var cutoff = DateTimeOffset.UtcNow - RecentJobWindow;
        var chosen = new List<MediaOperation>();
        foreach (var group in queue.GroupBy(operation => operation.EntityId))
        {
            var open = group.Where(operation => !IsFinished(operation.Status)).ToList();
            if (open.Count > 0)
            {
                chosen.AddRange(open);
                continue;
            }

            var latest = group.OrderByDescending(operation => operation.UpdatedAt).First();
            if (latest.UpdatedAt >= cutoff)
            {
                chosen.Add(latest);
            }
        }

        var measured = MeasuredSecondsPerFile(queue);
        var result = new List<FolderJobStatusDto>(chosen.Count);
        foreach (var operation in chosen.OrderBy(operation => operation.PositionKey))
        {
            result.Add(await ToStatusAsync(operation, libraries, ct, queue, measured).ConfigureAwait(false));
        }

        return result;
    }

    public async Task<FolderJobStatusDto?> GetAsync(Guid operationId, CancellationToken ct)
    {
        var operation = await _operations.GetByIdAsync(operationId, ct).ConfigureAwait(false);
        return operation is null || !IsFolderJob(operation)
            ? null
            : await ToStatusAsync(operation, _configuration.LoadLibraries(), ct).ConfigureAwait(false);
    }

    public async Task<FolderJobLogDto> GetLogAsync(Guid operationId, int offset, int limit, CancellationToken ct)
    {
        await RequireJobAsync(operationId, ct).ConfigureAwait(false);
        var page = await _ledger.ListAsync(operationId, Math.Max(0, offset), Math.Clamp(limit <= 0 ? DefaultLogPage : limit, 1, MaxLogPage), ct)
            .ConfigureAwait(false);
        return new FolderJobLogDto
        {
            OperationId = operationId.ToString("D"),
            Total = await _ledger.CountAsync(operationId, ct).ConfigureAwait(false),
            Offset = Math.Max(0, offset),
            Entries = page.Select(entry => new FolderJobLogEntryDto
            {
                OldPath = entry.OldPath,
                NewPath = entry.NewPath,
                Outcome = entry.Outcome,
                Detail = entry.Detail,
                At = entry.At,
            }).ToList(),
        };
    }

    /// <summary>Writes the whole log as CSV (old path, new path, outcome, detail, time) a page at a time.</summary>
    public async Task WriteLogCsvAsync(Guid operationId, Stream destination, CancellationToken ct)
    {
        await RequireJobAsync(operationId, ct).ConfigureAwait(false);
        await using var writer = new StreamWriter(destination, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true);
        // CSV lines end with a carriage return and line feed on every system.
        writer.NewLine = "\r\n";
        await writer.WriteLineAsync("old_path,new_path,outcome,detail,at").ConfigureAwait(false);
        var offset = 0;
        while (true)
        {
            var page = await _ledger.ListAsync(operationId, offset, MaxLogPage, ct).ConfigureAwait(false);
            foreach (var entry in page)
            {
                await writer.WriteLineAsync(string.Join(',',
                    Csv(entry.OldPath), Csv(entry.NewPath), Csv(entry.Outcome), Csv(entry.Detail),
                    Csv(entry.At.ToString("O", CultureInfo.InvariantCulture)))).ConfigureAwait(false);
            }

            if (page.Count < MaxLogPage)
            {
                break;
            }

            offset += MaxLogPage;
        }

        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The sentence shown when a conversion finishes: what was organised, and what needs a look.</summary>
    public static string CompletionMessage(FolderOrganiseRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var organised = result.Count(FolderOrganiseOutcomes.Moved)
            + result.Count(FolderOrganiseOutcomes.MovedWithUniqueName)
            + result.Count(FolderOrganiseOutcomes.AlreadyCorrect)
            + result.Count(FolderOrganiseOutcomes.DuplicateRemoved);
        var parts = new List<string> { $"{organised.ToString("N0", CultureInfo.InvariantCulture)} organised" };
        if (result.Failed > 0)
        {
            parts.Add($"{result.Failed.ToString("N0", CultureInfo.InvariantCulture)} could not be moved - see log");
        }

        var review = result.Count(FolderOrganiseOutcomes.NeedsReview);
        if (review > 0)
        {
            parts.Add($"{review.ToString("N0", CultureInfo.InvariantCulture)} waiting for Review");
        }

        var identifying = result.Count(FolderOrganiseOutcomes.QueuedForIdentification);
        if (identifying > 0)
        {
            parts.Add($"{identifying.ToString("N0", CultureInfo.InvariantCulture)} being identified first");
        }

        return string.Join(", ", parts) + ".";
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────────

    private async Task<MediaOperation> RequireJobAsync(Guid operationId, CancellationToken ct)
    {
        var operation = await _operations.GetByIdAsync(operationId, ct).ConfigureAwait(false);
        return operation is not null && IsFolderJob(operation)
            ? operation
            : throw new FolderJobException("That folder job was not found.", notFound: true);
    }

    private static bool IsFolderJob(MediaOperation operation) =>
        string.Equals(operation.QueueName, MediaOperationQueue.FolderJobs, StringComparison.Ordinal);

    private static bool IsFinished(string status) => status is MediaOperationStatus.Succeeded
        or MediaOperationStatus.Cancelled
        or MediaOperationStatus.FailedTerminal
        or MediaOperationStatus.DeadLettered
        or MediaOperationStatus.Blocked
        or MediaOperationStatus.NoResult
        or MediaOperationStatus.Skipped
        or MediaOperationStatus.MissingConfirmed
        or MediaOperationStatus.NotApplicable;

    private async Task<IReadOnlyList<MediaOperation>> JobsAsync(CancellationToken ct) =>
        (await _operations.GetQueueAsync(MediaOperationQueue.FolderJobs, 1000, ct).ConfigureAwait(false))
            .Where(operation => operation.EntityId is not null)
            .ToList();

    private async Task<IReadOnlyList<MediaOperation>> OpenOperationsAsync(CancellationToken ct) =>
        (await JobsAsync(ct).ConfigureAwait(false)).Where(operation => !IsFinished(operation.Status)).ToList();

    private async Task<MediaOperation?> FindOpenOperationAsync(Guid sourceId, CancellationToken ct) =>
        (await OpenOperationsAsync(ct).ConfigureAwait(false)).FirstOrDefault(operation =>
            operation.EntityId == sourceId && operation.OperationType == OrganiseFolder);

    private async Task PublishAsync(Guid operationId, CancellationToken ct)
    {
        var operation = await _operations.GetByIdAsync(operationId, ct).ConfigureAwait(false);
        if (operation is not null)
        {
            await _tracker.ReportProgressAsync(
                operationId, operation.ItemsTotal, operation.ItemsCompleted, operation.ItemsFailed, operation.ProgressPercent, ct: ct)
                .ConfigureAwait(false);
        }
    }

    private static string StoppedMessage(MediaOperation operation) =>
        operation.ItemsTotal > 0
            ? $"Stopped after {operation.ItemsCompleted.ToString("N0", CultureInfo.InvariantCulture)} of {operation.ItemsTotal.ToString("N0", CultureInfo.InvariantCulture)}; the rest will be tidied gradually by normal scans."
            : "Stopped; the rest will be tidied gradually by normal scans.";

    private async Task<FolderJobStatusDto> ToStatusAsync(
        MediaOperation operation,
        LibrariesConfiguration libraries,
        CancellationToken ct,
        IReadOnlyList<MediaOperation>? queue = null,
        double? measured = null)
    {
        queue ??= await JobsAsync(ct).ConfigureAwait(false);
        measured ??= MeasuredSecondsPerFile(queue);
        var state = StateOf(operation.Status);
        var status = new FolderJobStatusDto
        {
            OperationId = operation.Id.ToString("D"),
            SourceId = operation.EntityId?.ToString("D") ?? string.Empty,
            JobType = operation.OperationType,
            State = state,
            ItemsTotal = operation.ItemsTotal,
            ItemsCompleted = operation.ItemsCompleted,
            ItemsFailed = operation.ItemsFailed,
            ProgressPercent = state == "completed" ? 100 : operation.ProgressPercent,
            StartedAt = operation.StartedAt,
            CompletedAt = operation.CompletedAt,
            HasLog = operation.ItemsCompleted > 0 || state is "completed" or "cancelled" or "failed",
            Message = state switch
            {
                "completed" or "cancelled" => operation.ResultSummary,
                "failed" => operation.LastError ?? "Organising stopped before it finished.",
                "paused" => $"Paused at {operation.ItemsCompleted.ToString("N0", CultureInfo.InvariantCulture)} of {operation.ItemsTotal.ToString("N0", CultureInfo.InvariantCulture)}.",
                _ => null,
            },
        };

        if (state == "running" || state == "queued")
        {
            var perFile = PerFileSeconds(operation, libraries, measured);
            var upFront = TimeSpan.FromSeconds(perFile * Math.Max(1, operation.ItemsTotal));
            var elapsed = operation.StartedAt is { } started ? DateTimeOffset.UtcNow - started : TimeSpan.Zero;
            status.EstimatedSecondsRemaining = FolderConversionEstimator.Remaining(
                operation.ItemsTotal, operation.ItemsCompleted, elapsed, upFront) is { } remaining
                ? (int)Math.Ceiling(remaining.TotalSeconds)
                : null;
        }

        if (state == "queued")
        {
            var ahead = queue.FirstOrDefault(other => other.Id != operation.Id
                && other.Status is MediaOperationStatus.Leased or MediaOperationStatus.Running);
            if (ahead?.EntityId is { } aheadSource)
            {
                status.QueuedBehind = FolderName(libraries, aheadSource);
            }
        }

        return status;
    }

    private static string StateOf(string status) => status switch
    {
        MediaOperationStatus.Running or MediaOperationStatus.Leased => "running",
        MediaOperationStatus.Paused => "paused",
        MediaOperationStatus.Succeeded => "completed",
        MediaOperationStatus.Cancelled => "cancelled",
        MediaOperationStatus.FailedTerminal or MediaOperationStatus.DeadLettered or MediaOperationStatus.Blocked => "failed",
        _ => "queued",
    };

    /// <summary>Seconds a file takes: what earlier jobs measured, or the default for the folder's kind of media.</summary>
    private static double PerFileSeconds(MediaOperation operation, LibrariesConfiguration libraries, double? measured)
    {
        if (measured is > 0)
        {
            return measured.Value;
        }

        var library = operation.EntityId is { } sourceId ? FindLibrary(libraries, sourceId) : null;
        var video = library?.MediaTypes.Any(type => type.Equals("Movies", StringComparison.OrdinalIgnoreCase)
            || type.Equals("TV", StringComparison.OrdinalIgnoreCase)
            || type.Equals("TVShows", StringComparison.OrdinalIgnoreCase)) == true;
        return video ? FolderConversionEstimator.DefaultSecondsPerVideoFile : FolderConversionEstimator.DefaultSecondsPerOtherFile;
    }

    private async Task<double?> MeasuredSecondsPerFileAsync(CancellationToken ct) =>
        MeasuredSecondsPerFile(await JobsAsync(ct).ConfigureAwait(false));

    /// <summary>The average seconds a file took in the most recent finished job that did enough files to say.</summary>
    private static double? MeasuredSecondsPerFile(IReadOnlyList<MediaOperation> queue)
    {
        var finished = queue
            .Where(operation => operation.Status == MediaOperationStatus.Succeeded
                && operation.StartedAt is not null
                && operation.CompletedAt is not null
                && operation.ItemsCompleted >= FolderConversionEstimator.LiveRateAfterFiles)
            .OrderByDescending(operation => operation.CompletedAt)
            .FirstOrDefault();
        return finished is null
            ? null
            : (finished.CompletedAt!.Value - finished.StartedAt!.Value).TotalSeconds / finished.ItemsCompleted;
    }

    private static int EstimateSeconds(int video, int other, double? measured) =>
        (int)Math.Ceiling(FolderConversionEstimator.Estimate(video, Math.Max(0, other), measured).TotalSeconds);

    private static string FolderName(LibrariesConfiguration libraries, Guid sourceId)
    {
        var source = FindSource(libraries, sourceId);
        if (source is null)
        {
            return "another folder";
        }

        if (!string.IsNullOrWhiteSpace(source.Notes))
        {
            return source.Notes!;
        }

        var name = Path.GetFileName(source.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? source.Path : name;
    }

    private static Guid ParseSourceId(string value) =>
        Guid.TryParse(value, out var id) && id != Guid.Empty
            ? id
            : throw new FolderJobException("Choose a folder from your libraries.");

    private static LibraryFolderConfig? FindLibrary(LibrariesConfiguration libraries, Guid sourceId) =>
        libraries.Libraries.FirstOrDefault(library => library.Sources.Any(source =>
            Guid.TryParse(source.Id, out var id) && id == sourceId));

    private static LibrarySourceConfig? FindSource(LibrariesConfiguration libraries, Guid sourceId) =>
        libraries.Libraries.SelectMany(library => library.Sources)
            .FirstOrDefault(source => Guid.TryParse(source.Id, out var id) && id == sourceId);

    private static LibrariesConfiguration Clone(LibrariesConfiguration libraries) =>
        JsonSerializer.Deserialize<LibrariesConfiguration>(JsonSerializer.Serialize(libraries))!;

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
