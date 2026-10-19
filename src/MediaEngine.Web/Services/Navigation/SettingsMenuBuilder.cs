using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Formatting;

namespace MediaEngine.Web.Services.Navigation;

/// <summary>What the Settings menu needs to know about the person and the moment to draw the right rows.</summary>
/// <param name="AdministrationAllowed">A server administrator.</param>
/// <param name="HouseholdAdministrator">A household administrator (who is not necessarily a server administrator).</param>
/// <param name="IsMobile">A phone-sized screen: only pages offered on a phone are listed, and no attention shading is drawn.</param>
/// <param name="ReviewCount">Items waiting in Needs Review.</param>
/// <param name="SecurityNeedsAttention">Security carries the small attention dot.</param>
/// <param name="IsSectionEnabled">Switches a page off for reasons that are not about rights (a feature flag). Null means all are on.</param>
/// <param name="LocalAiBlocked">Local AI cannot work until the tools and libraries it needs are downloaded; its row shows red with "Off".</param>
public sealed record SettingsMenuContext(
    bool AdministrationAllowed,
    bool HouseholdAdministrator,
    bool IsMobile,
    int ReviewCount,
    bool SecurityNeedsAttention,
    Func<SettingsSection, bool>? IsSectionEnabled = null,
    bool LocalAiBlocked = false);

/// <summary>One line of the phone page selector: a page and how deep it sits in the tree.</summary>
public sealed record SettingsPhoneMenuOption(string Route, string Label, int Depth);

/// <summary>A group of phone page-selector lines.</summary>
public sealed record SettingsPhoneMenuGroup(string Label, IReadOnlyList<SettingsPhoneMenuOption> Options);

/// <summary>
/// Turns the Settings menu tree in <see cref="SettingsNav"/> into the rows the desktop tree draws and the lines the
/// phone page selector lists, so the two always show the same pages.
/// </summary>
public static class SettingsMenuBuilder
{
    public static IReadOnlyList<MediaSectionNavigationGroup> Build(SettingsMenuContext context) =>
        SettingsNav.FilteredMenuGroups(context.AdministrationAllowed, context.HouseholdAdministrator, node => IsOffered(node, context))
            .Select(group => new MediaSectionNavigationGroup(group.Label, group.Nodes.Select(node => BuildItem(node, context)).ToList()))
            .ToList();

    /// <summary>The phone page selector: the same groups and rows as the desktop tree, children listed under their parent.</summary>
    public static IReadOnlyList<SettingsPhoneMenuGroup> BuildPhoneSelector(IReadOnlyList<MediaSectionNavigationGroup> groups) =>
        groups
            .Select(group => new SettingsPhoneMenuGroup(group.Label, Lines(group.Items, 0).ToList()))
            .ToList();

    private static IEnumerable<SettingsPhoneMenuOption> Lines(IEnumerable<MediaSectionNavigationItem> items, int depth)
    {
        foreach (var item in items)
        {
            yield return new SettingsPhoneMenuOption(item.Route, item.Label, depth);
            if (item.Children is { Count: > 0 })
            {
                foreach (var line in Lines(item.Children, depth + 1))
                {
                    yield return line;
                }
            }
        }
    }

    private static bool IsOffered(SettingsMenuNode node, SettingsMenuContext context)
    {
        if (context.IsSectionEnabled is not null && !context.IsSectionEnabled(node.Section))
        {
            return false;
        }

        return !context.IsMobile
               || SettingsNav.IsVisibleOnMobile(node.Section)
               && SettingsNav.IsMobileRouteAvailable(node.Section, node.Subsection);
    }

    private static MediaSectionNavigationItem BuildItem(SettingsMenuNode node, SettingsMenuContext context)
    {
        // Needs Review is reached from the Library and Ingestion page (Recently Added is no longer a menu row), so that
        // row carries the count and the amber "waiting" shading;
        // Local AI shows red with "Off" while it cannot work; a parent shows its most urgent child's level. Phones show
        // plain rows, and Tools and requirements is never shaded.
        var showReview = node is { Section: SettingsSection.Ingestion, Subsection: null } && context.ReviewCount > 0;
        var count = showReview ? DisplayFormat.FormatCount(context.ReviewCount) : null;
        var localAiOff = node is { Section: SettingsSection.LocalAi, Subsection: null } && context.LocalAiBlocked && !context.IsMobile;

        return new MediaSectionNavigationItem(
            node.Label,
            node.Route,
            node.Icon,
            Meta: count,
            Exact: true,
            Children: node.Children.Count == 0
                ? null
                : node.Children.Select(child => BuildItem(child, context)).ToList(),
            MetaIsNotification: showReview,
            MetaAriaLabel: showReview
                ? $"{count} item{(context.ReviewCount == 1 ? "" : "s")} need review"
                : null,
            AttentionDot: node is { Section: SettingsSection.Security, Subsection: null } && context.SecurityNeedsAttention,
            Attention: localAiOff
                ? MediaSectionAttentionLevel.Blocked
                : showReview && !context.IsMobile
                    ? MediaSectionAttentionLevel.Waiting
                    : MediaSectionAttentionLevel.None,
            AttentionLabel: localAiOff ? "Off" : null);
    }
}
