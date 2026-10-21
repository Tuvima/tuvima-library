using MediaEngine.Contracts.Details;

namespace MediaEngine.Web.Components.Details;

/// <summary>
/// Whether a movie's Series row has anything to offer. The row exists to open the other movies of the Series, so when
/// none of them is in the library (only the open movie would show, as with 28 Years Later on its own) the row is left
/// out and the hero keeps the page. Only a movie's own page is affected: a movie Series' page lists its movies even
/// when it holds one, a TV show always has its episodes, and books and comics keep their numbered runs.
/// </summary>
public static class DetailSequenceRowRule
{
    public static bool ShowsRow(DetailEntityType pageType, SequencePlacementViewModel? placement)
    {
        if (pageType != DetailEntityType.Movie || placement is null)
        {
            return true;
        }

        // Another Series to switch to may hold other owned movies, so the selector stays reachable.
        if (placement.CanChooseContainer
            && placement.AvailableContainers.Select(option => option.ContainerId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
        {
            return true;
        }

        return placement.OrderedItems
            .Concat(placement.Groups.SelectMany(group => group.Items))
            .Concat(placement.AlsoInThisWorld)
            .Any(item => item.IsOwned && !item.IsCurrent
                && !string.Equals(item.Id, placement.CurrentItem.Id, StringComparison.OrdinalIgnoreCase));
    }
}
