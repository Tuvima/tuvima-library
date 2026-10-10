namespace MediaEngine.Web.Tests;

public sealed class Wave5AccessibilityGuardrailTests
{
    private static readonly string RepoRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void MainLayout_HasSkipLinkAndMainLandmark()
    {
        var source = Read("src/MediaEngine.Web/Shared/MainLayout.razor");

        Assert.Contains("href=\"#main-content\"", source);
        Assert.Contains("id=\"main-content\"", source);
        Assert.Contains("role=\"main\"", source);
    }

    [Fact]
    public void SharedDialogShell_HasDialogLabelAndAccessibleClose()
    {
        var source = Read("src/MediaEngine.Web/Components/Shared/AppDialogShell.razor");

        Assert.Contains("role=\"@(Modal && Context is null ? \"dialog\" : \"region\")\"", source);
        Assert.Contains("aria-modal=\"@(Modal && Context is null ? \"true\" : null)\"", source);
        Assert.Contains("aria-labelledby", source);
        Assert.Contains("aria-label=\"Close dialog\"", source);
    }

    [Fact]
    public void ActiveEditors_IncludeNavigationLockForUnsavedChanges()
    {
        Assert.Contains("NavigationLock", Read("src/MediaEngine.Web/Components/Collections/CollectionEditorShell.razor"));
        Assert.Contains("NavigationLock", Read("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor"));
    }

    [Fact]
    public void EditorFieldActions_ExposeTheirTooltipLabelsToAssistiveTechnology()
    {
        var source = Read("src/MediaEngine.Web/Components/Shared/AppFormFieldRow.razor");

        Assert.Contains("aria-label=\"@ActionLabel\"", source);
        Assert.Contains("aria-label=\"@ConfirmLabel\"", source);
        Assert.Contains("aria-label=\"@CancelLabel\"", source);
    }

    [Fact]
    public void ReaderAndFolderPicker_UseNativeControlsForClickActions()
    {
        var reader = Read("src/MediaEngine.Web/Components/Pages/EpubReader.razor");
        var folderBrowser = Read("src/MediaEngine.Web/Components/Shared/ServerFolderPicker.razor");

        Assert.Contains("Class=\"reader-tap-zone left\" AriaLabel=\"Previous page\"", reader);
        Assert.Contains("Class=\"reader-search-result\"", reader);
        Assert.Contains("Class=\"reader-list-open\"", reader);
        Assert.DoesNotContain("<div class=\"reader-search-result\"", reader);
        Assert.Contains("role=\"listbox\"", folderBrowser);
        Assert.Contains("AriaLabel=\"@($\"Open folder {folder.Name}\")\"", folderBrowser);
        Assert.Contains("aria-live=\"polite\"", folderBrowser);
        Assert.Contains("Select this folder", folderBrowser);
        Assert.DoesNotContain("@ondblclick", folderBrowser);
    }

    [Fact]
    public void ActiveSelectableSurfaces_ProvideKeyboardActivation()
    {
        var plugins = Read("src/MediaEngine.Web/Components/Settings/PluginSettingsTab.razor");
        var metadata = Read("src/MediaEngine.Web/Components/Settings/MetadataSettingsPage.razor");
        var batchHistory = Read("src/MediaEngine.Web/Components/Settings/IngestionBatchHistory.razor");

        Assert.Contains("<AppButton Label=\"Configure\"", plugins);
        Assert.DoesNotContain("role=\"button\"", plugins);
        Assert.Contains("<AppButton Label=\"Configure\"", metadata);
        Assert.Contains("aria-label=\"Used for\"", metadata);
        Assert.Contains("role=\"table\"", metadata);
        Assert.DoesNotContain("role=\"button\"", metadata);
        Assert.Contains("<AppNativeButton Type=\"button\"", batchHistory);
        Assert.Contains("aria-label=\"@($\"Open batch", batchHistory);
    }

    [Fact]
    public void ArtworkPreview_RemainsAnAccessibleFocusedComponent()
    {
        var adapter = Read("src/MediaEngine.Web/Components/MediaEditor/MediaEditorArtworkLightbox.razor");
        var source = Read("src/MediaEngine.Web/Components/Shared/MediaViewerShell.razor");

        Assert.Contains("<MediaViewerShell", adapter);
        Assert.Contains("role=\"dialog\"", source);
        Assert.Contains("aria-modal=\"true\"", source);
        Assert.Contains("AriaLabel=\"Close viewer\"", source);
        Assert.Contains("<AppFocusTrap", source);
    }

    [Fact]
    public void SettingsSurfaces_UseSharedControlsAndAnnouncedPageStates()
    {
        var settingsRoot = Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Components", "Settings");
        var sources = Directory.EnumerateFiles(settingsRoot, "*.razor", SearchOption.TopDirectoryOnly)
            .ToDictionary(path => Path.GetFileName(path)!, File.ReadAllText, StringComparer.OrdinalIgnoreCase);

        foreach (var (fileName, source) in sources)
        {
            Assert.DoesNotMatch(
                new System.Text.RegularExpressions.Regex(
                    @"<\s*(button|select|input)(?:\s|>)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                source);
        }

        Assert.Contains("AppPageStateKind.Loading", sources["OverviewTab.razor"]);
        Assert.Contains("AppPageStateKind.Error", sources["OverviewTab.razor"]);
        Assert.Contains("AppPageStateKind.Loading", sources["BackupRecoveryPanel.razor"]);
        Assert.Contains("AppPageStateKind.Error", sources["IngestionTasksTab.razor"]);
        Assert.Contains("AppPageStateKind.Empty", sources["LibrariesTab.razor"]);
        Assert.Contains("<AppDialog", sources["BackupRecoveryPanel.razor"]);
        Assert.DoesNotContain("InvokeAsync<bool>(\n            \"confirm\"", sources["BackupRecoveryPanel.razor"], StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot, relativePath));
}
