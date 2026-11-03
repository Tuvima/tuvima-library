using MediaEngine.Contracts.Editing;
using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.Organise;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// "Rename the files to match this order" (kind <c>video_rename</c>, universal editor v3.1). Optional, last, one change at a time, and it
/// cannot be undone. It uses the organise job's file mover and its folder rules (<see cref="VideoOrderRenamer"/>): only Managed folders are
/// touched, and one video in a Read-only or As-is folder makes the whole group refuse. A file only ever gets a new name inside the folder it
/// is already in.
/// </summary>
public sealed class VideoRenameEditSessionChangeHandler(
    IVideoOrderRepository order,
    VideoOrderRenamer renamer,
    ISystemActivityRepository activityRepo) : IEditSessionChangeHandler
{
    public string Kind => EditSessionChangeKinds.VideoRename;

    public async Task<string?> CheckAsync(EditSessionChange change, CancellationToken ct = default)
    {
        var group = await LoadAsync(change.EntityId, ct).ConfigureAwait(false);
        if (group is null)
        {
            return EditSessionReasons.SeriesGone;
        }

        return await renamer.CanRenameAllAsync(group.Entries.Select(entry => entry.AssetId), ct).ConfigureAwait(false)
            ? null
            : EditSessionReasons.RenameNotAllowed;
    }

    public async Task<Func<CancellationToken, Task>?> ApplyAsync(EditSessionChange change, CancellationToken ct = default)
    {
        // Read after the data changes were saved, so the names follow the order that was just chosen.
        var group = await LoadAsync(change.EntityId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(EditSessionReasons.SeriesGone);
        if (!await renamer.CanRenameAllAsync(group.Entries.Select(entry => entry.AssetId), ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(EditSessionReasons.RenameNotAllowed);
        }

        var result = await renamer.RenameAsync(group.Entries.Select(entry => new VideoRenameItem(entry.AssetId, entry.Title)).ToList(), ct).ConfigureAwait(false);
        await activityRepo.LogAsync(new SystemActivityEntry
        {
            OccurredAt = DateTimeOffset.UtcNow,
            ActionType = SystemActionType.PathUpdated,
            EntityId = change.EntityId,
            EntityType = "Work",
            Detail = $"Renamed {result.Renamed} video files to match their order.",
        }, ct).ConfigureAwait(false);
        return null;
    }

    private async Task<VideoOrderGroup?> LoadAsync(Guid parentId, CancellationToken ct) =>
        (await order.LoadAsync(parentId, ct).ConfigureAwait(false)).FirstOrDefault(group => group.ParentWorkId == parentId);
}
