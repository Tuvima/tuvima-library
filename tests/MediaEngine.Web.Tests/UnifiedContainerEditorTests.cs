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
        Assert.Contains("(\"membership\", \"Membership\"", editor, StringComparison.Ordinal);
        Assert.Contains("(\"companions\", \"Companions\"", editor, StringComparison.Ordinal);
        Assert.Contains("Ownership and visibility", editor, StringComparison.Ordinal);
        Assert.Contains("Primary area", editor, StringComparison.Ordinal);
        Assert.Contains("membershipMode:", editor, StringComparison.Ordinal);
        Assert.DoesNotContain("Create Smart Playlist", editor, StringComparison.Ordinal);
    }

    [Fact]
    public void GalleryCreation_UsesTheSharedEditorWorkspace()
    {
        var root = FindRepoRoot();
        var editor = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "Collections", "GalleryEditorShell.razor"));
        var page = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "Pages", "ViewGalleriesPage.razor"));

        Assert.Contains("sme-shell collection-editor-workspace", editor, StringComparison.Ordinal);
        Assert.Contains("(\"membership\", \"Membership\"", editor, StringComparison.Ordinal);
        Assert.Contains("(\"soundtrack\", \"Soundtrack\"", editor, StringComparison.Ordinal);
        Assert.Contains("SoundtrackPlaylistId", editor, StringComparison.Ordinal);
        Assert.Contains("GetManagedCollectionsAsync", editor, StringComparison.Ordinal);
        Assert.DoesNotContain("persistence is enabled", editor, StringComparison.OrdinalIgnoreCase);
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
