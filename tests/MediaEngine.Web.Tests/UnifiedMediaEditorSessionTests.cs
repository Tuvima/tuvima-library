namespace MediaEngine.Web.Tests;

public sealed class UnifiedMediaEditorSessionTests
{
    [Fact]
    public void Editor_UsesOneSaveBoundaryForDetailsArtworkAndReviewedPairing()
    {
        var shell = Read("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor")
            + Read("src/MediaEngine.Web/Components/MediaEditor/Sections/EditorHeaderSection.razor")
            + Read("src/MediaEngine.Web/Components/MediaEditor/Sections/EditorArtworkSection.razor");
        var code = Read("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor.cs");

        Assert.Contains("HasStagedEditorChanges", shell, StringComparison.Ordinal);
        Assert.Contains("DeferMutations=\"true\"", shell, StringComparison.Ordinal);
        Assert.Contains("PendingChanged=\"OnUnifiedArtworkPendingChanged\"", shell, StringComparison.Ordinal);
        Assert.Contains("OnClick=\"SaveAsync\"", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveReviewedPairingFromFooterAsync", shell, StringComparison.Ordinal);

        Assert.Contains("HasStagedEditorChanges => IsDirty", code, StringComparison.Ordinal);
        Assert.Contains("ApplyPendingChangesAsync", code, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveReviewedPairingDraftAsync", code, StringComparison.Ordinal);
        Assert.Contains("Earlier editor changes were saved, but the remaining artwork changes were not saved", code, StringComparison.Ordinal);
        Assert.Contains("PersistArtworkChangesImmediately=\"true\"", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void EditingDrawer_RehousesTheSameShellAndSectionsInsteadOfRewritingThem()
    {
        var drawer = Read("src/MediaEngine.Web/Components/Details/EditingDrawer.razor");
        var shell = Read("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor");
        var drawerPartial = Read("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.Drawer.cs");
        var code = Read("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor.cs");

        // The drawer hosts the one editor shell; the three sections are the existing section components.
        Assert.Contains("<SharedMediaEditorShell", drawer, StringComparison.Ordinal);
        Assert.Contains("Drawer=\"true\"", drawer, StringComparison.Ordinal);
        Assert.Contains("<EditorDetailsSection", shell, StringComparison.Ordinal);
        Assert.Contains("<EditorArtworkSection", shell, StringComparison.Ordinal);
        Assert.Contains("<EditorMatchSection", shell, StringComparison.Ordinal);
        Assert.Contains("<EditorHeaderSection", shell, StringComparison.Ordinal);
        Assert.Contains("sme-shell--drawer", shell, StringComparison.Ordinal);

        // The drawer swaps the header and the section rail for a tab strip, drops History, and keeps one save path.
        Assert.Contains("@if (!Drawer)", shell, StringComparison.Ordinal);
        Assert.Contains("!(Drawer && tabId == \"history\")", code, StringComparison.Ordinal);
        Assert.Contains("(!Inline || Drawer) && (_confirmDiscard", code, StringComparison.Ordinal);
        Assert.Contains("public async Task RequestCloseAsync()", drawerPartial, StringComparison.Ordinal);
        Assert.Contains("DirtyChanged.InvokeAsync(dirty)", drawerPartial, StringComparison.Ordinal);
        Assert.Contains("HasPendingNavigationChanges", drawerPartial, StringComparison.Ordinal);

        // No second editor, route or all-in-one workbench appears.
        Assert.DoesNotContain("@page", drawer, StringComparison.Ordinal);
        Assert.DoesNotContain("Workbench", drawer, StringComparison.OrdinalIgnoreCase);
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
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
