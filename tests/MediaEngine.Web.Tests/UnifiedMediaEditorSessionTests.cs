namespace MediaEngine.Web.Tests;

public sealed class UnifiedMediaEditorSessionTests
{
    [Fact]
    public void Editor_UsesOneSaveBoundaryForDetailsArtworkAndReviewedPairing()
    {
        var shell = Read("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor");
        var code = Read("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor.cs");

        Assert.Contains("HasStagedEditorChanges", shell, StringComparison.Ordinal);
        Assert.Contains("DeferMutations=\"true\"", shell, StringComparison.Ordinal);
        Assert.Contains("PendingChanged=\"OnUnifiedArtworkPendingChanged\"", shell, StringComparison.Ordinal);
        Assert.Contains("OnClick=\"SaveAsync\"", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveReviewedPairingFromFooterAsync", shell, StringComparison.Ordinal);

        Assert.Contains("IsDirty || _pairingReviewPending", code, StringComparison.Ordinal);
        Assert.Contains("ApplyPendingChangesAsync", code, StringComparison.Ordinal);
        Assert.Contains("SaveReviewedPairingDraftAsync(savedAnything)", code, StringComparison.Ordinal);
        Assert.Contains("Earlier editor changes were saved, but the remaining artwork changes were not saved", code, StringComparison.Ordinal);
        Assert.Contains("Details and artwork were saved, but the reviewed file pairing was not saved", code, StringComparison.Ordinal);
    }

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
        Assert.Contains("are staged until the editor Save action", workspace, StringComparison.Ordinal);
    }

    [Fact]
    public void PairingPreview_ReportsSaveOutcomeAndCanDiscardPendingReview()
    {
        var preview = Read("src/MediaEngine.Web/Components/MediaEditor/MediaEditorPairingPreview.razor");

        Assert.Contains("public async Task<bool> SaveReviewedPairingAsync()", preview, StringComparison.Ordinal);
        Assert.Contains("public string? LastSaveError", preview, StringComparison.Ordinal);
        Assert.Contains("public void DiscardPendingReview()", preview, StringComparison.Ordinal);
        Assert.Contains("PendingReviewChanged.InvokeAsync(false)", preview, StringComparison.Ordinal);
    }

    private static string Read(
        string relativePath,
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "") =>
        File.ReadAllText(Path.Combine(FindRepoRoot(sourceFile), relativePath));

    private static string FindRepoRoot(string sourceFile)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
