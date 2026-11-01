using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Tests;

public sealed class SettingsNavTests
{
    [Theory]
    [InlineData("users")]
    [InlineData("households")]
    [InlineData("advanced")]
    public void CanonicalAccessSections_AreAvailableOnMobile(string subsection)
    {
        Assert.True(SettingsNav.IsMobileRouteAvailable(SettingsSection.Access, subsection));
        Assert.False(SettingsNav.IsMobileRouteAvailable(SettingsSection.Access, subsection, "unknown-detail"));
    }

    [Fact]
    public void TheLibrariesPage_HasFiveTabsInOrder_EachNamedFromTheWordingFile()
    {
        var page = SettingsNav.MenuGroups.SelectMany(group => group.Nodes).Single(node => node.Section == SettingsSection.Libraries);

        Assert.Equal(["Libraries", "Activity", "Needs Review", "Matching", "Rules"], page.Tabs.Select(tab => tab.Label).ToArray());
        Assert.Equal(
            ["SettingsLibrariesTabs_Libraries", "SettingsLibrariesTabs_Activity", "SettingsLibrariesTabs_NeedsReview", "SettingsLibrariesTabs_Matching", "SettingsLibrariesTabs_Rules"],
            page.Tabs.Select(tab => tab.LabelKey!).ToArray());
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
        Assert.Contains("<RecentlyAddedPageContent", settingsSource, StringComparison.Ordinal);
        Assert.Contains("<LibraryRulesPage", settingsSource, StringComparison.Ordinal);
        Assert.Contains("<GeneralPage", settingsSource, StringComparison.Ordinal);
        Assert.Contains("<ToolsRequirementsTab", settingsSource, StringComparison.Ordinal);
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
        Assert.Equal(SettingsSection.Overview, SettingsNav.GetDefaultSection("personal"));
    }

    [Fact]
    public void Profile_HasOverviewActivityTasteLinkedAccountsAndImportAsTabs_AndNoAppsPage()
    {
        var profile = SettingsNav.MenuGroups[0].Nodes[0];
        Assert.Equal("Profile", profile.Label);
        Assert.Equal(["Overview", "Activity", "Taste", "Linked accounts", "Import"], profile.Tabs.Select(tab => tab.Label));
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

        // The app links and QR code moved to General, so Profile has no Apps tab, page or address.
        Assert.DoesNotContain(SettingsNav.MenuGroups.SelectMany(group => group.Nodes).SelectMany(node => node.Tabs), tab => tab.Label == "Apps");
        Assert.Null(SettingsNav.ResolveSubsection(SettingsSection.Overview, "apps"));
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
    [InlineData(SettingsSection.LocalAi, "advanced", "/settings/ai/advanced")]
    [InlineData(SettingsSection.Playback, "advanced", "/settings/playback/advanced")]
    [InlineData(SettingsSection.Network, "home", "/settings/network/home")]
    [InlineData(SettingsSection.Network, "advanced", "/settings/network/advanced")]
    [InlineData(SettingsSection.Access, "advanced", "/settings/access/advanced")]
    [InlineData(SettingsSection.Backups, "destinations", "/settings/backups/destinations")]
    [InlineData(SettingsSection.AdminOverview, "advanced", "/settings/system/advanced")]
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

        // Which AI features run is decided on the Plugins page, so Local AI keeps Status, Models and Advanced.
        Assert.Equal(["Status", "Models", "Advanced"], labels);
        Assert.Equal("status", SettingsNav.GetDefaultSubsection(SettingsSection.LocalAi).Slug);
        Assert.Null(SettingsNav.ResolveSubsection(SettingsSection.LocalAi, "unknown"));
    }

    [Fact]
    public void Matching_IsATabOfLibraries_AtItsOwnAddress()
    {
        Assert.Empty(SettingsNav.GetSubsections(SettingsSection.Providers));
        Assert.Equal("Matching", SettingsNav.GetItem(SettingsSection.Providers).Label);
        Assert.Equal("/settings/matching", SettingsNav.RouteFor(SettingsSection.Providers));
        var location = SettingsNav.FindMenuLocation(SettingsSection.Providers)!;
        Assert.Equal(["Libraries", "Matching"], [location.Page.Label, location.Tab!.Label]);
    }

    [Fact]
    public void SummaryFirstSections_UseRootRoutesWithoutRequiringSubsections()
    {
        Assert.Empty(SettingsNav.GetSubsections(SettingsSection.Profiles));
        Assert.Equal(["scheduled-tasks", "logs", "advanced"], SettingsNav.GetSubsections(SettingsSection.AdminOverview).Select(item => item.Slug));
        Assert.Empty(SettingsNav.GetSubsections(SettingsSection.Review));
        Assert.Empty(SettingsNav.GetSubsections(SettingsSection.LibraryRules));
        Assert.Equal(["destinations", "advanced"], SettingsNav.GetSubsections(SettingsSection.Backups).Select(item => item.Slug));
        Assert.Empty(SettingsNav.GetSubsections(SettingsSection.Tools));
        Assert.Empty(SettingsNav.GetSubsections(SettingsSection.General));
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

        Assert.True(SettingsNav.IsMobileRouteAvailable(SettingsSection.Network, "home"));
        Assert.False(SettingsNav.IsMobileRouteAvailable(SettingsSection.Network, "advanced"));
        Assert.True(SettingsNav.IsMobileRouteAvailable(SettingsSection.Providers));
        Assert.False(SettingsNav.IsMobileRouteAvailable(SettingsSection.Providers, null, "tmdb"));
        Assert.False(SettingsNav.IsMobileRouteAvailable(SettingsSection.Playback, "advanced"));
    }

