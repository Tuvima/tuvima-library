namespace MediaEngine.Web.Tests;

public sealed class UiArchitectureGuardrailTests
{
    private static readonly string RepoRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void RetiredLibraryItemsWorkspace_RemainsRemoved()
    {
        var directory = Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Components", "LibraryItems");
        var activeComponents = Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.razor")
                .Select(path => Path.GetFileNameWithoutExtension(path)!)
                .OrderBy(name => name)
                .ToArray()
            : [];

        Assert.Empty(activeComponents);
    }

    [Theory]
    [InlineData("src/MediaEngine.Web/Components/Library/SearchResultCard.razor")]
    [InlineData("src/MediaEngine.Web/Components/Library/LibraryMusicGrid.razor")]
    [InlineData("src/MediaEngine.Web/Components/Playback/ReaderHighlightsPanel.razor")]
    [InlineData("src/MediaEngine.Web/Components/Universe/CollectionHero.razor")]
    [InlineData("src/MediaEngine.Web/Components/Universe/MediaSearchPanel.razor")]
    [InlineData("src/MediaEngine.Web/Components/Universe/PersonCard.razor")]
    [InlineData("src/MediaEngine.Web/Components/Universe/TrackRow.razor")]
    public void RetiredStandaloneComponents_RemainRemoved(string relativePath)
    {
        Assert.False(File.Exists(Path.Combine(RepoRoot, relativePath)));
    }

    [Theory]
    [InlineData("src/MediaEngine.Web/Components/MediaTiles/MediaTileOverlay.razor")]
    [InlineData("src/MediaEngine.Web/Components/Settings/SettingsStatusBadge.razor")]
    [InlineData("src/MediaEngine.Web/Components/Settings/UniverseSettingsTab.razor")]
    [InlineData("src/MediaEngine.Web/Components/Settings/WikidataConfigTab.razor")]
    [InlineData("src/MediaEngine.Web/Components/Universe/AlphabeticalGrid.razor")]
    [InlineData("src/MediaEngine.Web/Components/Universe/AmbientBackground.razor")]
    [InlineData("src/MediaEngine.Web/Components/Universe/CollectionShell.razor")]
    [InlineData("src/MediaEngine.Web/Components/Universe/CollectionToolbar.razor")]
    [InlineData("src/MediaEngine.Web/Components/Universe/CompactHero.razor")]
    [InlineData("src/MediaEngine.Web/Components/Universe/GreetingBar.razor")]
    [InlineData("src/MediaEngine.Web/Components/Universe/HeroCarousel.razor")]
    [InlineData("src/MediaEngine.Web/Components/Universe/LandscapeCard.razor")]
    [InlineData("src/MediaEngine.Web/Components/Universe/ManualEntryForm.razor")]
    [InlineData("src/MediaEngine.Web/Components/Universe/MetadataChips.razor")]
    [InlineData("src/MediaEngine.Web/Components/Universe/MissingUniverseChip.razor")]
    [InlineData("src/MediaEngine.Web/Components/Universe/PendingFilesAlert.razor")]
    [InlineData("src/MediaEngine.Web/Components/Universe/PersonSwimlaneHeader.razor")]
    [InlineData("src/MediaEngine.Web/Components/Universe/ProgressIndicator.razor")]
    [InlineData("src/MediaEngine.Web/Components/Universe/SquareCard.razor")]
    [InlineData("src/MediaEngine.Web/Components/Universe/SwimlaneSection.razor")]
    [InlineData("src/MediaEngine.Web/Components/Universe/UniverseGuide.razor")]
    [InlineData("src/MediaEngine.Web/Components/Universe/WideCard.razor")]
    [InlineData("src/MediaEngine.Web/Components/Universe/WorkCard.razor")]
    [InlineData("src/MediaEngine.Web/Components/Watch/WatchPlaybackSpecs.razor")]
    public void RetiredDuplicateUiComponents_RemainRemoved(string relativePath)
    {
        Assert.False(File.Exists(Path.Combine(RepoRoot, relativePath)));
    }

