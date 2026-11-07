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
        // The series itself named with section ids is a section reorder; a section or a series without sections named with video ids orders videos.
        return groups.Any(group => group.ParentWorkId == change.EntityId) || IsSectionOrder(groups, change) ? null : EditSessionReasons.SeriesGone;
    }

    private static bool IsSectionOrder(IReadOnlyList<VideoOrderGroup> groups, EditSessionChange change) =>
        groups.Count > 0 && groups.All(group => group.ParentWorkId != change.EntityId)
        && change.OrderedIds.All(id => groups.Any(group => group.ParentWorkId == id));

    public async Task<Func<CancellationToken, Task>?> ApplyAsync(EditSessionChange change, CancellationToken ct = default)
    {
        var sections = await order.LoadAsync(change.EntityId, ct).ConfigureAwait(false);
        if (IsSectionOrder(sections, change))
        {
            var previousSections = sections.Select(group => group.ParentWorkId).ToList();
            if (await writer.SaveSectionsAsync(change.EntityId, change.OrderedIds, ct).ConfigureAwait(false) is null)
            {
                throw new InvalidOperationException(EditSessionReasons.OrderChanged);
            }

            return undoCt => writer.SaveSectionsAsync(change.EntityId, previousSections, undoCt);
        }

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
