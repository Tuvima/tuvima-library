using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Models.ViewDTOs;

/// <summary>
/// Canonical settings destinations rendered by the shared settings shell.
/// </summary>
public enum SettingsSection
{
    Overview,
    Profiles,
    Preferences,
    Playback,
    Listening,
    Reading,
    Security,

    AdminOverview,
    Libraries,
    Ingestion,
    RecentlyAdded,
    DevHarness,
    Providers,
    LocalAi,
    Plugins,
    Delivery,
    Tools,
    Network,
    Access,
    Server,

    Review,
    ProviderTester,
    EnrichmentTester,
}

/// <summary>
/// Declares how a settings destination should be exposed on a phone-sized or
/// native-mobile surface. This is a product capability decision, not a CSS
/// breakpoint: summary pages remain available for monitoring while complex
/// administration is intentionally reserved for larger screens.
/// </summary>
public enum SettingsMobileAvailability
{
    Full,
    SummaryOnly,
    DesktopOnly,
}

/// <summary>A primary settings destination group.</summary>
public sealed record SettingsGroupDef(
    string Key,
    string Label,
    string Icon,
    bool AdminOnly,
    SettingsSection DefaultSection);

/// <summary>A single settings route shown in the settings sidebar tree.</summary>
public sealed record SettingsItemDef(
    SettingsSection Value,
    string GroupKey,
    string? Slug,
    string Icon,
    string Label,
    bool AdminOnly,
    string? BadgeKey,
    IReadOnlyList<string> Aliases,
    string Source = "mixed",
    bool Placeholder = false,
    SettingsStatusKind Status = SettingsStatusKind.Planned,
    SettingsMobileAvailability MobileAvailability = SettingsMobileAvailability.Full,
    bool SectionBreakBefore = false,
    bool HouseholdAdminAccess = false);

public sealed record LaunchFeatureEvidence(
    bool Implemented,
    bool Invoked,
    bool Persisted,
    bool Presented,
    bool Tested)
{
    public bool IsComplete => Implemented && Invoked && Persisted && Presented && Tested;
}

/// <summary>A URL-addressable destination nested beneath a settings section.</summary>
public sealed record SettingsSubsectionDef(
    string Slug,
    string Label,
    string Icon);

/// <summary>
/// One row of the Settings menu tree. A parent opens its own main page; its children are the pages beneath it, so no
/// child repeats the parent. The same rows are the desktop menu and the phone page selector.
/// </summary>
/// <param name="Subsection">
/// The sub-page this row opens, or <see langword="null"/> for the section's own main page.
/// </param>
public sealed record SettingsMenuNode(
    string Label,
    string Icon,
    SettingsSection Section,
    string? Subsection = null,
    IReadOnlyList<SettingsMenuNode>? ChildNodes = null)
{
    public IReadOnlyList<SettingsMenuNode> Children => ChildNodes ?? [];

    public string Route => Subsection is null
        ? SettingsNav.RouteFor(Section)
        : SettingsNav.RouteFor(Section, Subsection);
}

/// <summary>A top-level group of the Settings menu (Personal, Server, Manage, Developer).</summary>
public sealed record SettingsMenuGroup(
    string Key,
    string Label,
    string Icon,
    IReadOnlyList<SettingsMenuNode> Nodes);

/// <summary>Where a section or sub-page sits in the menu: its group and the rows from the top level down to it.</summary>
public sealed record SettingsMenuLocation(SettingsMenuGroup Group, IReadOnlyList<SettingsMenuNode> Trail)
{
    public SettingsMenuNode Node => Trail[^1];

    public SettingsMenuNode? Parent => Trail.Count > 1 ? Trail[^2] : null;
}

/// <summary>
/// Result of resolving a route segment into a settings destination.
/// </summary>
public sealed record SettingsRouteResolution(
    SettingsSection Section,
    string CanonicalRoute,
    bool IsCanonicalRoute,
    bool IsKnownRoute,
    bool RequestedSectionAllowed)
{
    public bool ShouldRedirect => !IsCanonicalRoute || !RequestedSectionAllowed;
}

/// <summary>
/// Explicit route map for the Settings shell.
/// Keeps canonical slugs, grouping, and server-authorized navigation in one place.
/// </summary>
public static class SettingsNav
{
    private static bool _productionMode;
    private static readonly AsyncLocal<bool?> _scopedProductionMode = new();

    /// <summary>The Households sub-page of Profiles (server administrators only).</summary>
    public const string HouseholdsSubsection = "households";

    /// <summary>The Logs sub-page of System: the Activity log (Events), technical log, retention and export (server administrators only).</summary>
    public const string LogsSubsection = "logs";

    /// <summary>The Apps sub-page of Overview: store links, the server address and its QR code. It has no menu row; an icon on the Overview opens it.</summary>
    public const string AppsSubsection = "apps";

