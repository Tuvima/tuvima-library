using MediaEngine.Contracts.Details;

namespace MediaEngine.Api.Services.Details.Internals;

internal sealed partial class DetailCompositionOrchestrator
{
    /// <summary>
    /// Keeps every season (title, artwork, counts) but only one season's slice of episodes: the requested season,
    /// else the season being watched, starting at the window that holds the current episode.
    /// </summary>
    private static List<SequenceGroupViewModel> WindowSequenceGroups(
        IReadOnlyList<SequenceGroupViewModel> groups,
        SequenceWindowRequest window,
        SequenceGroupViewModel? selectedGroup)
    {
        // An unknown season key falls back to the season being watched instead of answering with no episodes.
        var target = (window.IsExplicit
                ? groups.FirstOrDefault(group => string.Equals(group.Key, window.GroupKey, StringComparison.OrdinalIgnoreCase))
                : null)
            ?? selectedGroup;
        var targetNamed = window.IsExplicit
            && target is not null
            && string.Equals(target.Key, window.GroupKey, StringComparison.OrdinalIgnoreCase);
        var limit = window.EffectiveLimit;
        return groups.Select(group =>
        {
            var windowItems = new List<SequenceItemViewModel>();
            var offset = 0;
            if (ReferenceEquals(group, target))
            {
                var currentIndex = group.Items.ToList().FindIndex(item => item.IsCurrent);
                // An offset past the end lands on the last full window rather than an empty one.
                var lastWindowStart = group.Items.Count <= 0 ? 0 : (group.Items.Count - 1) / limit * limit;
                offset = targetNamed
                    ? Math.Min(window.Offset, lastWindowStart)
                    : currentIndex < 0 ? 0 : currentIndex / limit * limit;
                windowItems = group.Items.Skip(offset).Take(limit).ToList();
            }

            return new SequenceGroupViewModel
            {
                OwnedCount = group.OwnedCount,
                CompletedCount = group.CompletedCount,
                Key = group.Key,
                Title = group.Title,
                EntityId = group.EntityId,
                ArtworkSmallUrl = group.ArtworkSmallUrl,
                ArtworkUrl = group.ArtworkUrl,
                BackgroundArtworkUrl = group.BackgroundArtworkUrl,
                TotalKnownItems = group.TotalKnownItems,
                HasAuthoritativeTotal = group.HasAuthoritativeTotal,
                ItemCount = group.ItemCount,
                ItemOffset = offset,
                Items = windowItems,
            };
        }).ToList();
    }
}
