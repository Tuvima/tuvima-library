using MediaEngine.Contracts.Library;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Services.Libraries;

/// <summary>
/// The first check of a newly added folder (Library setup spec D35, D61): the safety break and the hold on the folder's files. A folder added
/// to a library whose kind looks details up is identified as normal, but nothing in it is renamed, moved or tagged until the check ends. The
/// check ends when the configured number of its files have a final identification outcome, or, for a folder with fewer, when all of them
/// have and its ingestion has settled. It passes (the held moves and tags are released to the next run) unless more than the configured
/// share are Unmatched, which pauses the folder: its remaining identity jobs are not claimed and nothing is touched until an administrator
/// chooses Keep going or removes the folder.
/// </summary>
public sealed class FolderFirstCheckService(
    IFolderFirstCheckRepository checks,
    IConfigurationLoader configuration,
    ILogger<FolderFirstCheckService> logger,
    TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// True when a new folder in <paramref name="library"/> gets a first check: its kind looks details up (a plain kind is never Unmatched, D38)
    /// and the library is not a Vault library (the Vault has its own flow). The YouTube kind looks up by video ID, and a file with none is Unmatched.
    /// </summary>
    public static bool NeedsFirstCheck(LibraryFolderConfig library, IReadOnlyList<LibraryKindDefinition>? kinds, IReadOnlySet<string> vaultLibraryIds)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(vaultLibraryIds);
        if (vaultLibraryIds.Contains(library.Id))
        {
            return false;
        }

        var effective = LibraryKindLookupPolicy.For(library, kinds);
        return effective.Mode switch
        {
            LibraryLookupMode.Plain => false,
            LibraryLookupMode.YouTube => true,
            _ => !effective.BypassesExternalIdentity,
        };
    }

    /// <summary>
    /// Starts a first check for every folder in <paramref name="after"/> that was not in <paramref name="before"/> and whose library needs one.
    /// Call it before the new configuration is saved, so the hold is in place before the folder is first scanned. A folder that existed
    /// before never gets a check. Returns how many checks started.
    /// </summary>
    public async Task<int> StartForAddedFoldersAsync(
        LibrariesConfiguration before, LibrariesConfiguration after, IReadOnlySet<string> vaultLibraryIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var existing = before.Libraries.SelectMany(library => library.Sources).Select(source => source.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kinds = configuration.LoadLibraryKinds().Kinds;
        var started = 0;
        foreach (var library in after.Libraries)
        {
            if (!NeedsFirstCheck(library, kinds, vaultLibraryIds))
            {
                continue;
            }

            foreach (var source in library.ScannableSources)
            {
                if (string.IsNullOrWhiteSpace(source.Id) || existing.Contains(source.Id))
                {
                    continue;
                }

                if (await checks.StartAsync(library.Id, source.Id, source.Path, _clock.GetUtcNow(), ct).ConfigureAwait(false))
                {
                    started++;
                    logger.LogInformation(
                        "First check started for the new folder {Path} in library {LibraryId}: nothing in it is renamed, moved or tagged until it passes",
                        source.Path, library.Id);
                }
            }
        }

        return started;
    }

    /// <summary>
    /// Saves the libraries (<paramref name="save"/> is the loader's save) with the first checks that go with the change: the checks of the new folders start before the file is saved (so the
    /// hold is in place before a folder is first scanned), and the checks of removed folders are dropped after it. If the save fails the checks
    /// that were started are dropped again, so no check is left for a folder that was never added.
    /// </summary>
    public async Task SaveLibrariesWithFirstChecksAsync(
        Action<LibrariesConfiguration> save, LibrariesConfiguration before, LibrariesConfiguration after, IReadOnlySet<string> vaultLibraryIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(save);
        await StartForAddedFoldersAsync(before, after, vaultLibraryIds, ct).ConfigureAwait(false);
        var saved = false;
        try
        {
            save(after);
            saved = true;
        }
        finally
        {
            if (!saved)
            {
                await DropRemovedFoldersAsync(after, before, CancellationToken.None).ConfigureAwait(false);
            }
        }

        await DropRemovedFoldersAsync(before, after, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Drops the checks of folders in <paramref name="before"/> that are not in <paramref name="after"/>: the existing folder removal keeps the
    /// files on disk, and the folder's check state and held filing rows go with it. No file is moved. Returns how many checks were dropped.
    /// </summary>
    public async Task<int> DropRemovedFoldersAsync(LibrariesConfiguration before, LibrariesConfiguration after, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var remaining = after.Libraries.SelectMany(library => library.Sources).Select(source => source.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dropped = 0;
        foreach (var source in before.Libraries.SelectMany(library => library.Sources))
        {
            if (!string.IsNullOrWhiteSpace(source.Id) && !remaining.Contains(source.Id)
                && await checks.DropAsync(source.Id, ct).ConfigureAwait(false))
            {
                dropped++;
                logger.LogInformation("The folder {Path} was removed: its first check and held filing rows were dropped, no file was moved", source.Path);
            }
        }

        return dropped;
    }

    /// <summary>
    /// Looks at every folder that is being checked and ends the checks that are ready: passes the ones whose first files mostly matched and
    /// pauses the others. Returns how many checks ended.
    /// </summary>
    public async Task<int> EvaluateAsync(CancellationToken ct)
    {
        var running = await checks.ListCheckingAsync(ct).ConfigureAwait(false);
        if (running.Count == 0)
        {
            return 0;
        }

        var settings = configuration.LoadMaintenance();
        var firstFiles = Math.Max(FirstCheckLimits.MinFiles, settings.FirstCheckFiles);
        var ended = 0;
        foreach (var check in running)
        {
            ct.ThrowIfCancellationRequested();
            var progress = await checks.GetProgressAsync(check.SourceId, firstFiles, ct).ConfigureAwait(false);

            // The first N files have outcomes, or every file the Engine knows has one and nothing more is arriving (a folder with fewer files).
            var reachedFiles = progress.Tried >= firstFiles;
            var allDone = progress.Tried > 0 && progress.Waiting == 0 && progress.IngestionSettled;
            if (!reachedFiles && !allDone)
            {
                continue;
            }

            var now = _clock.GetUtcNow();
            if (FirstCheckLimits.ExceedsShare(progress.Unmatched, progress.Tried, settings.FirstCheckUnmatchedShare))
            {
                if (await checks.PauseAsync(check.SourceId, progress.Tried, progress.Unmatched, now, ct).ConfigureAwait(false))
                {
                    ended++;
                    logger.LogWarning(
                        "First check paused {Path}: {Unmatched} of {Tried} files did not match. Nothing in the folder was renamed, moved or tagged; an administrator chooses what happens next",
                        check.Path, progress.Unmatched, progress.Tried);
                }
            }
            else if (await checks.PassAsync(check.SourceId, progress.Tried, progress.Unmatched, now, ct).ConfigureAwait(false))
            {
                ended++;
                logger.LogInformation(
                    "First check passed for {Path}: {Unmatched} of {Tried} files did not match. Held moves and tags are released to the next run",
                    check.Path, progress.Unmatched, progress.Tried);
            }
        }

        return ended;
    }

    /// <summary>The folders that are checking or paused, with their counts and totals.</summary>
    public async Task<IReadOnlyList<FolderFirstCheckSnapshot>> ListAsync(CancellationToken ct) =>
        await checks.ListOpenAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// Keep going on a paused folder: identification resumes for the rest of the folder, files that are Unmatched stay Unmatched (and are
    /// looked up again later) and matched files' held moves and tags are released to the next run. Returns false when the folder is not paused.
    /// </summary>
    public async Task<bool> KeepGoingAsync(string sourceId, CancellationToken ct)
    {
        var kept = await checks.KeepGoingAsync(sourceId, _clock.GetUtcNow(), ct).ConfigureAwait(false);
        if (kept)
        {
            logger.LogInformation("An administrator chose Keep going for the paused folder {SourceId}: identification resumes", sourceId);
        }

        return kept;
    }

    /// <summary>
    /// Ends a folder's check because what the folder is changed (Change what this folder is): the check and the folder's held filing rows are
    /// dropped and no file is moved. The caller starts a new check for the folder once it has its new kind. Returns false when it had none.
    /// </summary>
    public Task<bool> EndBecauseKindChangedAsync(string sourceId, CancellationToken ct) => checks.DropAsync(sourceId, ct);

    /// <summary>The check to show on a source (checking or paused), or null.</summary>
    public static FolderFirstCheckDto? ToSourceDto(FolderFirstCheckSnapshot? snapshot) =>
        snapshot is null || !FolderFirstCheckStates.IsHeld(snapshot.Check.State)
            ? null
            : new FolderFirstCheckDto
            {
                State = snapshot.Check.State,
                Tried = snapshot.Check.Tried,
                Unmatched = snapshot.Check.Unmatched,
                TotalFiles = snapshot.TotalFiles,
            };

    /// <summary>One folder as the administrator's list shows it.</summary>
    public static FolderFirstCheckItemDto ToItemDto(FolderFirstCheckSnapshot snapshot) => new()
    {
        LibraryId = snapshot.Check.LibraryId,
        SourceId = snapshot.Check.SourceId,
        Path = snapshot.Check.Path,
        State = snapshot.Check.State,
        Tried = snapshot.Check.Tried,
        Unmatched = snapshot.Check.Unmatched,
        TotalFiles = snapshot.TotalFiles,
    };
}