    /// <summary>The Taste sub-page of Overview: your taste in plain groups (Genres, Moods, Eras), editable for your own and read-only for others.</summary>
    public const string TasteSubsection = "taste";

    /// <summary>The Activity page's address under Settings: everything watched, listened to, read and added to My list, with search, filters and a date rail.</summary>
    public const string ActivityRoute = "/settings/overview/activity";

    /// <summary>
    /// The Activity sub-page of Overview, named by the last part of its address so the address is written once. (This is the
    /// person's own Activity page, not the retired Activity and Audit section of the old settings.)
    /// </summary>
    public static readonly string ActivitySubsection = ActivityRoute[(ActivityRoute.LastIndexOf('/') + 1)..];

    /// <summary>The Activity page outside Settings, for a Kids profile (which has no Settings) and for quick access. It shows the signed-in person's own activity only.</summary>
    public const string MyActivityRoute = "/my-activity";

    /// <summary>The Taste sub-page of Overview (built with the Taste page).</summary>
    public const string TasteRoute = "/settings/overview/taste";

    private static readonly IReadOnlyDictionary<SettingsSection, LaunchFeatureEvidence> LaunchContracts =
        new Dictionary<SettingsSection, LaunchFeatureEvidence>
        {
            [SettingsSection.Overview] = Complete(),
            [SettingsSection.Profiles] = Complete(),
            [SettingsSection.Preferences] = Complete(),
            [SettingsSection.Playback] = Complete(),
            [SettingsSection.Listening] = Complete(),
            [SettingsSection.Reading] = Complete(),
            [SettingsSection.Security] = Complete(),
            [SettingsSection.AdminOverview] = Complete(),
            [SettingsSection.Libraries] = Complete(),
            [SettingsSection.Ingestion] = Complete(),
            [SettingsSection.RecentlyAdded] = Complete(),
            [SettingsSection.Providers] = Complete(),
            [SettingsSection.Review] = Complete(),
            [SettingsSection.Network] = Complete(),
            [SettingsSection.Server] = Complete(),
            [SettingsSection.LocalAi] = Complete(),
            [SettingsSection.Delivery] = Complete(),
            [SettingsSection.Tools] = Complete(),
            [SettingsSection.Access] = Complete(),
        };

    private static LaunchFeatureEvidence Complete() => new(true, true, true, true, true);

    public static void ConfigureEnvironment(bool productionMode) => _productionMode = productionMode;

    /// <summary>
    /// Applies <paramref name="productionMode"/> only to the calling execution flow until the returned scope is disposed.
    /// The process-wide setting from <see cref="ConfigureEnvironment"/> is shared by every caller, so a check that needs the
    /// other mode uses this instead of flipping it underneath callers that run in parallel.
    /// </summary>
    public static IDisposable UseEnvironmentScope(bool productionMode)
    {
        var previous = _scopedProductionMode.Value;
        _scopedProductionMode.Value = productionMode;
        return new EnvironmentScope(previous);
    }

    private sealed class EnvironmentScope(bool? previous) : IDisposable
    {
        public void Dispose() => _scopedProductionMode.Value = previous;
    }

    public static bool MeetsLaunchContract(SettingsSection section) =>
        LaunchContracts.TryGetValue(section, out var evidence) && evidence.IsComplete;

    public static readonly SettingsGroupDef[] AllGroups =
    [
        new("personal", "Personal", AppMaterialIcons.Outlined.Person, false, SettingsSection.Overview),
        new("server", "Server", AppMaterialIcons.Outlined.Dns, true, SettingsSection.Ingestion),
        new("manage", "Manage", AppMaterialIcons.Outlined.ManageAccounts, true, SettingsSection.Access),
        new("developer", "Developer", AppMaterialIcons.Outlined.DataObject, true, SettingsSection.LocalAi),
    ];

