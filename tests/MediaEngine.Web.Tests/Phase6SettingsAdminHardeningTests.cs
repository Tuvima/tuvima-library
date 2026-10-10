using MediaEngine.Web.Services.Ui;
namespace MediaEngine.Web.Tests;

public sealed class Phase6SettingsAdminHardeningTests
{
    [Fact]
    public void SystemOverview_RemainsConciseAndRoutesProcessingToIngestion()
    {
        var source = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/OverviewTab.razor");

        Assert.Contains("System exceptions", source, StringComparison.Ordinal);
        Assert.DoesNotContain("System Status", source, StringComparison.Ordinal);
        Assert.Contains("Recent Activity", source, StringComparison.Ordinal);
        Assert.Contains("CompositionAccessibleLabel", source, StringComparison.Ordinal);
        Assert.Contains("item.Percent.ToString", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.Max(item.Percent", source, StringComparison.Ordinal);
        Assert.Contains("<AppIconTile", source, StringComparison.Ordinal);
        Assert.Contains("AppActivityPresentation.For", source, StringComparison.Ordinal);
        Assert.Contains("<span>Type</span>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<BackupRecoveryPanel", source, StringComparison.Ordinal);
        Assert.Contains("/settings/ingestion", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Operational Snapshot", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Recent Ingestion Runs", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Run ID", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedPresentation_DefinesReusableMediaAndActivityColors()
    {
        var media = ReadRepoFile(@"src/MediaEngine.Web/Components/Shared/AppMediaPresentation.cs");
        var activity = ReadRepoFile(@"src/MediaEngine.Web/Components/Shared/AppActivityPresentation.cs");
        var iconTile = ReadRepoFile(@"src/MediaEngine.Web/Components/Shared/AppIconTile.razor");

        Assert.Contains("--tl-media-books", media, StringComparison.Ordinal);
        Assert.Contains("--tl-media-audiobooks", media, StringComparison.Ordinal);
        Assert.Contains("--tl-media-movies", media, StringComparison.Ordinal);
        Assert.Contains("--tl-media-tv", media, StringComparison.Ordinal);
        Assert.Contains("--tl-media-music", media, StringComparison.Ordinal);
        Assert.Contains("--tl-media-comics", media, StringComparison.Ordinal);
        Assert.Contains("public static AppActivityDescriptor For", activity, StringComparison.Ordinal);
        Assert.Contains("AccentColor", iconTile, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemOverview_UsesTheCompactReferenceSizingRhythm()
    {
        var overview = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/OverviewTab.razor");
        var styles = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/OverviewTab.razor.css");
        var settingsStyles = ReadRepoFile(@"src/MediaEngine.Web/Components/Pages/Settings.razor.css");
        var iconTile = ReadRepoFile(@"src/MediaEngine.Web/Components/Shared/AppIconTile.razor");

        Assert.Contains("admin-overview-card--attention admin-attention-strip", overview, StringComparison.Ordinal);
        Assert.Contains("padding: 18px", styles, StringComparison.Ordinal);
        Assert.Contains("height: 6px", styles, StringComparison.Ordinal);
        Assert.Contains("min-height: 46px", styles, StringComparison.Ordinal);
        Assert.Contains(".admin-overview-page ::deep .admin-library-summary", styles, StringComparison.Ordinal);
        Assert.Contains(".admin-library-breakdown ::deep .admin-library-media", styles, StringComparison.Ordinal);
        Assert.Contains(".admin-library-media::after", styles, StringComparison.Ordinal);
        Assert.Contains(".admin-composition-bar ::deep .admin-composition-bar__segment", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("admin-composition-legend", overview, StringComparison.Ordinal);
        Assert.Contains("var(--tl-font-size-secondary)", styles, StringComparison.Ordinal);
        Assert.Contains("font-size: clamp(1.875rem, 2vw, 2rem)", settingsStyles, StringComparison.Ordinal);
        Assert.Contains("font-weight: 600", settingsStyles, StringComparison.Ordinal);
        Assert.Contains("AppControlSize.Large => \"1.75rem\"", iconTile, StringComparison.Ordinal);
        Assert.DoesNotContain("admin-overview-primary-grid", overview, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsShell_OmitsRedundantEngineBadgeAndKeepsUnavailableState()
    {
        var source = ReadRepoFile(@"src/MediaEngine.Web/Components/Pages/Settings.razor");

        Assert.DoesNotContain("GetHeaderStatusLabel", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Engine online", source, StringComparison.Ordinal);
        Assert.Contains("Engine state could not be loaded", source, StringComparison.Ordinal);
        Assert.Contains("ShouldDeferForRoleResolution", source, StringComparison.Ordinal);
        Assert.Contains("SettingsNav.ResolveRoute(Section, CanManageAdministration, CanManageHousehold)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsNav_ClassifiesAdminSectionsWithTruthStatuses()
    {
        var source = ReadRepoFile(@"src/MediaEngine.Web/Models/ViewDTOs/SettingsNav.cs");

        Assert.Contains("SettingsStatusKind.Live", source, StringComparison.Ordinal);
        Assert.Contains("SettingsStatusKind.Partial", source, StringComparison.Ordinal);
        Assert.Contains("public static SettingsStatusKind GetStatus", source, StringComparison.Ordinal);
        Assert.Contains("SettingsSection.LocalAi", source, StringComparison.Ordinal);
        Assert.Contains("SettingsSection.Delivery", source, StringComparison.Ordinal);
        Assert.Contains("SettingsSection.Plugins", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LibrariesTab_RendersCataloguedLibrariesAndViewRootAdministration()
    {
        var source = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/LibrariesTab.razor");
        var nav = ReadRepoFile(@"src/MediaEngine.Web/Models/ViewDTOs/SettingsNav.cs");
        var settings = ReadRepoFile(@"src/MediaEngine.Web/Components/Pages/Settings.razor");

        Assert.DoesNotContain("aria-label=\"Media Management sections\"", source, StringComparison.Ordinal);
        Assert.Contains("new(SettingsSection.Libraries", nav, StringComparison.Ordinal);
        Assert.Contains("new(SettingsSection.Ingestion", nav, StringComparison.Ordinal);
        Assert.DoesNotContain("<SettingsSubsectionNav", settings, StringComparison.Ordinal);

        Assert.Contains("All libraries", source, StringComparison.Ordinal);
        Assert.Contains("Personal Space", ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/ViewLibrarySettings.razor"), StringComparison.Ordinal);
        Assert.DoesNotContain("Import folders", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Choose a media area", source, StringComparison.Ordinal);
        Assert.Contains("new(\"all\", \"All\"", source, StringComparison.Ordinal);
        Assert.Contains("new(\"read\", \"Read\"", source, StringComparison.Ordinal);
        Assert.Contains("new(\"watch\", \"Watch\"", source, StringComparison.Ordinal);
        Assert.Contains("new(\"listen\", \"Listen\"", source, StringComparison.Ordinal);
        Assert.Contains("<ViewLibrarySettings", source, StringComparison.Ordinal);
        Assert.Contains("/settings/libraries/view", source, StringComparison.Ordinal);
        Assert.Contains("Not checked", source, StringComparison.Ordinal);
        Assert.Contains("<h3>Folders</h3>", source, StringComparison.Ordinal);
        Assert.Contains("Existing folders stay unchanged", source, StringComparison.Ordinal);
        Assert.Contains("Primary destination", source, StringComparison.Ordinal);
        Assert.Contains("Duplicate handling", source, StringComparison.Ordinal);
        Assert.Contains("Visibility", source, StringComparison.Ordinal);
        Assert.Contains("AppDialogParameters<ServerFolderPicker>", source, StringComparison.Ordinal);
        Assert.Contains("GetLibrariesAsync", source, StringComparison.Ordinal);
        Assert.Contains("TestPathAsync", source, StringComparison.Ordinal);
        Assert.Contains("MutateLibraryAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HasUnsavedChanges", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Label=\"Save changes\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetFolderSettingsAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateFolderSettingsAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SourcePaths", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LibraryRoot", source, StringComparison.Ordinal);
        Assert.DoesNotContain("The Library Root applies to all libraries", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Global Library Root", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Move to Library", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Import in Place", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Global Paths &amp; Watch Folders", source, StringComparison.Ordinal);
        Assert.DoesNotContain("file-organization-page", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WatchTv_UsesSharedBrowseShellAndDirectShowDetails()
    {
        var watchPage = ReadRepoFile(@"src/MediaEngine.Web/Components/Pages/WatchPage.razor");
        var lanePage = ReadRepoFile(@"src/MediaEngine.Web/Components/Pages/MediaLanePage.razor");
        var browseShell = ReadRepoFile(@"src/MediaEngine.Web/Components/Browse/MediaBrowseShell.razor");
        var queryBuilder = ReadRepoFile(@"src/MediaEngine.Web/Components/Browse/BrowseQueryBuilder.cs");

        Assert.Contains("<MediaLanePage Title=\"Watch\"", watchPage, StringComparison.Ordinal);
        Assert.Contains("<MediaBrowseShell Tab=\"@Tab\"", lanePage, StringComparison.Ordinal);
        Assert.DoesNotContain("<TvBrowsePage", watchPage, StringComparison.Ordinal);
        Assert.Contains("IsTvShowsGrouping && !UseListLayout", browseShell, StringComparison.Ordinal);
        Assert.Contains("LoadDisplayCardsAsync(append)", browseShell, StringComparison.Ordinal);
        Assert.Contains("(\"tv\", \"shows\") => $\"/details/tvshow/{GetTvShowGroupRouteId(group):D}?context=watch\"", browseShell, StringComparison.Ordinal);
        Assert.Contains("group.RootWorkId ?? group.CollectionId", browseShell, StringComparison.Ordinal);
        Assert.DoesNotContain("(\"tv\", \"shows\") => \"show_name\"", queryBuilder, StringComparison.Ordinal);
    }

    [Fact]
    public void MetadataSettings_DoesNotUseHardcodedFallbackAsLiveConfig()
    {
        var source = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/MetadataSettingsPage.razor")
                     + ReadRepoFile(@"src/MediaEngine.Web/Services/Integration/MetadataSettingsStateService.cs");

        Assert.Contains("No sample provider state is being shown", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Load sample chain", source, StringComparison.Ordinal);
        Assert.Contains("GetProviderStatusAsync", source, StringComparison.Ordinal);
        Assert.Contains("TestProviderAsync", source, StringComparison.Ordinal);
        Assert.Contains("SaveProviderConfigAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Provider Setup", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Source Priority", source, StringComparison.Ordinal);
        Assert.Contains("[\"Books\", \"Audiobooks\", \"Comics\", \"Movies\", \"TV\", \"Music\"]", source, StringComparison.Ordinal);
        Assert.Contains("Where(IsUserVisibleProvider)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ContextualProviderName", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MetadataSettings_ReadsConfiguredPipelinesAndExplainsCanonicalResponsibilities()
    {
        var page = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/MetadataSettingsPage.razor");
        var state = ReadRepoFile(@"src/MediaEngine.Web/Services/Integration/MetadataSettingsStateService.cs");

        Assert.Contains("GetPipelinesAsync", state, StringComparison.Ordinal);
        Assert.Contains("BuildFlow(mediaType, pipelines, providers)", state, StringComparison.Ordinal);
        Assert.Contains("Canonical identity", page, StringComparison.Ordinal);
        Assert.Contains("What Happens Next", page, StringComparison.Ordinal);
        Assert.DoesNotContain("DragIndicator", page, StringComparison.Ordinal);
        Assert.DoesNotContain("SavePipelinesAsync", page, StringComparison.Ordinal);
    }

    [Fact]
    public void ProvidersTab_HasDownloadedIconsForVisibleProviders()
    {
        var expectedIcons = new[]
        {
            "apple_books.svg",
            "comicvine.png",
            "lrclib.png",
            "musicbrainz.svg",
            "subdl.png",
            "tmdb.svg",
            "wikidata_reconciliation.svg",
        };

        foreach (var icon in expectedIcons)
        {
            Assert.True(
                File.Exists(GetRepoPath($@"src/MediaEngine.Web/wwwroot/images/providers/{icon}")),
                $"Expected provider icon asset {icon} to exist.");
        }

        var providerConfigs = new[]
        {
            "apple_api.json",
            "comicvine.json",
            "lrclib.json",
            "musicbrainz.json",
            "subdl.json",
            "tmdb.json",
            "wikidata_reconciliation.json",
        };

        foreach (var config in providerConfigs)
        {
            var configJson = ReadRepoFile($@"config/providers/{config}");
            Assert.Contains("\"icon\"", configJson, StringComparison.Ordinal);
            Assert.Contains("images/providers/", configJson, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MetadataSettings_UseTheDedicatedTwoSurfaceExperience()
    {
        var settings = ReadRepoFile(@"src/MediaEngine.Web/Components/Pages/Settings.razor");
        var nav = ReadRepoFile(@"src/MediaEngine.Web/Models/ViewDTOs/SettingsNav.cs");
        var metadataTabPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            @"src/MediaEngine.Web/Components/Settings/MetadataMatchingTab.razor"));

        Assert.False(File.Exists(metadataTabPath));
        Assert.DoesNotContain("MetadataMatchingTab", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Metadata & Matching", nav, StringComparison.Ordinal);
        Assert.Contains("\"metadata\"", nav, StringComparison.Ordinal);
        Assert.Contains("\"providers\", \"Providers\"", nav, StringComparison.Ordinal);
        Assert.Contains("\"ingestion-flow\", \"Ingestion Flow\"", nav, StringComparison.Ordinal);
        Assert.DoesNotContain("\"enrichment\", \"Enrichment\"", nav, StringComparison.Ordinal);
        Assert.DoesNotContain("\"canonical\", \"Canonical & Universes\"", nav, StringComparison.Ordinal);
    }

    [Fact]
    public void AccessSettings_SeparatesAccountsFromProfiles()
    {
        var source = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/UsersAccessSettingsTab.razor");
        var users = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/ManagedAccessUsers.razor");

        Assert.Contains("<ManagedAccessApplications />", source, StringComparison.Ordinal);
        Assert.Contains("Authentication", source, StringComparison.Ordinal);
        Assert.Contains("<ManagedAccessUsers Scope=\"server\" />", source, StringComparison.Ordinal);
        Assert.Contains("<ManagedAccessUsers Scope=\"household\" />", source, StringComparison.Ordinal);
        Assert.Contains("Manage profiles", users, StringComparison.Ordinal);
        Assert.Contains("Invite user", users, StringComparison.Ordinal);
        Assert.DoesNotContain("external-logins", users, StringComparison.Ordinal);
        Assert.DoesNotContain("Owner Administrator\", \"library:read, ingest:write", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Access Rules", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Active sessions", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DeliveryPluginsAndLocalAi_AreTruthLabeled()
    {
        var delivery = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/PlaybackDeliverySettingsTab.razor");
        var plugins = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/PluginSettingsTab.razor");
        var localAi = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/LocalAiSettingsTab.razor");

        Assert.Contains("Variant Storage", delivery, StringComparison.Ordinal);
        Assert.Contains("Diagnostics", delivery, StringComparison.Ordinal);
        Assert.Contains("Install and update marketplace flows are planned", plugins, StringComparison.Ordinal);
        Assert.Contains("plugin-provided settings", plugins, StringComparison.Ordinal);
        Assert.Contains("Advanced settings", plugins, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"JSON\"", plugins, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Manifest\"", plugins, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings JSON", plugins, StringComparison.Ordinal);
        Assert.DoesNotContain("Plugin manifest JSON", plugins, StringComparison.Ordinal);
        Assert.DoesNotContain("_settingsJson", plugins, StringComparison.Ordinal);
        Assert.DoesNotContain("_manifestJson", plugins, StringComparison.Ordinal);
        Assert.Contains("_status?.IsReady != true", localAi, StringComparison.Ordinal);
        Assert.DoesNotContain("Local AI is partially available", localAi, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(string relativePath) =>
        File.ReadAllText(GetRepoPath(relativePath));

    private static string GetRepoPath(string relativePath) =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            relativePath));
}
