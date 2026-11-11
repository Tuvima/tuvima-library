using MediaEngine.Contracts.Details;

namespace MediaEngine.Web.Services.Navigation;

public sealed record DetailTabRouteResolution(
    string ActiveTab,
    bool HasRequestedTab,
    bool IsRequestedTabValid);

public static class DetailTabNavigation
{
    public static DetailTabRouteResolution Resolve(DetailPageViewModel model, string? requestedTab)
    {
        var visibleTabs = GetVisibleTabs(model).ToList();
        var fallback = visibleTabs.FirstOrDefault()?.Key ?? "overview";
        var hasRequested = !string.IsNullOrWhiteSpace(requestedTab);

        if (!hasRequested)
        {
            return new DetailTabRouteResolution(fallback, false, true);
        }

        // A tab that is not spelled as one of the page's tabs is not found: the first tab shows and the address stays as it is.
        var requested = requestedTab!.Trim();
        var match = visibleTabs.FirstOrDefault(tab => string.Equals(tab.Key, requested, StringComparison.OrdinalIgnoreCase));
        return match is null
            ? new DetailTabRouteResolution(fallback, true, false)
            : new DetailTabRouteResolution(match.Key, true, true);
    }

    public static IReadOnlyList<DetailTab> GetVisibleTabs(DetailPageViewModel model) =>
        model.Tabs
            .Where(tab => HasRenderableContent(model, tab.Key))
            .ToList();

    private static bool HasRenderableContent(DetailPageViewModel model, string key)
    {
        return key switch
        {
            "cast" or "people" or "credits" =>
                model.ContributorGroups.Count > 0,
            "tracks" => model.MediaGroups.Any(group =>
                string.Equals(group.Key, "tracks", StringComparison.OrdinalIgnoreCase)
                && group.Items.Count > 0),
            _ => true,
        };
    }
}