    public static readonly SettingsItemDef[] AllItems =
    [
        // Overview is the page everyone except a Kids profile lands on: your own profile, counts, recent activity and taste (D11).
        new(SettingsSection.Overview, "personal", "overview", AppMaterialIcons.Outlined.AccountCircle, "Overview", false, null, [], "sqlite", Status: SettingsStatusKind.Live),
        // Profiles is an editing page for administrators (D29): a server administrator, or a household administrator for their own
        // household. It is not an administrator-PIN page, so AdminOnly stays false; HouseholdAdminAccess plus IsVisible decide who sees it.
        new(SettingsSection.Profiles, "personal", "profiles", AppMaterialIcons.Outlined.Group, "Profiles", false, null, ["profile", "household"], "sqlite", Status: SettingsStatusKind.Live, HouseholdAdminAccess: true),
        new(SettingsSection.Preferences, "personal", "preferences", AppMaterialIcons.Outlined.Tune, "Preferences", false, null, [], "sqlite", Status: SettingsStatusKind.Live),
        new(SettingsSection.Playback, "personal", "playback", AppMaterialIcons.Outlined.PlayCircleOutline, "Playback", false, null, [], "sqlite", Status: SettingsStatusKind.Live),
        new(SettingsSection.Listening, "personal", "listening", AppMaterialIcons.Outlined.Headphones, "Listening", false, null, [], "sqlite", Status: SettingsStatusKind.Live),
        new(SettingsSection.Reading, "personal", "reading", AppMaterialIcons.Outlined.MenuBook, "Reading", false, null, [], "sqlite", Status: SettingsStatusKind.Live),
        new(SettingsSection.Security, "personal", "security", AppMaterialIcons.Outlined.Lock, "Security", false, null, ["account"], "sqlite", Status: SettingsStatusKind.Live),

        new(SettingsSection.Ingestion, "server", "ingestion", AppMaterialIcons.Outlined.VideoLibrary, "Library and Ingestion", true, null, [], Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.SummaryOnly),
        new(SettingsSection.RecentlyAdded, "server", "recently-added", AppMaterialIcons.Outlined.History, "Recently Added", true, null, [], Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.Full),
        new(SettingsSection.Libraries, "server", "libraries", AppMaterialIcons.Outlined.Folder, "Libraries", true, null, [], Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.SummaryOnly),
        new(SettingsSection.Providers, "server", "metadata", AppMaterialIcons.Outlined.Hub, "Metadata Providers", true, null, [], Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.SummaryOnly),
        new(SettingsSection.Review, "server", "review", AppMaterialIcons.Outlined.RateReview, "Needs Review", true, "review", [], "mixed", Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.DesktopOnly),
        new(SettingsSection.Network, "server", "network", AppMaterialIcons.Outlined.WifiTethering, "Network and Remote Access", true, null, [], "json+runtime", Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.SummaryOnly),
        new(SettingsSection.Delivery, "server", "delivery", AppMaterialIcons.Outlined.Stream, "Streaming", true, null, [], Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.SummaryOnly),

        new(SettingsSection.Access, "manage", "access", AppMaterialIcons.Outlined.AdminPanelSettings, "Users and Sign-in", true, null, [], Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.Full),
        new(SettingsSection.AdminOverview, "manage", "system", AppMaterialIcons.Outlined.DesktopWindows, "System", true, null, [], "json+sqlite", Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.SummaryOnly),
        new(SettingsSection.Tools, "manage", "tools", AppMaterialIcons.Outlined.Build, "Tools and requirements", true, null, [], "runtime", Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.Full),
        new(SettingsSection.Server, "manage", "backup-recovery", AppMaterialIcons.Outlined.Backup, "Backup and recovery", true, null, [], Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.SummaryOnly),

        new(SettingsSection.LocalAi, "developer", "ai", AppMaterialIcons.Outlined.Memory, "Local AI", true, null, [], Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.SummaryOnly),
        new(SettingsSection.Plugins, "developer", "plugins", AppMaterialIcons.Outlined.Extension, "Plugins", true, null, [], "sqlite", Status: SettingsStatusKind.Partial, MobileAvailability: SettingsMobileAvailability.SummaryOnly),
        // Developer Tools (settings revamp D35) opens on the Provider Tester, so that page owns the "developer" address and its
        // old "provider-tester" address. Enrichment Tester and Development data are its sub-pages; they are offered only when the
        // running Engine is a development build (see DevelopmentToolsState). The Enrichment Tester is temporary: it is removed at beta.
        new(SettingsSection.ProviderTester, "developer", "developer", AppMaterialIcons.Outlined.Construction, "Developer Tools", true, null, ["provider-tester"], "internal", Status: SettingsStatusKind.Experimental, MobileAvailability: SettingsMobileAvailability.DesktopOnly),
        new(SettingsSection.EnrichmentTester, "developer", "enrichment-tester", AppMaterialIcons.Outlined.Science, "Enrichment Tester", true, null, ["tester"], "internal", Status: SettingsStatusKind.Experimental, MobileAvailability: SettingsMobileAvailability.DesktopOnly),
        new(SettingsSection.DevHarness, "developer", "development-data", AppMaterialIcons.Outlined.Inventory2, "Development data", true, null, ["dev-harness", "harness", "ingestion-harness", "test-harness"], "internal", Status: SettingsStatusKind.Partial, MobileAvailability: SettingsMobileAvailability.DesktopOnly),
    ];

