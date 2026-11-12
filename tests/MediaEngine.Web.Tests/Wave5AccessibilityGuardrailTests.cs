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

        Assert.Contains("role=\"@(Modal && !IsFramed ? \"dialog\" : \"region\")\"", source);
        Assert.Contains("aria-modal=\"@(Modal && !IsFramed ? \"true\" : null)\"", source);
        Assert.Contains("aria-labelledby", source);
        Assert.Contains("aria-label=\"Close dialog\"", source);
    }

    [Fact]
    public void ReaderAndFolderPicker_UseNativeControlsForClickActions()
    {
        var reader = Read("src/MediaEngine.Web/Components/Pages/BookReader.razor");
        var folderBrowser = Read("src/MediaEngine.Web/Components/Shared/ServerFolderPicker.razor");

        // The top line is the reader frame's: Close (never a Back arrow) leaves the reader.
        Assert.Contains("OnClose=\"ExitReader\"", reader);
        Assert.DoesNotContain("ArrowBack", reader);
        Assert.Contains("<AppNativeButton Class=\"rf-row rf-row--grow\"", reader);
        Assert.DoesNotContain("<div class=\"rf-row\" @onclick", reader);
        // The folder list is the shared browser frame's grid with one keyboard model; a folder row is its own component (Editor v5 D32).
        Assert.Contains("role=\"grid\"", Read("src/MediaEngine.Web/Components/Shared/AppBrowserFrame.razor"));
        Assert.Contains("<AppBrowserFolderRow", folderBrowser);
        Assert.Contains("aria-live=\"polite\"", folderBrowser);
        Assert.Contains("Label=\"@ConfirmButtonLabel\"", folderBrowser);
        Assert.Contains("ConfirmLabel { get; set; } = \"Add\"", folderBrowser);
        // Editor v5 D32 (Shy, 2026-11-07) brings back open-on-click: a click on a folder row or its open button opens it, and the folder you are inside is the selection.
        var folderRow = Read("src/MediaEngine.Web/Components/Shared/AppBrowserFolderRow.razor");
        Assert.Contains("AriaLabel=\"@OpenLabelText\"", folderRow);
        Assert.Contains("@onclick=\"OpenAsync\"", folderRow);
        Assert.DoesNotContain("@ondblclick", folderBrowser);
    }

    [Fact]
    public void ActiveSelectableSurfaces_ProvideKeyboardActivation()
    {
        var plugins = Read("src/MediaEngine.Web/Components/Settings/PluginSettingsTab.razor");
        var metadata = Read("src/MediaEngine.Web/Components/Settings/MetadataSettingsPage.razor");
        var nowRow = Read("src/MediaEngine.Web/Components/Settings/IngestionMediaCard.razor");

        Assert.Contains("<AppIconButton Icon=\"@AppMaterialIcons.Outlined.Settings\"", plugins);
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
        Assert.Contains("AppPageStateKind.Error", sources["LibrariesTab.razor"]);
        Assert.Contains("<AppDialogShell", sources["BackupRecoveryPanel.razor"]);
        Assert.DoesNotContain("InvokeAsync<bool>(\n            \"confirm\"", sources["BackupRecoveryPanel.razor"], StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        LocalizedSource.ReadAllText(Path.Combine(RepoRoot, relativePath));
}
