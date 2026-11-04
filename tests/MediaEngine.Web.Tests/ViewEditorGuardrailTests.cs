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
        var chrome = Web("Components/Details/EditorWindowChrome.razor");
        var library = Web("Components/Details/EditorPanelHost.razor");
        var view = Web("Components/Details/ViewEditorPopover.razor");

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
    public void ThePhotosAndDocumentsScreensSpeakThroughTheRibbonNotAToast()
    {
        foreach (var file in new[] { "ViewPage", "ViewGalleryDetailPage", "ViewDocumentsPage", "ViewImmersiveViewer" })
        {
            var source = Web($"Components/Pages/{file}.razor");
            Assert.DoesNotContain("IAppToastService", source, StringComparison.Ordinal);
        }

        Assert.Contains("Details.ViewSavedRibbon", Web("Shared/MainLayout.razor"), StringComparison.Ordinal);
        Assert.Contains("Details.ViewEditorPopover", Web("Shared/MainLayout.razor"), StringComparison.Ordinal);
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
}
