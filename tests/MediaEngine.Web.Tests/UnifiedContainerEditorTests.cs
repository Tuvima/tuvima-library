namespace MediaEngine.Web.Tests;

public sealed class UnifiedContainerEditorTests
{
    [Fact]
    public void CollectionAndPlaylistCreation_UseOneEditorWithoutTheWizard()
    {
        var root = FindRepoRoot();
        var editor = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "Collections", "CollectionEditorShell.razor"));
        var launcher = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Services", "Editing", "CollectionEditorLauncherService.cs"));
        var models = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Services", "Editing", "CollectionEditorModels.cs"));

        Assert.False(File.Exists(Path.Combine(root, "src", "MediaEngine.Web", "Components", "Collections", "CollectionWizard.razor")));
        Assert.Contains("ShowAsync<CollectionEditorShell>", launcher, StringComparison.Ordinal);
        Assert.Contains("ContainerEditorKind", models, StringComparison.Ordinal);
        Assert.Contains("(\"membership\", \"Contents\"", editor, StringComparison.Ordinal);
        Assert.Contains("(\"companions\", \"Companions\"", editor, StringComparison.Ordinal);
        Assert.Contains("Ownership and visibility", editor, StringComparison.Ordinal);
        Assert.Contains("Primary area", editor, StringComparison.Ordinal);
        Assert.Contains("membershipMode:", editor, StringComparison.Ordinal);
        Assert.DoesNotContain("Create Smart Playlist", editor, StringComparison.Ordinal);
    }

    [Fact]
    public void GalleryCreation_UsesTheOneEditorWindowPhotosAndDocumentsUse()
    {
        var root = FindRepoRoot();
        var launcher = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Services", "Editing", "GalleryEditorLauncherService.cs"));
        var popover = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "Details", "ViewEditorPopover.razor"));
        var panel = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "Details", "ViewGalleryDetailsPanel.razor"));
        var page = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "Pages", "ViewGalleriesPage.razor"));

        Assert.False(File.Exists(Path.Combine(root, "src", "MediaEngine.Web", "Components", "Collections", "GalleryEditorShell.razor")));
        Assert.Contains("OpenGalleryAsync", launcher, StringComparison.Ordinal);
        Assert.Contains("<ViewGalleryDetailsPanel", popover, StringComparison.Ordinal);
        Assert.Contains("SoundtrackPlaylistId", File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Services", "Editing", "ViewEdit", "ViewGalleryEditStage.cs")), StringComparison.Ordinal);
        Assert.Contains("GetManagedCollectionsAsync", panel, StringComparison.Ordinal);
        Assert.Contains("GalleryEditorLauncher.OpenAsync", page, StringComparison.Ordinal);
        Assert.DoesNotContain("view-gallery-editor", page, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
