using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Models.ViewDTOs;

/// <summary>
/// Canonical settings destinations rendered by the shared settings shell.
/// </summary>
public enum SettingsSection
{
    Overview,
    Account,
    Playback,
    Privacy,

    AdminOverview,
    Libraries,
    Ingestion,
    RecentlyAdded,
    DevHarness,
    Providers,
    LocalAi,
    Plugins,
    Delivery,
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

/// <summary>A grouped sidebar node with expandable child settings routes.</summary>
public sealed record SettingsTreeGroupDef(
    string Key,
    string Label,
    string Icon,
    bool AdminOnly,
    bool Expandable,
    SettingsSection DefaultSection,
    IReadOnlyList<SettingsSection> Sections,
    string? ParentKey = null,
    SettingsSection? InsertAfter = null);

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

    private static readonly IReadOnlyDictionary<SettingsSection, LaunchFeatureEvidence> LaunchContracts =
        new Dictionary<SettingsSection, LaunchFeatureEvidence>
        {
            [SettingsSection.Overview] = Complete(),
            [SettingsSection.Account] = Complete(),
            [SettingsSection.Playback] = Complete(),
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
            [SettingsSection.Access] = Complete(),
        };

    private static LaunchFeatureEvidence Complete() => new(true, true, true, true, true);

    public static void ConfigureEnvironment(bool productionMode) => _productionMode = productionMode;

    public static bool MeetsLaunchContract(SettingsSection section) =>
        LaunchContracts.TryGetValue(section, out var evidence) && evidence.IsComplete;

    public static readonly SettingsGroupDef[] AllGroups =
    [
        new("personal", "Account", AppMaterialIcons.Outlined.Person, false, SettingsSection.Overview),
        new("administration", "Administration", AppMaterialIcons.Outlined.AdminPanelSettings, true, SettingsSection.AdminOverview),
        new("advanced", "Advanced", AppMaterialIcons.Outlined.Tune, true, SettingsSection.LocalAi),
    ];

