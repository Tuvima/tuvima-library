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
/// <param name="LocalAiBlocked">Local AI cannot work until the tools and libraries it needs are downloaded.</param>
/// <param name="AiFeaturesOn">An AI feature plugin is switched on. Local AI has no switch of its own, so its row shows red with "Broken" only when it is blocked while something switched on needs it; nothing switched off is ever red.</param>
/// <param name="IntegrationsBroken">A webhook that is switched on keeps failing; the Applications row shows red with "Broken".</param>
/// <param name="ToolsMissingReason">A required tool is missing or broken (not merely downloading): Tools and requirements shows red with this short reason, for example "FFmpeg missing".</param>
/// <param name="PluginsBroken">A plugin that is switched on cannot do its job; the Plugins row shows red with "Broken". Nothing switched off counts.</param>
public sealed record SettingsMenuContext(
    bool AdministrationAllowed,
    bool HouseholdAdministrator,
    bool IsMobile,
    int ReviewCount,
    bool SecurityNeedsAttention,
    Func<SettingsSection, bool>? IsSectionEnabled = null,
    bool LocalAiBlocked = false,
    bool PluginsBroken = false,
    bool AiFeaturesOn = true,
    bool IntegrationsBroken = false,
    string? ToolsMissingReason = null);

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

        // Phones and tablets are for consuming media: the Developer area (Local AI, Integrations, Plugins, Developer Tools) is not
        // listed there at all. Its pages stay reachable by address from a mobile browser.
        if (context.IsMobile && IsDeveloperArea(node))
        {
            return false;
        }

        return !context.IsMobile
               || SettingsNav.IsVisibleOnMobile(node.Section)
               && SettingsNav.IsMobileRouteAvailable(node.Section, node.Subsection);
    }

    // Integrations lives under the Access page but sits in the Developer menu group.
    private static bool IsDeveloperArea(SettingsMenuNode node) =>
        SettingsNav.GetGroup(node.Section).Key == "developer"
        || node is { Section: SettingsSection.Access, Subsection: "applications" };

    private static MediaSectionNavigationItem BuildItem(SettingsMenuNode node, SettingsMenuContext context)
    {
        // Needs Review is reached from the Library and Ingestion page (Recently Added is no longer a menu row), so that
        // row carries the amber "waiting" shading;
        // Local AI and Plugins show red with "Broken" while something switched on cannot work (Local AI: an AI feature is on and
        // the tools are missing); Tools and requirements shows red with what is missing, and Security shows red with "No password"
        // until the account is secured; a parent shows its most urgent child's level. Phones show plain rows.
        var showReview = node is { Section: SettingsSection.Ingestion, Subsection: null } && context.ReviewCount > 0;
        var localAiOff = node is { Section: SettingsSection.LocalAi, Subsection: null } && context.LocalAiBlocked && context.AiFeaturesOn && !context.IsMobile;
        var pluginsBroken = node is { Section: SettingsSection.Plugins, Subsection: null } && context.PluginsBroken && !context.IsMobile;
        var integrationsBroken = node is { Section: SettingsSection.Access, Subsection: "applications" } && context.IntegrationsBroken && !context.IsMobile;

        var toolsMissing = node is { Section: SettingsSection.Tools, Subsection: null } && context.ToolsMissingReason is not null && !context.IsMobile;
        // The Security row says why it is red: this-computer-only accounts have no password yet.
        var securityRed = node is { Section: SettingsSection.Security, Subsection: null } && context.SecurityNeedsAttention && !context.IsMobile;

        return new MediaSectionNavigationItem(
            node.Label,
            node.Route,
            node.Icon,
            // Libraries has its own pages below it (a group such as /settings/libraries/watch, then a type), so its row stays lit there.
            Exact: node is not { Section: SettingsSection.Libraries, Subsection: null },
            Children: node.Children.Count == 0
                ? null
                : node.Children.Select(child => BuildItem(child, context)).ToList(),
            // On a phone the row has no red shading, so it keeps the plain dot.
            AttentionDot: node is { Section: SettingsSection.Security, Subsection: null } && context.SecurityNeedsAttention && !securityRed,
            Attention: localAiOff || pluginsBroken || integrationsBroken || toolsMissing || securityRed
                ? MediaSectionAttentionLevel.Blocked
                : showReview && !context.IsMobile
                    ? MediaSectionAttentionLevel.Waiting
                    : MediaSectionAttentionLevel.None,
            AttentionLabel: toolsMissing ? context.ToolsMissingReason
                : securityRed ? "No password"
                : localAiOff || pluginsBroken || integrationsBroken ? "Broken" : null);
    }
}