    [Theory]
    [InlineData(SettingsSection.AdminOverview, "/settings/system")]
    [InlineData(SettingsSection.Libraries, "/settings/libraries")]
    [InlineData(SettingsSection.RecentlyAdded, "/settings/library-activity")]
    [InlineData(SettingsSection.DevHarness, "/settings/development-data")]
    [InlineData(SettingsSection.Providers, "/settings/matching")]
    [InlineData(SettingsSection.LibraryRules, "/settings/rules")]
    [InlineData(SettingsSection.LocalAi, "/settings/ai")]
    [InlineData(SettingsSection.Plugins, "/settings/plugins")]
    [InlineData(SettingsSection.Playback, "/settings/playback")]
    [InlineData(SettingsSection.Access, "/settings/access/users")]
    [InlineData(SettingsSection.Tools, "/settings/tools")]
    [InlineData(SettingsSection.General, "/settings/general")]
    [InlineData(SettingsSection.Backups, "/settings/backups")]
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
    [InlineData("ingestion")]
    [InlineData("recently-added")]
    [InlineData("metadata")]
    [InlineData("delivery")]
    [InlineData("notifications")]
    [InlineData("backup-recovery")]
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
        var resolution = SettingsNav.ResolveRoute("libraries", false);

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
    public void FilteredMenuGroups_Admin_RendersFourGroupsOfFlatPagesInOrder()
    {
        var groups = SettingsNav.FilteredMenuGroups(true);

        Assert.Equal(["personal", "library", "server", "developer"], groups.Select(group => group.Key));
        Assert.Equal(["Personal", "Library", "Server", "Developer"], groups.Select(group => group.Label));

        Assert.Equal(["Profile", "Manage Profiles", "Security & Devices"], Labels(groups, "personal"));
        Assert.Equal(["Libraries", "Playback"], Labels(groups, "library"));
        Assert.Equal(
            ["General", "Users & Households", "Network & Remote Access", "Tools", "Local AI", "Plugins", "Integrations", "Backups", "System"],
            Labels(groups, "server"));
        Assert.Equal(["Developer Tools"], Labels(groups, "developer"));

        Assert.Equal(["Overview", "Activity", "Taste", "Linked accounts", "Import"], TabLabels(groups, "Profile"));
        Assert.Empty(TabLabels(groups, "Manage Profiles"));
        Assert.Equal(["Sign-in", "Devices & Sessions"], TabLabels(groups, "Security & Devices"));
        Assert.Equal(["Libraries", "Activity", "Needs Review", "Matching", "Rules"], TabLabels(groups, "Libraries"));
        Assert.Equal(["Quality", "Advanced"], TabLabels(groups, "Playback"));
        Assert.Empty(TabLabels(groups, "General"));
        Assert.Equal(["Users", "Households", "Advanced"], TabLabels(groups, "Users & Households"));
        Assert.Equal(["Home network", "Remote access", "Advanced"], TabLabels(groups, "Network & Remote Access"));
        Assert.Empty(TabLabels(groups, "Tools"));
        Assert.Equal(["Status", "Models", "Advanced"], TabLabels(groups, "Local AI"));
        Assert.Empty(TabLabels(groups, "Plugins"));
        Assert.Empty(TabLabels(groups, "Integrations"));
        Assert.Equal(["Backups", "Destinations", "Advanced"], TabLabels(groups, "Backups"));
        Assert.Equal(["Health", "Scheduled tasks", "Logs", "Advanced"], TabLabels(groups, "System"));
        Assert.Equal(["Provider Tester", "Enrichment Tester", "Development data"], TabLabels(groups, "Developer Tools"));

        // A page's Advanced tab is always its last tab, and no other tab is drawn as Advanced (settings-and-setup-simplified D1).
        foreach (var node in groups.SelectMany(group => group.Nodes).Where(node => node.HasTabs))
        {
            Assert.All(node.Tabs.Take(node.Tabs.Count - 1), tab => Assert.False(tab.IsAdvanced, $"{node.Label}: {tab.Label}"));
            Assert.Equal(node.Tabs[^1].Label == "Advanced", node.Tabs[^1].IsAdvanced);
        }

        var allLabels = groups.SelectMany(group => group.Nodes).Select(node => node.Label)
            .Concat(groups.SelectMany(group => group.Nodes).SelectMany(node => node.Tabs).Select(tab => tab.Label)).ToArray();
        foreach (var gone in new[] { "Privacy & Data", "System Overview", "Live Ingestion", "Library & Ingestion", "Streaming", "Configuration", "Sign-in Methods", "Administration", "Advanced Settings", "Preferences", "Tools and requirements" })
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

        // Needs Review is a tab of Libraries: Library, Libraries, Needs Review.
        var review = SettingsNav.FindMenuLocation(SettingsSection.Review)!;
        Assert.Equal("Libraries", review.Page.Label);
        Assert.Equal("Needs Review", review.Tab!.Label);
        Assert.Null(review.Leaf);
    }

