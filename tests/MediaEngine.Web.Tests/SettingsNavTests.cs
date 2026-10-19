using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Tests;

public sealed class SettingsNavTests
{
    [Theory]
    [InlineData("users")]
    [InlineData("applications")]
    [InlineData("authentication")]
    public void CanonicalAccessSections_AreAvailableOnMobile(string subsection)
    {
        Assert.True(SettingsNav.IsMobileRouteAvailable(SettingsSection.Access, subsection));
        Assert.False(SettingsNav.IsMobileRouteAvailable(SettingsSection.Access, subsection, "unknown-detail"));
    }

    [Fact]
    public void SettingsPage_UsesTheMediaLaneShellInsteadOfAParallelSidebar()
    {
        var settingsSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Components/Pages/Settings.razor"));
        var mediaShellSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Components/MediaHub/MediaSectionShell.razor"));

        Assert.Contains("<MediaSectionShell Title=\"Settings\"", settingsSource, StringComparison.Ordinal);
        Assert.Contains("AccordionNavigation=\"true\"", settingsSource, StringComparison.Ordinal);
        Assert.Contains("settings-mobile-navigation", settingsSource, StringComparison.Ordinal);
        Assert.Contains("<IngestionTasksTab", settingsSource, StringComparison.Ordinal);
        Assert.Contains("<RecentlyAddedPageContent", settingsSource, StringComparison.Ordinal);
        Assert.DoesNotContain("<SettingsSubsectionNav", settingsSource, StringComparison.Ordinal);
        Assert.Contains("<SettingsPageFrame", settingsSource, StringComparison.Ordinal);
        Assert.Contains("<SidebarPageHeader", File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Components/Settings/Frame/SettingsPageFrame.razor")), StringComparison.Ordinal);
        Assert.DoesNotContain("<SidebarPageShell", settingsSource, StringComparison.Ordinal);
        Assert.DoesNotContain("<SidebarNavGroup", settingsSource, StringComparison.Ordinal);
        Assert.Contains("media-section-shell__rail-item--child", mediaShellSource, StringComparison.Ordinal);
        Assert.Contains("FixedColumnTree=\"true\"", settingsSource, StringComparison.Ordinal);
        Assert.False(File.Exists(GetRepoFilePath(@"src/MediaEngine.Web/Components/Shared/Shell/SidebarPageShell.razor")));
        Assert.False(File.Exists(GetRepoFilePath(@"src/MediaEngine.Web/Components/Shared/Shell/SidebarNavGroup.razor")));
        Assert.False(File.Exists(GetRepoFilePath(@"src/MediaEngine.Web/Components/Shared/Shell/SidebarNavItem.razor")));
    }

    [Fact]
    public void SettingsComponents_DoNotContainLegacyFoldersTab()
    {
        var root = GetRepoFilePath("");
        var legacyPath = Path.Combine(
            root,
            "src",
            "MediaEngine.Web",
            "Components",
            "Settings",
            "FoldersTab.razor");

        Assert.False(File.Exists(legacyPath));

        var settingsSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Components/Pages/Settings.razor"));
        Assert.Contains("<LibrariesTab Subsection=\"@_activeSubsection\" />", settingsSource, StringComparison.Ordinal);
        Assert.DoesNotContain("FoldersTab", settingsSource, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsPage_UsesServerAuthorityInsteadOfProfileRoles()
    {
        var settingsSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Components/Pages/Settings.razor"));
        var orchestratorSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Services/Integration/UIOrchestratorService.cs"));
        var sessionSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Services/Integration/ActiveProfileSessionService.cs"));

        Assert.Contains("await LoadAuthorityAsync()", settingsSource, StringComparison.Ordinal);
        Assert.Contains("SettingsNav.ResolveRoute(Section, CanManageAdministration, CanManageHousehold)", settingsSource, StringComparison.Ordinal);
        Assert.Contains("DashboardSessionAccessor Session", settingsSource, StringComparison.Ordinal);
        Assert.Contains("ShouldDeferForRoleResolution", settingsSource, StringComparison.Ordinal);
        Assert.DoesNotContain("_currentRole", settingsSource, StringComparison.Ordinal);
        Assert.Contains("SetActiveProfileAsync", orchestratorSource, StringComparison.Ordinal);
        Assert.Contains("AuthenticationStateProvider", sessionSource, StringComparison.Ordinal);
        Assert.Contains("tuvima:active_profile_id", sessionSource, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage", sessionSource, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsPage_GatesClientSideAdministrationNavigationOnServerAuthority()
    {
        var settingsSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Components/Pages/Settings.razor"));

        Assert.Contains("CanManageAdministration => Session.HasNavigation(\"settings.administration\")", settingsSource, StringComparison.Ordinal);
        Assert.Contains("SettingsNav.ResolveRoute(Section, CanManageAdministration, CanManageHousehold)", settingsSource, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureElevationForCurrentRouteAsync", settingsSource, StringComparison.Ordinal);
        Assert.DoesNotContain("IAdministratorElevationNavigationService", settingsSource, StringComparison.Ordinal);
    }

    [Fact]
    public void RouteFor_Profiles_UsesProfileSettingsUrl()
    {
        Assert.Equal("/settings/profiles", SettingsNav.RouteFor(SettingsSection.Profiles));
    }

    [Fact]
    public void Overview_IsTheFirstPersonalPage_AndTheLandingPageForEveryone()
    {
        Assert.Equal(SettingsSection.Overview, SettingsNav.AllItems[0].Value);
        Assert.Equal("personal", SettingsNav.AllItems[0].GroupKey);
        Assert.Equal("/settings/overview", SettingsNav.RouteFor(SettingsSection.Overview));
        Assert.Equal(SettingsSection.Overview, SettingsNav.FirstVisibleSection(administrationAllowed: false));
        Assert.Equal(SettingsSection.Overview, SettingsNav.FirstVisibleSection(administrationAllowed: true));
        Assert.Equal(SettingsSection.Overview, SettingsNav.FirstVisibleSection(administrationAllowed: false, householdAdministrator: true));
        Assert.True(SettingsNav.ResolveRoute("overview", administrationAllowed: false).RequestedSectionAllowed);
        Assert.True(SettingsNav.ResolveRoute("overview", administrationAllowed: false).IsCanonicalRoute);
        Assert.True(SettingsNav.IsMobileRouteAvailable(SettingsSection.Overview));
        Assert.True(SettingsNav.IsMobileRouteAvailable(SettingsSection.Overview, SettingsNav.AppsSubsection));
        Assert.Equal(SettingsSection.Overview, SettingsNav.GetDefaultSection("personal"));
    }

    [Fact]
    public void Overview_HasActivityThenTasteAsMenuChildren_AndItsAppsPageIsOffMenuUnderIt()
    {
        var overview = SettingsNav.MenuGroups[0].Nodes[0];
        Assert.Equal("Overview", overview.Label);
        Assert.Equal(["Activity", "Taste"], overview.Children.Select(child => child.Label));
        var activity = overview.Children[0];
        Assert.Equal("Activity", activity.Label);
        Assert.Equal(SettingsSection.Overview, activity.Section);
        Assert.Equal(SettingsNav.ActivitySubsection, activity.Subsection);
        Assert.Equal("/settings/overview/activity", activity.Route);
        Assert.Equal(["Overview", "Activity"], SettingsNav.FindMenuLocation(SettingsSection.Overview, SettingsNav.ActivitySubsection)!.Trail.Select(node => node.Label));
        // Overview still opens on its own page, and every kind of person can open Activity (it is a consumption page that works on a phone).
        Assert.Equal("/settings/overview", overview.Route);
        Assert.True(SettingsNav.IsMobileRouteAvailable(SettingsSection.Overview, SettingsNav.ActivitySubsection));
        Assert.True(SettingsNav.ResolveRoute("overview", administrationAllowed: false).RequestedSectionAllowed);

        // Apps opens from an icon on the Overview, so it is not a menu row but keeps its place in the breadcrumb.
        Assert.DoesNotContain(SettingsNav.Flatten(SettingsNav.MenuGroups.SelectMany(group => group.Nodes)), node => node.Label == "Apps");
        Assert.Equal("/settings/overview/apps", SettingsNav.RouteFor(SettingsSection.Overview, SettingsNav.AppsSubsection));
        var location = SettingsNav.FindMenuLocation(SettingsSection.Overview, SettingsNav.AppsSubsection);
        Assert.NotNull(location);
        Assert.Equal("personal", location!.Group.Key);
        Assert.Equal(["Overview", "Apps"], location.Trail.Select(node => node.Label));
        Assert.Equal(["Overview"], SettingsNav.FindMenuLocation(SettingsSection.Overview)!.Trail.Select(node => node.Label));
    }

    [Fact]
    public void Overview_RoutesForActivityAndTaste_AreTheStringsTheLinksUse()
    {
        Assert.Equal("/settings/overview/activity", SettingsNav.ActivityRoute);
        Assert.Equal(SettingsNav.ActivityRoute, SettingsNav.RouteFor(SettingsSection.Overview, SettingsNav.ActivitySubsection));
        Assert.Equal("/my-activity", SettingsNav.MyActivityRoute);
        Assert.Equal("/settings/overview/taste", SettingsNav.TasteRoute);
    }

    [Theory]
    [InlineData(SettingsSection.LocalAi, "vocabulary", "/settings/ai/vocabulary")]
    [InlineData(SettingsSection.Delivery, "hardware", "/settings/delivery/hardware")]
    [InlineData(SettingsSection.Delivery, "optimized-copies", "/settings/delivery/optimized-copies")]
    [InlineData(SettingsSection.Providers, "providers", "/settings/metadata/providers")]
    [InlineData(SettingsSection.Providers, "ingestion-flow", "/settings/metadata/ingestion-flow")]
    public void RouteFor_Subsection_UsesNestedCanonicalUrl(
        SettingsSection section,
        string subsection,
        string expectedRoute)
    {
        Assert.Equal(expectedRoute, SettingsNav.RouteFor(section, subsection));
    }

    [Fact]
    public void LocalAi_Subsections_ReplaceTheFormerTabStrip()
    {
        var labels = SettingsNav.GetSubsections(SettingsSection.LocalAi)
            .Select(item => item.Label)
            .ToArray();

        // Local AI keeps today's content (Runtime, Vocabulary, Automation) until the Developer tools spec rebuilds it.
        Assert.Equal([
            "Runtime",
            "Vocabulary",
            "Automation",
        ], labels);
        Assert.Equal("models", SettingsNav.GetDefaultSubsection(SettingsSection.LocalAi).Slug);
        Assert.Null(SettingsNav.ResolveSubsection(SettingsSection.LocalAi, "unknown"));
    }

    [Fact]
    public void MetadataManagement_UsesTwoClearSubsections()
    {
        var subsections = SettingsNav.GetSubsections(SettingsSection.Providers).ToArray();

        Assert.Equal(["Providers", "Ingestion Flow"], subsections.Select(item => item.Label));
        Assert.Equal(["providers", "ingestion-flow"], subsections.Select(item => item.Slug));
    }

    [Fact]
    public void SummaryFirstSections_UseRootRoutesWithoutRequiringSubsections()
    {
        Assert.Equal(["households"], SettingsNav.GetSubsections(SettingsSection.Profiles).Select(item => item.Slug));
        Assert.Empty(SettingsNav.GetSubsections(SettingsSection.Playback));
        Assert.Equal(["logs"], SettingsNav.GetSubsections(SettingsSection.AdminOverview).Select(item => item.Slug));
        Assert.Empty(SettingsNav.GetSubsections(SettingsSection.Review));
        Assert.Empty(SettingsNav.GetSubsections(SettingsSection.Server));
        Assert.Empty(SettingsNav.GetSubsections(SettingsSection.Tools));
    }

    [Fact]
    public void MobileAvailability_WithholdsComplexAdministrationButKeepsMonitoringSummaries()
    {
        Assert.Equal(SettingsMobileAvailability.Full, SettingsNav.GetMobileAvailability(SettingsSection.Playback));
        Assert.Equal(SettingsMobileAvailability.SummaryOnly, SettingsNav.GetMobileAvailability(SettingsSection.AdminOverview));
        Assert.Equal(SettingsMobileAvailability.DesktopOnly, SettingsNav.GetMobileAvailability(SettingsSection.DevHarness));

        Assert.True(SettingsNav.IsMobileRouteAvailable(SettingsSection.Network, "overview"));
        Assert.False(SettingsNav.IsMobileRouteAvailable(SettingsSection.Network, "advanced"));
        Assert.True(SettingsNav.IsMobileRouteAvailable(SettingsSection.Providers, "providers"));
        Assert.False(SettingsNav.IsMobileRouteAvailable(SettingsSection.Providers, "providers", "tmdb"));
    }

    [Theory]
    [InlineData(SettingsSection.AdminOverview, "/settings/system")]
    [InlineData(SettingsSection.Preferences, "/settings/preferences")]
    [InlineData(SettingsSection.Playback, "/settings/playback")]
    [InlineData(SettingsSection.Libraries, "/settings/libraries")]
    [InlineData(SettingsSection.Ingestion, "/settings/ingestion")]
    [InlineData(SettingsSection.RecentlyAdded, "/settings/recently-added")]
    [InlineData(SettingsSection.DevHarness, "/settings/developer")]
    [InlineData(SettingsSection.Providers, "/settings/metadata/providers")]
    [InlineData(SettingsSection.LocalAi, "/settings/ai")]
    [InlineData(SettingsSection.Plugins, "/settings/plugins")]
    [InlineData(SettingsSection.Delivery, "/settings/delivery")]
    [InlineData(SettingsSection.Access, "/settings/access/users")]
    [InlineData(SettingsSection.Tools, "/settings/tools")]
    [InlineData(SettingsSection.Server, "/settings/backup-recovery")]
    [InlineData(SettingsSection.Review, "/settings/review")]
    [InlineData(SettingsSection.ProviderTester, "/settings/provider-tester")]
    [InlineData(SettingsSection.EnrichmentTester, "/settings/enrichment-tester")]
    public void ResolveRoute_CanonicalSegments_AreStable(SettingsSection section, string expectedRoute)
    {
        var segment = expectedRoute.Split('/', StringSplitOptions.RemoveEmptyEntries)[1];
        var resolution = SettingsNav.ResolveRoute(segment, true);

        Assert.Equal(section, resolution.Section);
        Assert.Equal(expectedRoute, resolution.CanonicalRoute);
        Assert.True(resolution.IsCanonicalRoute);
        Assert.True(resolution.IsKnownRoute);
        Assert.True(resolution.RequestedSectionAllowed);
        Assert.False(resolution.ShouldRedirect);
    }

    [Fact]
    public void ResolveRoute_BaseSettings_MapsToUserOverview()
    {
        var resolution = SettingsNav.ResolveRoute(null, true);

        Assert.Equal(SettingsSection.Overview, resolution.Section);
        Assert.Equal("/settings/overview", resolution.CanonicalRoute);
        Assert.False(resolution.IsCanonicalRoute);
        Assert.True(resolution.IsKnownRoute);
        Assert.True(resolution.ShouldRedirect);
    }

    [Fact]
    public void ResolveRoute_RemovedAdminAlias_IsUnknown()
    {
        var resolution = SettingsNav.ResolveRoute("admin", true);

        Assert.Equal(SettingsSection.Overview, resolution.Section);
        Assert.Equal("/not-found", resolution.CanonicalRoute);
        Assert.False(resolution.IsKnownRoute);
        Assert.True(resolution.ShouldRedirect);
    }

    [Fact]
    public void ResolveRoute_ProfileSegment_MapsToProfile()
    {
        var resolution = SettingsNav.ResolveRoute("profile", true);

        Assert.Equal(SettingsSection.Profiles, resolution.Section);
        Assert.Equal("/settings/profiles", resolution.CanonicalRoute);
        Assert.False(resolution.IsCanonicalRoute);
        Assert.True(resolution.IsKnownRoute);
        Assert.True(resolution.RequestedSectionAllowed);
        Assert.True(resolution.ShouldRedirect);
    }

    [Fact]
    public void ResolveRoute_DisplaySegment_IsUnknownAndRoutesToNotFound()
    {
        var resolution = SettingsNav.ResolveRoute("display", true);

        Assert.Equal(SettingsSection.Overview, resolution.Section);
        Assert.Equal("/not-found", resolution.CanonicalRoute);
        Assert.False(resolution.IsCanonicalRoute);
        Assert.False(resolution.IsKnownRoute);
        Assert.False(resolution.RequestedSectionAllowed);
        Assert.True(resolution.ShouldRedirect);
    }

    [Theory]
    [InlineData("models")]
    [InlineData("features")]
    [InlineData("encode")]
    [InlineData("users")]
    [InlineData("api-keys")]
    [InlineData("activity")]
    public void ResolveRoute_RemovedAliasesAreUnknown(string alias)
    {
        var resolution = SettingsNav.ResolveRoute(alias, true);

        Assert.Equal("/not-found", resolution.CanonicalRoute);
        Assert.False(resolution.IsKnownRoute);
        Assert.True(resolution.ShouldRedirect);
    }

    [Fact]
    public void ResolveRoute_DisallowedAdminPage_FallsBackToTheFirstPageThePersonMaySee()
    {
        // Everyone's first page is their own Overview.
        var resolution = SettingsNav.ResolveRoute("metadata", false);

        Assert.Equal(SettingsSection.Overview, resolution.Section);
        Assert.Equal("/settings/overview", resolution.CanonicalRoute);
        Assert.False(resolution.IsCanonicalRoute);
        Assert.True(resolution.IsKnownRoute);
        Assert.False(resolution.RequestedSectionAllowed);
        Assert.True(resolution.ShouldRedirect);
    }

    [Theory]
    [InlineData("providers")]
    [InlineData("wikidata")]
    public void ResolveRoute_RemovedMetadataAliases_AreUnknown(string segment)
    {
        var resolution = SettingsNav.ResolveRoute(segment, true);

        Assert.Equal(SettingsSection.Overview, resolution.Section);
        Assert.Equal("/not-found", resolution.CanonicalRoute);
        Assert.False(resolution.IsKnownRoute);
        Assert.True(resolution.ShouldRedirect);
    }

    [Fact]
    public void FilteredGroups_NonAdmin_OnlyShowsPublicGroups()
    {
        var groups = SettingsNav.FilteredGroups(false).Select(group => group.Key).ToArray();

        Assert.Equal(["personal"], groups);
    }

    [Fact]
    public void FilteredMenuGroups_Admin_RendersPersonalServerManageAndDeveloperInOrder()
    {
        var groups = SettingsNav.FilteredMenuGroups(true);

        Assert.Equal(["personal", "server", "manage", "developer"], groups.Select(group => group.Key));
        Assert.Equal(["Personal", "Server", "Manage", "Developer"], groups.Select(group => group.Label));

        Assert.Equal(["Overview", "Profiles", "Preferences", "Security"], Labels(groups, "personal"));
        Assert.Equal(["Library and Ingestion", "Network and Remote Access", "Streaming"], Labels(groups, "server"));
        Assert.Equal(["Users and Sign-in", "System"], Labels(groups, "manage"));
        Assert.Equal(["Local AI", "Integrations", "Plugins", "Developer Tools"], Labels(groups, "developer"));

        Assert.Equal(["Households"], ChildLabels(groups, "Profiles"));
        Assert.Equal(["Playback", "Listening", "Reading"], ChildLabels(groups, "Preferences"));
        Assert.Equal(["Libraries", "Metadata Providers"], ChildLabels(groups, "Library and Ingestion"));
        Assert.Equal(["Ingestion Flow"], ChildLabels(groups, "Metadata Providers"));
        Assert.Equal(["Local Network", "Remote Access", "Advanced"], ChildLabels(groups, "Network and Remote Access"));
        Assert.Equal(["Hardware Acceleration", "Optimized Copies", "Diagnostics"], ChildLabels(groups, "Streaming"));
        Assert.Equal(["Sign-in Methods"], ChildLabels(groups, "Users and Sign-in"));
        Assert.Equal(["Tools and requirements", "Logs", "Backup and recovery"], ChildLabels(groups, "System"));
        Assert.Equal(["Runtime", "Vocabulary", "Automation"], ChildLabels(groups, "Local AI"));
        Assert.Empty(ChildLabels(groups, "Plugins"));
        Assert.Equal(["Provider Tester", "Enrichment Tester"], ChildLabels(groups, "Developer Tools"));
        Assert.Empty(ChildLabels(groups, "Integrations"));

        var allLabels = SettingsNav.Flatten(groups.SelectMany(group => group.Nodes)).Select(node => node.Label).ToArray();
        foreach (var gone in new[] { "Privacy & Data", "System Overview", "Live Ingestion", "Needs Review", "Administration", "Advanced Settings" })
        {
            Assert.DoesNotContain(gone, allLabels);
        }

        // Households lives only under Profiles; Users and Sign-in and Server no longer carry one.
        var householdsRows = SettingsNav.Flatten(groups.SelectMany(group => group.Nodes)).Where(node => node.Label == "Households").ToArray();
        Assert.Equal("/settings/profiles/households", Assert.Single(householdsRows).Route);
    }

    [Fact]
    public void MenuTree_GivesEveryRowAnIconAndSendsEachParentToItsOwnPage()
    {
        var nodes = SettingsNav.Flatten(SettingsNav.MenuGroups.SelectMany(group => group.Nodes)).ToArray();

        Assert.All(SettingsNav.MenuGroups, group => Assert.False(string.IsNullOrWhiteSpace(group.Icon)));
        Assert.All(nodes, node => Assert.False(string.IsNullOrWhiteSpace(node.Icon), $"{node.Label} has no icon."));
        // No two rows open the same page, so no child can repeat its parent.
        Assert.Equal(nodes.Length, nodes.Select(node => node.Route).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var parent in nodes.Where(node => node.Children.Count > 0))
        {
            Assert.DoesNotContain(parent.Children, child => string.Equals(child.Route, parent.Route, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(parent.Children, child => child.Subsection is null && child.Section == parent.Section);
        }
    }

    [Fact]
    public void MenuTree_EveryRowOpensARealPageOfItsSection()
    {
        foreach (var node in SettingsNav.Flatten(SettingsNav.MenuGroups.SelectMany(group => group.Nodes)))
        {
            var segment = node.Route.Split('/', StringSplitOptions.RemoveEmptyEntries)[1];
            var resolution = SettingsNav.ResolveRoute(segment, true);
            Assert.Equal(node.Section, resolution.Section);
            Assert.True(resolution.IsKnownRoute, node.Label);
            if (node.Subsection is not null)
            {
                Assert.NotNull(SettingsNav.ResolveSubsection(node.Section, node.Subsection));
            }
        }
    }

    [Fact]
    public void EveryPage_IncludingTheOffMenuOnes_ResolvesToTheGroupItsDefinitionNames()
    {
        foreach (var item in SettingsNav.AllItems)
        {
            var location = SettingsNav.FindMenuLocation(item.Value);
            Assert.NotNull(location);
            Assert.Equal(item.GroupKey, location!.Group.Key);
            Assert.Equal(item.Label, location.Node.Label);
        }

        // Needs Review is off the menu, so it resolves through its parent: Server, Library and Ingestion, Needs Review.
        Assert.Equal(["Library and Ingestion", "Needs Review"], SettingsNav.FindMenuLocation(SettingsSection.Review)!.Trail.Select(node => node.Label));
    }

    [Fact]
    public void FindMenuLocation_PutsIntegrationsUnderDeveloperAndLandingViewsOnTheirParent()
    {
        var integrations = SettingsNav.FindMenuLocation(SettingsSection.Access, "applications");
        Assert.Equal("developer", integrations!.Group.Key);
        Assert.Equal(["Integrations"], integrations.Trail.Select(node => node.Label));

        var signIn = SettingsNav.FindMenuLocation(SettingsSection.Access, "authentication");
        Assert.Equal(["Users and Sign-in", "Sign-in Methods"], signIn!.Trail.Select(node => node.Label));
        Assert.Equal("manage", signIn.Group.Key);

        Assert.Equal(["Users and Sign-in"], SettingsNav.FindMenuLocation(SettingsSection.Access, "users")!.Trail.Select(node => node.Label));
        Assert.Equal(["Network and Remote Access"], SettingsNav.FindMenuLocation(SettingsSection.Network, "overview")!.Trail.Select(node => node.Label));
        Assert.Equal(
            ["Library and Ingestion", "Metadata Providers", "Ingestion Flow"],
            SettingsNav.FindMenuLocation(SettingsSection.Providers, "ingestion-flow")!.Trail.Select(node => node.Label));
        Assert.Equal(
            ["Library and Ingestion", "Metadata Providers"],
            SettingsNav.FindMenuLocation(SettingsSection.Providers, "providers")!.Trail.Select(node => node.Label));
        var playback = SettingsNav.FindMenuLocation(SettingsSection.Playback);
        Assert.Equal("personal", playback!.Group.Key);
        Assert.Equal(["Preferences", "Playback"], playback.Trail.Select(node => node.Label));
    }

    [Fact]
    public void Preferences_ResolvesToItsOwnSummaryPage()
    {
        var resolution = SettingsNav.ResolveRoute("preferences", false);

        Assert.Equal(SettingsSection.Preferences, resolution.Section);
        Assert.Equal("/settings/preferences", resolution.CanonicalRoute);
        Assert.True(resolution.IsCanonicalRoute);
        Assert.True(resolution.RequestedSectionAllowed);
        Assert.True(SettingsNav.IsMobileRouteAvailable(SettingsSection.Preferences));
    }

    [Theory]
    [InlineData("profiles", SettingsSection.Profiles, "/settings/profiles")]
    [InlineData("playback", SettingsSection.Playback, "/settings/playback")]
    [InlineData("listening", SettingsSection.Listening, "/settings/listening")]
    [InlineData("reading", SettingsSection.Reading, "/settings/reading")]
    [InlineData("security", SettingsSection.Security, "/settings/security")]
    [InlineData("system", SettingsSection.AdminOverview, "/settings/system")]
    [InlineData("libraries", SettingsSection.Libraries, "/settings/libraries")]
    [InlineData("ingestion", SettingsSection.Ingestion, "/settings/ingestion")]
    [InlineData("recently-added", SettingsSection.RecentlyAdded, "/settings/recently-added")]
    [InlineData("metadata", SettingsSection.Providers, "/settings/metadata/providers")]
    [InlineData("network", SettingsSection.Network, "/settings/network/overview")]
    [InlineData("delivery", SettingsSection.Delivery, "/settings/delivery")]
    [InlineData("tools", SettingsSection.Tools, "/settings/tools")]
    [InlineData("access", SettingsSection.Access, "/settings/access/users")]
    [InlineData("backup-recovery", SettingsSection.Server, "/settings/backup-recovery")]
    [InlineData("ai", SettingsSection.LocalAi, "/settings/ai")]
    [InlineData("plugins", SettingsSection.Plugins, "/settings/plugins")]
    [InlineData("developer", SettingsSection.DevHarness, "/settings/developer")]
    [InlineData("provider-tester", SettingsSection.ProviderTester, "/settings/provider-tester")]
    [InlineData("enrichment-tester", SettingsSection.EnrichmentTester, "/settings/enrichment-tester")]
    public void EveryPreExistingSettingsAddress_StillOpensTheSamePage(string segment, SettingsSection section, string canonicalRoute)
    {
        var resolution = SettingsNav.ResolveRoute(segment, true);

        Assert.Equal(section, resolution.Section);
        Assert.Equal(canonicalRoute, resolution.CanonicalRoute);
        Assert.True(resolution.IsKnownRoute);
        Assert.True(resolution.RequestedSectionAllowed);
    }

    [Theory]
    [InlineData(SettingsSection.Network, "local")]
    [InlineData(SettingsSection.Network, "remote")]
    [InlineData(SettingsSection.Network, "advanced")]
    [InlineData(SettingsSection.Providers, "providers")]
    [InlineData(SettingsSection.Providers, "ingestion-flow")]
    [InlineData(SettingsSection.Access, "users")]
    [InlineData(SettingsSection.Access, "applications")]
    [InlineData(SettingsSection.Access, "authentication")]
    [InlineData(SettingsSection.LocalAi, "vocabulary")]
    [InlineData(SettingsSection.LocalAi, "automation")]
    [InlineData(SettingsSection.Delivery, "hardware")]
    [InlineData(SettingsSection.Delivery, "optimized-copies")]
    [InlineData(SettingsSection.Delivery, "diagnostics")]
    public void EverySubPageAddressThatSurvivedTheServerRegroup_StillResolves(SettingsSection section, string slug)
    {
        Assert.NotNull(SettingsNav.ResolveSubsection(section, slug));
        Assert.Equal($"{SettingsNav.RouteFor(section, slug)}", $"/settings/{SettingsNav.GetItem(section).Slug}/{slug}");
    }

    [Theory]
    [InlineData(SettingsSection.Network, "streaming")]
    [InlineData(SettingsSection.LocalAi, "runtime")]
    [InlineData(SettingsSection.Plugins, "jobs-health")]
    [InlineData(SettingsSection.Plugins, "catalog")]
    [InlineData(SettingsSection.Plugins, "capabilities")]
    [InlineData(SettingsSection.Plugins, "danger")]
    [InlineData(SettingsSection.Delivery, "scheduling")]
    [InlineData(SettingsSection.Delivery, "storage")]
    [InlineData(SettingsSection.Delivery, "active-jobs")]
    public void SubPagesTheServerRegroupFoldedAway_HaveNoRoute_AndNoRedirect(SettingsSection section, string slug)
    {
        // Pre-release: the old addresses are simply gone (the settings page sends an unknown sub-page to Not found).
        Assert.Null(SettingsNav.ResolveSubsection(section, slug));
        Assert.Throws<ArgumentOutOfRangeException>(() => SettingsNav.RouteFor(section, slug));
    }

    [Fact]
    public void PrivacyAndData_NoLongerExists()
    {
        Assert.DoesNotContain(SettingsNav.AllItems, item => item.Label.Contains("Privacy", StringComparison.Ordinal));
        Assert.False(SettingsNav.ResolveRoute("privacy", true).IsKnownRoute);
    }

    [Fact]
    public void Member_SeesOnlyThePersonalGroup()
    {
        var groups = SettingsNav.FilteredMenuGroups(false);

        Assert.Equal(["personal"], groups.Select(group => group.Key));
        // Profiles is for administrators: a member (and a Kids profile, which the shell keeps out of Settings) never sees it.
        Assert.Equal(["Overview", "Preferences", "Security"], groups[0].Nodes.Select(node => node.Label));
        Assert.Equal(["Playback", "Listening", "Reading"], groups[0].Nodes[1].Children.Select(node => node.Label));
    }

    [Fact]
    public void HouseholdAdministrator_SeesOnlyThePersonalGroup_WithProfilesButNoHouseholdsPage()
    {
        var groups = SettingsNav.FilteredMenuGroups(false, householdAdministrator: true);

        Assert.Equal(["personal"], groups.Select(group => group.Key));
        Assert.Equal(["Overview", "Profiles", "Preferences", "Security"], groups[0].Nodes.Select(node => node.Label));
        Assert.Empty(ChildLabels(groups, "Profiles"));
    }

    [Fact]
    public void ServerAdministrator_SeesProfilesWithItsHouseholdsPage()
    {
        var groups = SettingsNav.FilteredMenuGroups(true);

        Assert.Equal(["Households"], ChildLabels(groups, "Profiles"));
        Assert.Equal("/settings/profiles", SettingsNav.RouteFor(SettingsSection.Profiles));
        Assert.Equal("/settings/profiles/households", SettingsNav.RouteFor(SettingsSection.Profiles, "households"));
    }

    [Fact]
    public void ProfilesPage_IsRefusedToAMember_AndOpenToBothKindsOfAdministrator()
    {
        Assert.False(SettingsNav.IsVisible(SettingsSection.Profiles, administrationAllowed: false));
        Assert.False(SettingsNav.ResolveRoute("profiles", false).RequestedSectionAllowed);
        Assert.True(SettingsNav.ResolveRoute("profiles", false, householdAdministrator: true).RequestedSectionAllowed);
        Assert.True(SettingsNav.ResolveRoute("profiles", true).RequestedSectionAllowed);

        // The bare /settings address opens the person's own Overview, whatever their role.
        Assert.Equal(SettingsSection.Overview, SettingsNav.ResolveRoute(null, false).Section);
        Assert.Equal(SettingsSection.Overview, SettingsNav.ResolveRoute(null, false, householdAdministrator: true).Section);
        Assert.Equal(SettingsSection.Overview, SettingsNav.ResolveRoute(null, true).Section);
    }

    private static string[] Labels(IReadOnlyList<SettingsMenuGroup> groups, string groupKey) =>
        groups.Single(group => group.Key == groupKey).Nodes.Select(node => node.Label).ToArray();

    private static string[] ChildLabels(IReadOnlyList<SettingsMenuGroup> groups, string parentLabel) =>
        SettingsNav.Flatten(groups.SelectMany(group => group.Nodes))
            .FirstOrDefault(node => node.Label == parentLabel && node.Children.Count > 0)
            ?.Children.Select(node => node.Label).ToArray() ?? [];

    [Theory]
    [InlineData("review", SettingsSection.Review, "/settings/review")]
    [InlineData("provider-tester", SettingsSection.ProviderTester, "/settings/provider-tester")]
    [InlineData("enrichment-tester", SettingsSection.EnrichmentTester, "/settings/enrichment-tester")]
    public void ResolveRoute_SecondaryRoutes_StillResolveForAdmins(string segment, SettingsSection expectedSection, string expectedRoute)
    {
        var resolution = SettingsNav.ResolveRoute(segment, true);

        Assert.Equal(expectedSection, resolution.Section);
        Assert.Equal(expectedRoute, resolution.CanonicalRoute);
        Assert.True(resolution.IsKnownRoute);
        Assert.True(resolution.RequestedSectionAllowed);
    }

    [Theory]
    [InlineData("server", SettingsSection.Ingestion, "/settings/ingestion")]
    [InlineData("manage", SettingsSection.Access, "/settings/access/users")]
    [InlineData("developer", SettingsSection.LocalAi, "/settings/ai")]
    [InlineData("personal", SettingsSection.Overview, "/settings/overview")]
    public void GroupDefaults_ResolveToExpectedCanonicalRoutes(string groupKey, SettingsSection expectedSection, string expectedRoute)
    {
        var section = SettingsNav.GetDefaultSection(groupKey);

        Assert.Equal(expectedSection, section);
        Assert.Equal(expectedRoute, SettingsNav.RouteFor(section));
    }

    [Fact]
    public void ProfilesRoute_AcceptsAPersonId_AndRejectsOtherSubsections()
    {
        var id = Guid.NewGuid().ToString("D");

        Assert.True(SettingsNav.IsMobileRouteAvailable(SettingsSection.Profiles));
        Assert.True(SettingsNav.IsMobileRouteAvailable(SettingsSection.Profiles, id));
        Assert.False(SettingsNav.IsMobileRouteAvailable(SettingsSection.Profiles, "household"));
        Assert.True(SettingsNav.IsMobileRouteAvailable(SettingsSection.Profiles, "households"));
        Assert.False(SettingsNav.IsMobileRouteAvailable(SettingsSection.Profiles, id, "extra"));
        Assert.Null(SettingsNav.ResolveSubsection(SettingsSection.Profiles, "household"));
        Assert.Equal("households", SettingsNav.ResolveSubsection(SettingsSection.Profiles, "households")?.Slug);
    }

    [Fact]
    public void SettingsPage_ShowsThePersonPageForAProfilesIdAndNotFoundForAnythingElse()
    {
        var settingsSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Components/Pages/Settings.razor"));

        Assert.Contains("<ProfileDetailPage ProfileId=\"@ProfileDetailId\" NameChanged=\"OnProfileNameChanged\" />", settingsSource, StringComparison.Ordinal);
        Assert.Contains("_activeSection == SettingsSection.Profiles && Guid.TryParse(Subsection, out _)", settingsSource, StringComparison.Ordinal);
        Assert.Contains("$\"/settings/profiles/{_activeSubsection}\"", settingsSource, StringComparison.Ordinal);
        Assert.Contains("- Profiles - Tuvima Library", settingsSource, StringComparison.Ordinal);
        // A non-id subsection still goes through the normal lookup, which only knows Households for Profiles and so ends at /not-found otherwise.
        Assert.Contains("<HouseholdsTab />", settingsSource, StringComparison.Ordinal);
        Assert.Contains("SettingsNav.ResolveSubsection(_activeSection, Subsection)", settingsSource, StringComparison.Ordinal);
    }

    [Fact]
    public void LibrariesRoute_IsCanonicalAndFoldersAliasRemainsRemoved()
    {
        var libraries = SettingsNav.ResolveRoute("libraries", true);
        Assert.True(libraries.IsKnownRoute);
        Assert.Equal(SettingsSection.Libraries, libraries.Section);
        Assert.Equal("/settings/libraries", libraries.CanonicalRoute);

        var folders = SettingsNav.ResolveRoute("folders", true);
        Assert.False(folders.IsKnownRoute);
        Assert.Equal("/not-found", folders.CanonicalRoute);

        var importFolders = SettingsNav.ResolveRoute("import-folders", true);
        Assert.False(importFolders.IsKnownRoute);
        Assert.Equal("/not-found", importFolders.CanonicalRoute);
    }

    [Fact]
    public void IngestionRoute_IsCanonical()
    {
        var resolution = SettingsNav.ResolveRoute("ingestion", true);

        Assert.True(resolution.IsKnownRoute);
        Assert.Equal(SettingsSection.Ingestion, resolution.Section);
        Assert.Equal("/settings/ingestion", resolution.CanonicalRoute);
        Assert.Contains(SettingsNav.AllItems, item => item.Label == "Library and Ingestion");
        Assert.DoesNotContain(SettingsNav.AllItems, item => item.Label == "Live Ingestion");
    }

    [Fact]
    public void AccountWithoutAdministration_SeesOwnSecurityButNotReviewOrServerAdministration()
    {
        var visible = SettingsNav.Flatten(SettingsNav.FilteredMenuGroups(false).SelectMany(group => group.Nodes))
            .Select(node => node.Section)
            .ToArray();

        Assert.DoesNotContain(SettingsSection.Profiles, visible);
        Assert.Contains(SettingsSection.Playback, visible);
        Assert.Contains(SettingsSection.Security, visible);
        Assert.DoesNotContain(SettingsSection.Review, visible);
        Assert.DoesNotContain(SettingsSection.Server, visible);
        Assert.DoesNotContain(SettingsSection.Providers, visible);
    }

    [Fact]
    public void HouseholdAdministrator_SeesProfilesAndThePersonalSectionsButNoServerAdministration()
    {
        var groups = SettingsNav.FilteredMenuGroups(false, householdAdministrator: true).Select(group => group.Key).ToArray();

        Assert.Equal(["personal"], groups);
        var household = SettingsNav.ResolveRoute("household", false, householdAdministrator: true);
        Assert.Equal(SettingsSection.Profiles, household.Section);
        Assert.False(household.IsCanonicalRoute);
        Assert.True(household.RequestedSectionAllowed);
        Assert.False(SettingsNav.ResolveRoute("access", false).RequestedSectionAllowed);
        Assert.False(SettingsNav.ResolveRoute("network", false).RequestedSectionAllowed);
    }

    [Fact]
    public void Member_PersonalTree_IsOverviewThenPreferencesWithItsThreePagesAndSecurityWithItsDevices()
    {
        var personal = SettingsNav.FilteredMenuGroups(false).Single(group => group.Key == "personal");

        var sections = SettingsNav.Flatten(personal.Nodes).Select(node => node.Section).ToArray();

        Assert.Equal(
            [
                SettingsSection.Overview, SettingsSection.Overview, SettingsSection.Overview,
                SettingsSection.Preferences, SettingsSection.Playback,
                SettingsSection.Listening, SettingsSection.Reading, SettingsSection.Security,
                SettingsSection.Security,
            ],
            sections);
    }

    [Fact]
    public void HouseholdAdministrator_PersonalTree_HasProfilesAfterOverviewAndNoHouseholdsPage()
    {
        var personal = SettingsNav.FilteredMenuGroups(false, householdAdministrator: true).Single(group => group.Key == "personal");

        var rows = SettingsNav.Flatten(personal.Nodes).Select(node => node.Route).ToArray();

        Assert.Equal("/settings/overview", rows[0]);
        Assert.Equal("/settings/overview/activity", rows[1]);
        Assert.Equal("/settings/overview/taste", rows[2]);
        Assert.Equal("/settings/profiles", rows[3]);
        Assert.DoesNotContain("/settings/profiles/households", rows);
    }

    [Fact]
    public void ResolveRoute_AccountAlias_MapsToSecurity()
    {
        var resolution = SettingsNav.ResolveRoute("account", false);

        Assert.Equal(SettingsSection.Security, resolution.Section);
        Assert.Equal("/settings/security", resolution.CanonicalRoute);
        Assert.False(resolution.IsCanonicalRoute);
        Assert.True(resolution.ShouldRedirect);
    }

    [Fact]
    public void ServerAdministrator_HasNoHouseholdsUnderUsersAndSignIn_AndThereIsNoMyHouseholdTab()
    {
        var personalLabels = SettingsNav.Flatten(SettingsNav.FilteredMenuGroups(true).Single(group => group.Key == "personal").Nodes)
            .Select(node => node.Label)
            .ToArray();

        Assert.DoesNotContain("My household", personalLabels);
        Assert.DoesNotContain(SettingsNav.GetSubsections(SettingsSection.Access), subsection => subsection.Slug == "household");
        Assert.DoesNotContain(SettingsNav.GetSubsections(SettingsSection.Access), subsection => subsection.Label.Contains("Household", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(SettingsNav.GetSubsections(SettingsSection.Access), subsection => subsection.Slug == "users" && subsection.Label == "Users");
        Assert.False(SettingsNav.IsMobileRouteAvailable(SettingsSection.Access, "household"));
        // The old address still resolves, to the Users tab.
        Assert.Equal("users", SettingsNav.ResolveSubsection(SettingsSection.Access, "household")?.Slug);
        Assert.Equal("/settings/access/users", SettingsNav.RouteFor(SettingsSection.Access, "household"));
    }

    [Fact]
    public void PlainMember_DoesNotSeeUsersAndAccess_AndDirectNavigationIsRefused()
    {
        var visible = SettingsNav.Flatten(SettingsNav.FilteredMenuGroups(false).SelectMany(group => group.Nodes))
            .Select(node => node.Section)
            .ToArray();

        Assert.DoesNotContain(SettingsSection.Access, visible);
        var access = SettingsNav.ResolveRoute("access", false);
        Assert.False(access.RequestedSectionAllowed);
        Assert.True(access.ShouldRedirect);
    }

    private static string GetRepoFilePath(string relativePath) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));
}