    [Fact]
    public void ManagedCollectionClientFeature_IsolatedWithoutMovingAiMethods()
    {
        var facade = Read("src/MediaEngine.Web/Services/Integration/EngineApiClient.cs");
        var features = new[]
        {
            Read("src/MediaEngine.Web/Services/Integration/EngineApiClient.ManagedCollections.cs"),
            Read("src/MediaEngine.Web/Services/Integration/EngineApiClient.LibraryOperations.cs"),
            Read("src/MediaEngine.Web/Services/Integration/EngineApiClient.CollectionGroups.cs")
        };

        Assert.Contains("public sealed partial class EngineApiClient", facade);
        Assert.Contains("GetManagedCollectionsAsync", features[0]);
        Assert.Contains("GetCollectionCatalogAsync", features[0]);
        Assert.Contains("NormalizeManagedCollectionArtwork", features[0]);
        Assert.All(features, feature =>
            Assert.DoesNotContain("/ai/", feature, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EngineApiClient_RemainsSplitByCohesiveArea()
    {
        var areas = new Dictionary<string, string>
        {
            ["Details"] = "GetDetailPageAsync",
            ["Display"] = "GetDisplayHomeAsync",
            ["Operations"] = "GetIngestionQueueProgressAsync",
            ["People"] = "GetPersonsAsync",
            ["Playback"] = "GetPlaybackManifestAsync",
            ["SearchMisc"] = "SearchWorksAsync",
            ["Settings"] = "GetServerGeneralAsync",
        };

        foreach (var (area, representativeMethod) in areas)
        {
            var implementation = Read(
                $"src/MediaEngine.Web/Services/Integration/EngineApiClient.{area}.cs");
            var contract = Read(
                $"src/MediaEngine.Web/Services/Integration/IEngineApiClient.{area}.cs");

            Assert.Contains("public sealed partial class EngineApiClient", implementation);
            Assert.Contains("public partial interface IEngineApiClient", contract);
            Assert.Contains(representativeMethod, implementation);
            Assert.Contains(representativeMethod, contract);
        }

        var facade = Read("src/MediaEngine.Web/Services/Integration/EngineApiClient.cs");
        var interfaceFacade = Read("src/MediaEngine.Web/Services/Integration/IEngineApiClient.cs");

        Assert.True(facade.Split('\n').Length <= 2_600);
        Assert.True(interfaceFacade.Split('\n').Length <= 230);
        Assert.Contains("private async Task<T?> GetAsync<T>(", facade);
        Assert.Contains("GetUniverseGraphAsync", facade);
        Assert.Contains("SearchUniverseAsync", facade);
        Assert.Contains("GetUniverseGraphAsync", interfaceFacade);
        Assert.Contains("SearchUniverseAsync", interfaceFacade);
    }

    [Fact]
    public void EmptyStagedApiClients_RemainRemoved()
    {
        Assert.False(File.Exists(Path.Combine(
            RepoRoot, "src", "MediaEngine.Web", "Services", "Integration", "Clients", "LibraryClient.cs")));
        Assert.False(File.Exists(Path.Combine(
            RepoRoot, "src", "MediaEngine.Web", "Services", "Integration", "Clients", "CollectionClient.cs")));

        var program = Read("src/MediaEngine.Web/Program.cs");
        Assert.DoesNotContain("AddScoped<LibraryClient>", program);
        Assert.DoesNotContain("AddScoped<CollectionClient>", program);
    }

    [Fact]
    public void LibraryItemHelpers_ContainOnlyActiveMediaTypeFormatting()
    {
        var helpers = Read("src/MediaEngine.Web/Components/LibraryItems/LibraryItemHelpers.cs");

        Assert.Contains("GetMediaTypeIcon", helpers);
        Assert.Contains("FormatMediaType", helpers);
        Assert.DoesNotContain("GetStatusColor", helpers);
        Assert.DoesNotContain("GetStatusChipStyle", helpers);
        Assert.DoesNotContain("GetConfidenceColor", helpers);
        Assert.DoesNotContain("Legacy statuses", helpers);
    }

    [Fact]
    public void IngestionDashboard_BackgroundLoopsAreOwnedAndAwaited()
    {
        var state = Read("src/MediaEngine.Web/Services/Integration/IngestionLiveDashboardState.cs");
        var component = Read("src/MediaEngine.Web/Components/Settings/IngestionTasksTab.razor");

        Assert.Contains("IDisposable, IAsyncDisposable", state);
        Assert.Contains("private Task? _pollTask", state);
        Assert.Contains("private Task? _snapshotRefreshTask", state);
        Assert.Contains("private Task? _liveNotifyTask", state);
        Assert.Contains("private bool _signalsDisposed", state);
        Assert.Contains("await Task.WhenAll(tasks)", state);
        Assert.Contains("if (!_signalsDisposed)", state);
        Assert.DoesNotContain("Task.Run", state);
        Assert.Contains("@implements IAsyncDisposable", component);
        Assert.Contains("await Dashboard.StopAsync()", component);
    }

    [Fact]
    public void IngestionDashboard_StateHoldsOnlyTheLifecycleAndTheEnginesSnapshots()
    {
        var lifecycle = Read("src/MediaEngine.Web/Services/Integration/IngestionLiveDashboardState.cs");
        var css = Read("src/MediaEngine.Web/Components/Settings/IngestionTasksTab.razor.css");

        Assert.Contains("public sealed class IngestionLiveDashboardState", lifecycle);
        Assert.Contains("public IngestionQueueProgressDto? QueueProgress", lifecycle);
        Assert.False(File.Exists(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Services", "Integration", "IngestionLiveDashboardState.Projection.cs")));
        Assert.False(File.Exists(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Components", "Settings", "IngestionLiveDashboard.razor")));

        var retiredCssFamilies = new[]
        {
            "library-update-batch-head-chip",
            "library-update-progress-block",
            "library-update-stage-list",
            "library-update-step__node",
            "library-update-stage__bar"
        };
        Assert.All(retiredCssFamilies, family => Assert.DoesNotContain(family, css));
    }

    [Fact]
    public void MetadataSettingsPage_UsesRouteControlledSurfaces()
    {
        var markup = Read("src/MediaEngine.Web/Components/Settings/MetadataSettingsPage.razor");
        var settings = Read("src/MediaEngine.Web/Components/Pages/Settings.razor");

        Assert.DoesNotContain("<ProviderStageSelector", markup);
        Assert.Contains("[Parameter] public string? Subsection", markup);
        Assert.Contains("[Parameter] public string? Detail", markup);
        Assert.Contains("<MetadataSettingsPage Subsection=\"@_activeSubsection\" Detail=\"@Detail\" />", settings);
    }

    [Fact]
    public void MetadataAdministration_UsesTheSharedSettingsHeaderAndTheMenuTreeInsteadOfATabStrip()
    {
        var shell = Read("src/MediaEngine.Web/Components/Settings/MetadataAdminShell.razor");
        var settings = Read("src/MediaEngine.Web/Components/Pages/Settings.razor");
        var metadata = Read("src/MediaEngine.Web/Components/Settings/MetadataSettingsPage.razor");

        Assert.DoesNotContain("<SidebarPageHeader", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("<nav", shell, StringComparison.Ordinal);
        Assert.Contains("<SettingsPageFrame", settings, StringComparison.Ordinal);
        Assert.Contains("<SidebarPageHeader", Read("src/MediaEngine.Web/Components/Settings/Frame/SettingsPageFrame.razor"), StringComparison.Ordinal);
        Assert.DoesNotContain("<SettingsSubsectionNav", settings, StringComparison.Ordinal);
        Assert.Contains("<MetadataAdminShell ActiveSubsection=\"@ActiveSurface\">", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("<SettingsSubsectionNav", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("Source Priority", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("DragIndicator", metadata, StringComparison.Ordinal);
    }

    // Sub-pages are rows of the Settings menu tree (spec D2), so no Settings page draws its own sideways tab strip.
    // AppTabs stays the one component for sections inside a single page.
    [Fact]
    public void NoSettingsPage_RendersASubPageTabStrip()
    {
        var retired = new[] { "<SettingsSubsectionNav", "<SurfaceNavigationBar", "<SurfaceTabBar" };
        var files = Directory.EnumerateFiles(Path.Combine(RepoRoot, "src/MediaEngine.Web/Components/Settings"), "*.razor", SearchOption.AllDirectories)
            .Append(Path.Combine(RepoRoot, "src/MediaEngine.Web/Components/Pages/Settings.razor"));

        var offenders = files
            .SelectMany(path => retired.Where(tag => File.ReadAllText(path).Contains(tag, StringComparison.Ordinal))
                .Select(tag => $"{Path.GetRelativePath(RepoRoot, path)} renders {tag}"))
            .ToArray();

        Assert.True(offenders.Length == 0, string.Join("; ", offenders));
        Assert.False(File.Exists(Path.Combine(RepoRoot, "src/MediaEngine.Web/Components/Shared/SettingsSubsectionNav.razor")));
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot, relativePath));
}
