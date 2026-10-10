namespace MediaEngine.Web.Tests;

public sealed class ViewLibrarySurfaceTests
{
    [Fact]
    public void ViewRoutes_UseTheDefiniteHeightLaneScroller()
    {
        var source = Read("src/MediaEngine.Web/Shared/MainLayout.razor");
        Assert.Contains("\"/collections\", \"/view\"", source, StringComparison.Ordinal);
        var css = Read("src/MediaEngine.Web/Components/Pages/ViewContentPage.razor.css");
        Assert.Contains(".view-content-page:not(.view-content-page--fill-height)", css, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewPhotos_UsesPersonalTimelineWithoutPhysicalLibraryControls()
    {
        var source = Read("src/MediaEngine.Web/Components/Pages/ViewPage.razor");

        Assert.Contains("@page \"/view\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("@page \"/view/{LibraryId:guid}\"", source, StringComparison.Ordinal);
        Assert.Contains("<ViewSectionShell", source, StringComparison.Ordinal);
        Assert.Contains("<PageTitle>Photos - Tuvima Library</PageTitle>", source, StringComparison.Ordinal);
        Assert.Contains("Search photos, dates, devices, locations, and tags", source, StringComparison.Ordinal);
        Assert.Contains("<AppDiscoveryFilterBar", source, StringComparison.Ordinal);
        Assert.Contains("role=\"tablist\"", Read("src/MediaEngine.Web/Components/Shared/AppDiscoveryFilterBar.razor"), StringComparison.Ordinal);
        Assert.Contains("<ViewImmersiveViewer", source, StringComparison.Ordinal);
        Assert.Contains("ToggleFavoriteAsync", source, StringComparison.Ordinal);
        Assert.Contains("ArchiveViewItemAsync", source, StringComparison.Ordinal);
        Assert.Contains("TrashViewItemAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("view-tile__actions", source, StringComparison.Ordinal);
        Assert.Contains("GetViewAssetsAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("profileId=", source, StringComparison.Ordinal);
        Assert.Contains("ViewMediaGrantService", source, StringComparison.Ordinal);
        Assert.Contains("/view-media/{grant.Value}", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ToAbsoluteEngineUrl", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SupplyParameterFromQuery", source, StringComparison.Ordinal);
        Assert.Contains("Photos quick filters", source, StringComparison.Ordinal);
        Assert.Contains("[\"all\"] = \"All\"", source, StringComparison.Ordinal);
        Assert.Contains("[\"favorites\"] = \"Favorites\"", source, StringComparison.Ordinal);
        Assert.Contains("[\"videos\"] = \"Videos\"", source, StringComparison.Ordinal);
        Assert.Contains("[\"archive\"] = \"Archive\"", source, StringComparison.Ordinal);
        Assert.Contains("Add media", source, StringComparison.Ordinal);
        Assert.Contains("<InputFile", source, StringComparison.Ordinal);
        Assert.Contains("UploadViewMediaAsync", source, StringComparison.Ordinal);
        Assert.Contains("role=\"status\"", source, StringComparison.Ordinal);
        Assert.Contains("role=\"alert\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("view-library-picker", source, StringComparison.Ordinal);
        Assert.DoesNotContain("view-summary", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Scan library", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedLibrary.Name", source, StringComparison.Ordinal);
        Assert.DoesNotContain("@page \"/photos\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Wikidata", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Provider", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MediaEditorLauncherService", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewWorkspace_ExposesArtworkAlongsidePersonalMediaDestinations()
    {
        var shell = Read("src/MediaEngine.Web/Components/Pages/ViewSectionShell.razor");

        Assert.Contains("new(\"Photos\", \"/view\"", shell, StringComparison.Ordinal);
        Assert.Contains("new(\"Galleries\", \"/view/galleries\"", shell, StringComparison.Ordinal);
        Assert.Contains("new(\"Library\"", shell, StringComparison.Ordinal);
        Assert.Contains("new(\"Artwork Library\", \"/view/artwork\"", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("new(\"Media\", \"/view/artwork\"", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("new(\"Universes\", \"/view/artwork/universes\"", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("new(\"All Images\", \"/view/artwork/assets\"", shell, StringComparison.Ordinal);
        Assert.Contains("new(\"People\", \"/view/people\"", shell, StringComparison.Ordinal);
        Assert.Contains("new(\"Places\", \"/view/places\"", shell, StringComparison.Ordinal);
        Assert.Contains("IsSmart: gallery.Kind == ViewGalleryKind.Smart", shell, StringComparison.Ordinal);
        Assert.Contains("DropTarget: gallery.Kind == ViewGalleryKind.Manual", shell, StringComparison.Ordinal);
        Assert.Contains("new SmartGalleryCreateTarget", shell, StringComparison.Ordinal);
        Assert.Equal(1, shell.Split("new(\"Photos\", \"/view\"", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("Libraries", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Favorites", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Videos", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Archive", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Trash", shell, StringComparison.Ordinal);

        AssertViewRoute("ViewGalleriesPage.razor", "/view/galleries");
        AssertViewRoute("ViewArtworkPage.razor", "/view/artwork");
        AssertViewRoute("ViewPeoplePage.razor", "/view/people");
        AssertViewRoute("ViewPlacesPage.razor", "/view/places");
    }

    [Fact]
    public void ViewArtwork_UsesOneAssetBrowserAndExistingEditors()
    {
        var artwork = Read("src/MediaEngine.Web/Components/Pages/ViewArtworkPage.razor");
        var client = Read("src/MediaEngine.Web/Services/Integration/EngineApiClient.Artwork.cs");
        var browser = Read("src/MediaEngine.Web/Components/Artwork/ArtworkAssetBrowser.razor");
        var browserStyles = Read("src/MediaEngine.Web/Components/Artwork/ArtworkAssetBrowser.razor.css");
        var gallery = Read("src/MediaEngine.Web/Components/Artwork/ArtworkJustifiedGallery.razor");
        var galleryStyles = Read("src/MediaEngine.Web/Components/Artwork/ArtworkJustifiedGallery.razor.css");
        var picker = Read("src/MediaEngine.Web/Components/Artwork/ArtworkAssetPickerDialog.razor");

        Assert.Contains("Artwork Library", artwork, StringComparison.Ordinal);
        Assert.Contains("<ArtworkAssetBrowser", artwork, StringComparison.Ordinal);
        Assert.Contains("Manage usage", artwork, StringComparison.Ordinal);
        Assert.Contains("ArtworkWorkspaceDialog", artwork, StringComparison.Ordinal);
        Assert.Contains("Search titles, people, characters, universes, and IDs", browser, StringComparison.Ordinal);
        Assert.Contains("Recommended", browser, StringComparison.Ordinal);
        Assert.Contains("Related", browser, StringComparison.Ordinal);
        Assert.Contains("All Artwork", browser, StringComparison.Ordinal);
        Assert.Contains("<AppDiscoveryFilterBar", browser, StringComparison.Ordinal);
        Assert.Contains("UsageQuickFilters", browser, StringComparison.Ordinal);
        Assert.Contains("People / actors", browser, StringComparison.Ordinal);
        Assert.Contains("MediaTypeChangedAsync(ChangeEventArgs args)", browser, StringComparison.Ordinal);
        Assert.Contains("SortChangedAsync(ChangeEventArgs args)", browser, StringComparison.Ordinal);
        Assert.Contains("TimelineMonths", browser, StringComparison.Ordinal);
        Assert.Contains("artwork-asset-browser__date-group", browser, StringComparison.Ordinal);
        Assert.DoesNotContain("artwork-asset-browser__filter-rail", browser, StringComparison.Ordinal);
        Assert.Contains("LoadMoreAsync", browser, StringComparison.Ordinal);
        Assert.Contains("<ArtworkJustifiedGallery", browser, StringComparison.Ordinal);
        Assert.Contains("if (!PickerMode) _viewerAsset = asset", browser, StringComparison.Ordinal);
        Assert.DoesNotContain("artwork-asset-browser__inspector", browser, StringComparison.Ordinal);
        Assert.DoesNotContain("grid-template-columns", browserStyles, StringComparison.Ordinal);
        Assert.Contains("ItemKey", gallery, StringComparison.Ordinal);
        Assert.Contains("UseContain", gallery, StringComparison.Ordinal);
        Assert.Contains("(max-width: 1400px) 32vw, 480px", gallery, StringComparison.Ordinal);
        Assert.Contains("display: flex", galleryStyles, StringComparison.Ordinal);
        Assert.Contains("gap: 7px", galleryStyles, StringComparison.Ordinal);
        Assert.Contains("height: 11.5rem", galleryStyles, StringComparison.Ordinal);
        Assert.Contains("object-fit: cover", galleryStyles, StringComparison.Ordinal);
        Assert.Contains("object-fit: contain", galleryStyles, StringComparison.Ordinal);
        Assert.Contains("max-width: min(100%, 32rem)", galleryStyles, StringComparison.Ordinal);
        Assert.Contains("<ArtworkAssetBrowser", picker, StringComparison.Ordinal);
        var workspace = Read("src/MediaEngine.Web/Components/Artwork/ArtworkWorkspace.razor");
        Assert.Contains("OnClick=\"@(() => BeginDelete(variant))\"", workspace, StringComparison.Ordinal);
        Assert.Contains("Choose from Library", workspace, StringComparison.Ordinal);
        Assert.Contains("From URL", workspace, StringComparison.Ordinal);
        Assert.Contains("Set Preferred", workspace, StringComparison.Ordinal);
        Assert.Contains("@if (AllowEdit)", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("Editing artwork", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("Edit artwork", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("View · Library Artwork", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("StartInEditMode", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain(">Refresh</AppButton>", workspace, StringComparison.Ordinal);
        Assert.Contains("AvailableRoles", workspace, StringComparison.Ordinal);
        Assert.Contains("ArtworkRolePresentationResolver", workspace, StringComparison.Ordinal);
        Assert.Contains("ArtworkAssetPickerDialog", workspace, StringComparison.Ordinal);
        Assert.Contains("tuvimaArtwork.openFullSize", workspace, StringComparison.Ordinal);
        Assert.Contains("<MediaViewerShell", browser, StringComparison.Ordinal);
        Assert.Contains("<MediaViewerShell", Read("src/MediaEngine.Web/Components/MediaEditor/MediaEditorArtworkLightbox.razor"), StringComparison.Ordinal);
        Assert.DoesNotContain("MediaEditorLauncher.OpenAsync", artwork, StringComparison.Ordinal);
        Assert.Contains("<ArtworkWorkspace", Read("src/MediaEngine.Web/Components/MediaEditor/Sections/EditorArtworkSection.razor"), StringComparison.Ordinal);
        Assert.Contains("<ArtworkWorkspace", Read("src/MediaEngine.Web/Components/MediaEditor/PersonEditorDialog.razor"), StringComparison.Ordinal);
        Assert.Contains("<ArtworkWorkspace", Read("src/MediaEngine.Web/Components/Collections/CollectionEditorShell.razor"), StringComparison.Ordinal);
        Assert.Contains("/api/v1/display/artwork", client, StringComparison.Ordinal);
        Assert.Contains("ArtworkAssetQuery request", client, StringComparison.Ordinal);
        Assert.Contains("generation != _generation", browser, StringComparison.Ordinal);
        Assert.Contains("await LoadAsync();", browser, StringComparison.Ordinal);
        Assert.DoesNotContain("owned item", artwork, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Wikidata", artwork, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ViewPrimaryRoutes_ShareTheLaneAlignedContentFrame()
    {
        string[] routes =
        [
            "ViewPage.razor",
            "ViewFoldersPage.razor",
            "ViewGalleriesPage.razor",
            "ViewArtworkPage.razor",
            "ViewPeoplePage.razor",
            "ViewPlacesPage.razor",
        ];

        foreach (var route in routes)
        {
            Assert.Contains("<ViewContentPage", Read($"src/MediaEngine.Web/Components/Pages/{route}"), StringComparison.Ordinal);
        }

        var frame = Read("src/MediaEngine.Web/Components/Pages/ViewContentPage.razor.css");
        Assert.Contains("align-content: start", frame, StringComparison.Ordinal);
        Assert.Contains("font-family: var(--font-ui, inherit)", frame, StringComparison.Ordinal);
        Assert.Contains("padding: 28px clamp(20px, 2.2vw, 40px) 48px", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewCapabilityPages_DoNotPresentSyntheticMedia()
    {
        var galleries = Read("src/MediaEngine.Web/Components/Pages/ViewGalleriesPage.razor");
        var galleryEditor = Read("src/MediaEngine.Web/Components/Collections/GalleryEditorShell.razor");
        var people = Read("src/MediaEngine.Web/Components/Pages/ViewPeoplePage.razor");
        var places = Read("src/MediaEngine.Web/Components/Pages/ViewPlacesPage.razor");
        var mapComponent = Read("src/MediaEngine.Web/Components/View/ViewMap.razor");
        var mapStyles = Read("src/MediaEngine.Web/Components/View/ViewMap.razor.css");
        var mapScript = Read("src/MediaEngine.Web/wwwroot/js/view-map.js");
        var placesStyles = Read("src/MediaEngine.Web/Components/Pages/ViewPlacesPage.razor.css");
        var contentPageStyles = Read("src/MediaEngine.Web/Components/Pages/ViewContentPage.razor.css");
        var story = Read("src/MediaEngine.Web/Components/View/ViewPlaceStoryPanel.razor");
        var timeline = Read("src/MediaEngine.Web/Components/View/ViewPlacesTimeline.razor");

        Assert.Contains("GalleryEditorLauncher.OpenAsync", galleries, StringComparison.Ordinal);
        Assert.Contains("CreateViewGalleryAsync", galleryEditor, StringComparison.Ordinal);
        Assert.Contains("ViewRuleBuilder", galleryEditor, StringComparison.Ordinal);
        Assert.Contains("ViewDiscoveryCapabilityStates", people, StringComparison.Ordinal);
        Assert.Contains("GetViewPeopleAsync", people, StringComparison.Ordinal);
        Assert.Contains("GetViewAtlasAsync", places, StringComparison.Ordinal);
        Assert.Contains("World map of authorized photo and video locations", places, StringComparison.Ordinal);
        Assert.Contains("ViewPlaceStoryPanel", places, StringComparison.Ordinal);
        Assert.Contains("Place Story", story, StringComparison.Ordinal);
        Assert.Contains("ViewPlacesTimeline", places, StringComparison.Ordinal);
        Assert.Contains("Timeline granularity", timeline, StringComparison.Ordinal);
        Assert.Contains("<ViewImmersiveViewer", places, StringComparison.Ordinal);
        Assert.Contains("ShowUnmappedAsync", places, StringComparison.Ordinal);
        Assert.Contains("Journey layer", places, StringComparison.Ordinal);
        Assert.Contains("tiles.openfreemap.org/styles/dark", mapScript, StringComparison.Ordinal);
        Assert.Contains("Math.min(requestedZoom, 4.25)", mapScript, StringComparison.Ordinal);
        Assert.Contains("map.setStyle(baseStyle())", mapScript, StringComparison.Ordinal);
        Assert.Contains("collapseAttribution(container)", mapScript, StringComparison.Ordinal);
        Assert.Contains("setProjection({ type: 'mercator' })", mapScript, StringComparison.Ordinal);
        Assert.Contains("applyAtlasLabelPolicy(map)", mapScript, StringComparison.Ordinal);
        Assert.Contains("fitAtlasWorld(state, false)", mapScript, StringComparison.Ordinal);
        Assert.Contains("atlasOverviewLabels", mapScript, StringComparison.Ordinal);
        Assert.Contains("['North America', -105, 45, 'continent']", mapScript, StringComparison.Ordinal);
        Assert.Contains("if (/(country|admin-0|admin_0)/.test(identity)) return 2.75", mapScript, StringComparison.Ordinal);
        Assert.Contains("renderWorldCopies: false", mapScript, StringComparison.Ordinal);
        Assert.DoesNotContain("setProjection({ type: 'globe' })", mapScript, StringComparison.Ordinal);
        Assert.Contains("maplibregl-compact-show", mapStyles, StringComparison.Ordinal);
        Assert.Contains("tuvima-map-hotspot-anchor", mapStyles, StringComparison.Ordinal);
        Assert.Contains("new maplibregl.Marker({ element: anchor, anchor: 'center' })", mapScript, StringComparison.Ordinal);
        Assert.Contains("const anchorHotspot", mapScript, StringComparison.Ordinal);
        Assert.DoesNotContain("group.items.reduce((sum, item) => sum + item.latitude", mapScript, StringComparison.Ordinal);
        Assert.Contains("map.once('idle', () => { renderOverviewLabels(state); renderHotspots(state); })", mapScript, StringComparison.Ordinal);
        Assert.Contains("group.representative?.thumbnailUrl", mapScript, StringComparison.Ordinal);
        Assert.Contains("new ResizeObserver", mapScript, StringComparison.Ordinal);
        Assert.Contains("view-map.js?v=20260926.2", mapComponent, StringComparison.Ordinal);
        Assert.DoesNotContain("getNorthEast().equals", mapScript, StringComparison.Ordinal);
        Assert.Contains("northEast.lng === southWest.lng && northEast.lat === southWest.lat", mapScript, StringComparison.Ordinal);
        Assert.Contains("PlacesTimeScale", timeline, StringComparison.Ordinal);
        Assert.Contains("@onfocus", timeline, StringComparison.Ordinal);
        Assert.Contains("SelectedYear", timeline, StringComparison.Ordinal);
        Assert.Contains("SelectedYear=\"@_year\"", places, StringComparison.Ordinal);
        Assert.Contains("places-timeline__actions", timeline, StringComparison.Ordinal);
        Assert.Contains("_atlas.UnmappedAssetCount > 0", places, StringComparison.Ordinal);
        Assert.Contains("no usable GPS coordinates", places, StringComparison.Ordinal);
        Assert.Contains("FillAvailableHeight=\"true\"", places, StringComparison.Ordinal);
        Assert.Contains(".view-content-page--fill-height", contentPageStyles, StringComparison.Ordinal);
        Assert.Contains("height: 100%;", contentPageStyles, StringComparison.Ordinal);
        Assert.DoesNotContain(".view-atlas-page{height:calc(100dvh", placesStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("fake", galleries, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ViewPhotosAndGalleries_ExposeTypedSelectionAndManualOnlyDrops()
    {
        var photos = Read("src/MediaEngine.Web/Components/Pages/ViewPage.razor");
        var timeline = Read("src/MediaEngine.Web/Components/Pages/ViewPhotoTimeline.razor");
        var galleries = Read("src/MediaEngine.Web/Components/Pages/ViewGalleriesPage.razor");
        var galleryEditor = Read("src/MediaEngine.Web/Components/Collections/GalleryEditorShell.razor");
        var shell = Read("src/MediaEngine.Web/Components/Pages/ViewSectionShell.razor");
        var timelineStyles = Read("src/MediaEngine.Web/Components/Pages/ViewPhotoTimeline.razor.css");
        var selectionStyles = Read("src/MediaEngine.Web/Components/Pages/ViewSelectionToolbar.razor.css");

        Assert.Contains("ViewSelectionToolbar", photos, StringComparison.Ordinal);
        Assert.Contains("<AppDiscoveryFilterBar", photos, StringComparison.Ordinal);
        Assert.Contains("OnSelectDateGroup", timeline, StringComparison.Ordinal);
        Assert.DoesNotContain("Select @group.Items.Count", timeline, StringComparison.Ordinal);
        Assert.Contains("aria-checked", timeline, StringComparison.Ordinal);
        Assert.Contains("view-date-group__select", timeline, StringComparison.Ordinal);
        Assert.Contains("DurationSeconds", timeline, StringComparison.Ordinal);
        Assert.Contains("Density=\"@Workspace.Density\"", photos, StringComparison.Ordinal);
        Assert.Contains("view-density--@Density", timeline, StringComparison.Ordinal);
        Assert.Contains("view-tile__select-control", timeline, StringComparison.Ordinal);
        Assert.Contains(".view-tile.is-selected", timelineStyles, StringComparison.Ordinal);
        Assert.Contains("::deep .view-tile__select-control[aria-checked=\"true\"]", timelineStyles, StringComparison.Ordinal);
        Assert.Contains("width:max-content", selectionStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("background:rgba(30,41,59,.42)", selectionStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("<AppCheckbox", timeline, StringComparison.Ordinal);
        Assert.Contains("ViewGalleryKind.Manual", shell, StringComparison.Ordinal);
        Assert.Contains("ViewGalleryKind.Smart", galleries, StringComparison.Ordinal);
        Assert.Contains("<ViewRuleBuilder", galleryEditor, StringComparison.Ordinal);
        Assert.Contains(".Take(12)", shell, StringComparison.Ordinal);
        Assert.Contains("new ManualGalleryNavigationDropTarget", shell, StringComparison.Ordinal);
        Assert.Contains("new NewGalleryNavigationDropTarget", shell, StringComparison.Ordinal);
        Assert.Contains("finally", shell, StringComparison.Ordinal);
        Assert.Contains("AssetDrag.Clear()", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewScopeRequests_KeepResolvedMineOwnerOutOfProfileOnlyParameter()
    {
        var workspace = Read("src/MediaEngine.Web/Services/Integration/ViewWorkspaceService.cs");
        var client = Read("src/MediaEngine.Web/Services/Integration/EngineApiClient.View.cs");

        Assert.Contains("ScopeKind.CarriesProfileId()", workspace, StringComparison.Ordinal);
        Assert.Contains("options.Scope.CarriesProfileId()", client, StringComparison.Ordinal);
        Assert.Contains("scope.CarriesProfileId()", client, StringComparison.Ordinal);
    }

    [Fact]
    public void GalleryDetail_ProvidesOwnerManagementAndKeepsSmartMembershipRuleOnly()
    {
        var detail = Read("src/MediaEngine.Web/Components/Pages/ViewGalleryDetailPage.razor");
        var editor = Read("src/MediaEngine.Web/Components/Collections/GalleryEditorShell.razor");

        Assert.Contains("private bool IsOwner", detail, StringComparison.Ordinal);
        Assert.Contains("GetViewGalleryShareTargetsAsync", detail, StringComparison.Ordinal);
        Assert.Contains("GetViewGallerySharesAsync", detail, StringComparison.Ordinal);
        Assert.Contains("ReplaceViewGallerySharesAsync", detail, StringComparison.Ordinal);
        Assert.Contains("GalleryEditorLauncher.OpenAsync", detail, StringComparison.Ordinal);
        Assert.Contains("UpdateViewGalleryAsync", editor, StringComparison.Ordinal);
        Assert.Contains("DeleteViewGalleryAsync", editor, StringComparison.Ordinal);
        Assert.Contains("DeleteViewGalleryAsync", Read("src/MediaEngine.Web/Components/Pages/ViewSectionShell.razor"), StringComparison.Ordinal);
        Assert.Contains("<AppDialog", detail, StringComparison.Ordinal);
        Assert.Contains("<ViewRuleBuilder", editor, StringComparison.Ordinal);
        Assert.Contains("_gallery.Kind == ViewGalleryKind.Manual", detail, StringComparison.Ordinal);
        Assert.Contains("Manual membership stays available as a quick action", editor, StringComparison.Ordinal);
        Assert.Contains("(\"membership\", \"Membership\"", editor, StringComparison.Ordinal);
        Assert.DoesNotContain("Label=\"Edit\"", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Label=\"Delete\"", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ListenPlaylistRail_UsesTypedPlaylistDropWithoutChangingPlaylistBehavior()
    {
        var listen = Read("src/MediaEngine.Web/Components/Pages/ListenBrowsePage.razor");

        Assert.Contains("DropTarget: IsManualPlaylist(playlist) ? new PlaylistNavigationDropTarget(playlist.Id) : null", listen, StringComparison.Ordinal);
        Assert.Contains("MediaSectionNavigationDropEvent dropEvent", listen, StringComparison.Ordinal);
        Assert.Contains("dropEvent.Target is not PlaylistNavigationDropTarget playlistTarget", listen, StringComparison.Ordinal);
        Assert.Contains("AudioDrag.WorkIds", listen, StringComparison.Ordinal);
        Assert.Contains("ApiClient.AddCollectionItemAsync", listen, StringComparison.Ordinal);
        Assert.Contains("AudioDrag.Clear()", listen, StringComparison.Ordinal);
        Assert.DoesNotContain("DropCollectionId", listen, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewStyles_UseResponsiveStableMixedMediaGeometry()
    {
        var styles = Read("src/MediaEngine.Web/Components/Pages/ViewPhotoTimeline.razor.css");
        var scrubber = Read("src/MediaEngine.Web/Components/Pages/ViewTimelineScrubber.razor");
        var timelineScript = Read("src/MediaEngine.Web/wwwroot/js/view-timeline.js");

        Assert.Contains("--view-aspect", styles, StringComparison.Ordinal);
        Assert.Contains("object-fit: cover", styles, StringComparison.Ordinal);
        Assert.Contains("--target-row-height:10rem", styles, StringComparison.Ordinal);
        Assert.Contains("flex:var(--view-aspect)", styles, StringComparison.Ordinal);
        Assert.Contains("<AppTimelineNavigator", scrubber, StringComparison.Ordinal);
        Assert.Contains("resolveTimelineScrollRoot", timelineScript, StringComparison.Ordinal);
        Assert.Contains("new ResizeObserver", timelineScript, StringComparison.Ordinal);
        Assert.Contains(":focus-visible", styles, StringComparison.Ordinal);
        Assert.Contains("@media (max-width:640px)", styles, StringComparison.Ordinal);
        Assert.Contains("@media (hover:none)", styles, StringComparison.Ordinal);
    }

    private static string Read(string relativePath)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MediaEngine.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static void AssertViewRoute(string fileName, string route)
    {
        var source = Read($"src/MediaEngine.Web/Components/Pages/{fileName}");
        Assert.Contains($"@page \"{route}\"", source, StringComparison.Ordinal);
        Assert.Contains("<ViewSectionShell", source, StringComparison.Ordinal);
    }
}
