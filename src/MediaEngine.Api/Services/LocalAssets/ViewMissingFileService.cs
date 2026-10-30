using System.Globalization;
using System.Text.Json;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.LocalAssets;

/// <summary>
/// Notices files that were deleted outside Library and tidies up after the keep-for period (Safer deleting in View). It only ever changes
/// records: a place that is gone is marked missing, an item with no viewable file left is hidden, and after the retention setting the
/// leftover record is removed. It never touches a file, and a folder that cannot be reached marks nothing (D4).
/// </summary>
public sealed class ViewMissingFileService(
    ViewStorageService storage,
    IViewMissingFileRepository missing,
    IConfigurationLoader configuration,
    ISystemActivityRepository activity,
    TimeProvider clock,
    ILogger<ViewMissingFileService> logger) : IViewMissingFileSweeper
{
    private const int PurgeBatch = 200;

    /// <summary>
    /// Looks at every place indexed under the folders of one library (or all libraries): marks the ones whose file is gone and clears the
    /// mark on the ones that came back. Returns how many items changed state.
    /// </summary>
    public async Task<int> SweepAsync(Guid? libraryId, CancellationToken ct = default)
    {
        var changedItems = 0;
        var roots = await storage.GetEnabledSourceRootsAsync(ct).ConfigureAwait(false);
        foreach (var (rootLibraryId, sourceId, path) in roots.Where(root => libraryId is null || root.LibraryId == libraryId))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                changedItems += await SweepFolderAsync(sourceId, path, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A folder that cannot be read right now is treated as unreachable, never as empty.
                logger.LogWarning(exception, "A View folder of library {LibraryId} could not be checked for missing files.", rootLibraryId);
                await missing.SetFolderReachableAsync(sourceId, false, ct).ConfigureAwait(false);
            }
        }

        return changedItems;
    }

    private async Task<int> SweepFolderAsync(Guid sourceId, string root, CancellationToken ct)
    {
        var reachable = Directory.Exists(root);
        await missing.SetFolderReachableAsync(sourceId, reachable, ct).ConfigureAwait(false);
        if (!reachable)
        {
            return 0;
        }

        var toMark = new List<Guid>();
        var toClear = new List<Guid>();
        foreach (var place in await missing.GetPlacesAsync(sourceId, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var exists = File.Exists(place.FilePath);
            if (!exists && place.MissingSince is null)
            {
                toMark.Add(place.Id);
            }
            else if (exists && place.MissingSince is not null)
            {
                toClear.Add(place.Id);
            }
        }

        var changed = await missing.ApplyAsync(toMark, toClear, clock.GetUtcNow(), ct).ConfigureAwait(false);
        if (toMark.Count > 0 || toClear.Count > 0)
        {
            logger.LogInformation("View folder check: {Marked} files marked missing, {Cleared} came back, {Items} items changed.",
                toMark.Count, toClear.Count, changed);
        }

        return changed;
    }

    /// <summary>
    /// Removes the record of every item that has been Missing for longer than the setting, folders that could not be reached excepted.
    /// Returns how many items were removed. "Never" (0 days) removes nothing.
    /// </summary>
    public async Task<int> PurgeAsync(CancellationToken ct = default)
    {
        var days = configuration.LoadPhotoFiling().MissingFileRetentionDays;
        if (days <= 0)
        {
            return 0;
        }

        var cutoff = clock.GetUtcNow().AddDays(-days);
        var removed = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var candidates = await missing.GetPurgeCandidatesAsync(cutoff, PurgeBatch, ct).ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                break;
            }

            var removedThisBatch = 0;
            foreach (var itemId in candidates)
            {
                if (await missing.PurgeItemAsync(itemId, cutoff, ct).ConfigureAwait(false))
                {
                    removedThisBatch++;
                }
            }

            removed += removedThisBatch;
            if (removedThisBatch == 0)
            {
                break;
            }
        }

        if (removed > 0)
        {
            await RecordAsync(removed, days, ct).ConfigureAwait(false);
        }

        return removed;
    }

    /// <summary>One Activity entry per run: a count only, never a file name or title.</summary>
    private async Task RecordAsync(int removed, int days, CancellationToken ct)
    {
        try
        {
            var count = removed.ToString("N0", CultureInfo.InvariantCulture);
            await activity.LogAsync(new SystemActivityEntry
            {
                ActionType = SystemActionType.PersonalMissingPurged,
                CollectionName = removed == 1 ? "Removed the record of 1 missing file" : $"Removed the records of {count} missing files",
                EntityType = "PersonalMedia",
                Detail = $"They had been missing for more than {days} days. No file was touched.",
                ChangesJson = JsonSerializer.Serialize(new { photos = removed, videos = 0, count = removed }),
            }, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The Activity log could not record the removal of missing-file records.");
        }
    }
}