    /// <summary>
    /// The one Settings menu. Every parent opens its own main page and its children never repeat it (a sub-page of the
    /// same section is a child only when it is a different page). Recently Added and Needs Review stay out of the menu:
    /// Recently Added opens from a Needs Review row's "Find in recent history" and Needs Review from the administrator banner (see <see cref="OffMenuParents"/>). The
    /// desktop tree, the phone page selector and the breadcrumb all read this.
    /// </summary>
    public static readonly SettingsMenuGroup[] MenuGroups =
    [
        new("personal", "Personal", AppMaterialIcons.Outlined.Person,
        [
            Node("Overview", AppMaterialIcons.Outlined.AccountCircle, SettingsSection.Overview, children:
            [
                Node("Activity", AppMaterialIcons.Outlined.History, SettingsSection.Overview, ActivitySubsection),
                Node("Taste", AppMaterialIcons.Outlined.Category, SettingsSection.Overview, TasteSubsection),
            ]),
            Node("Profiles", AppMaterialIcons.Outlined.Group, SettingsSection.Profiles, children:
            [
                Node("Households", AppMaterialIcons.Outlined.Home, SettingsSection.Profiles, HouseholdsSubsection),
            ]),
            Node("Preferences", AppMaterialIcons.Outlined.Tune, SettingsSection.Preferences, children:
            [
                Node("Playback", AppMaterialIcons.Outlined.PlayCircleOutline, SettingsSection.Playback),
                Node("Listening", AppMaterialIcons.Outlined.Headphones, SettingsSection.Listening),
                Node("Reading", AppMaterialIcons.Outlined.MenuBook, SettingsSection.Reading),
            ]),
            Node("Security", AppMaterialIcons.Outlined.Lock, SettingsSection.Security, children:
            [
                Node("Devices", AppMaterialIcons.Outlined.Devices, SettingsSection.Security, "devices"),
            ]),
        ]),
        new("server", "Server", AppMaterialIcons.Outlined.Dns,
        [
            Node("Library and Ingestion", AppMaterialIcons.Outlined.VideoLibrary, SettingsSection.Ingestion, children:
            [
                Node("Libraries", AppMaterialIcons.Outlined.Folder, SettingsSection.Libraries),
                Node("Metadata Providers", AppMaterialIcons.Outlined.Hub, SettingsSection.Providers, children:
                [
                    Node("Ingestion Flow", AppMaterialIcons.Outlined.AccountTree, SettingsSection.Providers, "ingestion-flow"),
                ]),
            ]),
            Node("Network and Remote Access", AppMaterialIcons.Outlined.WifiTethering, SettingsSection.Network, children:
            [
                Node("Local Network", AppMaterialIcons.Outlined.Lan, SettingsSection.Network, "local"),
                Node("Remote Access", AppMaterialIcons.Outlined.Public, SettingsSection.Network, "remote"),
                Node("Advanced", AppMaterialIcons.Outlined.Tune, SettingsSection.Network, "advanced"),
            ]),
            Node("Streaming", AppMaterialIcons.Outlined.Stream, SettingsSection.Delivery, children:
            [
                Node("Hardware Acceleration", AppMaterialIcons.Outlined.Memory, SettingsSection.Delivery, "hardware"),
                Node("Optimized Copies", AppMaterialIcons.Outlined.Storage, SettingsSection.Delivery, "optimized-copies"),
                Node("Diagnostics", AppMaterialIcons.Outlined.MonitorHeart, SettingsSection.Delivery, "diagnostics"),
            ]),
        ]),
        new("manage", "Manage", AppMaterialIcons.Outlined.ManageAccounts,
        [
            Node("Users and Sign-in", AppMaterialIcons.Outlined.AdminPanelSettings, SettingsSection.Access, children:
            [
                Node("Sign-in Methods", AppMaterialIcons.Outlined.Security, SettingsSection.Access, "authentication"),
            ]),
            Node("System", AppMaterialIcons.Outlined.DesktopWindows, SettingsSection.AdminOverview, children:
            [
                Node("Tools and requirements", AppMaterialIcons.Outlined.Build, SettingsSection.Tools),
                Node("Logs", AppMaterialIcons.Outlined.List, SettingsSection.AdminOverview, LogsSubsection),
                Node("Backup and recovery", AppMaterialIcons.Outlined.Backup, SettingsSection.Server),
            ]),
        ]),
        new("developer", "Developer", AppMaterialIcons.Outlined.DataObject,
        [
            Node("Local AI", AppMaterialIcons.Outlined.Memory, SettingsSection.LocalAi, children:
            [
                Node("Status", AppMaterialIcons.Outlined.Memory, SettingsSection.LocalAi, "status"),
                Node("Model and hardware", AppMaterialIcons.Outlined.Storage, SettingsSection.LocalAi, "models"),
                Node("Budget", AppMaterialIcons.Outlined.Speed, SettingsSection.LocalAi, "budget"),
                Node("Usage", AppMaterialIcons.Outlined.Assessment, SettingsSection.LocalAi, "usage"),
            ]),
            Node("Integrations", AppMaterialIcons.Outlined.Link, SettingsSection.Access, "applications"),
            Node("Plugins", AppMaterialIcons.Outlined.Extension, SettingsSection.Plugins),
            Node("Developer Tools", AppMaterialIcons.Outlined.Construction, SettingsSection.ProviderTester, children:
            [
                Node("Enrichment Tester", AppMaterialIcons.Outlined.Science, SettingsSection.EnrichmentTester),
                Node("Development data", AppMaterialIcons.Outlined.Inventory2, SettingsSection.DevHarness),
            ]),
        ]),
    ];

