using MediaEngine.Contracts.Details;

namespace MediaEngine.Web.Components.Details;

/// <summary>
/// Where a movie's Series row starts. The row exists to open the other movies of the Series, so when it has nothing else
/// to offer (no other movie in the library, none the Engine knows is missing for "Show missing", no other Series to
/// switch to), it starts collapsed to its slim bar, as with 28 Years Later on its own. The person can still open it,
/// and their own open or closed choice for Series rows wins. Only a movie's own page is affected: a movie Series' page,
/// TV shows, books and comics start open.
/// </summary>
public static class DetailSequenceRowRule
{
    public static bool StartsCollapsed(DetailEntityType pageType, SequencePlacementViewModel? placement)
    {
        if (pageType != DetailEntityType.Movie || placement is null)
        {
            return false;
        }

        if (placement.CanChooseContainer
            && placement.AvailableContainers.Select(option => option.ContainerId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
        {
            return false;
        }

        var others = placement.OrderedItems
            .Concat(placement.Groups.SelectMany(group => group.Items))
            .Concat(placement.AlsoInThisWorld)
            .Where(item => !item.IsCurrent && !string.Equals(item.Id, placement.CurrentItem.Id, StringComparison.OrdinalIgnoreCase));

        // An owned movie can be opened, and a missing one can be shown with "Show missing": either keeps the row open.
        return !others.Any();
    }
}
