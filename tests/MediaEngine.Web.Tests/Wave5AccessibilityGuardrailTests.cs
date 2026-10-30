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
        var reader = Read("src/MediaEngine.Web/Components/Pages/BookReader.razor");
        var folderBrowser = Read("src/MediaEngine.Web/Components/Shared/ServerFolderPicker.razor");

        Assert.Contains("<AppNativeButton Class=\"br-icon-btn\" OnClick=\"ExitReader\"", reader);
        Assert.Contains("<AppNativeButton Class=\"br-row br-row--grow\"", reader);
        Assert.DoesNotContain("<div class=\"br-row\" @onclick", reader);
        Assert.Contains("role=\"listbox\"", folderBrowser);
        Assert.Contains("AriaLabel=\"@($\"Open folder {folder.Name}\")\"", folderBrowser);
        Assert.Contains("aria-live=\"polite\"", folderBrowser);
        Assert.Contains("Label=\"@ConfirmLabel\"", folderBrowser);
        Assert.Contains("ConfirmLabel { get; set; } = \"Add\"", folderBrowser);
        // Shy's 2026-10-18 addendum (AD2) replaces open-on-click: a single click selects, a double click or the row's open button opens.
        Assert.Contains("AriaLabel=\"@($\"Open folder {folder.Name}\")\"", folderBrowser);
        Assert.Contains("aria-label=\"@($\"Select folder {folder.Name}\")\"", folderBrowser);
        Assert.Contains("@ondblclick", folderBrowser);
    }

    [Fact]
    public void ActiveSelectableSurfaces_ProvideKeyboardActivation()
    {
        var plugins = Read("src/MediaEngine.Web/Components/Settings/PluginSettingsTab.razor");
        var metadata = Read("src/MediaEngine.Web/Components/Settings/MetadataSettingsPage.razor");
        var nowRow = Read("src/MediaEngine.Web/Components/Settings/IngestionMediaCard.razor");

        Assert.Contains("<AppButton Label=\"Configure\"", plugins);
        Assert.DoesNotContain("role=\"button\"", plugins);
        Assert.DoesNotContain("role=\"button\"", metadata);
        Assert.Contains("<AppNativeButton Type=\"button\"", nowRow);
        Assert.Contains("AriaLabel=\"@OpenAriaLabel\"", nowRow);
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

        Assert.Contains("AppPageStateKind.Loading", sources["BackupRecoveryPanel.razor"]);
        Assert.Contains("AppPageStateKind.Error", sources["IngestionTasksTab.razor"]);
        Assert.Contains("<AppDialog", sources["BackupRecoveryPanel.razor"]);
        Assert.DoesNotContain("InvokeAsync<bool>(\n            \"confirm\"", sources["BackupRecoveryPanel.razor"], StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot, relativePath));
}
