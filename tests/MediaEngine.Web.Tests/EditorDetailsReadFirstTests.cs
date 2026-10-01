namespace MediaEngine.Web.Tests;

public sealed class EditorDetailsReadFirstTests
{
    [Fact]
    public void DetailsStartsReadOnlyAndKeepsOneInlineEditActionPair()
    {
        var root = FindRepositoryRoot();
        var markup = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "MediaEditor", "SharedMediaEditorShell.razor"));
        var code = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "MediaEditor", "SharedMediaEditorShell.razor.cs"));

        Assert.Contains("@if (!_detailsEditing)", markup, StringComparison.Ordinal);
        Assert.Contains("Title=\"Metadata\"", markup, StringComparison.Ordinal);
        Assert.Contains("Label=\"Edit\"", markup, StringComparison.Ordinal);
        Assert.Contains("OnClick=\"SaveDetailsAsync\"", markup, StringComparison.Ordinal);
        Assert.Contains("OnClick=\"CancelDetailsEdit\"", markup, StringComparison.Ordinal);
        Assert.Contains("<MudExpansionPanel Text=\"File &amp; processing\">", markup, StringComparison.Ordinal);
        Assert.Contains("QueueFullEnrichmentAsync", markup, StringComparison.Ordinal);
        Assert.Contains("keepEditorOpen: true", code, StringComparison.Ordinal);
        Assert.Contains("LoadSingleItemAsync(CurrentEntityId, resetEditorState: true", code, StringComparison.Ordinal);
        Assert.Contains("_detailsEditing = false", code, StringComparison.Ordinal);
    }

    [Fact]
    public void DetailsUsesLandscapeEpisodeArtworkAndCompactReadSections()
    {
        var root = FindRepositoryRoot();
        var markup = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "MediaEditor", "SharedMediaEditorShell.razor"));
        var code = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "MediaEditor", "SharedMediaEditorShell.razor.cs"));

        Assert.Contains("sme-details-artwork @ArtworkShapeClass", markup, StringComparison.Ordinal);
        Assert.Contains("Title=\"Recent activity\"", markup, StringComparison.Ordinal);
        Assert.Contains("Take(3)", markup, StringComparison.Ordinal);
        Assert.Contains("scopeId, \"episode\"", code, StringComparison.Ordinal);
        Assert.Contains("return \"is-landscape\"", code, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
