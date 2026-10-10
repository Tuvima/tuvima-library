using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Tests;

public sealed class SettingsNavTests
{
    [Theory]
    [InlineData("household")]
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
        Assert.DoesNotContain("<SettingsReviewQueueTab", settingsSource, StringComparison.Ordinal);
        Assert.DoesNotContain("<SettingsSubsectionNav", settingsSource, StringComparison.Ordinal);
        Assert.Contains("<SidebarPageHeader", settingsSource, StringComparison.Ordinal);
        Assert.DoesNotContain("<SidebarPageShell", settingsSource, StringComparison.Ordinal);
        Assert.DoesNotContain("<SidebarNavGroup", settingsSource, StringComparison.Ordinal);
        Assert.Contains("media-section-shell__rail-item--child", mediaShellSource, StringComparison.Ordinal);
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
        Assert.Contains("<LibrariesTab Subsection=\"@_activeSubsection\" Scope=\"@LibraryScope\" LibraryNameChanged=\"OnLibraryNameChanged\" />", settingsSource, StringComparison.Ordinal);
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
    public void RouteFor_Overview_UsesProfileSettingsUrl()
    {
        Assert.Equal("/settings/profile", SettingsNav.RouteFor(SettingsSection.Overview));
    }

    [Theory]
    [InlineData(SettingsSection.LocalAi, "models", "/settings/ai/models")]
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