    private static SettingsMenuNode Node(
        string label,
        string icon,
        SettingsSection section,
        string? subsection = null,
        IReadOnlyList<SettingsMenuNode>? children = null) =>
        new(label, icon, section, subsection, children);

    public static readonly IReadOnlyDictionary<SettingsSection, IReadOnlyList<SettingsSubsectionDef>> Subsections =
        new Dictionary<SettingsSection, IReadOnlyList<SettingsSubsectionDef>>
        {
            [SettingsSection.Overview] =
            [
                new(AppsSubsection, "Apps", AppMaterialIcons.Outlined.Smartphone),
                new(ActivitySubsection, "Activity", AppMaterialIcons.Outlined.History),
                new(TasteSubsection, "Taste", AppMaterialIcons.Outlined.Category),
            ],
            [SettingsSection.Profiles] =
            [
                new(HouseholdsSubsection, "Households", AppMaterialIcons.Outlined.Home),
            ],
            [SettingsSection.Preferences] = [],
            [SettingsSection.Playback] = [],
            [SettingsSection.Listening] = [],
            [SettingsSection.Reading] = [],
            [SettingsSection.Security] =
            [
                new("devices", "Devices", AppMaterialIcons.Outlined.Devices),
            ],
            [SettingsSection.AdminOverview] =
            [
                new(LogsSubsection, "Logs", AppMaterialIcons.Outlined.List),
            ],
            [SettingsSection.Libraries] = [],
            [SettingsSection.Ingestion] = [],
            [SettingsSection.RecentlyAdded] = [],
            [SettingsSection.DevHarness] = [],
            [SettingsSection.Providers] =
            [
                new("providers", "Providers", AppMaterialIcons.Outlined.Dns),
                new("ingestion-flow", "Ingestion Flow", AppMaterialIcons.Outlined.AccountTree),
            ],
            [SettingsSection.LocalAi] =
            [
                new("status", "Status", AppMaterialIcons.Outlined.Memory),
                new("models", "Model and hardware", AppMaterialIcons.Outlined.Storage),
                new("budget", "Budget", AppMaterialIcons.Outlined.Speed),
                new("usage", "Usage", AppMaterialIcons.Outlined.Assessment),
            ],
            [SettingsSection.Plugins] = [],
            [SettingsSection.Delivery] =
            [
                new("hardware", "Hardware Acceleration", AppMaterialIcons.Outlined.Memory),
                new("optimized-copies", "Optimized Copies", AppMaterialIcons.Outlined.Storage),
                new("diagnostics", "Diagnostics", AppMaterialIcons.Outlined.MonitorHeart),
            ],
            [SettingsSection.Network] =
            [
                new("overview", "Overview", AppMaterialIcons.Outlined.Dashboard),
                new("local", "Local Network", AppMaterialIcons.Outlined.Lan),
                new("remote", "Remote Access", AppMaterialIcons.Outlined.Public),
                new("advanced", "Advanced", AppMaterialIcons.Outlined.Tune),
            ],
            [SettingsSection.Access] =
            [
                new("users", "Users", AppMaterialIcons.Outlined.ManageAccounts),
                new("applications", "Integrations", AppMaterialIcons.Outlined.Link),
                new("authentication", "Sign-in Methods", AppMaterialIcons.Outlined.Security),
            ],
            [SettingsSection.Tools] = [],
            [SettingsSection.Server] = [],
            [SettingsSection.Review] = [],
            [SettingsSection.ProviderTester] = [new("overview", "Overview", AppMaterialIcons.Outlined.Biotech)],
            [SettingsSection.EnrichmentTester] = [new("overview", "Overview", AppMaterialIcons.Outlined.Science)],
        };

    private static readonly Dictionary<SettingsSection, SettingsItemDef> _itemsBySection =
        AllItems.ToDictionary(item => item.Value);

