using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// Puts the videos of a series or section in the order the person chose (kind <c>video_order</c>, universal editor v3.1). Library's own data:
/// no file is renamed. The check confirms the series is there and the list is a sensible one; the write itself refuses (and so undoes the
/// whole unit) when the list no longer names exactly the videos that are in the series, so a stale screen can never drop or invent a video.
/// </summary>
public sealed class VideoOrderEditSessionChangeHandler(IVideoOrderRepository order, VideoOrderWriter writer) : IEditSessionChangeHandler
{
    public string Kind => EditSessionChangeKinds.VideoOrder;

    public async Task<string?> CheckAsync(EditSessionChange change, CancellationToken ct = default)
    {
        if (change.OrderedIds.Count < 2)
        {
            return EditSessionReasons.NothingToOrder;
        }

        if (change.OrderedIds.Distinct().Count() != change.OrderedIds.Count)
        {
            return EditSessionReasons.OrderChanged;
        }

        var groups = await order.LoadAsync(change.EntityId, ct).ConfigureAwait(false);
        return groups.Any(group => group.ParentWorkId == change.EntityId) ? null : EditSessionReasons.SeriesGone;
    }

    public async Task<Func<CancellationToken, Task>?> ApplyAsync(EditSessionChange change, CancellationToken ct = default)
    {
        var before = (await order.LoadAsync(change.EntityId, ct).ConfigureAwait(false))
            .FirstOrDefault(group => group.ParentWorkId == change.EntityId)
            ?? throw new InvalidOperationException(EditSessionReasons.SeriesGone);
        var previous = before.Entries.Select(entry => entry.WorkId).ToList();

        if (await writer.SaveAsync(change.EntityId, change.OrderedIds, ct).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException(EditSessionReasons.OrderChanged);
        }

        // Putting the earlier order back is the same write with the earlier list.
        return undoCt => writer.SaveAsync(change.EntityId, previous, undoCt);
    }
}
