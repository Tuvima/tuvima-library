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
    Home,
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
    Notifications,
    Network,
    Access,
    Integrations,
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
    bool SectionBreakBefore = false);

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
/// One tab of a Settings page. A tab is a link to its own address, so deep links, reload and Back all work; it opens one
/// (section, sub-page) of the Settings route table.
/// </summary>
/// <param name="Subsection">The sub-page this tab opens, or <see langword="null"/> for the section's own main page.</param>
public sealed record SettingsPageTab(
    string Label,
    string Icon,
    SettingsSection Section,
    string? Subsection = null)
{
    public string Route => Subsection is null
        ? SettingsNav.RouteFor(Section)
        : SettingsNav.RouteFor(Section, Subsection);
}

/// <summary>
/// One row of the flat Settings menu: a page. A page with several related areas carries one row of tabs; a page with a
/// single purpose has none. The same rows are the desktop menu and the phone page selector.
/// </summary>
/// <param name="PageTabs">The page's tabs, in order, or <see langword="null"/> for a page with a single purpose.</param>
/// <param name="AlsoSections">Sections that open under this page without being one of its tabs (off-menu pages and retired addresses).</param>
public sealed record SettingsMenuNode(
    string Label,
    string Icon,
    SettingsSection Section,
    IReadOnlyList<SettingsPageTab>? PageTabs = null,
    IReadOnlyList<SettingsSection>? AlsoSections = null)
{
    public IReadOnlyList<SettingsPageTab> Tabs => PageTabs ?? [];

    public bool HasTabs => Tabs.Count > 0;

    /// <summary>Where the row opens: its first tab, or the page's own address.</summary>
    public string Route => Tabs.Count > 0 ? Tabs[0].Route : SettingsNav.RouteFor(Section);

    /// <summary>Every section that opens under this page.</summary>
    public IEnumerable<SettingsSection> Sections =>
        Tabs.Select(tab => tab.Section).Append(Section).Concat(AlsoSections ?? []).Distinct();

    /// <summary>
    /// The tab a page of this row sits on, or <see langword="null"/> when it is not on a tab (an off-menu page, or a list the
    /// tabs open). A sub-page the tabs do not name falls to the section's own tab.
    /// </summary>
    public SettingsPageTab? TabFor(SettingsSection section, string? subsection)
    {
        var inSection = Tabs.Where(tab => tab.Section == section).ToList();
        if (inSection.Count == 0)
        {
            return null;
        }

        var requested = string.IsNullOrWhiteSpace(subsection) ? null : SettingsNav.NormalizeSlug(subsection);
        return inSection.FirstOrDefault(tab => tab.Subsection is not null
                   && requested is not null
                   && string.Equals(SettingsNav.NormalizeSlug(tab.Subsection), requested, StringComparison.OrdinalIgnoreCase))
               ?? inSection.FirstOrDefault(tab => tab.Subsection is null)
               ?? (requested is null ? inSection[0] : null);
    }
}

/// <summary>A top-level group of the Settings menu (Personal, Server, Platform, System, Developer).</summary>
public sealed record SettingsMenuGroup(
    string Key,
    string Label,
    string Icon,
    IReadOnlyList<SettingsMenuNode> Nodes);