    [Fact]
    public void FindMenuLocation_NamesThePageAndTabForEverySubPage()
    {
        var integrations = SettingsNav.FindMenuLocation(SettingsSection.Integrations)!;
        Assert.Equal("server", integrations.Group.Key);
        Assert.Equal("Integrations", integrations.Page.Label);
        Assert.Null(integrations.Tab);

        var advanced = SettingsNav.FindMenuLocation(SettingsSection.Access, "advanced")!;
        Assert.Equal(["Users & Households", "Advanced"], [advanced.Page.Label, advanced.Tab!.Label]);
        Assert.True(advanced.Tab.IsAdvanced);
        Assert.Equal("server", advanced.Group.Key);

        Assert.Equal("Users", SettingsNav.FindMenuLocation(SettingsSection.Access, "users")!.Tab!.Label);
        Assert.Equal("Home network", SettingsNav.FindMenuLocation(SettingsSection.Network, "home")!.Tab!.Label);
        var matching = SettingsNav.FindMenuLocation(SettingsSection.Providers)!;
        Assert.Equal(["library", "Libraries", "Matching"], [matching.Group.Key, matching.Page.Label, matching.Tab!.Label]);
    }

    [Theory]
    [InlineData("preferences")]
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
    [InlineData("library-activity", SettingsSection.RecentlyAdded, "/settings/library-activity")]
    [InlineData("matching", SettingsSection.Providers, "/settings/matching")]
    [InlineData("rules", SettingsSection.LibraryRules, "/settings/rules")]
    [InlineData("network", SettingsSection.Network, "/settings/network/home")]
    [InlineData("playback", SettingsSection.Playback, "/settings/playback")]
    [InlineData("general", SettingsSection.General, "/settings/general")]
    [InlineData("tools", SettingsSection.Tools, "/settings/tools")]
    [InlineData("access", SettingsSection.Access, "/settings/access/users")]
    [InlineData("backups", SettingsSection.Backups, "/settings/backups")]
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
    [InlineData(SettingsSection.Network, "home")]
    [InlineData(SettingsSection.Network, "remote")]
    [InlineData(SettingsSection.Network, "advanced")]
    [InlineData(SettingsSection.Access, "users")]
    [InlineData(SettingsSection.Access, "households")]
    [InlineData(SettingsSection.Access, "advanced")]
    [InlineData(SettingsSection.LocalAi, "status")]
    [InlineData(SettingsSection.LocalAi, "models")]
    [InlineData(SettingsSection.LocalAi, "advanced")]
    [InlineData(SettingsSection.Playback, "advanced")]
    [InlineData(SettingsSection.Backups, "destinations")]
    [InlineData(SettingsSection.Backups, "advanced")]
    [InlineData(SettingsSection.AdminOverview, "scheduled-tasks")]
    [InlineData(SettingsSection.AdminOverview, "logs")]
    [InlineData(SettingsSection.AdminOverview, "advanced")]
    public void EveryTabAddressOfTheSimplifiedSettings_Resolves(SettingsSection section, string slug)
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
    [InlineData(SettingsSection.Playback, "scheduling")]
    [InlineData(SettingsSection.Playback, "storage")]
    [InlineData(SettingsSection.Playback, "active-jobs")]
    [InlineData(SettingsSection.Playback, "hardware")]
    [InlineData(SettingsSection.Playback, "optimized-copies")]
    [InlineData(SettingsSection.Playback, "diagnostics")]
    [InlineData(SettingsSection.Network, "overview")]
    [InlineData(SettingsSection.Network, "local")]
    [InlineData(SettingsSection.Access, "authentication")]
    [InlineData(SettingsSection.LocalAi, "budget")]
    [InlineData(SettingsSection.LocalAi, "usage")]
    [InlineData(SettingsSection.Providers, "ingestion-flow")]
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
    [InlineData("library", SettingsSection.Libraries, "/settings/libraries")]
    [InlineData("server", SettingsSection.General, "/settings/general")]
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
    public void TheLibraryAndIngestionOverview_IsGone_AndItsAddressIsNotFound()
    {
        var resolution = SettingsNav.ResolveRoute("ingestion", true);

        Assert.False(resolution.IsKnownRoute);
        Assert.Equal("/not-found", resolution.CanonicalRoute);
        Assert.DoesNotContain(SettingsNav.AllItems, item => item.Label is "Library & Ingestion" or "Live Ingestion");
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
        Assert.DoesNotContain(SettingsSection.Backups, visible);
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
        // Retired addresses are not forwarded (settings-and-setup-simplified D19).
        Assert.Null(SettingsNav.ResolveSubsection(SettingsSection.Access, "household"));
        Assert.Throws<ArgumentOutOfRangeException>(() => SettingsNav.RouteFor(SettingsSection.Access, "household"));
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
