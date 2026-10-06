namespace MediaEngine.Web.Tests;

public sealed class DashboardReliabilityGuardrailTests
{
    [Fact]
    public void Routes_WrapsRoutedPagesInErrorBoundary()
    {
        var source = Read(@"src\MediaEngine.Web\Components\Routes.razor");

        Assert.Contains("<ErrorBoundary>", source, StringComparison.Ordinal);
        Assert.Contains("<AppErrorState", source, StringComparison.Ordinal);
        Assert.Contains("<FocusOnNavigate", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ReconnectModal_AutomaticallyRecoversAfterADevelopmentRestart()
    {
        var markup = Read(@"src\MediaEngine.Web\Components\Layout\ReconnectModal.razor");
        var script = Read(@"src\MediaEngine.Web\Components\Layout\ReconnectModal.razor.js");

        Assert.Contains("Tuvima Library will keep retrying", markup, StringComparison.Ordinal);
        Assert.Contains("scheduleAutoRetry()", script, StringComparison.Ordinal);
        Assert.Contains("autoRetryMaximumDelayMs = 10_000", script, StringComparison.Ordinal);
        Assert.Contains("window.setTimeout", script, StringComparison.Ordinal);
        Assert.Contains("retryInFlight", script, StringComparison.Ordinal);
        Assert.Contains("location.reload()", script, StringComparison.Ordinal);
        Assert.Contains("if (!reconnectModal.open)", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"src\MediaEngine.Web\Components\Universe\PosterSwimlane.razor", "@key=\"item.Id\"")]
    [InlineData(@"src\MediaEngine.Web\Components\MediaTiles\MediaTileGrid.razor", "@key=\"item.RenderKey\"")]
    [InlineData(@"src\MediaEngine.Web\Components\Library\LibraryConfigurableTable.razor", "@key=\"item.EntityId\"")]
    [InlineData(@"src\MediaEngine.Web\Shared\MainLayout.razor", "@key=\"link.Path\"")]
    [InlineData(@"src\MediaEngine.Web\Components\Settings\SettingsReviewQueueTab.razor", "@key=\"item.Id\"")]
    [InlineData(@"src\MediaEngine.Web\Components\Settings\IngestionBatchHistory.razor", "@key=\"operation.BatchId\"")]
    [InlineData(@"src\MediaEngine.Web\Components\Settings\IngestionLiveDashboard.razor", "@key=\"StageDetailKey(detail)\"")]
    [InlineData(@"src\MediaEngine.Web\Components\Settings\ProviderTesterToolTab.razor", "@key=\"resultKey\"")]
    public void HighRiskListComponents_UseStableKeys(string relativePath, string expectedKey)
    {
        var source = Read(relativePath);

        Assert.Contains(expectedKey, source, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewQueue_KeepsExistingRowsVisibleDuringRefresh()
    {
        var source = Read(@"src\MediaEngine.Web\Components\Settings\SettingsReviewQueueTab.razor");
        var normalized = source.ReplaceLineEndings("\n");

        Assert.Contains("_loading && _items.Count == 0", source, StringComparison.Ordinal);
        Assert.Contains("settings-review-refresh-bar", source, StringComparison.Ordinal);
        var catchStart = normalized.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
        var finallyStart = normalized.IndexOf("finally", catchStart, StringComparison.Ordinal);
        Assert.True(catchStart >= 0 && finallyStart > catchStart);
        Assert.DoesNotContain("_items = [];", normalized[catchStart..finallyStart], StringComparison.Ordinal);
    }

    [Fact]
    public void HomeDiscovery_DebouncesBroadRealtimeReloads()
    {
        var source = Read(@"src\MediaEngine.Web\Components\Pages\LibraryBrowsePage.razor");

        Assert.Contains("LastStateChangeRequiresSnapshotRefresh", source, StringComparison.Ordinal);
        Assert.Contains("ScheduleRefresh", source, StringComparison.Ordinal);
        Assert.Contains("_loadInProgress", source, StringComparison.Ordinal);
        Assert.Contains("_loading = !_hasLoadedOnce", source, StringComparison.Ordinal);
    }

    [Fact]
    public void IngestionDashboard_DoesNotClearOperationDetailsWhenRowsAreSkipped()
    {
        var source = Read(@"src\MediaEngine.Web\Services\Integration\IngestionLiveDashboardState.cs");

        Assert.Contains("LoadSnapshotAsync", source, StringComparison.Ordinal);
        Assert.Contains("_loadInProgress", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_operationDetails.Clear();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_capabilitiesByEntity.Clear();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowseShell_DetailedRoutesDoNotRenderHero()
    {
        var source = Read(@"src\MediaEngine.Web\Components\Browse\MediaBrowseShell.razor");
        var normalized = source.ReplaceLineEndings("\n");

        Assert.DoesNotContain("<MediaBrowseHero", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BrowseHeroViewModel? _hero", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetJourneyAsync(limit: 24)", source, StringComparison.Ordinal);
        Assert.Contains("if (!_hasLoadedOnce)\n            StateHasChanged();", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowseShell_ReloadsWhenLayoutModeChanges()
    {
        var source = Read(@"src\MediaEngine.Web\Components\Browse\MediaBrowseShell.razor");

        Assert.Contains("LayoutToggleClass(LibraryLayoutMode.Card)", source, StringComparison.Ordinal);
        Assert.Contains("LayoutToggleClass(LibraryLayoutMode.List)", source, StringComparison.Ordinal);
        Assert.Contains("await NavigateAndReloadAsync(reloadData: true);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("await NavigateAndReloadAsync(reloadData: false);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WatchAndReadDefaultTabsKeepExplicitBrowseRoutes()
    {
        var browseShell = Read(@"src\MediaEngine.Web\Components\Browse\MediaBrowseShell.razor");
        var watchPage = Read(@"src\MediaEngine.Web\Components\Pages\WatchPage.razor");
        var readPage = Read(@"src\MediaEngine.Web\Components\Pages\ReadPage.razor");

        Assert.Contains("&& !Preset.UseExplicitDefaultTabRoute", browseShell, StringComparison.Ordinal);
        Assert.Contains("UseExplicitDefaultTabRoute = true", watchPage, StringComparison.Ordinal);
        Assert.Contains("UseExplicitDefaultTabRoute = true", readPage, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowseShell_UsesDisplayCardsForTvShowCardView()
    {
        var source = Read(@"src\MediaEngine.Web\Components\Browse\MediaBrowseShell.razor");

        Assert.Contains("IsTvShowsGrouping", source, StringComparison.Ordinal);
        Assert.Contains("IsTvShowsGrouping && !UseListLayout", source, StringComparison.Ordinal);
        Assert.Contains("LoadDisplayCardsAsync(append)", source, StringComparison.Ordinal);
        Assert.Contains("Display API did not return a TV browse page.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowseShell_NormalizesMediaTypeWhenFilteringContainerGroups()
    {
        var source = Read(@"src\MediaEngine.Web\Components\Browse\MediaBrowseShell.razor");

        Assert.Contains(".Where(group => MediaTypeMatches(group.PrimaryMediaType, mediaType))", source, StringComparison.Ordinal);
        Assert.Contains("NormalizeEditorMediaType(candidate)", source, StringComparison.Ordinal);
        Assert.Contains("NormalizeEditorMediaType(requested)", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".Where(group => string.Equals(group.PrimaryMediaType, mediaType, StringComparison.OrdinalIgnoreCase))", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowseShell_BindsSortValueInsteadOfLiteralFieldName()
    {
        var source = Read(@"src\MediaEngine.Web\Components\Browse\MediaBrowseShell.razor");

        Assert.Contains("Value=\"@_sortBy\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Value=\"_sortBy\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CinematicHeroCarousel_UsesSelectableTimedDashes()
    {
        var source = Read(@"src\MediaEngine.Web\Components\Cinematic\CinematicHeroCarousel.razor");
        var styles = Read(@"src\MediaEngine.Web\Components\Cinematic\CinematicHeroCarousel.razor.css");

        Assert.Contains("cinematic-hero-carousel__timeline", source, StringComparison.Ordinal);
        Assert.Contains("cinematic-hero-carousel__dash-fill", source, StringComparison.Ordinal);
        Assert.Contains("<AppNativeButton Type=\"button\"", source, StringComparison.Ordinal);
        Assert.Contains("SelectSlideAsync(slideIndex)", source, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"@(isActive ? \"true\" : null)\"", source, StringComparison.Ordinal);
        Assert.Contains("--cinematic-hero-rotation-duration: 8s", styles, StringComparison.Ordinal);
        Assert.Contains("@key=\"TimelineFillKey\"", source, StringComparison.Ordinal);
        Assert.Contains("@keyframes cinematic-hero-dash-fill", styles, StringComparison.Ordinal);
        Assert.Contains("animation-play-state: paused", styles, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-reduced-motion: reduce)", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("cinematic-hero-carousel__counter", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CinematicLandingHero_UsesDetailBackdropFitMaskAndLayeredFade()
    {
        var surface = Read(@"src\MediaEngine.Web\Components\Cinematic\CinematicHeroSurface.razor");
        var surfaceStyles = Read(@"src\MediaEngine.Web\Components\Cinematic\CinematicHeroSurface.razor.css");
        var carouselStyles = Read(@"src\MediaEngine.Web\Components\Cinematic\CinematicHeroCarousel.razor.css");

        Assert.Contains("class=\"cinematic-hero-surface-scope\"", surface, StringComparison.Ordinal);
        Assert.Contains(".cinematic-hero-surface-scope ::deep .cinematic-hero-surface", surfaceStyles, StringComparison.Ordinal);
        Assert.Contains("object-fit: cover", surfaceStyles, StringComparison.Ordinal);
        Assert.Contains("object-position: var(--hero-image-position, center top)", surfaceStyles, StringComparison.Ordinal);
        Assert.Contains("mask-image: none", surfaceStyles, StringComparison.Ordinal);
        Assert.Contains("linear-gradient(to right, rgba(var(--hero-shadow-rgb), 0.9)", surfaceStyles, StringComparison.Ordinal);
        Assert.Contains("--hero-wash-accent", surfaceStyles, StringComparison.Ordinal);
        Assert.Contains("tl-detail-media-stage--background::before", surfaceStyles, StringComparison.Ordinal);
        Assert.Contains("content: none", surfaceStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("tl-detail-media-stage__cover-wrap", surfaceStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("tl-detail-media-stage__background", carouselStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("object-fit: cover", carouselStyles, StringComparison.Ordinal);
        Assert.Contains("--cinematic-hero-min-height: var(--cinematic-hero-height)", carouselStyles, StringComparison.Ordinal);
        Assert.Contains("--cinematic-hero-height: clamp(34rem,80svh,calc(100svh - var(--app-topbar-height,65px)))", carouselStyles, StringComparison.Ordinal);
        Assert.Contains("height: var(--cinematic-hero-height, 95svh) !important", surfaceStyles, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 720px), (max-height: 620px)", surfaceStyles, StringComparison.Ordinal);
        Assert.Contains("height: auto !important", surfaceStyles, StringComparison.Ordinal);
    }

    [Fact]
    public void CinematicLandingHero_UsesDetailContentWithExplicitCarouselContext()
    {
        var carousel = Read(@"src\MediaEngine.Web\Components\Cinematic\CinematicHeroCarousel.razor");
        var detail = Read(@"src\MediaEngine.Web\Components\Details\DetailHero.razor");
        var content = Read(@"src\MediaEngine.Web\Components\Details\DetailHeroContent.razor");

        Assert.Contains("<DetailHeroContent", carousel, StringComparison.Ordinal);
        Assert.Contains("<DetailHeroContent", detail, StringComparison.Ordinal);
        Assert.Contains("tl-detail-hero__synopsis", content, StringComparison.Ordinal);
        Assert.DoesNotContain("cinematic-hero__section-identity", carousel, StringComparison.Ordinal);
        Assert.Contains("Featured Content", carousel, StringComparison.Ordinal);
        Assert.Contains("Continue Watching", carousel, StringComparison.Ordinal);
        Assert.Contains("Continue Reading", carousel, StringComparison.Ordinal);
        Assert.Contains("Continue Listening", carousel, StringComparison.Ordinal);
        Assert.Contains("Icon = IsOpenAction ? null : PrimaryActionIcon", carousel, StringComparison.Ordinal);
        Assert.Contains("cinematic-hero-carousel__detail-link", carousel, StringComparison.Ordinal);
        Assert.Contains("OpenLandingReaderAsync", carousel, StringComparison.Ordinal);
        Assert.Contains("PlayLandingAlbumAsync", carousel, StringComparison.Ordinal);
        Assert.Contains("PlayLandingAudiobookAsync", carousel, StringComparison.Ordinal);
        Assert.Contains("RestartRoute(ActiveHero.PrimaryNavigationUrl)", carousel, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedHeroContent_CentersTheIdentityStackWithoutClippingMetadata()
    {
        var actionRow = Read(@"src\MediaEngine.Web\Components\Details\HeroActionRow.razor");
        var sharedStyles = Read(@"src\MediaEngine.Web\Components\Details\DetailHeroContent.razor.css");
        var detailStyles = Read(@"src\MediaEngine.Web\Components\Details\DetailPage.razor.css");

        Assert.Contains("PrimaryActions.Skip(1)", actionRow, StringComparison.Ordinal);
        Assert.Contains("align-items: center", sharedStyles, StringComparison.Ordinal);
        Assert.Contains("gap: clamp(0.65rem, 1.2svh, 0.9rem)", sharedStyles, StringComparison.Ordinal);
        Assert.Contains("min-height: 3rem", sharedStyles, StringComparison.Ordinal);
        Assert.Contains("height: auto", sharedStyles, StringComparison.Ordinal);
        Assert.Contains("row-gap: 0.35rem", sharedStyles, StringComparison.Ordinal);
        Assert.Contains("min-height: clamp(5rem, 15svh, 8rem)", sharedStyles, StringComparison.Ordinal);
        Assert.Contains("max-height: clamp(4.75rem, 13svh, 7.25rem)", sharedStyles, StringComparison.Ordinal);
        Assert.Contains("align-items: center", detailStyles, StringComparison.Ordinal);
        Assert.Contains("flex-wrap: nowrap", Read(@"src\MediaEngine.Web\Components\Details\HeroGenreList.razor.css"), StringComparison.Ordinal);
    }

    [Fact]
    public void DetailAndLanePagesUseSurfaceNavigationWhileHomeFlowsDirectlyIntoShelves()
    {
        var detailTabs = Read(@"src\MediaEngine.Web\Components\Details\DetailTabs.razor");
        var mediaHub = Read(@"src\MediaEngine.Web\Components\MediaHub\MediaHubPage.razor");
        var home = Read(@"src\MediaEngine.Web\Components\Pages\LibraryBrowsePage.razor");
        var navigationStyles = Read(@"src\MediaEngine.Web\Components\Cinematic\SurfaceNavigationBar.razor.css");
        var carouselStyles = Read(@"src\MediaEngine.Web\Components\Cinematic\CinematicHeroCarousel.razor.css");

        Assert.Contains("<SurfaceNavigationBar", detailTabs, StringComparison.Ordinal);
        Assert.Contains("<SurfaceNavigationBar", mediaHub, StringComparison.Ordinal);
        Assert.DoesNotContain("<SurfaceNavigationBar", home, StringComparison.Ordinal);
        Assert.DoesNotContain("<SurfaceTabBar", home, StringComparison.Ordinal);
        Assert.Contains("min-height: 4.25rem", navigationStyles, StringComparison.Ordinal);
        Assert.Contains("border-bottom: 2px solid transparent", navigationStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("border-top: 1px solid", navigationStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("border-bottom: 1px solid", navigationStyles, StringComparison.Ordinal);
        Assert.Contains(".cinematic-hero-carousel ::deep .cinematic-hero__content", carouselStyles, StringComparison.Ordinal);
        Assert.Contains("height: 100%", carouselStyles, StringComparison.Ordinal);
        Assert.Contains("min-height: var(--cinematic-hero-min-height)", carouselStyles, StringComparison.Ordinal);
        Assert.Contains("--cinematic-hero-min-height: 0px", carouselStyles, StringComparison.Ordinal);
    }

    [Fact]
    public void WatchLandingSpotlights_UseTvShowCardsInsteadOfEpisodeContinueCards()
    {
        var source = Read(@"src\MediaEngine.Web\Services\MediaTiles\MediaTileComposerService.cs");

        Assert.Contains("string.Equals(page.Key, \"watch\"", source, StringComparison.Ordinal);
        Assert.Contains("!string.Equals(card.MediaType, \"TV\"", source, StringComparison.Ordinal);
        Assert.Contains("string.Equals(shelf.Key, \"tv-shows\"", source, StringComparison.Ordinal);
        Assert.Contains("TV landing spotlights use show cards exclusively", source, StringComparison.Ordinal);
    }

    [Fact]
    public void IngestionDashboard_ExplainsIconMeaningWithSimpleTooltips()
    {
        var source = Read(@"src\MediaEngine.Web\Components\Settings\IngestionLiveDashboard.razor")
                     + Read(@"src\MediaEngine.Web\Components\Settings\IngestionLiveDashboard.razor.cs");

        Assert.Contains("AppTooltip Text=\"Reload the latest ingestion status.\"", source, StringComparison.Ordinal);
        Assert.Contains("AppTooltip Text=\"Start a new scan of the watched folders.\"", source, StringComparison.Ordinal);
        Assert.Contains("StageIconTooltip(stage)", source, StringComparison.Ordinal);
        Assert.Contains("StageProgressTooltip(stage)", source, StringComparison.Ordinal);
        Assert.Contains("StageDetailTooltip(detail)", source, StringComparison.Ordinal);
        Assert.Contains("BatchSelectionTooltip(batch)", source, StringComparison.Ordinal);
        Assert.Contains("BatchMediaTooltip(chip)", source, StringComparison.Ordinal);
        Assert.Contains("BatchArtifactTooltip(chip)", source, StringComparison.Ordinal);
        Assert.Contains("BatchActivityTooltip(batch)", source, StringComparison.Ordinal);
        Assert.Contains("Recent batches show the latest scans and whether they are active or complete.", source, StringComparison.Ordinal);
        Assert.Contains("Matches files to retail catalog data like title, cover, and description.", source, StringComparison.Ordinal);
        Assert.Contains("Links matched items to Wikidata IDs so relationships can be built.", source, StringComparison.Ordinal);
        Assert.Contains("Files or works that have a direct Wikidata ID.", source, StringComparison.Ordinal);
        Assert.Contains("Extra Wikidata IDs found for people, series, universes, or story details.", source, StringComparison.Ordinal);
        Assert.Contains("Items Tuvima Library could not confidently finish.", source, StringComparison.Ordinal);
        Assert.Contains("People with extra profile data such as biography or images.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AppErrorState_ProvidesTitleMessageAndRetryAffordance()
    {
        var source = Read(@"src\MediaEngine.Web\Components\Shared\AppErrorState.razor");

        Assert.Contains("[Parameter] public string? Title", source, StringComparison.Ordinal);
        Assert.Contains("[Parameter] public string? Message", source, StringComparison.Ordinal);
        Assert.Contains("[Parameter] public EventCallback Retry", source, StringComparison.Ordinal);
        Assert.Contains("app-error-state__retry", source, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath)));
}