    public static readonly SettingsItemDef[] AllItems =
    [
        new(SettingsSection.Overview, "personal", "profile", AppMaterialIcons.Outlined.Person, "Profile", false, null, [], "sqlite", Status: SettingsStatusKind.Live),
        new(SettingsSection.Account, "personal", "account", AppMaterialIcons.Outlined.ManageAccounts, "Security", false, null, [], "sqlite", Status: SettingsStatusKind.Live),
        new(SettingsSection.Playback, "personal", "playback", AppMaterialIcons.Outlined.PlayCircleOutline, "Playback & Reading", false, null, [], "sqlite", Status: SettingsStatusKind.Live),
        new(SettingsSection.Privacy, "personal", "privacy", AppMaterialIcons.Outlined.Lock, "Privacy & Data", false, null, [], "unavailable", Placeholder: true),

        new(SettingsSection.AdminOverview, "administration", "system", AppMaterialIcons.Outlined.Dashboard, "System Overview", true, null, [], "json+sqlite", Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.SummaryOnly),
        new(SettingsSection.Libraries, "administration", "libraries", AppMaterialIcons.Outlined.VideoLibrary, "Libraries", true, null, [], Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.SummaryOnly),
        new(SettingsSection.Ingestion, "administration", "ingestion", AppMaterialIcons.Outlined.Sync, "Live Ingestion", true, null, [], Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.SummaryOnly),
        new(SettingsSection.RecentlyAdded, "administration", "recently-added", AppMaterialIcons.Outlined.History, "Recently Added", true, null, [], Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.Full),
        new(SettingsSection.Providers, "administration", "metadata", AppMaterialIcons.Outlined.Storage, "Metadata Providers", true, null, [], Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.SummaryOnly),
        new(SettingsSection.Review, "administration", "review", AppMaterialIcons.Outlined.RateReview, "Needs Review", true, "review", [], "mixed", Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.DesktopOnly),
        new(SettingsSection.Network, "administration", "network", AppMaterialIcons.Outlined.WifiTethering, "Network & Remote Access", true, null, [], "json+runtime", Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.SummaryOnly),
        new(SettingsSection.Delivery, "administration", "delivery", AppMaterialIcons.Outlined.VideoSettings, "Playback & Delivery", true, null, [], Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.SummaryOnly),
        new(SettingsSection.Access, "administration", "access", AppMaterialIcons.Outlined.Group, "Users & Access", true, null, [], Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.Full),
        new(SettingsSection.Server, "administration", "backup-recovery", AppMaterialIcons.Outlined.Backup, "Backup & Recovery", true, null, [], Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.SummaryOnly),

        new(SettingsSection.LocalAi, "advanced", "ai", AppMaterialIcons.Outlined.Memory, "Local AI", true, null, [], Status: SettingsStatusKind.Live, MobileAvailability: SettingsMobileAvailability.SummaryOnly),
        new(SettingsSection.Plugins, "advanced", "plugins", AppMaterialIcons.Outlined.Extension, "Plugins", true, null, [], "sqlite", Status: SettingsStatusKind.Partial, MobileAvailability: SettingsMobileAvailability.SummaryOnly),
        new(SettingsSection.DevHarness, "advanced", "developer", AppMaterialIcons.Outlined.Construction, "Developer Tools", true, null, ["dev-harness", "harness", "ingestion-harness", "test-harness"], "internal", Status: SettingsStatusKind.Partial, MobileAvailability: SettingsMobileAvailability.DesktopOnly),
        new(SettingsSection.ProviderTester, "advanced", "provider-tester", AppMaterialIcons.Outlined.Biotech, "Provider Tester", true, null, [], "internal", Status: SettingsStatusKind.Experimental, MobileAvailability: SettingsMobileAvailability.DesktopOnly),
        new(SettingsSection.EnrichmentTester, "advanced", "enrichment-tester", AppMaterialIcons.Outlined.Science, "Enrichment Tester", true, null, ["tester"], "internal", Status: SettingsStatusKind.Experimental, MobileAvailability: SettingsMobileAvailability.DesktopOnly),
    ];

    public static readonly SettingsTreeGroupDef[] TreeGroups =
    [
        new("personal", "Personal", AppMaterialIcons.Outlined.Person, false, false, SettingsSection.Overview,
            [SettingsSection.Overview, SettingsSection.Account, SettingsSection.Playback, SettingsSection.Privacy]),
        new("administration", "Administration", AppMaterialIcons.Outlined.AdminPanelSettings, true, false, SettingsSection.AdminOverview,
            [
                SettingsSection.AdminOverview,
                SettingsSection.Network,
                SettingsSection.Delivery,
                SettingsSection.Access,
                SettingsSection.Server,
            ]),
        new("library-ingestion", "Library & Ingestion", AppMaterialIcons.Outlined.VideoLibrary, true, true, SettingsSection.Ingestion,
            [
                SettingsSection.Ingestion,
                SettingsSection.RecentlyAdded,
                SettingsSection.Libraries,
                SettingsSection.Providers,
            ], ParentKey: "administration", InsertAfter: SettingsSection.AdminOverview),
        new("advanced", "Advanced", AppMaterialIcons.Outlined.Tune, true, false, SettingsSection.LocalAi,
            [SettingsSection.LocalAi, SettingsSection.Plugins, SettingsSection.DevHarness]),
    ];