    private static readonly Dictionary<string, SettingsItemDef> _itemsBySlug =
        AllItems
            .Where(item => !string.IsNullOrWhiteSpace(item.Slug))
            .ToDictionary(item => NormalizeKey(item.Slug!), item => item, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, SettingsItemDef> _itemsByAlias =
        AllItems
            .SelectMany(item => item.Aliases.Select(alias => new KeyValuePair<string, SettingsItemDef>(NormalizeKey(alias), item)))
            .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, SettingsGroupDef> _groupsByKey =
        AllGroups.ToDictionary(group => group.Key, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<SettingsSection> _landingSections =
    [
        SettingsSection.Overview,
        SettingsSection.Profiles,
        SettingsSection.Preferences,
        SettingsSection.Playback,
        SettingsSection.Listening,
        SettingsSection.Reading,
        SettingsSection.Security,
        SettingsSection.AdminOverview,
        SettingsSection.Libraries,
        SettingsSection.Ingestion,
        SettingsSection.RecentlyAdded,
        SettingsSection.Review,
        SettingsSection.Delivery,
        SettingsSection.Tools,
        SettingsSection.Access,
        SettingsSection.Server,
        SettingsSection.LocalAi,
        SettingsSection.Plugins,
        SettingsSection.ProviderTester,
        SettingsSection.EnrichmentTester,
    ];

    public static IEnumerable<SettingsGroupDef> FilteredGroups(bool administrationAllowed) =>
        AllGroups.Where(group => !group.AdminOnly || administrationAllowed);

    /// <summary>
    /// The menu a person may see: every row they may open, in order, with groups that have no visible rows removed.
    /// A hidden row hides its children. <paramref name="extraFilter"/> lets the caller drop rows for reasons that are
    /// not about rights (a feature switched off, a page not offered on a phone).
    /// </summary>
    /// <param name="householdAdministrator">
    /// A household administrator who is not a server administrator: they also see the pages marked
    /// <see cref="SettingsItemDef.HouseholdAdminAccess"/> (Profiles today), and nothing else in Server.
    /// </param>
    public static IReadOnlyList<SettingsMenuGroup> FilteredMenuGroups(
        bool administrationAllowed,
        bool householdAdministrator = false,
        Func<SettingsMenuNode, bool>? extraFilter = null)
    {
        return MenuGroups
            .Select(group => group with { Nodes = FilterNodes(group.Nodes, administrationAllowed, householdAdministrator, extraFilter) })
            .Where(group => group.Nodes.Count > 0)
            .ToList();
    }

    private static IReadOnlyList<SettingsMenuNode> FilterNodes(
        IReadOnlyList<SettingsMenuNode> nodes,
        bool administrationAllowed,
        bool householdAdministrator,
        Func<SettingsMenuNode, bool>? extraFilter)
    {
        var kept = new List<SettingsMenuNode>();
        foreach (var node in nodes)
        {
            if (!IsVisible(node.Section, administrationAllowed, householdAdministrator)
                || !IsNodeVisible(node, administrationAllowed)
                || extraFilter is not null && !extraFilter(node))
            {
                continue;
            }

            kept.Add(node with { ChildNodes = FilterNodes(node.Children, administrationAllowed, householdAdministrator, extraFilter) });
        }

        return kept;
    }

    /// <summary>
    /// A sub-page row can be narrower than its section: Households (under Profiles) is for server administrators only,
    /// while the Profiles list itself also serves a household administrator.
    /// </summary>
    public static bool IsNodeVisible(SettingsMenuNode node, bool administrationAllowed) =>
        !(node is { Section: SettingsSection.Profiles, Subsection: HouseholdsSubsection }) || administrationAllowed;

    /// <summary>Every row of the menu, parents before their children.</summary>
    public static IEnumerable<SettingsMenuNode> Flatten(IEnumerable<SettingsMenuNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children))
            {
                yield return child;
            }
        }
    }

    /// <summary>
    /// Finds where a page sits in the menu. A sub-page the menu does not list (a landing view, a profile or provider
    /// detail) resolves to its section's main row. A page the menu leaves out but that has a parent in
    /// <see cref="OffMenuParents"/> (Recently Added, Needs Review) resolves to that parent's trail plus itself, so its breadcrumb
    /// reads Server, Library and Ingestion, Recently Added. Returns <see langword="null"/> for other pages the menu
    /// leaves out.
    /// </summary>
    public static SettingsMenuLocation? FindMenuLocation(SettingsSection section, string? subsection = null)
    {
        var requested = string.IsNullOrWhiteSpace(subsection) ? null : NormalizeKey(subsection);
        SettingsMenuLocation? fallback = null;
        foreach (var group in MenuGroups)
        {
            foreach (var trail in Trails(group.Nodes, []))
            {
                var node = trail[^1];
                if (node.Section != section)
                {
                    continue;
                }

                if (node.Subsection is null)
                {
                    fallback ??= new SettingsMenuLocation(group, trail);
                }
                else if (requested is not null && string.Equals(NormalizeKey(node.Subsection), requested, StringComparison.OrdinalIgnoreCase))
                {
                    return new SettingsMenuLocation(group, trail);
                }
            }
        }

        if (fallback is not null && requested is not null
            && OffMenuSubsections.Contains((section, requested))
            && ResolveSubsection(section, requested) is { } offMenu)
        {
            return new SettingsMenuLocation(fallback.Group, [.. fallback.Trail, new SettingsMenuNode(offMenu.Label, offMenu.Icon, section, offMenu.Slug)]);
        }

        if (fallback is null && OffMenuParents.TryGetValue(section, out var parentSection)
            && FindMenuLocation(parentSection) is { } parent)
        {
            var item = GetItem(section);
            return new SettingsMenuLocation(parent.Group, [.. parent.Trail, new SettingsMenuNode(item.Label, item.Icon, section)]);
        }

        return fallback;
    }

    /// <summary>
    /// Pages that have their own route but no row in the menu, and the menu page they open from. They keep their
    /// place in the breadcrumb; the menu does not list them.
    /// </summary>
    public static readonly IReadOnlyDictionary<SettingsSection, SettingsSection> OffMenuParents =
        new Dictionary<SettingsSection, SettingsSection>
        {
            [SettingsSection.RecentlyAdded] = SettingsSection.Ingestion,
            [SettingsSection.Review] = SettingsSection.Ingestion,
        };

    /// <summary>
    /// Sub-pages that have their own route but no row in the menu, because an icon on their section's main page opens them.
    /// They sit under that section's main row in the breadcrumb (Personal, Overview, Apps). Slugs are in their normalised form.
    /// </summary>
    public static readonly IReadOnlySet<(SettingsSection Section, string Subsection)> OffMenuSubsections =
        new HashSet<(SettingsSection, string)>
        {
            (SettingsSection.Overview, AppsSubsection),
        };

    private static IEnumerable<IReadOnlyList<SettingsMenuNode>> Trails(
        IReadOnlyList<SettingsMenuNode> nodes, IReadOnlyList<SettingsMenuNode> ancestors)
    {
        foreach (var node in nodes)
        {
            var trail = ancestors.Append(node).ToList();
            yield return trail;
            foreach (var child in Trails(node.Children, trail))
            {
                yield return child;
            }
        }
    }

    public static IReadOnlyList<SettingsItemDef> FilteredItems(SettingsGroupDef group, bool administrationAllowed)
    {
        var hasAdmin = administrationAllowed;
        return AllItems
            .Where(item => string.Equals(item.GroupKey, group.Key, StringComparison.OrdinalIgnoreCase))
            .Where(item => !item.AdminOnly || hasAdmin)
            .ToList();
    }

    public static SettingsItemDef GetItem(SettingsSection section) => _itemsBySection[section];

    public static SettingsStatusKind GetStatus(SettingsSection section) => GetItem(section).Status;

    public static SettingsMobileAvailability GetMobileAvailability(SettingsSection section) =>
        GetItem(section).MobileAvailability;

    public static bool IsVisibleOnMobile(SettingsSection section) =>
        GetMobileAvailability(section) != SettingsMobileAvailability.DesktopOnly;

    public static bool IsMobileRouteAvailable(SettingsSection section, string? subsection = null, string? detail = null)
    {
        if (!IsVisibleOnMobile(section))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(detail))
        {
            return false;
        }

        var normalized = subsection?.Trim().ToLowerInvariant() ?? string.Empty;
        return section switch
        {
            SettingsSection.Libraries => string.IsNullOrWhiteSpace(normalized) || normalized == "view",
            SettingsSection.Profiles => string.IsNullOrWhiteSpace(normalized) || normalized == HouseholdsSubsection || Guid.TryParse(normalized, out _),
            SettingsSection.Providers => normalized is "" or "providers",
            SettingsSection.Network => normalized is "" or "overview",
            SettingsSection.Access => normalized is "" or "users" or "applications" or "authentication",
            // The Logs page (technical log, retention) is a desktop and tablet page; a phone keeps the System summary.
            SettingsSection.AdminOverview or SettingsSection.Delivery or SettingsSection.LocalAi or SettingsSection.Plugins =>
                string.IsNullOrWhiteSpace(normalized),
            _ => true,
        };
    }

    public static bool IsMobileSubsectionAvailable(SettingsSection section, string subsection) =>
        IsMobileRouteAvailable(section, subsection);

    public static SettingsGroupDef GetGroup(SettingsSection section) => _groupsByKey[GetItem(section).GroupKey];

    public static SettingsSection GetDefaultSection(string groupKey) => _groupsByKey[groupKey].DefaultSection;

    public static bool IsVisible(SettingsSection section, bool administrationAllowed, bool householdAdministrator = false)
    {
        var item = GetItem(section);
        if ((_scopedProductionMode.Value ?? _productionMode)
            && (item.Status != SettingsStatusKind.Live || !MeetsLaunchContract(section)))
        {
            return false;
        }

        // Overview is everyone's own page. Profiles is for administrators only: a member (and a Kids profile) never sees it. A household administrator reaches it
        // through HouseholdAdminAccess; the Engine still limits what they can do there to their own household.
        return administrationAllowed || section is SettingsSection.Overview or SettingsSection.Preferences or SettingsSection.Playback
            or SettingsSection.Listening or SettingsSection.Reading or SettingsSection.Security
            || householdAdministrator && item.HouseholdAdminAccess;
    }

    public static SettingsSection FirstVisibleSection(bool administrationAllowed, bool householdAdministrator = false) =>
        AllItems.First(item => IsVisible(item.Value, administrationAllowed, householdAdministrator)).Value;

    public static string RouteFor(SettingsSection section)
    {
        var sectionRoute = SectionRouteFor(section);
        if (section == SettingsSection.Access)
        {
            return $"{sectionRoute}/users";
        }
        if (_landingSections.Contains(section))
        {
            return sectionRoute;
        }

        var defaultSubsection = GetSubsections(section).FirstOrDefault();
        return defaultSubsection is null
            ? sectionRoute
            : $"{sectionRoute}/{defaultSubsection.Slug}";
    }

    public static IReadOnlyList<SettingsSubsectionDef> GetSubsections(SettingsSection section) =>
        Subsections.TryGetValue(section, out var subsections) ? subsections : [];

    public static SettingsSubsectionDef GetDefaultSubsection(SettingsSection section) =>
        GetSubsections(section).First();

    public static SettingsSubsectionDef? ResolveSubsection(SettingsSection section, string? slug)
    {
        var subsections = GetSubsections(section);
        if (string.IsNullOrWhiteSpace(slug))
        {
            return subsections.FirstOrDefault();
        }

        var normalized = NormalizeKey(slug);
        normalized = (section, normalized) switch
        {
            // The old My household tab is gone: people, PINs and sign-ins live on each person's profile page.
            (SettingsSection.Access, "household") => "users",
            _ => normalized,
        };
        return subsections.FirstOrDefault(subsection =>
            string.Equals(NormalizeKey(subsection.Slug), normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static string RouteFor(SettingsSection section, string subsectionSlug)
    {
        var subsection = ResolveSubsection(section, subsectionSlug)
            ?? throw new ArgumentOutOfRangeException(nameof(subsectionSlug), subsectionSlug, "Unknown settings subsection.");

        if (GetSubsections(section).Count == 0)
        {
            return SectionRouteFor(section);
        }

        return $"{SectionRouteFor(section)}/{subsection.Slug}";
    }

    /// <summary>
    /// The address of the Events tab of System &gt; Logs (the Activity log), optionally opened on one filter
    /// (<c>added</c>, <c>review</c>, <c>failed</c> or <c>system</c>; no filter means All). Every link to the log is built here.
    /// </summary>
    public static string ActivityLogRoute(string? filter = null)
    {
        var route = $"{RouteFor(SettingsSection.AdminOverview, LogsSubsection)}?tab=events";
        return string.IsNullOrWhiteSpace(filter) ? route : $"{route}&filter={Uri.EscapeDataString(filter)}";
    }

    public static SettingsRouteResolution ResolveRoute(string? segment, bool administrationAllowed, bool householdAdministrator = false)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            // The bare /settings address opens the first page this person may see (Profiles for an administrator).
            var landing = FirstVisibleSection(administrationAllowed, householdAdministrator);
            return new SettingsRouteResolution(
                landing,
                RouteFor(landing),
                IsCanonicalRoute: false,
                IsKnownRoute: true,
                RequestedSectionAllowed: true);
        }

        var normalized = NormalizeKey(segment);

        if (_itemsBySlug.TryGetValue(normalized, out var canonicalItem))
        {
            if (IsVisible(canonicalItem.Value, administrationAllowed, householdAdministrator))
            {
                return new SettingsRouteResolution(
                    canonicalItem.Value,
                    RouteFor(canonicalItem.Value),
                    IsCanonicalRoute: true,
                    IsKnownRoute: true,
                    RequestedSectionAllowed: true);
            }

            var fallback = FirstVisibleSection(administrationAllowed, householdAdministrator);
            return new SettingsRouteResolution(
                fallback,
                RouteFor(fallback),
                IsCanonicalRoute: false,
                IsKnownRoute: true,
                RequestedSectionAllowed: false);
        }

        if (_itemsByAlias.TryGetValue(normalized, out var aliasedItem))
        {
            if (IsVisible(aliasedItem.Value, administrationAllowed, householdAdministrator))
            {
                return new SettingsRouteResolution(
                    aliasedItem.Value,
                    RouteFor(aliasedItem.Value),
                    IsCanonicalRoute: false,
                    IsKnownRoute: true,
                    RequestedSectionAllowed: true);
            }

            var fallback = FirstVisibleSection(administrationAllowed, householdAdministrator);
            return new SettingsRouteResolution(
                fallback,
                RouteFor(fallback),
                IsCanonicalRoute: false,
                IsKnownRoute: true,
                RequestedSectionAllowed: false);
        }

        return new SettingsRouteResolution(
            FirstVisibleSection(administrationAllowed, householdAdministrator),
            "/not-found",
            IsCanonicalRoute: false,
            IsKnownRoute: false,
            RequestedSectionAllowed: false);
    }

    public static SettingsSection? ParseFromRoute(string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return SettingsSection.Overview;
        }

        var resolution = ResolveRoute(segment, administrationAllowed: true);
        return resolution.IsKnownRoute ? resolution.Section : null;
    }

    private static string NormalizeKey(string value)
    {
        var chars = value.Where(char.IsLetterOrDigit).ToArray();
        return new string(chars).ToLowerInvariant();
    }

    private static string SectionRouteFor(SettingsSection section)
    {
        var item = GetItem(section);
        return $"/settings/{item.Slug}";
    }

}
