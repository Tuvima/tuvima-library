using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Formatting;

namespace MediaEngine.Web.Services.Navigation;

/// <summary>What the Settings menu needs to know about the person and the moment to draw the right rows.</summary>
/// <param name="AdministrationAllowed">A server administrator.</param>
/// <param name="IsMobile">A phone-sized screen: only pages offered on a phone are listed, and no attention shading is drawn.</param>
/// <param name="ReviewCount">Items waiting in Needs Review.</param>
/// <param name="SecurityNeedsAttention">Security &amp; Devices carries the small attention dot.</param>
/// <param name="IsSectionEnabled">Switches a page or tab off for reasons that are not about rights (a feature flag). Null means all are on.</param>
/// <param name="LocalAiBlocked">Local AI cannot work until the tools and libraries it needs are downloaded.</param>
/// <param name="AiFeaturesOn">An AI feature plugin is switched on. Local AI has no switch of its own, so its row shows red with "Broken" only when it is blocked while something switched on needs it; nothing switched off is ever red.</param>
/// <param name="IntegrationsBroken">A webhook that is switched on keeps failing; the Integrations row shows red with "Broken".</param>
/// <param name="ToolsMissingReason">A required tool is missing or broken (not merely downloading): the System row shows red with this short reason, for example "FFmpeg missing".</param>
/// <param name="PluginsBroken">A plugin that is switched on cannot do its job; the Plugins row shows red with "Broken". Nothing switched off counts.</param>
/// <param name="RestrictedProfile">A Kids profile: the menu lists Profile and nothing else.</param>
public sealed record SettingsMenuContext(
    bool AdministrationAllowed,
    bool IsMobile,
    int ReviewCount,
    bool SecurityNeedsAttention,
    Func<SettingsSection, bool>? IsSectionEnabled = null,
    bool LocalAiBlocked = false,
    bool PluginsBroken = false,
    bool AiFeaturesOn = true,
    bool IntegrationsBroken = false,
    string? ToolsMissingReason = null,
    bool RestrictedProfile = false);

/// <summary>One line of the phone page selector: a page.</summary>
public sealed record SettingsPhoneMenuOption(string Route, string Label);

/// <summary>A group of phone page-selector lines.</summary>
public sealed record SettingsPhoneMenuGroup(string Label, IReadOnlyList<SettingsPhoneMenuOption> Options);

/// <summary>
/// Turns the flat Settings menu in <see cref="SettingsNav"/> into the rows the desktop menu draws and the lines the phone page
/// selector lists, so the two always show the same pages.
/// </summary>
public static class SettingsMenuBuilder
{
    public static IReadOnlyList<MediaSectionNavigationGroup> Build(SettingsMenuContext context) =>
        FilteredGroups(context)
            .Select(group => new MediaSectionNavigationGroup(group.Label, group.Nodes.Select(node => BuildItem(node, context)).ToList()))
            .ToList();

    /// <summary>The menu this person may see, page by page, each page with the tabs they may open.</summary>
    public static IReadOnlyList<SettingsMenuGroup> FilteredGroups(SettingsMenuContext context) =>
        SettingsNav.FilteredMenuGroups(
            context.AdministrationAllowed,
            node => IsOffered(node, context),
            tab => IsTabOffered(tab, context),
            context.RestrictedProfile);

    /// <summary>The phone page selector: the same groups and pages as the desktop menu, one line each.</summary>
    public static IReadOnlyList<SettingsPhoneMenuGroup> BuildPhoneSelector(IReadOnlyList<MediaSectionNavigationGroup> groups) =>
        groups
            .Select(group => new SettingsPhoneMenuGroup(group.Label, group.Items.Select(item => new SettingsPhoneMenuOption(item.Route, item.Label)).ToList()))
            .ToList();

    private static bool IsOffered(SettingsMenuNode node, SettingsMenuContext context)
    {
        if (context.IsSectionEnabled is not null && !context.IsSectionEnabled(node.Section))
        {
            return false;
        }

        // Phones and tablets are for consuming media: the Platform and Developer areas (Local AI, Plugins, Integrations, Developer
        // Tools) are not listed there at all. Their pages stay reachable by address from a mobile browser.
        if (context.IsMobile && IsDeveloperArea(node))
        {
            return false;
        }

        return !context.IsMobile || node.HasTabs || IsOfferedOnMobile(node.Section, null);
    }

    private static bool IsTabOffered(SettingsPageTab tab, SettingsMenuContext context) =>
        (context.IsSectionEnabled is null || context.IsSectionEnabled(tab.Section))
        && (!context.IsMobile || IsOfferedOnMobile(tab.Section, tab.Subsection));

    private static bool IsOfferedOnMobile(SettingsSection section, string? subsection) =>
        SettingsNav.IsVisibleOnMobile(section) && SettingsNav.IsMobileRouteAvailable(section, subsection);

    private static bool IsDeveloperArea(SettingsMenuNode node) =>
        SettingsNav.GetGroup(node.Section).Key is "platform" or "developer";

    private static MediaSectionNavigationItem BuildItem(SettingsMenuNode node, SettingsMenuContext context)
    {
        // Needs Review is reached from the Library & Ingestion page (Recently Added is no longer a menu row), so that row carries the
        // amber "waiting" shading; Local AI, Plugins and Integrations show red with "Broken" while something switched on cannot work
        // (Local AI: an AI feature is on and the tools are missing); System shows red with what is missing, and Security & Devices shows
        // red with "No password" until the account is secured. Phones show plain rows.
        var sections = node.Sections.ToList();
        var showReview = sections.Contains(SettingsSection.Ingestion) && context.ReviewCount > 0;
        var localAiOff = node.Section == SettingsSection.LocalAi && context.LocalAiBlocked && context.AiFeaturesOn && !context.IsMobile;
        var pluginsBroken = node.Section == SettingsSection.Plugins && context.PluginsBroken && !context.IsMobile;
        var integrationsBroken = node.Section == SettingsSection.Integrations && context.IntegrationsBroken && !context.IsMobile;
        var toolsMissing = sections.Contains(SettingsSection.Tools) && context.ToolsMissingReason is not null && !context.IsMobile;
        // The Security & Devices row says why it is red: this-computer-only accounts have no password yet.
        var securityRed = node.Section == SettingsSection.Security && context.SecurityNeedsAttention && !context.IsMobile;

        return new MediaSectionNavigationItem(
            node.Label,
            node.Route,
            node.Icon,
            // A page stays lit on all of its tabs and on the pages beneath them (a library's own page, a provider's detail).
            Exact: false,
            // On a phone the row has no red shading, so it keeps the plain dot.
            AttentionDot: node.Section == SettingsSection.Security && context.SecurityNeedsAttention && !securityRed,
            Attention: localAiOff || pluginsBroken || integrationsBroken || toolsMissing || securityRed
                ? MediaSectionAttentionLevel.Blocked
                : showReview && !context.IsMobile
                    ? MediaSectionAttentionLevel.Waiting
                    : MediaSectionAttentionLevel.None,
            AttentionLabel: toolsMissing ? context.ToolsMissingReason
                : securityRed ? "No password"
                : localAiOff || pluginsBroken || integrationsBroken ? "Broken" : null,
            ActiveRoutes: sections.Select(SettingsNav.BaseRouteFor).Distinct().ToList(),
            InactiveRoutes: null);
    }
}
