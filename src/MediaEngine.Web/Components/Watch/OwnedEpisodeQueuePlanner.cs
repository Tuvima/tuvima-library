using MediaEngine.Contracts.Details;

namespace MediaEngine.Web.Components.Watch;

public static class OwnedEpisodeQueuePlanner
{
    public static IReadOnlyList<Guid> NextPlayableCandidates(SequencePlacementViewModel? sequence, Guid currentWorkId, int limit = 3)
    {
        if (sequence is null || limit <= 0)
        {
            return [];
        }
        // The list may be carried flat or inside its season groups (a long show is sent season by season).
        IReadOnlyList<SequenceItemViewModel> items = sequence.OrderedItems.Count > 0
            ? sequence.OrderedItems
            : sequence.Groups.SelectMany(group => group.Items).ToList();
        var currentIndex = -1;
        for (var index = 0; index < items.Count; index++)
        {
            if (Guid.TryParse(items[index].Id, out var id) && id == currentWorkId)
            {
                currentIndex = index;
                break;
            }
        }

        if (currentIndex < 0)
        {
            return [];
        }
        return items.Skip(currentIndex + 1)
            .Where(item => item.IsOwned && item.EntityType == DetailEntityType.TvEpisode)
            .Select(item => Guid.TryParse(item.Id, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty && id != currentWorkId)
            .Distinct()
            .Take(limit)
            .ToList();
    }
}
