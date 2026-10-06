namespace MediaEngine.Web.Tests;

public sealed class EditionCoverArtworkUiTests
{
    [Fact]
    public void ArtworkTabRequiresImpactReviewBeforeEditionCoverSave()
    {
        var root = FindRepositoryRoot();
        var markup = File.ReadAllText(Path.Combine(root,
            "src", "MediaEngine.Web", "Components", "MediaEditor", "Sections", "EditorArtworkSection.razor"));
        var code = File.ReadAllText(Path.Combine(root,
            "src", "MediaEngine.Web", "Components", "MediaEditor", "SharedMediaEditorShell.razor.cs"));

        Assert.Contains("Current owner: <code>@EditionCoverOwnerLabel</code>", markup, StringComparison.Ordinal);
        Assert.Contains("@editionReview.AffectedFiles.Count", markup, StringComparison.Ordinal);
        Assert.Contains("Save Edition cover", markup, StringComparison.Ordinal);
        Assert.Contains("PreviewMediaEditorEditionCoverAsync", code, StringComparison.Ordinal);
        Assert.Contains("SaveMediaEditorEditionCoverAsync", code, StringComparison.Ordinal);
        Assert.Contains("await PreviewEditionCoverAsync(assetId, item.VariantId);", code, StringComparison.Ordinal);
        Assert.Contains("await SetPreferredArtworkVariantAsync(item.VariantId);", code, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "MediaEngine.Web")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
