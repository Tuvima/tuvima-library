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
    public void EditingMode_ReusesTheEditorGuardsAndKeepsEveryControlKeyboardReachable()
    {
        var details = "src/MediaEngine.Web/Components/Details/";
        var bar = Read(details + "EditingModeBar.razor");
        var drawer = Read(details + "EditingDrawer.razor");
        var pencil = Read(details + "EditableFieldPencil.razor");
        var hero = Read(details + "EditingHeroArtworkButtons.razor");
        var shell = Read("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor");

        // One unsaved-input guard for the page (the editor's own lock and guard) and the shell's lock inside the drawer.
        Assert.Contains("<NavigationLock", bar);
        Assert.Contains("MediaEditorNavigationGuard", bar);
        Assert.Contains("OnBeforeInternalNavigation", bar);
        Assert.Contains("<NavigationLock", shell);
        Assert.Contains("role=\"group\" aria-label=\"Editing\"", bar);
        Assert.Contains("role=\"alert\"", bar);

        // The drawer is a labelled region; Esc closes it and focus returns to the control that opened it.
        Assert.Contains("aria-labelledby=\"tl-editing-drawer-title\"", drawer);
        Assert.Contains("string.Equals(args.Key, \"Escape\", StringComparison.Ordinal)", drawer);
        Assert.Contains("tuvimaFocusReturn.remember", drawer);
        Assert.Contains("tuvimaFocusReturn.restore", drawer);
        Assert.Contains("AriaLabel=\"Close editor drawer\"", drawer);

        // Pencils are real buttons with names, locks are focusable with the reason, and results are announced.
        Assert.Contains("AriaLabel=\"@($\"Edit {field.Label.ToLowerInvariant()}\")\"", pencil);
        Assert.Contains("tabindex=\"0\" role=\"img\" aria-label=\"@($\"{field.Label} is locked. {why}\")\"", pencil);
        Assert.Contains("role=\"alert\"", pencil);
        Assert.Contains("role=\"status\"", pencil);
        Assert.Contains("Escape", pencil);
        Assert.Contains("role=\"group\" aria-label=\"Edit hero artwork\"", hero);

        // Motion is optional and the drawer never traps a pointer-free user.
        Assert.Contains("prefers-reduced-motion", Read(details + "EditingDrawer.razor.css"));
        Assert.DoesNotContain("<AppFocusTrap", drawer);
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
        Assert.Contains("<AppButton Label=\"Configure\"", metadata);
        Assert.Contains("aria-label=\"Used for\"", metadata);
        Assert.Contains("role=\"table\"", metadata);
        Assert.DoesNotContain("role=\"button\"", metadata);
        Assert.Contains("<AppNativeButton Type=\"button\"", nowRow);
        Assert.Contains("AriaLabel=\"@OpenAriaLabel\"", nowRow);
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

        Assert.Contains("AppPageStateKind.Loading", sources["BackupRecoveryPanel.razor"]);
        Assert.Contains("AppPageStateKind.Error", sources["IngestionTasksTab.razor"]);
        Assert.Contains("<AppDialog", sources["BackupRecoveryPanel.razor"]);
        Assert.DoesNotContain("InvokeAsync<bool>(\n            \"confirm\"", sources["BackupRecoveryPanel.razor"], StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot, relativePath));
}
