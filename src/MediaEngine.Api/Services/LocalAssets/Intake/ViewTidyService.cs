using System.Globalization;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Api.Services.LocalAssets.Intake;

/// <summary>
/// Tidy this folder (Photo and video filing spec D17): files what is already in a Managed folder into the date layout, in place. It only
/// renames inside the folder, on the same drive, so there is no copy and no temporary space. A run is durable: the plan is written to a
/// ledger first, each rename is recorded as it happens, and a run that was stopped resumes from the ledger without losing or repeating a file.
/// </summary>
public sealed class ViewTidyService(
    ViewStorageService storage,
    ViewLibraryService library,
    IViewSharedLibraryRepository sharedLibrary,
    IViewTidyRepository tidy,
    IConfigurationLoader configuration,
    TimeProvider time,
    ILogger<ViewTidyService> logger)
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    private sealed record Target(Guid SourceId, Guid LibraryId, string Path, string ContentKind, string Name);

    /// <summary>Asks the worker to look for work now.</summary>
    public void RequestRun()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already asked for.
        }
    }

    internal Task<bool> WaitForRequestAsync(TimeSpan timeout, CancellationToken ct) => _wake.WaitAsync(timeout, ct);

    /// <summary>What Tidy would do right now, or the state of the run that is going or last finished. Null when the folder cannot be tidied.</summary>
    public async Task<ViewTidyStatusDto?> GetStatusAsync(Guid sourceId, CancellationToken ct = default)
    {
        var target = await ResolveAsync(sourceId, ct).ConfigureAwait(false);
        if (target is null)
        {
            return null;
        }

        var latest = await tidy.GetLatestJobAsync(sourceId, ct).ConfigureAwait(false);
        var scheduled = await tidy.IsScheduledAsync(sourceId, ct).ConfigureAwait(false);
        var hour = Math.Clamp(configuration.LoadPhotoFiling().TidyHour, 0, 23);
        if (latest is { State: ViewTidyStates.Running })
        {
            return Status(target, latest, 0, null, null, scheduled, hour);
        }

        var plan = await PlanAsync(target, ct).ConfigureAwait(false);
        var first = plan.FirstOrDefault();
        return Status(target, latest, plan.Count,
            first is null ? null : Path.GetRelativePath(target.Path, first.FromPath),
            first is null ? null : Path.GetRelativePath(target.Path, first.ToPath), scheduled, hour);
    }

    /// <summary>Plans the renames and starts the run. Returns null when the folder cannot be tidied; refuses when a run is going or nothing needs tidying.</summary>
    public async Task<ViewTidyStatusDto?> StartAsync(Guid sourceId, Guid? startedBy, string trigger, CancellationToken ct = default)
    {
        var target = await ResolveAsync(sourceId, ct).ConfigureAwait(false);
        if (target is null)
        {
            return null;
        }

        if (await tidy.GetLatestJobAsync(sourceId, ct).ConfigureAwait(false) is { State: ViewTidyStates.Running })
        {
            throw new InvalidOperationException("This folder is already being tidied.");
        }

        var plan = await PlanAsync(target, ct).ConfigureAwait(false);
        if (plan.Count == 0)
        {
            throw new InvalidOperationException("Everything in this folder is already filed by date.");
        }

        var job = new ViewTidyJob(Guid.NewGuid(), sourceId, trigger, ViewTidyStates.Running, plan.Count, 0, 0, null, time.GetUtcNow(), null);
        await tidy.CreateJobAsync(job, plan.Select(move => move with { JobId = job.Id }).ToList(), startedBy, ct).ConfigureAwait(false);
        RequestRun();
        return await GetStatusAsync(sourceId, ct).ConfigureAwait(false);
    }

    public async Task<ViewTidyStatusDto?> SetScheduledAsync(Guid sourceId, bool scheduled, CancellationToken ct = default)
    {
        if (await ResolveAsync(sourceId, ct).ConfigureAwait(false) is null)
        {
            return null;
        }

        await tidy.SetScheduledAsync(sourceId, scheduled, time.GetUtcNow(), ct).ConfigureAwait(false);
        return await GetStatusAsync(sourceId, ct).ConfigureAwait(false);
    }

    /// <summary>Starts the nightly runs for every scheduled folder that has something to tidy. A folder already being tidied is left alone.</summary>
    public async Task StartScheduledAsync(CancellationToken ct = default)
    {
        foreach (var sourceId in await tidy.GetScheduledSourceIdsAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await StartAsync(sourceId, null, "scheduled", ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Already going, or nothing to tidy tonight.
            }
        }
    }

    /// <summary>Runs every job still marked running: new ones, and ones a stop interrupted (the ledger says what is left).</summary>
    public async Task RunPendingAsync(CancellationToken ct = default)
    {
        foreach (var job in await tidy.GetRunningJobsAsync(ct).ConfigureAwait(false))
        {
            await RunAsync(job, ct).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(ViewTidyJob job, CancellationToken ct)
    {
        var target = await ResolveAsync(job.SourceId, ct).ConfigureAwait(false);
        if (target is null)
        {
            await tidy.FinishJobAsync(job.Id, ViewTidyStates.Failed, "The folder is no longer there, so nothing was changed.", time.GetUtcNow(), ct).ConfigureAwait(false);
            return;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.Path));
        foreach (var move in await tidy.GetPlannedMovesAsync(job.Id, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!ViewStorageService.Contains(root, move.FromPath) || !ViewStorageService.Contains(root, move.ToPath))
                {
                    await tidy.FailMoveAsync(job.Id, move.SourceFileId, "outside the folder", ct).ConfigureAwait(false);
                }
                else if (File.Exists(move.FromPath) && !File.Exists(move.ToPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(move.ToPath)!);
                    File.Move(move.FromPath, move.ToPath, overwrite: false);
                    await tidy.CompleteMoveAsync(job.Id, move.SourceFileId, move.ToPath, ct).ConfigureAwait(false);
                }
                else if (!File.Exists(move.FromPath) && File.Exists(move.ToPath))
                {
                    // A stop came between the rename and its record: only the record is missing.
                    await tidy.CompleteMoveAsync(job.Id, move.SourceFileId, move.ToPath, ct).ConfigureAwait(false);
                }
                else
                {
                    await tidy.FailMoveAsync(job.Id, move.SourceFileId, File.Exists(move.ToPath) ? "a file is already there" : "the file is gone", ct)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "A file could not be renamed while tidying a folder; the rest continue.");
                await tidy.FailMoveAsync(job.Id, move.SourceFileId, "it could not be renamed", ct).ConfigureAwait(false);
            }
        }

        PruneEmptyFolders(root);
        var done = await tidy.GetLatestJobAsync(job.SourceId, ct).ConfigureAwait(false) ?? job;
        var message = done.FailedFiles == 0
            ? $"{Count(done.MovedFiles)} filed by date."
            : $"{Count(done.MovedFiles)} filed by date; {Count(done.FailedFiles)} could not be renamed and stayed where they were.";
        await tidy.FinishJobAsync(job.Id, ViewTidyStates.Completed, message, time.GetUtcNow(), ct).ConfigureAwait(false);
    }

    // ── Planning ─────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<ViewTidyMove>> PlanAsync(Target target, CancellationToken ct)
    {
        var pattern = configuration.LoadPhotoFiling().LayoutPattern;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.Path));
        var moves = new List<ViewTidyMove>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in await tidy.GetSourceFilesAsync(target.SourceId, ct).ConfigureAwait(false))
        {
            if (!File.Exists(file.FilePath) || !ViewStorageService.Contains(root, file.FilePath))
            {
                continue;
            }

            // The date the filer would have used: the one the file carries, else the one in its name, else the file's own date.
            var candidate = ViewLibraryService.TryCreateCandidate(file.FilePath);
            if (candidate is null)
            {
                continue;
            }

            var date = (await library.ReadCaptureDateAsync(candidate, ct).ConfigureAwait(false)).Value;
            var directory = Path.Combine(root, ViewFilingLayout.FolderFor(pattern, date));
            var current = Path.GetDirectoryName(Path.GetFullPath(file.FilePath))!;
            if (string.Equals(Path.TrimEndingDirectorySeparator(current), Path.TrimEndingDirectorySeparator(directory), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = ViewFilingLayout.NamesFor(directory, [Path.GetFileName(file.FilePath)], path => File.Exists(path) || taken.Contains(path))[0];
            var destination = Path.Combine(directory, name);
            taken.Add(destination);
            moves.Add(new ViewTidyMove(Guid.Empty, file.SourceFileId, Path.GetFullPath(file.FilePath), destination, ViewTidyStates.Planned));
        }

        return moves;
    }

    private async Task<Target?> ResolveAsync(Guid sourceId, CancellationToken ct)
    {
        foreach (var (space, source, path) in await storage.GetEnabledSourcesAsync(ct).ConfigureAwait(false))
        {
            if (source.Id == sourceId)
            {
                return source.StorageMode == ViewSourceStorageMode.Managed && Tidyable(source.ContentKind)
                    ? new Target(source.Id, space.LibraryId, path, source.ContentKind, source.Name)
                    : null;
            }
        }

        foreach (var source in (await sharedLibrary.GetSourcesAsync(ct).ConfigureAwait(false)).Where(candidate => candidate.Enabled))
        {
            if (source.Id == sourceId)
            {
                return source.StorageMode == ViewSourceStorageMode.Managed && Tidyable(source.ContentKind)
                    ? new Target(source.Id, source.LibraryId, storage.GetSharedSourcePath(source), source.ContentKind, source.Name)
                    : null;
            }
        }

        return null;
    }

    private static bool Tidyable(string contentKind) => contentKind is ViewContentKinds.Photos or ViewContentKinds.PersonalVideos;

    private static ViewTidyStatusDto Status(Target target, ViewTidyJob? job, int willMove, string? from, string? to, bool scheduled, int hour) =>
        new(target.SourceId, target.Name, willMove, from, to, job is { State: ViewTidyStates.Running }, scheduled, hour,
            (int)(job?.TotalFiles ?? 0), (int)(job?.MovedFiles ?? 0), (int)(job?.FailedFiles ?? 0), job?.Message);

    private static void PruneEmptyFolders(string root)
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                         .OrderByDescending(path => path.Length))
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leftover empty folders are harmless.
        }
    }

    private static string Count(long files) => files == 1 ? "1 file" : files.ToString("N0", CultureInfo.InvariantCulture) + " files";
}
