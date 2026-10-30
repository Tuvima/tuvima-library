namespace MediaEngine.Web.Tests;

public sealed class UnifiedMediaEditorSessionTests
{
    [Fact]
    public void ArtworkWorkspace_DefersSupportedMutationsAndDiscardClearsTheDraft()
    {
        var workspace = Read("src/MediaEngine.Web/Components/Artwork/ArtworkWorkspace.razor");

        Assert.Contains("public bool DeferMutations", workspace, StringComparison.Ordinal);
        Assert.Contains("public bool HasPendingChanges", workspace, StringComparison.Ordinal);
        Assert.Contains("PendingArtworkMutationKind.Upload", workspace, StringComparison.Ordinal);
        Assert.Contains("PendingArtworkMutationKind.Url", workspace, StringComparison.Ordinal);
        Assert.Contains("PendingArtworkMutationKind.Link", workspace, StringComparison.Ordinal);
        Assert.Contains("PendingArtworkMutationKind.Preferred", workspace, StringComparison.Ordinal);
        Assert.Contains("PendingArtworkMutationKind.Remove", workspace, StringComparison.Ordinal);
        Assert.Contains("public async Task<(bool Saved, string? Error)> ApplyPendingChangesAsync()", workspace, StringComparison.Ordinal);
        Assert.Contains("public void DiscardPendingChanges()", workspace, StringComparison.Ordinal);
        Assert.Contains("<div class=\"artwork-workspace-core__toolbar\">", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("are staged until the editor Save action", workspace, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtworkWorkspace_PersistsTilePreferenceAndKeepsDeleteSeparateAndContextSafe()
    {
        var workspace = Read("src/MediaEngine.Web/Components/Artwork/ArtworkWorkspace.razor");
        var styles = Read("src/MediaEngine.Web/Components/Artwork/ArtworkWorkspace.razor.css");

        Assert.Contains("OnClick=\"(() => SelectPreferredVariantAsync(variant))\"", workspace, StringComparison.Ordinal);
        Assert.Contains("public bool PersistArtworkChangesImmediately", workspace, StringComparison.Ordinal);
        Assert.Contains("DeferMutations && !PersistArtworkChangesImmediately", workspace, StringComparison.Ordinal);
        Assert.Contains("new ArtworkLinkRequest(assetId, target.Role, variantContext, Preferred: true", workspace, StringComparison.Ordinal);
        Assert.Contains("if (MatchesCurrentTarget(target))", workspace, StringComparison.Ordinal);
        Assert.Contains("OnClick=\"@(() => BeginDelete(variant))\"", workspace, StringComparison.Ordinal);
        Assert.Contains("AriaLabel=\"@($\"Remove {ActiveRole.Label} artwork variant\")\"", workspace, StringComparison.Ordinal);
        Assert.Contains(".artwork-workspace-core__variant-tile:focus-within", styles, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 900px)", styles, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 600px)", styles, StringComparison.Ordinal);
    }

    private static string Read(
        string relativePath,
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "") =>
        File.ReadAllText(Path.Combine(FindRepoRoot(sourceFile), relativePath));

    private static string FindRepoRoot(string sourceFile)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