/// <summary>Where a page sits in the menu: its group, its row, the tab it is on, and the name of an off-tab page beneath the row.</summary>
/// <param name="Tab">The tab the page is on; <see langword="null"/> for a page the row has no tab for.</param>
/// <param name="Leaf">The name of a page that sits beneath the row without being a tab (Needs Review, Apps), for the breadcrumb.</param>
public sealed record SettingsMenuLocation(
    SettingsMenuGroup Group,
    SettingsMenuNode Page,
    SettingsPageTab? Tab,
    string? Leaf = null)
{
    public SettingsMenuNode Node => Page;
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

    /// <summary>The Households sub-page of Users and Sign-in (server administrators only).</summary>
    public const string HouseholdsSubsection = "households";

    /// <summary>The Logs sub-page of System: the Activity log (Events), technical log, retention and export (server administrators only).</summary>
    public const string LogsSubsection = "logs";

    /// <summary>The Scheduled Tasks sub-page of System: every task with its status, schedule, last and next run and recent runs (server administrators only).</summary>
    public const string ScheduledTasksSubsection = "scheduled-tasks";

    /// <summary>The Devices &amp; Sessions sub-page of Security &amp; Devices.</summary>
    public const string DevicesSubsection = "devices";

    /// <summary>The Ingestion Flow sub-page of Metadata Providers, a tab of Library &amp; Ingestion.</summary>
    public const string IngestionFlowSubsection = "ingestion-flow";

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
            [SettingsSection.Home] = Complete(),
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
            [SettingsSection.Notifications] = Complete(),
            [SettingsSection.Access] = Complete(),
            [SettingsSection.Integrations] = Complete(),
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
        new("platform", "Platform", AppMaterialIcons.Outlined.Extension, true, SettingsSection.LocalAi),
        new("system", "System", AppMaterialIcons.Outlined.DesktopWindows, true, SettingsSection.AdminOverview),
        new("developer", "Developer", AppMaterialIcons.Outlined.DataObject, true, SettingsSection.ProviderTester),
    ];

    public static readonly SettingsItemDef[] AllItems =
    [
        // Profile is the page everyone lands on, a Kids profile included (D12): your own profile, counts, recent activity and taste.
        new(SettingsSection.Overview, "personal", "overview", AppMaterialIcons.Outlined.AccountCircle, "Profile", false, null, [], "sqlite", Status: SettingsStatusKind.Live),
        // Manage Profiles opens the household list for an administrator and your own profile for everyone else. Preferences, Playback,
        // Listening, Reading and Home are no longer pages of their own: each is a section of a profile's Preferences tab, and their old
        // addresses forward there.
        new(SettingsSection.Profiles, "personal", "profiles", AppMaterialIcons.Outlined.Group, "Manage Profiles", false, null, ["profile", "household"], "sqlite", Status: SettingsStatusKind.Live),
        new(SettingsSection.Preferences, "personal", "preferences", AppMaterialIcons.Outlined.Tune, "Preferences", false, null, [], "sqlite", Status: SettingsStatusKind.Live),
        new(SettingsSection.Playback, "personal", "playback", AppMaterialIcons.Outlined.PlayCircleOutline, "Playback", false, null, [], "sqlite", Status: SettingsStatusKind.Live),
        new(SettingsSection.Listening, "personal", "listening", AppMaterialIcons.Outlined.Headphones, "Listening", false, null, [], "sqlite", Status: SettingsStatusKind.Live),
        new(SettingsSection.Reading, "personal", "reading", AppMaterialIcons.Outlined.MenuBook, "Reading", false, null, [], "sqlite", Status: SettingsStatusKind.Live),
        new(SettingsSection.Home, "personal", "home", AppMaterialIcons.Outlined.Home, "Home", false, null, [], "sqlite", Status: SettingsStatusKind.Live),
        new(SettingsSection.Security, "personal", "security", AppMaterialIcons.Outlined.Lock, "Security & Devices", false, null, ["account"], "sqlite", Status: SettingsStatusKind.Live),

        new(SettingsSection.Ingestion, "server", "ingestion", AppMaterialIcons.Outlined.VideoLibrary, "Library & Ingestion", true, null, [], Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.SummaryOnly),
        new(SettingsSection.RecentlyAdded, "server", "recently-added", AppMaterialIcons.Outlined.History, "Recently Added", true, null, [], Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.Full),
        new(SettingsSection.Libraries, "server", "libraries", AppMaterialIcons.Outlined.Folder, "Libraries", true, null, [], Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.SummaryOnly),
        new(SettingsSection.Providers, "server", "metadata", AppMaterialIcons.Outlined.Hub, "Metadata Providers", true, null, [], Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.SummaryOnly),
        new(SettingsSection.Review, "server", "review", AppMaterialIcons.Outlined.RateReview, "Needs Review", true, "review", [], "mixed", Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.DesktopOnly),
        new(SettingsSection.Network, "server", "network", AppMaterialIcons.Outlined.WifiTethering, "Network & Remote Access", true, null, [], "json+runtime", Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.SummaryOnly),
        new(SettingsSection.Delivery, "server", "delivery", AppMaterialIcons.Outlined.Stream, "Streaming", true, null, [], Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.SummaryOnly),
        new(SettingsSection.Access, "server", "access", AppMaterialIcons.Outlined.AdminPanelSettings, "Users & Households", true, null, [], Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.Full),

        new(SettingsSection.LocalAi, "platform", "ai", AppMaterialIcons.Outlined.Memory, "Local AI", true, null, [], Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.SummaryOnly),
        new(SettingsSection.Plugins, "platform", "plugins", AppMaterialIcons.Outlined.Extension, "Plugins", true, null, [], "sqlite", Status: SettingsStatusKind.Partial, MobileAvailability: SettingsMobileAvailability.SummaryOnly),
        new(SettingsSection.Integrations, "platform", "integrations", AppMaterialIcons.Outlined.Link, "Integrations", true, null, [], "sqlite", Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.Full),

        new(SettingsSection.AdminOverview, "system", "system", AppMaterialIcons.Outlined.DesktopWindows, "System", true, null, [], "json+sqlite", Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.SummaryOnly),
        new(SettingsSection.Tools, "system", "tools", AppMaterialIcons.Outlined.Build, "Configuration", true, null, [], "runtime", Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.Full),
        new(SettingsSection.Notifications, "system", "notifications", AppMaterialIcons.Outlined.Mail, "Notifications", true, null, [], "sqlite", Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.Full),
        new(SettingsSection.Server, "system", "backup-recovery", AppMaterialIcons.Outlined.Backup, "Backups", true, null, [], Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.SummaryOnly),

        // Developer Tools opens on the Provider Tester, so that page owns the "developer" address and its old "provider-tester" address.
        // Enrichment Tester and Development data are its other tabs; they are offered only when the running Engine is a development
        // build (see DevelopmentToolsState). The Enrichment Tester is temporary: it is removed at beta.
        new(SettingsSection.ProviderTester, "developer", "developer", AppMaterialIcons.Outlined.Construction, "Provider Tester", true, null, ["provider-tester"], "internal", Status: SettingsStatusKind.Experimental, MobileAvailability: SettingsMobileAvailability.DesktopOnly),
        new(SettingsSection.EnrichmentTester, "developer", "enrichment-tester", AppMaterialIcons.Outlined.Science, "Enrichment Tester", true, null, ["tester"], "internal", Status: SettingsStatusKind.Experimental, MobileAvailability: SettingsMobileAvailability.DesktopOnly),
        new(SettingsSection.DevHarness, "developer", "development-data", AppMaterialIcons.Outlined.Inventory2, "Development data", true, null, ["dev-harness", "harness", "ingestion-harness", "test-harness"], "internal", Status: SettingsStatusKind.Partial, MobileAvailability: SettingsMobileAvailability.DesktopOnly),
    ];

    /// <summary>
    /// The one Settings menu: groups of flat pages, no expanding rows. A page with several related areas carries one row of
    /// route-linked tabs (<see cref="SettingsPageTab"/>); a page with a single purpose has none. Recently Added and Needs Review
    /// stay out of the menu and open beneath Library &amp; Ingestion (see <see cref="OffMenuParents"/>). The desktop menu, the phone
    /// page selector, the tab strip and the breadcrumb all read this.
    /// </summary>
    public static readonly SettingsMenuGroup[] MenuGroups =
    [
        new("personal", "Personal", AppMaterialIcons.Outlined.Person,
        [
            Page("Profile", AppMaterialIcons.Outlined.AccountCircle, SettingsSection.Overview,
            [
                Tab("Overview", AppMaterialIcons.Outlined.AccountCircle, SettingsSection.Overview),
                Tab("Activity", AppMaterialIcons.Outlined.History, SettingsSection.Overview, ActivitySubsection),
                Tab("Taste", AppMaterialIcons.Outlined.Category, SettingsSection.Overview, TasteSubsection),
            ]),
            Page("Manage Profiles", AppMaterialIcons.Outlined.Group, SettingsSection.Profiles, also:
            [
                SettingsSection.Preferences, SettingsSection.Playback, SettingsSection.Listening, SettingsSection.Reading, SettingsSection.Home,
            ]),
            Page("Security & Devices", AppMaterialIcons.Outlined.Lock, SettingsSection.Security,
            [
                Tab("Sign-in", AppMaterialIcons.Outlined.Key, SettingsSection.Security),
                Tab("Devices & Sessions", AppMaterialIcons.Outlined.Devices, SettingsSection.Security, DevicesSubsection),
            ]),
        ]),
        new("server", "Server", AppMaterialIcons.Outlined.Dns,
        [
            Page("Library & Ingestion", AppMaterialIcons.Outlined.VideoLibrary, SettingsSection.Ingestion,
            [
                Tab("Overview", AppMaterialIcons.Outlined.Dashboard, SettingsSection.Ingestion),
                Tab("Libraries", AppMaterialIcons.Outlined.Folder, SettingsSection.Libraries),
                Tab("Ingestion Flow", AppMaterialIcons.Outlined.AccountTree, SettingsSection.Providers, IngestionFlowSubsection),
            ], also: [SettingsSection.RecentlyAdded, SettingsSection.Review]),
            Page("Network & Remote Access", AppMaterialIcons.Outlined.WifiTethering, SettingsSection.Network,
            [
                Tab("Overview", AppMaterialIcons.Outlined.Dashboard, SettingsSection.Network, "overview"),
                Tab("Local Network", AppMaterialIcons.Outlined.Lan, SettingsSection.Network, "local"),
                Tab("Remote Access", AppMaterialIcons.Outlined.Public, SettingsSection.Network, "remote"),
                Tab("Advanced", AppMaterialIcons.Outlined.Tune, SettingsSection.Network, "advanced"),
            ]),
            Page("Streaming", AppMaterialIcons.Outlined.Stream, SettingsSection.Delivery,
            [
                Tab("Limits & Quality", AppMaterialIcons.Outlined.Speed, SettingsSection.Delivery),
                Tab("Hardware Acceleration", AppMaterialIcons.Outlined.Memory, SettingsSection.Delivery, "hardware"),
                Tab("Optimized Copies", AppMaterialIcons.Outlined.Storage, SettingsSection.Delivery, "optimized-copies"),
                Tab("Diagnostics", AppMaterialIcons.Outlined.MonitorHeart, SettingsSection.Delivery, "diagnostics"),
            ]),
            Page("Users & Households", AppMaterialIcons.Outlined.AdminPanelSettings, SettingsSection.Access,
            [
                Tab("Users", AppMaterialIcons.Outlined.ManageAccounts, SettingsSection.Access, "users"),
                Tab("Sign-in Methods", AppMaterialIcons.Outlined.Security, SettingsSection.Access, "authentication"),
                Tab("Households", AppMaterialIcons.Outlined.Home, SettingsSection.Access, HouseholdsSubsection),
            ]),
        ]),
        new("platform", "Platform", AppMaterialIcons.Outlined.Extension,
        [
            Page("Local AI", AppMaterialIcons.Outlined.Memory, SettingsSection.LocalAi,
            [
                Tab("Status", AppMaterialIcons.Outlined.Memory, SettingsSection.LocalAi, "status"),
                Tab("Models & Hardware", AppMaterialIcons.Outlined.Storage, SettingsSection.LocalAi, "models"),
                Tab("Budget", AppMaterialIcons.Outlined.Speed, SettingsSection.LocalAi, "budget"),
                Tab("Usage", AppMaterialIcons.Outlined.Assessment, SettingsSection.LocalAi, "usage"),
            ]),
            Page("Plugins", AppMaterialIcons.Outlined.Extension, SettingsSection.Plugins),
            Page("Integrations", AppMaterialIcons.Outlined.Link, SettingsSection.Integrations),
        ]),
        new("system", "System", AppMaterialIcons.Outlined.DesktopWindows,
        [
            Page("System", AppMaterialIcons.Outlined.DesktopWindows, SettingsSection.AdminOverview,
            [
                Tab("Overview", AppMaterialIcons.Outlined.Dashboard, SettingsSection.AdminOverview),
                Tab("Scheduled Tasks", AppMaterialIcons.Outlined.Schedule, SettingsSection.AdminOverview, ScheduledTasksSubsection),
                Tab("Logs", AppMaterialIcons.Outlined.List, SettingsSection.AdminOverview, LogsSubsection),
                Tab("Backups", AppMaterialIcons.Outlined.Backup, SettingsSection.Server),
                Tab("Configuration", AppMaterialIcons.Outlined.Build, SettingsSection.Tools),
            ], also: [SettingsSection.Notifications]),
        ]),
        new("developer", "Developer", AppMaterialIcons.Outlined.DataObject,
        [
            Page("Developer Tools", AppMaterialIcons.Outlined.Construction, SettingsSection.ProviderTester,
            [
                Tab("Provider Tester", AppMaterialIcons.Outlined.Biotech, SettingsSection.ProviderTester),
                Tab("Enrichment Tester", AppMaterialIcons.Outlined.Science, SettingsSection.EnrichmentTester),
                Tab("Development data", AppMaterialIcons.Outlined.Inventory2, SettingsSection.DevHarness),
            ]),
        ]),
    ];

    private static SettingsMenuNode Page(
        string label,
        string icon,
        SettingsSection section,
        IReadOnlyList<SettingsPageTab>? tabs = null,
        IReadOnlyList<SettingsSection>? also = null) =>
        new(label, icon, section, tabs, also);

    private static SettingsPageTab Tab(string label, string icon, SettingsSection section, string? subsection = null) =>
        new(label, icon, section, subsection);

    public static readonly IReadOnlyDictionary<SettingsSection, IReadOnlyList<SettingsSubsectionDef>> Subsections =
        new Dictionary<SettingsSection, IReadOnlyList<SettingsSubsectionDef>>
        {
            [SettingsSection.Overview] =
            [
                new(AppsSubsection, "Apps", AppMaterialIcons.Outlined.Smartphone),
                new(ActivitySubsection, "Activity", AppMaterialIcons.Outlined.History),
                new(TasteSubsection, "Taste", AppMaterialIcons.Outlined.Category),
            ],
            [SettingsSection.Profiles] = [],
            [SettingsSection.Preferences] = [],
            [SettingsSection.Playback] = [],
            [SettingsSection.Listening] = [],
            [SettingsSection.Reading] = [],
            [SettingsSection.Home] = [],
            [SettingsSection.Security] =
            [
                new(DevicesSubsection, "Devices & Sessions", AppMaterialIcons.Outlined.Devices),
            ],
            [SettingsSection.AdminOverview] =
            [
                new(ScheduledTasksSubsection, "Scheduled Tasks", AppMaterialIcons.Outlined.Schedule),
                new(LogsSubsection, "Logs", AppMaterialIcons.Outlined.List),
            ],
            [SettingsSection.Libraries] = [],
            [SettingsSection.Ingestion] = [],
            [SettingsSection.RecentlyAdded] = [],
            [SettingsSection.DevHarness] = [],
            [SettingsSection.Providers] =
            [
                new("providers", "Providers", AppMaterialIcons.Outlined.Dns),
                new(IngestionFlowSubsection, "Ingestion Flow", AppMaterialIcons.Outlined.AccountTree),
            ],
            [SettingsSection.LocalAi] =
            [
                new("status", "Status", AppMaterialIcons.Outlined.Memory),
                new("models", "Models & Hardware", AppMaterialIcons.Outlined.Storage),
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
                new("authentication", "Sign-in Methods", AppMaterialIcons.Outlined.Security),
                new(HouseholdsSubsection, "Households", AppMaterialIcons.Outlined.Home),
            ],
            [SettingsSection.Integrations] = [],
            [SettingsSection.Tools] = [],
            [SettingsSection.Notifications] = [],
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
        SettingsSection.Home,
        SettingsSection.Security,
        SettingsSection.AdminOverview,
        SettingsSection.Libraries,
        SettingsSection.Ingestion,
        SettingsSection.RecentlyAdded,
        SettingsSection.Review,
        SettingsSection.Delivery,
        SettingsSection.Tools,
        SettingsSection.Notifications,
        SettingsSection.Access,
        SettingsSection.Integrations,
        SettingsSection.Server,
        SettingsSection.LocalAi,
        SettingsSection.Plugins,
        SettingsSection.ProviderTester,
        SettingsSection.EnrichmentTester,
    ];

    public static IEnumerable<SettingsGroupDef> FilteredGroups(bool administrationAllowed) =>
        AllGroups.Where(group => !group.AdminOnly || administrationAllowed);

    /// <summary>
    /// The menu a person may see: every page they may open, in order, with groups that have no visible page removed. A page keeps
    /// only the tabs they may open, and a page left with none of its tabs is dropped. A Kids profile sees Profile and nothing else.
    /// <paramref name="extraFilter"/> and <paramref name="extraTabFilter"/> let the caller drop pages and tabs for reasons that are
    /// not about rights (a feature switched off, a page not offered on a phone).
    /// </summary>
    public static IReadOnlyList<SettingsMenuGroup> FilteredMenuGroups(
        bool administrationAllowed,
        Func<SettingsMenuNode, bool>? extraFilter = null,
        Func<SettingsPageTab, bool>? extraTabFilter = null,
        bool restricted = false)
    {
        return MenuGroups
            .Select(group => group with { Nodes = FilterNodes(group.Nodes, administrationAllowed, restricted, extraFilter, extraTabFilter) })
            .Where(group => group.Nodes.Count > 0)
            .ToList();
    }

    private static IReadOnlyList<SettingsMenuNode> FilterNodes(
        IReadOnlyList<SettingsMenuNode> nodes,
        bool administrationAllowed,
        bool restricted,
        Func<SettingsMenuNode, bool>? extraFilter,
        Func<SettingsPageTab, bool>? extraTabFilter)
    {
        var kept = new List<SettingsMenuNode>();
        foreach (var node in nodes)
        {
            if (!IsVisible(node.Section, administrationAllowed, restricted)
                || extraFilter is not null && !extraFilter(node))
            {
                continue;
            }

            var tabs = node.Tabs
                .Where(tab => IsVisible(tab.Section, administrationAllowed, restricted)
                              && (extraTabFilter is null || extraTabFilter(tab)))
                .ToList();
            if (node.Tabs.Count > 0 && tabs.Count == 0)
            {
                continue;
            }

            kept.Add(node with { PageTabs = node.Tabs.Count == 0 ? null : tabs });
        }

        return kept;
    }

    /// <summary>
    /// Finds where a page sits in the menu: the row that owns its section, and the tab it is on. A sub-page the tabs do not
    /// name (a landing view, a profile or provider detail) falls to the section's own tab. A page that opens beneath a row without
    /// being a tab (Recently Added and Needs Review under Library &amp; Ingestion, Apps under Profile) has no tab and names itself
    /// as the <see cref="SettingsMenuLocation.Leaf"/>, so its breadcrumb reads Server, Library &amp; Ingestion, Needs Review.
    /// Returns <see langword="null"/> for a page the menu does not place.
    /// </summary>
    public static SettingsMenuLocation? FindMenuLocation(SettingsSection section, string? subsection = null)
    {
        foreach (var group in MenuGroups)
        {
            foreach (var node in group.Nodes)
            {
                if (!node.Sections.Contains(section))
                {
                    continue;
                }

                var requested = string.IsNullOrWhiteSpace(subsection) ? null : NormalizeSlug(subsection);
                if (requested is not null && OffMenuSubsections.Contains((section, requested))
                    && ResolveSubsection(section, requested) is { } offMenu)
                {
                    return new SettingsMenuLocation(group, node, null, offMenu.Label);
                }

                var tab = node.TabFor(section, subsection);
                if (tab is not null)
                {
                    return new SettingsMenuLocation(group, node, tab);
                }

                var item = GetItem(section);
                return new SettingsMenuLocation(group, node, null, string.Equals(item.Label, node.Label, StringComparison.Ordinal) ? null : item.Label);
            }
        }

        return null;
    }

    /// <summary>
    /// Pages that have their own route but no tab, and the page they open from. They keep their place in the breadcrumb; the
    /// tab strip does not list them.
    /// </summary>
    public static readonly IReadOnlyDictionary<SettingsSection, SettingsSection> OffMenuParents =
        new Dictionary<SettingsSection, SettingsSection>
        {
            [SettingsSection.RecentlyAdded] = SettingsSection.Ingestion,
            [SettingsSection.Review] = SettingsSection.Ingestion,
        };

    /// <summary>
    /// Sub-pages that have their own route but no tab, because a link on their page's main tab opens them. They sit beneath
    /// that tab in the breadcrumb (Personal, Profile, Apps). Slugs are in their normalised form.
    /// </summary>
    public static readonly IReadOnlySet<(SettingsSection Section, string Subsection)> OffMenuSubsections =
        new HashSet<(SettingsSection, string)>
        {
            (SettingsSection.Overview, AppsSubsection),
        };

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

        var normalized = subsection?.Trim().ToLowerInvariant() ?? string.Empty;

        // Libraries has levels of its own (group and type pages); a phone reads them, it cannot change folders there.
        if (section == SettingsSection.Libraries)
        {
            return string.IsNullOrWhiteSpace(normalized) ? string.IsNullOrWhiteSpace(detail) : IsLibraryRouteSegment(normalized);
        }

        if (!string.IsNullOrWhiteSpace(detail))
        {
            return false;
        }

        return section switch
        {
            SettingsSection.Profiles => string.IsNullOrWhiteSpace(normalized) || Guid.TryParse(normalized, out _),
            SettingsSection.Providers => normalized is "" or "providers",
            SettingsSection.Network => normalized is "" or "overview",
            SettingsSection.Access => normalized is "" or "users" or "authentication" or HouseholdsSubsection,
            // The Logs page (technical log, retention) is a desktop and tablet page; a phone keeps the System summary.
            SettingsSection.AdminOverview or SettingsSection.Delivery or SettingsSection.LocalAi or SettingsSection.Plugins =>
                string.IsNullOrWhiteSpace(normalized),
            _ => true,
        };
    }

    /// <summary>
    /// The first address segment under /settings/libraries that names a level of the Libraries screen: a library's own Id (its page),
    /// the Import folder, View (its kinds), or an older group address (watch, listen, read, vault, "other") that redirects.
    /// </summary>
    public static bool IsLibraryRouteSegment(string? value) =>
        value?.Trim().ToLowerInvariant() is "watch" or "listen" or "read" or "view" or "vault" or "import" or "other"
        || Guid.TryParse(value?.Trim(), out _);

    public static bool IsMobileSubsectionAvailable(SettingsSection section, string subsection) =>
        IsMobileRouteAvailable(section, subsection);

    public static SettingsGroupDef GetGroup(SettingsSection section) => _groupsByKey[GetItem(section).GroupKey];

    public static SettingsSection GetDefaultSection(string groupKey) => _groupsByKey[groupKey].DefaultSection;

    /// <summary>
    /// Whether this person may open the page. Profile is everyone's own page, a Kids profile's included. A Kids profile
    /// (<paramref name="restricted"/>) may open Profile and nothing else. Manage Profiles, Preferences and Security &amp; Devices are
    /// everyone's own to look after; the Engine still limits what a member can change to their own profile. Everything else in
    /// Settings is for server administrators.
    /// </summary>
    public static bool IsVisible(SettingsSection section, bool administrationAllowed, bool restricted = false)
    {
        var item = GetItem(section);
        if ((_scopedProductionMode.Value ?? _productionMode)
            && (item.Status != SettingsStatusKind.Live || !MeetsLaunchContract(section)))
        {
            return false;
        }

        if (restricted)
        {
            return section == SettingsSection.Overview;
        }

        return administrationAllowed || !item.AdminOnly;
    }

    public static SettingsSection FirstVisibleSection(bool administrationAllowed, bool restricted = false) =>
        AllItems.First(item => IsVisible(item.Value, administrationAllowed, restricted)).Value;

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

    public static SettingsRouteResolution ResolveRoute(string? segment, bool administrationAllowed, bool restricted = false)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            // The bare /settings address opens the first page this person may see (Profile).
            var landing = FirstVisibleSection(administrationAllowed, restricted);
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
            if (IsVisible(canonicalItem.Value, administrationAllowed, restricted))
            {
                return new SettingsRouteResolution(
                    canonicalItem.Value,
                    RouteFor(canonicalItem.Value),
                    IsCanonicalRoute: true,
                    IsKnownRoute: true,
                    RequestedSectionAllowed: true);
            }

            var fallback = FirstVisibleSection(administrationAllowed, restricted);
            return new SettingsRouteResolution(
                fallback,
                RouteFor(fallback),
                IsCanonicalRoute: false,
                IsKnownRoute: true,
                RequestedSectionAllowed: false);
        }

        if (_itemsByAlias.TryGetValue(normalized, out var aliasedItem))
        {
            if (IsVisible(aliasedItem.Value, administrationAllowed, restricted))
            {
                return new SettingsRouteResolution(
                    aliasedItem.Value,
                    RouteFor(aliasedItem.Value),
                    IsCanonicalRoute: false,
                    IsKnownRoute: true,
                    RequestedSectionAllowed: true);
            }

            var fallback = FirstVisibleSection(administrationAllowed, restricted);
            return new SettingsRouteResolution(
                fallback,
                RouteFor(fallback),
                IsCanonicalRoute: false,
                IsKnownRoute: true,
                RequestedSectionAllowed: false);
        }

        return new SettingsRouteResolution(
            FirstVisibleSection(administrationAllowed, restricted),
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

    /// <summary>A route segment reduced to lower-case letters and digits, the form every slug and alias is compared in.</summary>
    public static string NormalizeSlug(string value) => NormalizeKey(value);

    private static string NormalizeKey(string value)
    {
        var chars = value.Where(char.IsLetterOrDigit).ToArray();
        return new string(chars).ToLowerInvariant();
    }

    /// <summary>The address every page of a section sits under (<c>/settings/network</c>), without a tab.</summary>
    public static string BaseRouteFor(SettingsSection section) => SectionRouteFor(section);

    private static string SectionRouteFor(SettingsSection section)
    {
        var item = GetItem(section);
        return $"/settings/{item.Slug}";
    }

}
