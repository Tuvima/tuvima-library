using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Tests;

public sealed class SettingsNavTests
{
    [Theory]
    [InlineData("users")]
    [InlineData("authentication")]
    public void CanonicalAccessSections_AreAvailableOnMobile(string subsection)
    {
        Assert.True(SettingsNav.IsMobileRouteAvailable(SettingsSection.Access, subsection));
        Assert.False(SettingsNav.IsMobileRouteAvailable(SettingsSection.Access, subsection, "unknown-detail"));
    }

    [Fact]
    public void SettingsPage_UsesTheMediaLaneShellInsteadOfAParallelSidebar()
    {
        var settingsSource = LocalizedSource.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Components/Pages/Settings.razor"));
        var mediaShellSource = LocalizedSource.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Components/MediaHub/MediaSectionShell.razor"));

        Assert.Contains("<MediaSectionShell Title=\"Settings\"", settingsSource, StringComparison.Ordinal);
        Assert.DoesNotContain("AccordionNavigation", settingsSource, StringComparison.Ordinal);
        Assert.Contains("<SettingsPageTabs", settingsSource, StringComparison.Ordinal);
        Assert.Contains("settings-mobile-navigation", settingsSource, StringComparison.Ordinal);
        Assert.Contains("<IngestionTasksTab", settingsSource, StringComparison.Ordinal);
        Assert.Contains("<RecentlyAddedPageContent", settingsSource, StringComparison.Ordinal);
        Assert.DoesNotContain("<SettingsSubsectionNav", settingsSource, StringComparison.Ordinal);
        Assert.Contains("<SettingsPageFrame", settingsSource, StringComparison.Ordinal);
        Assert.Contains("<SidebarPageHeader", LocalizedSource.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Components/Settings/Frame/SettingsPageFrame.razor")), StringComparison.Ordinal);
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

        var settingsSource = LocalizedSource.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Components/Pages/Settings.razor"));
        Assert.Contains("<LibrariesTab Subsection=\"@_activeSubsection\" Detail=\"@Detail\" />", settingsSource, StringComparison.Ordinal);
        Assert.DoesNotContain("FoldersTab", settingsSource, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsPage_UsesServerAuthorityInsteadOfProfileRoles()
    {
        var settingsSource = LocalizedSource.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Components/Pages/Settings.razor"));
        var orchestratorSource = LocalizedSource.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Services/Integration/UIOrchestratorService.cs"));
        var sessionSource = LocalizedSource.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Services/Integration/ActiveProfileSessionService.cs"));

        Assert.Contains("await LoadAuthorityAsync()", settingsSource, StringComparison.Ordinal);
        Assert.Contains("SettingsNav.ResolveRoute(Section, CanManageAdministration, _restricted)", settingsSource, StringComparison.Ordinal);
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
        var settingsSource = LocalizedSource.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Components/Pages/Settings.razor"));

        Assert.Contains("CanManageAdministration => Session.HasNavigation(\"settings.administration\")", settingsSource, StringComparison.Ordinal);
        Assert.Contains("SettingsNav.ResolveRoute(Section, CanManageAdministration, _restricted)", settingsSource, StringComparison.Ordinal);
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
        Assert.Equal(SettingsSection.Overview, SettingsNav.FirstVisibleSection(administrationAllowed: false, restricted: true));
        Assert.True(SettingsNav.ResolveRoute("overview", administrationAllowed: false).RequestedSectionAllowed);
        Assert.True(SettingsNav.ResolveRoute("overview", administrationAllowed: false).IsCanonicalRoute);
        Assert.True(SettingsNav.IsMobileRouteAvailable(SettingsSection.Overview));
        Assert.True(SettingsNav.IsMobileRouteAvailable(SettingsSection.Overview, SettingsNav.AppsSubsection));
        Assert.Equal(SettingsSection.Overview, SettingsNav.GetDefaultSection("personal"));
    }

    [Fact]
    public void Profile_HasOverviewActivityAndTasteAsTabs_AndItsAppsPageIsOffMenuUnderIt()
    {
        var profile = SettingsNav.MenuGroups[0].Nodes[0];
        Assert.Equal("Profile", profile.Label);
        Assert.Equal(["Overview", "Activity", "Taste", "Import"], profile.Tabs.Select(tab => tab.Label));
        var activity = profile.Tabs[1];
        Assert.Equal(SettingsSection.Overview, activity.Section);
        Assert.Equal(SettingsNav.ActivitySubsection, activity.Subsection);
        Assert.Equal("/settings/overview/activity", activity.Route);
        var location = SettingsNav.FindMenuLocation(SettingsSection.Overview, SettingsNav.ActivitySubsection)!;
        Assert.Equal(["Profile", "Activity"], [location.Page.Label, location.Tab!.Label]);
        // The page still opens on its own first tab, and every kind of person can open Activity (it works on a phone).
        Assert.Equal("/settings/overview", profile.Route);
        Assert.True(SettingsNav.IsMobileRouteAvailable(SettingsSection.Overview, SettingsNav.ActivitySubsection));
        Assert.True(SettingsNav.ResolveRoute("overview", administrationAllowed: false).RequestedSectionAllowed);

        // Apps opens from a small link on Profile, so it is not a tab but keeps its place in the breadcrumb.
        Assert.DoesNotContain(SettingsNav.MenuGroups.SelectMany(group => group.Nodes).SelectMany(node => node.Tabs), tab => tab.Label == "Apps");
        Assert.Equal("/settings/overview/apps", SettingsNav.RouteFor(SettingsSection.Overview, SettingsNav.AppsSubsection));
        var apps = SettingsNav.FindMenuLocation(SettingsSection.Overview, SettingsNav.AppsSubsection);
        Assert.NotNull(apps);
        Assert.Equal("personal", apps!.Group.Key);
        Assert.Equal("Profile", apps.Page.Label);
        Assert.Null(apps.Tab);
        Assert.Equal("Apps", apps.Leaf);
        Assert.Equal("Overview", SettingsNav.FindMenuLocation(SettingsSection.Overview)!.Tab!.Label);
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
    [InlineData(SettingsSection.LocalAi, "models", "/settings/ai/models")]
    [InlineData(SettingsSection.Delivery, "hardware", "/settings/delivery/hardware")]
    [InlineData(SettingsSection.Delivery, "optimized-copies", "/settings/delivery/optimized-copies")]
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

        // Which AI features run is decided on the Plugins page, so Local AI keeps Status, Models & Hardware, Budget and Usage.
        Assert.Equal(["Status", "Models & Hardware", "Budget", "Usage"], labels);
        Assert.Equal("status", SettingsNav.GetDefaultSubsection(SettingsSection.LocalAi).Slug);
        Assert.Null(SettingsNav.ResolveSubsection(SettingsSection.LocalAi, "unknown"));
    }

    [Fact]
    public void MetadataManagement_HoldsOnlyTheIngestionFlow()
    {
        var subsections = SettingsNav.GetSubsections(SettingsSection.Providers).ToArray();

        Assert.Equal(["Ingestion Flow"], subsections.Select(item => item.Label));
        Assert.Equal(["ingestion-flow"], subsections.Select(item => item.Slug));
    }

    [Fact]
    public void SummaryFirstSections_UseRootRoutesWithoutRequiringSubsections()
    {
        Assert.Empty(SettingsNav.GetSubsections(SettingsSection.Profiles));
        Assert.Equal(["scheduled-tasks", "logs"], SettingsNav.GetSubsections(SettingsSection.AdminOverview).Select(item => item.Slug));
        Assert.Empty(SettingsNav.GetSubsections(SettingsSection.Review));
        Assert.Empty(SettingsNav.GetSubsections(SettingsSection.Server));
        Assert.Empty(SettingsNav.GetSubsections(SettingsSection.Tools));
    }

    [Fact]
    public void APersonsPreferencesTab_IsAvailableOnAPhone_ButTheirAccessTabIsNot()
    {
        var id = Guid.NewGuid().ToString("D");

        Assert.True(SettingsNav.IsMobileRouteAvailable(SettingsSection.Profiles, id, "preferences"));
        Assert.False(SettingsNav.IsMobileRouteAvailable(SettingsSection.Profiles, id, "access"));
    }

    [Fact]
    public void MobileAvailability_WithholdsComplexAdministrationButKeepsMonitoringSummaries()
    {
        Assert.Equal(SettingsMobileAvailability.Full, SettingsNav.GetMobileAvailability(SettingsSection.Profiles));
        Assert.Equal(SettingsMobileAvailability.SummaryOnly, SettingsNav.GetMobileAvailability(SettingsSection.AdminOverview));
        Assert.Equal(SettingsMobileAvailability.DesktopOnly, SettingsNav.GetMobileAvailability(SettingsSection.DevHarness));

        Assert.True(SettingsNav.IsMobileRouteAvailable(SettingsSection.Network, "overview"));
        Assert.False(SettingsNav.IsMobileRouteAvailable(SettingsSection.Network, "advanced"));
        Assert.True(SettingsNav.IsMobileRouteAvailable(SettingsSection.Providers, "ingestion-flow"));
        Assert.False(SettingsNav.IsMobileRouteAvailable(SettingsSection.Providers, "ingestion-flow", "tmdb"));
    }

    [Theory]
    [InlineData(SettingsSection.AdminOverview, "/settings/system")]
    [InlineData(SettingsSection.Libraries, "/settings/libraries")]
    [InlineData(SettingsSection.Ingestion, "/settings/ingestion")]
    [InlineData(SettingsSection.RecentlyAdded, "/settings/recently-added")]
    [InlineData(SettingsSection.DevHarness, "/settings/development-data")]
    [InlineData(SettingsSection.Providers, "/settings/metadata/ingestion-flow")]
    [InlineData(SettingsSection.LocalAi, "/settings/ai")]
    [InlineData(SettingsSection.Plugins, "/settings/plugins")]
    [InlineData(SettingsSection.Delivery, "/settings/delivery")]
    [InlineData(SettingsSection.Access, "/settings/access/users")]
    [InlineData(SettingsSection.Tools, "/settings/tools")]
    [InlineData(SettingsSection.Notifications, "/settings/notifications")]
    [InlineData(SettingsSection.Server, "/settings/backup-recovery")]
    [InlineData(SettingsSection.Review, "/settings/review")]
    [InlineData(SettingsSection.ProviderTester, "/settings/developer")]
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
    public void FilteredMenuGroups_Admin_RendersFiveGroupsOfFlatPagesInOrder()
    {
        var groups = SettingsNav.FilteredMenuGroups(true);

        Assert.Equal(["personal", "server", "platform", "system", "developer"], groups.Select(group => group.Key));
        Assert.Equal(["Personal", "Server", "Platform", "System", "Developer"], groups.Select(group => group.Label));

        Assert.Equal(["Profile", "Manage Profiles", "Security & Devices"], Labels(groups, "personal"));
        Assert.Equal(["Library & Ingestion", "Network & Remote Access", "Streaming", "Users & Households"], Labels(groups, "server"));
        Assert.Equal(["Local AI", "Plugins", "Integrations"], Labels(groups, "platform"));
        Assert.Equal(["System"], Labels(groups, "system"));
        Assert.Equal(["Developer Tools"], Labels(groups, "developer"));

        Assert.Equal(["Overview", "Activity", "Taste", "Import"], TabLabels(groups, "Profile"));
        Assert.Empty(TabLabels(groups, "Manage Profiles"));
        Assert.Equal(["Sign-in", "Devices & Sessions"], TabLabels(groups, "Security & Devices"));
        Assert.Equal(["Overview", "Libraries", "Ingestion Flow"], TabLabels(groups, "Library & Ingestion"));
        Assert.Equal(["Overview", "Local Network", "Remote Access", "Advanced"], TabLabels(groups, "Network & Remote Access"));
        Assert.Equal(["Limits & Quality", "Hardware Acceleration", "Optimized Copies", "Diagnostics"], TabLabels(groups, "Streaming"));
        Assert.Equal(["Users", "Sign-in Methods", "Households"], TabLabels(groups, "Users & Households"));
        Assert.Equal(["Status", "Models & Hardware", "Budget", "Usage"], TabLabels(groups, "Local AI"));
        Assert.Empty(TabLabels(groups, "Plugins"));
        Assert.Empty(TabLabels(groups, "Integrations"));
        Assert.Equal(["Overview", "Scheduled Tasks", "Logs", "Backups", "Configuration"], TabLabels(groups, "System"));
        Assert.Equal(["Provider Tester", "Enrichment Tester", "Development data"], TabLabels(groups, "Developer Tools"));

        var allLabels = groups.SelectMany(group => group.Nodes).Select(node => node.Label)
            .Concat(groups.SelectMany(group => group.Nodes).SelectMany(node => node.Tabs).Select(tab => tab.Label)).ToArray();
        foreach (var gone in new[] { "Privacy & Data", "System Overview", "Live Ingestion", "Needs Review", "Administration", "Advanced Settings", "Preferences", "Tools and requirements" })
        {
            Assert.DoesNotContain(gone, allLabels);
        }

        // Households lives only as a tab of Users & Households.
        Assert.Equal("/settings/access/households", Assert.Single(groups.SelectMany(group => group.Nodes).SelectMany(node => node.Tabs), tab => tab.Label == "Households").Route);
    }

    [Fact]
    public void MenuTree_GivesEveryPageAndTabAnIcon_AndNoTwoPagesOpenTheSameAddress()
    {
        var nodes = SettingsNav.MenuGroups.SelectMany(group => group.Nodes).ToArray();

        Assert.All(SettingsNav.MenuGroups, group => Assert.False(string.IsNullOrWhiteSpace(group.Icon)));
        Assert.All(nodes, node => Assert.False(string.IsNullOrWhiteSpace(node.Icon), $"{node.Label} has no icon."));
        Assert.All(nodes.SelectMany(node => node.Tabs), tab => Assert.False(string.IsNullOrWhiteSpace(tab.Icon), $"{tab.Label} has no icon."));
        // No two pages open the same page, and a page's tabs are distinct addresses.
        Assert.Equal(nodes.Length, nodes.Select(node => node.Route).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var node in nodes.Where(node => node.HasTabs))
        {
            Assert.Equal(node.Tabs.Count, node.Tabs.Select(tab => tab.Route).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(node.Tabs[0].Route, node.Route);
        }
    }

    [Fact]
    public void MenuTree_EveryPageAndTabOpensARealPageOfItsSection()
    {
        foreach (var node in SettingsNav.MenuGroups.SelectMany(group => group.Nodes))
        {
            var segment = node.Route.Split('/', StringSplitOptions.RemoveEmptyEntries)[1];
            var resolution = SettingsNav.ResolveRoute(segment, true);
            Assert.True(resolution.IsKnownRoute, node.Label);
            Assert.Contains(resolution.Section, node.Sections);

            foreach (var tab in node.Tabs)
            {
                var tabSegment = tab.Route.Split('/', StringSplitOptions.RemoveEmptyEntries)[1];
                Assert.Equal(tab.Section, SettingsNav.ResolveRoute(tabSegment, true).Section);
                if (tab.Subsection is not null)
                {
                    Assert.NotNull(SettingsNav.ResolveSubsection(tab.Section, tab.Subsection));
                }
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
        }

        // Needs Review is off the menu, so it opens beneath its page: Server, Library & Ingestion, Needs Review.
        var review = SettingsNav.FindMenuLocation(SettingsSection.Review)!;
        Assert.Equal("Library & Ingestion", review.Page.Label);
        Assert.Null(review.Tab);
        Assert.Equal("Needs Review", review.Leaf);
    }

    [Fact]
    public void FindMenuLocation_NamesThePageAndTabForEverySubPage()
    {
        var integrations = SettingsNav.FindMenuLocation(SettingsSection.Integrations)!;
        Assert.Equal("platform", integrations.Group.Key);
        Assert.Equal("Integrations", integrations.Page.Label);
        Assert.Null(integrations.Tab);

        var signIn = SettingsNav.FindMenuLocation(SettingsSection.Access, "authentication")!;
        Assert.Equal(["Users & Households", "Sign-in Methods"], [signIn.Page.Label, signIn.Tab!.Label]);
        Assert.Equal("server", signIn.Group.Key);

        Assert.Equal("Users", SettingsNav.FindMenuLocation(SettingsSection.Access, "users")!.Tab!.Label);
        Assert.Equal("Overview", SettingsNav.FindMenuLocation(SettingsSection.Network, "overview")!.Tab!.Label);
        var flow = SettingsNav.FindMenuLocation(SettingsSection.Providers, "ingestion-flow")!;
        Assert.Equal(["Library & Ingestion", "Ingestion Flow"], [flow.Page.Label, flow.Tab!.Label]);
    }

    [Theory]
    [InlineData("preferences")]
    [InlineData("playback")]
    [InlineData("listening")]
    [InlineData("reading")]
    [InlineData("home")]
    public void RetiredPreferencePages_AreNotAddresses_BecauseTheyAreTabsOfAProfile(string segment)
    {
        var resolution = SettingsNav.ResolveRoute(segment, false);

        Assert.False(resolution.IsKnownRoute);
        Assert.Equal("/not-found", resolution.CanonicalRoute);
    }

    [Theory]
    [InlineData("profiles", SettingsSection.Profiles, "/settings/profiles")]
    [InlineData("security", SettingsSection.Security, "/settings/security")]
    [InlineData("system", SettingsSection.AdminOverview, "/settings/system")]
    [InlineData("libraries", SettingsSection.Libraries, "/settings/libraries")]
    [InlineData("ingestion", SettingsSection.Ingestion, "/settings/ingestion")]
    [InlineData("recently-added", SettingsSection.RecentlyAdded, "/settings/recently-added")]
    [InlineData("metadata", SettingsSection.Providers, "/settings/metadata/ingestion-flow")]
    [InlineData("network", SettingsSection.Network, "/settings/network/overview")]
    [InlineData("delivery", SettingsSection.Delivery, "/settings/delivery")]
    [InlineData("tools", SettingsSection.Tools, "/settings/tools")]
    [InlineData("notifications", SettingsSection.Notifications, "/settings/notifications")]
    [InlineData("access", SettingsSection.Access, "/settings/access/users")]
    [InlineData("backup-recovery", SettingsSection.Server, "/settings/backup-recovery")]
    [InlineData("ai", SettingsSection.LocalAi, "/settings/ai")]
    [InlineData("plugins", SettingsSection.Plugins, "/settings/plugins")]
    [InlineData("developer", SettingsSection.ProviderTester, "/settings/developer")]
    [InlineData("provider-tester", SettingsSection.ProviderTester, "/settings/developer")]
    [InlineData("enrichment-tester", SettingsSection.EnrichmentTester, "/settings/enrichment-tester")]
    [InlineData("development-data", SettingsSection.DevHarness, "/settings/development-data")]
    [InlineData("dev-harness", SettingsSection.DevHarness, "/settings/development-data")]
    [InlineData("harness", SettingsSection.DevHarness, "/settings/development-data")]
    [InlineData("ingestion-harness", SettingsSection.DevHarness, "/settings/development-data")]
    [InlineData("test-harness", SettingsSection.DevHarness, "/settings/development-data")]
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
    [InlineData(SettingsSection.Providers, "ingestion-flow")]
    [InlineData(SettingsSection.Access, "users")]
    [InlineData(SettingsSection.Access, "authentication")]
    [InlineData(SettingsSection.LocalAi, "models")]
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
        // Manage Profiles is everyone's own to look after (a member lands on their own profile); a Kids profile sees Profile only.
        Assert.Equal(["Profile", "Manage Profiles", "Security & Devices"], groups[0].Nodes.Select(node => node.Label));
        Assert.Equal(["Profile"], SettingsNav.FilteredMenuGroups(false, restricted: true).Single().Nodes.Select(node => node.Label));
    }

    [Fact]
    public void ServerAdministrator_SeesHouseholdsAsATabOfUsersAndHouseholds()
    {
        var groups = SettingsNav.FilteredMenuGroups(true);

        Assert.Contains("Households", TabLabels(groups, "Users & Households"));
        Assert.Equal("/settings/profiles", SettingsNav.RouteFor(SettingsSection.Profiles));
        Assert.Equal("/settings/access/households", SettingsNav.RouteFor(SettingsSection.Access, "households"));
        Assert.Throws<ArgumentOutOfRangeException>(() => SettingsNav.RouteFor(SettingsSection.Profiles, "households"));
    }

    [Fact]
    public void ManageProfiles_IsOpenToEveryoneSignedIn_AndAKidsProfileGetsProfileOnly()
    {
        Assert.True(SettingsNav.IsVisible(SettingsSection.Profiles, administrationAllowed: false));
        Assert.True(SettingsNav.ResolveRoute("profiles", false).RequestedSectionAllowed);
        Assert.True(SettingsNav.ResolveRoute("profiles", true).RequestedSectionAllowed);
        Assert.False(SettingsNav.ResolveRoute("profiles", false, restricted: true).RequestedSectionAllowed);
        Assert.True(SettingsNav.ResolveRoute("overview", false, restricted: true).RequestedSectionAllowed);

        // The bare /settings address opens the person's own Profile, whatever they are.
        Assert.Equal(SettingsSection.Overview, SettingsNav.ResolveRoute(null, false).Section);
        Assert.Equal(SettingsSection.Overview, SettingsNav.ResolveRoute(null, false, restricted: true).Section);
        Assert.Equal(SettingsSection.Overview, SettingsNav.ResolveRoute(null, true).Section);
    }

    [Fact]
    public void DeveloperTools_OpensOnTheProviderTester_AndHasThreeTabs()
    {
        var groups = SettingsNav.FilteredMenuGroups(true);
        var developerTools = Assert.Single(groups.SelectMany(group => group.Nodes), node => node.Label == "Developer Tools");

        Assert.Equal(SettingsSection.ProviderTester, developerTools.Section);
        Assert.Equal("/settings/developer", developerTools.Route);
        Assert.Equal(
            [(SettingsSection.ProviderTester, "Provider Tester"), (SettingsSection.EnrichmentTester, "Enrichment Tester"), (SettingsSection.DevHarness, "Development data")],
            developerTools.Tabs.Select(tab => (tab.Section, tab.Label)));
    }

    [Fact]
    public void ProviderTesterAndDevelopmentData_KeepTheirDesktopOnlyAvailability()
    {
        Assert.Equal(SettingsMobileAvailability.DesktopOnly, SettingsNav.GetMobileAvailability(SettingsSection.ProviderTester));
        Assert.Equal(SettingsMobileAvailability.DesktopOnly, SettingsNav.GetMobileAvailability(SettingsSection.EnrichmentTester));
        Assert.Equal(SettingsMobileAvailability.DesktopOnly, SettingsNav.GetMobileAvailability(SettingsSection.DevHarness));
    }

    [Fact]
    public void DeveloperTools_IsNotShownToAnyoneWhoIsNotAServerAdministrator()
    {
        var groups = SettingsNav.FilteredMenuGroups(administrationAllowed: false);

        Assert.DoesNotContain(groups, group => group.Key == "developer");
        Assert.DoesNotContain(
            groups.SelectMany(group => group.Nodes),
            node => node.Sections.Any(section => section is SettingsSection.ProviderTester or SettingsSection.EnrichmentTester or SettingsSection.DevHarness));
    }

    private static string[] Labels(IReadOnlyList<SettingsMenuGroup> groups, string groupKey) =>
        groups.Single(group => group.Key == groupKey).Nodes.Select(node => node.Label).ToArray();

    private static string[] TabLabels(IReadOnlyList<SettingsMenuGroup> groups, string pageLabel) =>
        groups.SelectMany(group => group.Nodes)
            .First(node => node.Label == pageLabel)
            .Tabs.Select(tab => tab.Label).ToArray();

    [Theory]
    [InlineData("review", SettingsSection.Review, "/settings/review")]
    [InlineData("provider-tester", SettingsSection.ProviderTester, "/settings/developer")]
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
    [InlineData("platform", SettingsSection.LocalAi, "/settings/ai")]
    [InlineData("system", SettingsSection.AdminOverview, "/settings/system")]
    [InlineData("developer", SettingsSection.ProviderTester, "/settings/developer")]
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
        Assert.False(SettingsNav.IsMobileRouteAvailable(SettingsSection.Profiles, "households"));
        Assert.False(SettingsNav.IsMobileRouteAvailable(SettingsSection.Profiles, id, "extra"));
        Assert.Null(SettingsNav.ResolveSubsection(SettingsSection.Profiles, "household"));
        Assert.Null(SettingsNav.ResolveSubsection(SettingsSection.Profiles, "households"));
        Assert.Equal("households", SettingsNav.ResolveSubsection(SettingsSection.Access, "households")?.Slug);
    }

    [Fact]
    public void SettingsPage_ShowsThePersonPageForAProfilesIdAndNotFoundForAnythingElse()
    {
        var settingsSource = LocalizedSource.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Components/Pages/Settings.razor"));

        Assert.Contains("<ProfileDetailPage ProfileId=\"@ProfileDetailId\" Tab=\"@ProfileTab\" NameChanged=\"OnProfileNameChanged\" />", settingsSource, StringComparison.Ordinal);
        Assert.Contains("_activeSection == SettingsSection.Profiles && Guid.TryParse(Subsection, out _)", settingsSource, StringComparison.Ordinal);
        Assert.Contains("- Manage Profiles - Library", settingsSource, StringComparison.Ordinal);
        // A non-id subsection still goes through the normal lookup, which knows no subsection for Profiles and so ends at /not-found otherwise.
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
        Assert.Contains(SettingsNav.AllItems, item => item.Label == "Library & Ingestion");
        Assert.DoesNotContain(SettingsNav.AllItems, item => item.Label == "Live Ingestion");
    }

    [Fact]
    public void AccountWithoutAdministration_SeesOwnProfileAndSecurityButNotReviewOrServerAdministration()
    {
        var visible = SettingsNav.FilteredMenuGroups(false).SelectMany(group => group.Nodes)
            .SelectMany(node => node.Sections)
            .ToArray();

        Assert.Contains(SettingsSection.Profiles, visible);
        Assert.Contains(SettingsSection.Security, visible);
        Assert.DoesNotContain(SettingsSection.Review, visible);
        Assert.DoesNotContain(SettingsSection.Server, visible);
        Assert.DoesNotContain(SettingsSection.Providers, visible);
        Assert.False(SettingsNav.ResolveRoute("access", false).RequestedSectionAllowed);
        Assert.False(SettingsNav.ResolveRoute("network", false).RequestedSectionAllowed);
    }

    [Fact]
    public void Member_PersonalPages_AreProfileThenManageProfilesThenSecurityAndDevices()
    {
        var personal = SettingsNav.FilteredMenuGroups(false).Single(group => group.Key == "personal");

        Assert.Equal(["/settings/overview", "/settings/profiles", "/settings/security"], personal.Nodes.Select(node => node.Route));
        // Playback, Listening, Reading and Home are tabs of a profile, not pages.
        Assert.Equal([SettingsSection.Profiles], personal.Nodes[1].Sections);
    }

    [Fact]
    public void ResolveRoute_Household_MapsToManageProfiles()
    {
        var household = SettingsNav.ResolveRoute("household", false);
        Assert.Equal(SettingsSection.Profiles, household.Section);
        Assert.False(household.IsCanonicalRoute);
        Assert.True(household.RequestedSectionAllowed);
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
    public void ServerAdministrator_HasHouseholdsInUsersAndHouseholds_AndThereIsNoMyHouseholdTab()
    {
        var personalLabels = SettingsNav.FilteredMenuGroups(true).Single(group => group.Key == "personal").Nodes
            .SelectMany(node => node.Tabs.Select(tab => tab.Label).Append(node.Label))
            .ToArray();

        Assert.DoesNotContain("My household", personalLabels);
        Assert.DoesNotContain(SettingsNav.GetSubsections(SettingsSection.Access), subsection => subsection.Slug == "household");
        Assert.Contains(SettingsNav.GetSubsections(SettingsSection.Access), subsection => subsection.Slug == "households" && subsection.Label == "Households");
        Assert.Contains(SettingsNav.GetSubsections(SettingsSection.Access), subsection => subsection.Slug == "users" && subsection.Label == "Users");
        Assert.False(SettingsNav.IsMobileRouteAvailable(SettingsSection.Access, "household"));
        // The old address still resolves, to the Users tab.
        Assert.Equal("users", SettingsNav.ResolveSubsection(SettingsSection.Access, "household")?.Slug);
        Assert.Equal("/settings/access/users", SettingsNav.RouteFor(SettingsSection.Access, "household"));
    }

    [Fact]
    public void PlainMember_DoesNotSeeUsersAndAccess_AndDirectNavigationIsRefused()
    {
        var visible = SettingsNav.FilteredMenuGroups(false).SelectMany(group => group.Nodes)
            .SelectMany(node => node.Sections)
            .ToArray();

        Assert.DoesNotContain(SettingsSection.Access, visible);
        var access = SettingsNav.ResolveRoute("access", false);
        Assert.False(access.RequestedSectionAllowed);
        Assert.True(access.ShouldRedirect);
    }

    private static string GetRepoFilePath(string relativePath) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));
}
