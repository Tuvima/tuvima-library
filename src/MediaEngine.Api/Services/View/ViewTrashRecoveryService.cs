using MediaEngine.Api.Services.LocalAssets;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Api.Services.View;

/// <summary>What one pass over the Trash staging folders did.</summary>
public sealed record ViewTrashRecoveryReport(int ItemsRestored, int ItemsFinished, int FoldersLeftAlone)
{
    public static ViewTrashRecoveryReport None { get; } = new(0, 0, 0);
}

/// <summary>
/// Finishes or undoes any Empty Trash that did not complete: a crash after files were parked but before the item's records were
/// removed, or a parked file that could not be deleted at the time. For each staging folder it looks at the item itself. If the
/// item is still in the data store the files go back to where they came from (nothing was lost); if it is gone, the parked files are
/// deleted (the item was already removed). It never decides from the manifest alone, never writes outside the owner's profile folder
/// and never touches a folder that is not an item's own staging folder.
/// </summary>
public sealed class ViewTrashRecoveryService(
    IViewPersonalSpaceRepository spaces,
    ViewStorageService storage,
    IViewTrashPurgeRepository purge,
    ILogger<ViewTrashRecoveryService> logger)
{
    /// <summary>Runs one pass, waiting for any Empty Trash in progress.</summary>
    public async Task<ViewTrashRecoveryReport> SweepAsync(CancellationToken ct = default)
    {
        await ViewTrashStaging.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await SweepLockedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            ViewTrashStaging.Gate.Release();
        }
    }

    /// <summary>One pass; the caller already holds <see cref="ViewTrashStaging.Gate"/>.</summary>
    internal async Task<ViewTrashRecoveryReport> SweepLockedAsync(CancellationToken ct)
    {
        int restored = 0, finished = 0, left = 0;
        foreach (var space in await spaces.GetAllAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            string profileRoot;
            try
            {
                profileRoot = storage.GetProfileRoot(space, requireWrite: false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException
                or ConfigValidationException or ArgumentException)
            {
                // No usable profile folder means nothing can have been parked there.
                logger.LogDebug(exception, "Skipped the Trash recovery sweep for a Personal Space whose storage could not be resolved");
                continue;
            }

            var stagingRoot = ViewTrashStaging.StagingRoot(profileRoot);
            if (!Directory.Exists(stagingRoot))
            {
                continue;
            }

            if (!ViewStorageService.TryResolveContainedPath(profileRoot, stagingRoot, out _, out _))
            {
                logger.LogWarning("A Trash staging folder is not a plain folder inside the profile folder, so it was left alone");
                left++;
                continue;
            }

            IReadOnlyList<string> folders;
            try
            {
                folders = Directory.GetDirectories(stagingRoot);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "The Trash staging folder could not be listed");
                left++;
                continue;
            }

            foreach (var itemFolder in folders)
            {
                ct.ThrowIfCancellationRequested();
                switch (await SweepItemFolderAsync(profileRoot, itemFolder, ct).ConfigureAwait(false))
                {
                    case SweepResult.Restored:
                        restored++;
                        break;
                    case SweepResult.Finished:
                        finished++;
                        break;
                    default:
                        left++;
                        break;
                }
            }
        }

        if (restored + finished + left > 0)
        {
            logger.LogInformation(
                "Trash recovery sweep: {Restored} item(s) had their files put back, {Finished} item(s) had their parked files deleted, {Left} folder(s) left alone",
                restored, finished, left);
        }

        return new ViewTrashRecoveryReport(restored, finished, left);
    }

    private async Task<SweepResult> SweepItemFolderAsync(string profileRoot, string itemFolder, CancellationToken ct)
    {
        if (!Guid.TryParseExact(Path.GetFileName(itemFolder), "D", out var itemId)
            || !ViewStorageService.TryResolveContainedPath(profileRoot, itemFolder, out _, out _))
        {
            logger.LogWarning("A folder in Trash staging is not an item's own staging folder, so it was left alone");
            return SweepResult.LeftAlone;
        }

        var manifest = ViewTrashStaging.ReadManifest(itemFolder, logger);
        if (manifest is null)
        {
            // A crash before anything was parked leaves an empty folder; anything else without a manifest cannot be placed.
            if (ViewTrashStaging.RemoveItemFolder(itemFolder, logger))
            {
                return SweepResult.Finished;
            }

            logger.LogWarning("A Trash staging folder holds files but no readable manifest, so it was left alone");
            return SweepResult.LeftAlone;
        }

        if (manifest.ItemId != itemId)
        {
            logger.LogWarning("A Trash staging manifest does not match its folder, so it was left alone");
            return SweepResult.LeftAlone;
        }

        if (await purge.ItemExistsAsync(itemId, ct).ConfigureAwait(false))
        {
            // The records were never removed, so the files belong where they were.
            if (!ViewTrashStaging.MoveBack(profileRoot, itemFolder, manifest, logger))
            {
                return SweepResult.LeftAlone;
            }

            ViewTrashStaging.RemoveItemFolder(itemFolder, logger);
            return SweepResult.Restored;
        }

        // The item is gone from the library: what is parked is what was meant to be deleted.
        return ViewTrashStaging.DeleteStaged(itemFolder, manifest, logger) ? SweepResult.Finished : SweepResult.LeftAlone;
    }

    private enum SweepResult
    {
        Restored,
        Finished,
        LeftAlone,
    }
}

/// <summary>Runs the Trash recovery sweep once when the Engine starts, so an interrupted Empty Trash is settled before anyone looks.</summary>
public sealed class ViewTrashRecoveryHostedService(
    ViewTrashRecoveryService recovery,
    ILogger<ViewTrashRecoveryHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            await recovery.SweepAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The Engine is stopping; the next start sweeps again.
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "The Trash recovery sweep failed; it will run again at the next start");
        }
    }
}