    public static readonly IReadOnlyDictionary<SettingsSection, IReadOnlyList<SettingsSubsectionDef>> Subsections =
        new Dictionary<SettingsSection, IReadOnlyList<SettingsSubsectionDef>>
        {
            [SettingsSection.Overview] = [],
            [SettingsSection.Account] = [],
            [SettingsSection.Playback] = [],
            [SettingsSection.Privacy] =
            [
                new("history", "Personal History", AppMaterialIcons.Outlined.History),
                new("tracking", "Tracking", AppMaterialIcons.Outlined.Timeline),
                new("personalization", "Personalization", AppMaterialIcons.Outlined.AutoAwesome),
                new("export-reset", "Export & Reset", AppMaterialIcons.Outlined.SettingsBackupRestore),
            ],
            [SettingsSection.AdminOverview] = [],
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
                new("models", "Models & Runtime", AppMaterialIcons.Outlined.Storage),
                new("vocabulary", "Vocabulary", AppMaterialIcons.Outlined.Spellcheck),
                new("automation", "Automation", AppMaterialIcons.Outlined.Schedule),
            ],
            [SettingsSection.Plugins] =
            [
                new("jobs-health", "Health & Jobs", AppMaterialIcons.Outlined.HealthAndSafety),
                new("catalog", "Approved Catalog", AppMaterialIcons.Outlined.Verified),
                new("capabilities", "Capabilities", AppMaterialIcons.Outlined.CheckCircleOutline),
                new("danger", "Danger Zone", AppMaterialIcons.Outlined.Delete),
            ],
            [SettingsSection.Delivery] =
            [
                new("scheduling", "Scheduling", AppMaterialIcons.Outlined.Schedule),
                new("storage", "Variant Storage", AppMaterialIcons.Outlined.Storage),
                new("active-jobs", "Active Jobs", AppMaterialIcons.Outlined.PendingActions),
                new("diagnostics", "Diagnostics", AppMaterialIcons.Outlined.MonitorHeart),
            ],
            [SettingsSection.Network] =
            [
                new("overview", "Overview", AppMaterialIcons.Outlined.Dashboard),
                new("local", "Local Network", AppMaterialIcons.Outlined.Lan),
                new("remote", "Remote Access", AppMaterialIcons.Outlined.Public),
                new("streaming", "Streaming", AppMaterialIcons.Outlined.Stream),
                new("advanced", "Advanced", AppMaterialIcons.Outlined.Tune),
            ],
            [SettingsSection.Access] =
            [
                new("users", "Users", AppMaterialIcons.Outlined.ManageAccounts),
                new("applications", "Applications", AppMaterialIcons.Outlined.Apps),
                new("authentication", "Authentication", AppMaterialIcons.Outlined.AdminPanelSettings),
            ],
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
        SettingsSection.Playback,
        SettingsSection.AdminOverview,
        SettingsSection.Libraries,
        SettingsSection.Ingestion,
        SettingsSection.RecentlyAdded,
        SettingsSection.Review,
        SettingsSection.Delivery,
        SettingsSection.Access,
        SettingsSection.Server,
        SettingsSection.LocalAi,
        SettingsSection.Plugins,
        SettingsSection.ProviderTester,
        SettingsSection.EnrichmentTester,
    ];

    public static IEnumerable<SettingsGroupDef> FilteredGroups(bool administrationAllowed) =>
        AllGroups.Where(group => !group.AdminOnly || administrationAllowed);

    public static IEnumerable<SettingsTreeGroupDef> FilteredTreeGroups(bool administrationAllowed)
    {
        var hasAdmin = administrationAllowed;
        return TreeGroups
            .Where(group => string.IsNullOrWhiteSpace(group.ParentKey))
            .Where(group => !group.AdminOnly || hasAdmin)
            .Where(group => group.Sections.Any(section => IsVisible(section, administrationAllowed))
                            || FilteredChildTreeGroups(group, administrationAllowed).Any());
    }

    public static IEnumerable<SettingsTreeGroupDef> FilteredChildTreeGroups(SettingsTreeGroupDef parent, bool administrationAllowed)
    {
        var hasAdmin = administrationAllowed;
        return TreeGroups
            .Where(group => string.Equals(group.ParentKey, parent.Key, StringComparison.OrdinalIgnoreCase))
            .Where(group => !group.AdminOnly || hasAdmin)
            .Where(group => group.Sections.Any(section => IsVisible(section, administrationAllowed)));
    }

    public static IReadOnlyList<SettingsItemDef> FilteredTreeItems(SettingsTreeGroupDef group, bool administrationAllowed) =>
        group.Sections
            .Select(GetItem)
            .Where(item => IsVisible(item.Value, administrationAllowed))
            .ToList();

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
            SettingsSection.Libraries => string.IsNullOrWhiteSpace(normalized) || normalized == "view" || Guid.TryParse(normalized, out _),
            SettingsSection.Providers => normalized is "" or "providers",
            SettingsSection.Network => normalized is "" or "overview",
            SettingsSection.Access => normalized is "" or "users" or "applications" or "authentication",
            SettingsSection.Delivery or SettingsSection.LocalAi or SettingsSection.Plugins =>
                string.IsNullOrWhiteSpace(normalized),
            _ => true,
        };
    }

    public static bool IsMobileSubsectionAvailable(SettingsSection section, string subsection) =>
        IsMobileRouteAvailable(section, subsection);

    public static SettingsGroupDef GetGroup(SettingsSection section) => _groupsByKey[GetItem(section).GroupKey];

    public static SettingsSection GetDefaultSection(string groupKey) => _groupsByKey[groupKey].DefaultSection;

    public static bool IsVisible(SettingsSection section, bool administrationAllowed)
    {
        var item = GetItem(section);
        if (_productionMode
            && (item.Status != SettingsStatusKind.Live || !MeetsLaunchContract(section)))
        {
            return false;
        }

        if (section == SettingsSection.Privacy)
        {
            return false;
        }

        return administrationAllowed || section is SettingsSection.Overview
            or SettingsSection.Account or SettingsSection.Playback;
    }

    public static SettingsSection FirstVisibleSection(bool administrationAllowed) =>
        AllItems.First(item => IsVisible(item.Value, administrationAllowed)).Value;

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
            (SettingsSection.LocalAi, "runtime") => "models",
            (SettingsSection.LocalAi, "schedule") => "automation",
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

    public static SettingsRouteResolution ResolveRoute(string? segment, bool administrationAllowed)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return new SettingsRouteResolution(
                SettingsSection.Overview,
                RouteFor(SettingsSection.Overview),
                IsCanonicalRoute: false,
                IsKnownRoute: true,
                RequestedSectionAllowed: true);
        }

        var normalized = NormalizeKey(segment);

        if (_itemsBySlug.TryGetValue(normalized, out var canonicalItem))
        {
            if (IsVisible(canonicalItem.Value, administrationAllowed))
            {
                return new SettingsRouteResolution(
                    canonicalItem.Value,
                    RouteFor(canonicalItem.Value),
                    IsCanonicalRoute: true,
                    IsKnownRoute: true,
                    RequestedSectionAllowed: true);
            }

            var fallback = FirstVisibleSection(administrationAllowed);
            return new SettingsRouteResolution(
                fallback,
                RouteFor(fallback),
                IsCanonicalRoute: false,
                IsKnownRoute: true,
                RequestedSectionAllowed: false);
        }

        if (_itemsByAlias.TryGetValue(normalized, out var aliasedItem))
        {
            if (IsVisible(aliasedItem.Value, administrationAllowed))
            {
                return new SettingsRouteResolution(
                    aliasedItem.Value,
                    RouteFor(aliasedItem.Value),
                    IsCanonicalRoute: false,
                    IsKnownRoute: true,
                    RequestedSectionAllowed: true);
            }

            var fallback = FirstVisibleSection(administrationAllowed);
            return new SettingsRouteResolution(
                fallback,
                RouteFor(fallback),
                IsCanonicalRoute: false,
                IsKnownRoute: true,
                RequestedSectionAllowed: false);
        }

        return new SettingsRouteResolution(
            FirstVisibleSection(administrationAllowed),
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