        Assert.Equal([
            "Models & Runtime",
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
        Assert.Empty(SettingsNav.GetSubsections(SettingsSection.Overview));
        Assert.Empty(SettingsNav.GetSubsections(SettingsSection.Playback));
        Assert.Empty(SettingsNav.GetSubsections(SettingsSection.AdminOverview));
        Assert.Empty(SettingsNav.GetSubsections(SettingsSection.Review));
        Assert.Empty(SettingsNav.GetSubsections(SettingsSection.Server));
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
        Assert.Equal("/settings/profile", resolution.CanonicalRoute);
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

        Assert.Equal(SettingsSection.Overview, resolution.Section);
        Assert.Equal("/settings/profile", resolution.CanonicalRoute);
        Assert.True(resolution.IsCanonicalRoute);
        Assert.True(resolution.IsKnownRoute);
        Assert.True(resolution.RequestedSectionAllowed);
        Assert.False(resolution.ShouldRedirect);
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
    [InlineData("security")]
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
    public void ResolveRoute_DisallowedAdminPage_FallsBackToUserOverview()
    {
        var resolution = SettingsNav.ResolveRoute("metadata", false);

        Assert.Equal(SettingsSection.Overview, resolution.Section);
        Assert.Equal("/settings/profile", resolution.CanonicalRoute);
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
    public void FilteredTreeGroups_Admin_RendersUserAndAdminSettingsTree()
    {
        var groups = SettingsNav.FilteredTreeGroups(true).Select(group => group.Key).ToArray();

        Assert.Equal(["personal", "administration", "advanced"], groups);

        var userLabels = SettingsNav.FilteredTreeItems(SettingsNav.TreeGroups.Single(group => group.Key == "personal"), true)
            .Select(item => item.Label)
            .ToArray();

        Assert.Equal(["Profile", "Security", "Playback & Reading"], userLabels);

        var adminLabels = SettingsNav.FilteredTreeItems(SettingsNav.TreeGroups.Single(group => group.Key == "administration"), true)
            .Select(item => item.Label)
            .ToArray();

        Assert.Equal([
            "System Overview",
            "Network & Remote Access",
            "Playback & Delivery",
            "Users & Access",
            "Backup & Recovery",
        ], adminLabels);

        var adminGroup = SettingsNav.TreeGroups.Single(group => group.Key == "administration");
        var childGroups = SettingsNav.FilteredChildTreeGroups(adminGroup, true).Select(group => group.Key).ToArray();

        Assert.Equal(["library-ingestion"], childGroups);
        var libraryIngestion = SettingsNav.TreeGroups.Single(group => group.Key == "library-ingestion");
        Assert.Equal("Library & Ingestion", libraryIngestion.Label);
        Assert.Equal(SettingsSection.Ingestion, libraryIngestion.DefaultSection);
        Assert.Equal([
            "Live Ingestion",
            "Recently Added",
            "Libraries",
            "Metadata Providers",
        ], SettingsNav.FilteredTreeItems(libraryIngestion, true).Select(item => item.Label));
        Assert.False(SettingsNav.AllItems.Single(item => item.Value == SettingsSection.Libraries).SectionBreakBefore);

        Assert.DoesNotContain("Reg" + "istry", adminLabels);
        Assert.DoesNotContain("Maintenance", adminLabels);
        Assert.DoesNotContain("Provider Tester", adminLabels);
        Assert.DoesNotContain("Enrichment Tester", adminLabels);
        Assert.DoesNotContain("Wikidata", adminLabels);
        Assert.DoesNotContain("AI Models", adminLabels);
        Assert.DoesNotContain("AI Features", adminLabels);
        Assert.DoesNotContain("AI Vocabulary", adminLabels);
        Assert.DoesNotContain("AI Schedule", adminLabels);
        Assert.DoesNotContain("Encode", adminLabels);
        Assert.DoesNotContain("Offline Variants", adminLabels);
        Assert.DoesNotContain("Security", adminLabels);
        Assert.DoesNotContain("Users", adminLabels);
    }

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
    [InlineData("administration", SettingsSection.AdminOverview, "/settings/system")]
    [InlineData("personal", SettingsSection.Overview, "/settings/profile")]
    public void GroupDefaults_ResolveToExpectedCanonicalRoutes(string groupKey, SettingsSection expectedSection, string expectedRoute)
    {
        var section = SettingsNav.GetDefaultSection(groupKey);

        Assert.Equal(expectedSection, section);
        Assert.Equal(expectedRoute, SettingsNav.RouteFor(section));
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
        Assert.Contains(SettingsNav.AllItems, item => item.Label == "Live Ingestion");
    }

    [Fact]
    public void AccountWithoutAdministration_SeesOwnSecurityButNotReviewOrServerAdministration()
    {
        var visible = SettingsNav.TreeGroups
            .SelectMany(group => SettingsNav.FilteredTreeItems(group, false))
            .Select(item => item.Value)
            .ToArray();

        Assert.Contains(SettingsSection.Overview, visible);
        Assert.Contains(SettingsSection.Playback, visible);
        Assert.Contains(SettingsSection.Account, visible);
        Assert.DoesNotContain(SettingsSection.Review, visible);
        Assert.DoesNotContain(SettingsSection.Server, visible);
        Assert.DoesNotContain(SettingsSection.Providers, visible);
    }

    [Fact]
    public void HouseholdAdministrator_SeesMyHouseholdButNoServerAdministration()
    {
        var groups = SettingsNav.FilteredTreeGroups(false, true).Select(group => group.Key).ToArray();
        var personalLabels = SettingsNav.FilteredTreeItems(SettingsNav.TreeGroups.Single(group => group.Key == "personal"), false, true)
            .Select(item => item.Label)
            .ToArray();

        Assert.Equal(["personal"], groups);
        Assert.Contains("My household", personalLabels);
        Assert.Equal(SettingsSection.Household, SettingsNav.ResolveRoute("household", false, true).Section);
        Assert.True(SettingsNav.ResolveRoute("household", false, true).RequestedSectionAllowed);
        Assert.False(SettingsNav.ResolveRoute("access", false, true).RequestedSectionAllowed);
        Assert.False(SettingsNav.ResolveRoute("network", false, true).RequestedSectionAllowed);
    }

    [Fact]
    public void ServerAdministrator_ManagesTheirHouseholdInsideUsersAndAccess()
    {
        var personalLabels = SettingsNav.FilteredTreeItems(SettingsNav.TreeGroups.Single(group => group.Key == "personal"), true, true)
            .Select(item => item.Label)
            .ToArray();

        Assert.DoesNotContain("My household", personalLabels);
        Assert.Contains(SettingsNav.GetSubsections(SettingsSection.Access), subsection => subsection.Slug == "household" && subsection.Label == "My household");
        Assert.Contains(SettingsNav.GetSubsections(SettingsSection.Access), subsection => subsection.Slug == "users" && subsection.Label == "Households");
        Assert.True(SettingsNav.IsMobileRouteAvailable(SettingsSection.Access, "household"));
    }

    [Fact]
    public void PlainMember_DoesNotSeeMyHouseholdOrUsersAndAccess_AndDirectNavigationIsRefused()
    {
        var visible = SettingsNav.TreeGroups
            .SelectMany(group => SettingsNav.FilteredTreeItems(group, false, false))
            .Select(item => item.Value)
            .ToArray();

        Assert.DoesNotContain(SettingsSection.Household, visible);
        Assert.DoesNotContain(SettingsSection.Access, visible);
        var household = SettingsNav.ResolveRoute("household", false, false);
        Assert.False(household.RequestedSectionAllowed);
        Assert.True(household.ShouldRedirect);
        var access = SettingsNav.ResolveRoute("access", false, false);
        Assert.False(access.RequestedSectionAllowed);
        Assert.True(access.ShouldRedirect);
    }

    private static string GetRepoFilePath(string relativePath) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));
}
