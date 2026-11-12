using MediaEngine.Web.Services.Editing;
namespace MediaEngine.Web.Tests;

/// <summary>
/// Rules of the photos, videos, documents and galleries editor (spec photos-documents-editor) that must not drift back: one frame for every editor,
/// no field saved outside the editor, no toast for what the editor or a quick action did, and the retired surfaces staying gone.
/// </summary>
public sealed class ViewEditorGuardrailTests
{
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static string Web(string relative) => File.ReadAllText(Path.Combine(Root(), "src", "MediaEngine.Web", relative.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void EveryEditorWindowIsDrawnByTheOneSharedFrame()
    {
        var chrome = Web("Components/Editor/EditorWindowChrome.razor");
        var library = Web("Components/Editor/EditorPanelHost.razor");
        var view = Web("Components/Editor/ViewEditorPopover.razor");

        Assert.Contains("tl-editor-window__footer", chrome, StringComparison.Ordinal);
        Assert.Contains("<EditorWindowChrome", library, StringComparison.Ordinal);
        Assert.Contains("<EditorWindowChrome", view, StringComparison.Ordinal);
        // Neither editor draws its own footer, tabs or close button: that is the frame's job, so the two cannot look different.
        foreach (var source in new[] { library, view })
        {
            Assert.DoesNotContain("tl-editor-window__footer", source, StringComparison.Ordinal);
            Assert.DoesNotContain("<AppTabs", source, StringComparison.Ordinal);
            Assert.DoesNotContain("tl-editor-window__close", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NoViewScreenSavesAFieldOutsideTheEditor()
    {
        var pages = Directory.GetFiles(Path.Combine(Root(), "src", "MediaEngine.Web", "Components"), "*.razor", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).StartsWith("View", StringComparison.Ordinal));
        string[] fieldWrites =
        [
            "UpdateViewItemDescriptionAsync", "UpdateViewItemTagsAsync", "UpdateViewItemLocationAsync", "UpdateViewItemPeopleAsync",
            "UpdateViewItemCapturedAtAsync", "UpdateViewDocumentDetailsAsync",
        ];

        foreach (var path in pages)
        {
            var source = File.ReadAllText(path);
            foreach (var call in fieldWrites)
            {
                Assert.False(source.Contains(call, StringComparison.Ordinal), $"{Path.GetFileName(path)} saves a field with {call}; fields are saved only by the editor's Save changes.");
            }
        }
    }

    [Fact]
    public void TrashAndArchiveAlwaysAskOnceFirstOnEveryViewSurface()
    {
        // Spec view-section WP1: the selection bar, tile menu, viewer, Gallery viewer, Places and Documents all ask before Trash or Archive.
        foreach (var (file, call) in new[]
        {
            ("ViewPage", "ViewTrashConfirmation.AskTrashAsync"), ("ViewPage", "ViewTrashConfirmation.AskArchiveAsync"),
            ("ViewGalleryDetailPage", "ViewTrashConfirmation.AskTrashAsync"), ("ViewGalleryDetailPage", "ViewTrashConfirmation.AskArchiveAsync"),
            ("ViewPlacesPage", "ViewTrashConfirmation.AskTrashAsync"), ("ViewPlacesPage", "ViewTrashConfirmation.AskArchiveAsync"),
            ("ViewDocumentsPage", "ViewTrashConfirmation.AskTrashAsync"),
        })
        {
            Assert.Contains(call, Web($"Components/Pages/{file}.razor"), StringComparison.Ordinal);
        }

        Assert.Contains("ViewActionPolicy.For", Web("Components/Pages/ViewImmersiveViewer.razor"), StringComparison.Ordinal);
        Assert.Contains("ViewActionPolicy.For", Web("Components/Pages/ViewPage.razor"), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryViewSurfaceThatAsksThePolicyPassesTheDeviceRule()
    {
        // Move to Trash is offered only where EditorDeviceRule.CanEditItems is true (never a TV). The policy field has no default, so a caller cannot forget it;
        // this checks the value each caller passes. The Trash handlers' refusal on a TV is covered at runtime in ViewEmptyTrashTests (ViewPage, People, Places, Gallery, Documents).
        foreach (var file in new[] { "ViewPage", "ViewPeoplePage", "ViewImmersiveViewer" })
        {
            Assert.Contains("DeviceMayEditItems: EditorHost.CanEdit", Web($"Components/Pages/{file}.razor"), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ThePhotosAndDocumentsScreensSpeakThroughTheRibbonNotAToast()
    {
        // Every View screen, panel and setting speaks through the ribbon (spec view-section WP2), not only the four photo pages.
        var viewSources = Directory.EnumerateFiles(Path.Combine(Root(), "src", "MediaEngine.Web", "Components"), "View*.razor", SearchOption.AllDirectories)
            .ToList();
        Assert.NotEmpty(viewSources);
        foreach (var path in viewSources)
        {
            Assert.DoesNotContain("IAppToastService", File.ReadAllText(path), StringComparison.Ordinal);
        }

        Assert.Contains("Editor.ViewSavedRibbon", Web("Shared/MainLayout.razor"), StringComparison.Ordinal);
        Assert.Contains("Editor.ViewEditorPopover", Web("Shared/MainLayout.razor"), StringComparison.Ordinal);
    }

    [Fact]
    public void AGalleryLoadsItsNextPageAsTheEndOfTheListNears()
    {
        var page = Web("Components/Pages/ViewGalleryDetailPage.razor");

        Assert.Contains("load-more-observer.js", page, StringComparison.Ordinal);
        Assert.Contains("LoadMoreFromObserverAsync", page, StringComparison.Ordinal);
        // Load more stays under the list as the fallback.
        Assert.Contains("Common_LoadMore", page, StringComparison.Ordinal);
    }

    [Fact]
    public void TheViewerSidePanelOnlyShowsAndOffersOneEditButton()
    {
        var viewer = Web("Components/Pages/ViewImmersiveViewer.razor");

        Assert.Contains("EditorHost.OpenItemsAsync", viewer, StringComparison.Ordinal);
        Assert.DoesNotContain("<AppTextarea", viewer, StringComparison.Ordinal);
        Assert.DoesNotContain("<AppTagInput", viewer, StringComparison.Ordinal);
        Assert.DoesNotContain("<AppAutocomplete", viewer, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveDescriptionAsync", viewer, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRetiredEditingSurfacesStayGone()
    {
        Assert.False(File.Exists(Path.Combine(Root(), "src", "MediaEngine.Web", "Components", "Pages", "ViewDocumentDetailsDialog.razor")));
        Assert.False(File.Exists(Path.Combine(Root(), "src", "MediaEngine.Web", "Components", "Pages", "ViewDocumentBulkDialog.razor")));
        Assert.False(File.Exists(Path.Combine(Root(), "src", "MediaEngine.Web", "Components", "Collections", "GalleryEditorShell.razor")));
    }

    [Fact]
    public void EditingStaysInLibraryAndNeverRewritesTheFiles()
    {
        var session = Web("Services/Editing/ViewEdit/ViewEditSession.cs");
        var service = File.ReadAllText(Path.Combine(Root(), "src", "MediaEngine.Api", "Services", "View", "ViewItemsEditService.cs"));

        Assert.Contains("ShowsFilesStatus => false", session, StringComparison.Ordinal);
        Assert.Contains("FileWriteMediaTypes => []", session, StringComparison.Ordinal);
        Assert.DoesNotContain("File.WriteAllBytes", service, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Move", service, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSelectionBarTheTileMenuAndTheViewerListTheActionsInOneOrder()
    {
        // Each surface names an action by a marker in its own markup; read in file order, the markers must follow the shared order.
        var surfaces = new Dictionary<string, (string Source, (string Action, string Marker)[] Markers)>
        {
            ["selection bar"] = (Web("Components/Pages/ViewSelectionToolbar.razor"), new[]
            {
                (ViewItemActions.Favorite, "OnFavorite.InvokeAsync"),
                (ViewItemActions.AddToGallery, "ViewSelectionToolbar_AddToGallery"),
                (ViewItemActions.Download, "ViewSelectionToolbar_DownloadSelectedItems"),
                (ViewItemActions.Share, "Common_SubmitToSharedLibrary"),
                (ViewItemActions.Archive, "ViewSelectionToolbar_Archive\"]</AppNativeButton>"),
                (ViewItemActions.Edit, "ViewEdit_Edit"),
                (ViewItemActions.Trash, "Common_Trash"),
            }),
            ["tile menu"] = (Web("Components/Pages/ViewPhotoTimeline.razor"), new[]
            {
                (ViewItemActions.Edit, "Common_Edit"),
                (ViewItemActions.Trash, "Common_Trash"),
            }),
            ["viewer"] = (Web("Components/Shared/MediaViewerShell.razor"), new[]
            {
                (ViewItemActions.Favorite, "Capabilities.CanFavorite"),
                (ViewItemActions.AddToGallery, "Capabilities.CanAddToGallery"),
                (ViewItemActions.Download, "Capabilities.CanDownload"),
                (ViewItemActions.Archive, "Capabilities.CanArchive"),
                (ViewItemActions.Edit, "@ContextActions"),
                (ViewItemActions.Trash, "Capabilities.CanTrash"),
            }),
        };

        foreach (var (surface, (source, markers)) in surfaces)
        {
            var positions = markers.Select(entry => (entry.Action, At: source.IndexOf(entry.Marker, StringComparison.Ordinal))).ToList();
            Assert.All(positions, entry => Assert.True(entry.At >= 0, $"The {surface} lost its {entry.Action} action."));
            var expected = ViewItemActions.Order.Where(action => positions.Any(entry => entry.Action == action)).ToList();
            Assert.Equal(expected, positions.OrderBy(entry => entry.At).Select(entry => entry.Action).ToList());
        }
    }

    [Fact]
    public void WhoseLibraryIsChosenOnlyBy_TheViewingSwitchInTheRail()
    {
        // One switch at the top of the rail picks the scope for every View page; a page-level scope picker would let two disagree.
        foreach (var page in new[] { "ViewPage", "ViewPlacesPage", "ViewDocumentsPage", "ViewPeoplePage" })
        {
            var source = Web($"Components/Pages/{page}.razor");
            Assert.DoesNotContain("ViewScopeOptions", source, StringComparison.Ordinal);
            Assert.DoesNotContain("SelectScopeAsync", source, StringComparison.Ordinal);
            Assert.Contains("Workspace.ScopeChanged += ScopeSwitchedAsync", source, StringComparison.Ordinal);
            Assert.Contains("Workspace.ScopeChanged -= ScopeSwitchedAsync", source, StringComparison.Ordinal);
        }

        var shell = Web("Components/Pages/ViewSectionShell.razor");
        Assert.Contains("<ViewScopeSwitch", shell, StringComparison.Ordinal);
        Assert.Contains("NavigationHeader=", shell, StringComparison.Ordinal);
    }
}
