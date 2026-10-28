using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Ui;
namespace MediaEngine.Web.Tests;

public sealed class ServerFolderPickerUiTests
{
    private static readonly string RepoRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void SharedPicker_IsAUniversalServerVisibleFolderComponent()
    {
        var picker = Read("src/MediaEngine.Web/Components/Shared/ServerFolderPicker.razor");

        Assert.Contains("@namespace MediaEngine.Web.Components.Shared", picker, StringComparison.Ordinal);
        Assert.Contains("[Parameter] public string SelectionMode", picker, StringComparison.Ordinal);
        Assert.Contains("[Parameter] public string? CurrentSourceId", picker, StringComparison.Ordinal);
        Assert.Contains("[Parameter] public IReadOnlyCollection<string>? AllowedStorageLocationIds", picker, StringComparison.Ordinal);
        Assert.Contains("GetServerFolderRootsAsync", picker, StringComparison.Ordinal);
        Assert.Contains("BrowseServerFoldersAsync", picker, StringComparison.Ordinal);
        Assert.Contains("ValidateServerFolderAsync", picker, StringComparison.Ordinal);
        Assert.DoesNotContain("Enter path manually", picker, StringComparison.Ordinal);
        Assert.Contains("Label=\"@ConfirmLabel\"", picker, StringComparison.Ordinal);
        Assert.Contains("ConfirmLabel { get; set; } = \"Add\"", picker, StringComparison.Ordinal);
        Assert.DoesNotContain("InputFile", picker, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(275)", picker, StringComparison.Ordinal);
        Assert.Contains("preserveSelection: true", picker, StringComparison.Ordinal);
        Assert.Contains("_browseRequest?.Cancel()", picker, StringComparison.Ordinal);
    }

    [Fact]
    public void Picker_ShowsPathsExactlyAsTheEngineReportsThemWithNoHostTranslation()
    {
        var picker = Read("src/MediaEngine.Web/Components/Shared/ServerFolderPicker.razor");
        var engine = Read("src/MediaEngine.Api/Services/Settings/ServerFolderBrowserService.cs");

        // The path box and the footer take the Engine's own text (display path / validated path), never a host-mapped one.
        Assert.Contains("_pathText = browse.DisplayPath", picker, StringComparison.Ordinal);
        Assert.Contains("_selectedPathText = _validation.Path", picker, StringComparison.Ordinal);
        foreach (var translation in new[] { "HostPath", "host_path", "HOST_PATH", "PathMapping", "MapToHost", "TranslatePath" })
        {
            Assert.DoesNotContain(translation, picker, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(translation, engine, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Picker_PutsThePathRowAboveTheListsAndTakesAnOptionalMediaType()
    {
        var picker = Read("src/MediaEngine.Web/Components/Shared/ServerFolderPicker.razor");

        Assert.Contains("[Parameter] public string? MediaType", picker, StringComparison.Ordinal);
        Assert.Contains("AppMediaPresentation.IconFor(MediaType, outlined: true)", picker, StringComparison.Ordinal);
        Assert.True(
            picker.IndexOf("server-folder-picker__pathbar", StringComparison.Ordinal) < picker.IndexOf("server-folder-picker__body", StringComparison.Ordinal),
            "The path row must come before the location list and browser.");
        Assert.Contains("@ondblclick", picker, StringComparison.Ordinal);
        Assert.Contains("navigator.clipboard.writeText", picker, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OneSharedMappingGivesSixDistinctIconsForTheSixMediaTypesAndAGenericFolderOtherwise(bool outlined)
    {
        var types = new[] { "Movies", "TV", "Music", "Books", "Comics", "Audiobooks" };
        var icons = types.Select(type => AppMediaPresentation.IconFor(type, outlined)).ToList();
        var generic = AppMediaPresentation.IconFor(null, outlined);

        Assert.Equal(6, icons.Distinct().Count());
        Assert.DoesNotContain(generic, icons);
        Assert.Equal(generic, AppMediaPresentation.IconFor("Photos", outlined));
        Assert.Equal(AppMediaPresentation.IconFor("TV", outlined), AppMediaPresentation.IconFor("TVShows", outlined));
    }

    [Fact]
    public void AddFolderWizard_PassesTheChosenMediaTypeAndOnlyThatCallerDoes()
    {
        // The Add folder wizard opens the picker for a library folder; the panel only opens it for the Import folder.
        var wizard = Read("src/MediaEngine.Web/Components/Settings/Libraries/AddFolderWizard.razor");
        var panel = Read("src/MediaEngine.Web/Components/Settings/LibraryFoldersPanel.razor");

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(wizard, @"MediaType=""@_type\.MediaTypes\.FirstOrDefault\(\)"""));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(wizard, @"<ServerFolderPicker\b"));
        Assert.Contains("Inline=\"true\"", wizard, StringComparison.Ordinal);
        Assert.DoesNotContain("component.MediaType", panel, StringComparison.Ordinal);
        foreach (var untyped in new[]
                 {
                     "src/MediaEngine.Web/Components/Settings/Libraries/ViewFoldersTab.razor",
                     "src/MediaEngine.Web/Components/Settings/ToolsRequirementsTab.razor",
                 })
        {
            Assert.DoesNotContain(".MediaType", Read(untyped), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Picker_IsReusedAcrossLibraryAndPersonalSpaceFlows()
    {
        var panel = Read("src/MediaEngine.Web/Components/Settings/LibraryFoldersPanel.razor");
        var wizard = Read("src/MediaEngine.Web/Components/Settings/Libraries/AddFolderWizard.razor");
        var view = Read("src/MediaEngine.Web/Components/Settings/Libraries/ViewFoldersTab.razor");

        Assert.Contains("AppDialogParameters<ServerFolderPicker>", panel, StringComparison.Ordinal);
        Assert.Contains("ServerFolderSelectionModes.ManagedLibrary", panel, StringComparison.Ordinal);
        Assert.Contains("<ServerFolderPicker", wizard, StringComparison.Ordinal);
        Assert.Contains("ServerFolderSelectionModes.ExistingLibrary", wizard, StringComparison.Ordinal);
        Assert.Contains("AppDialogParameters<ServerFolderPicker>", view, StringComparison.Ordinal);
        Assert.Contains("ServerFolderSelectionModes.PersonalSpaceManaged", Read("src/MediaEngine.Web/Components/Settings/Libraries/ViewFoldersTab.razor"), StringComparison.Ordinal);
        Assert.Contains("ServerFolderSelectionModes.PersonalSpaceExisting", view, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryFoldersPanel_ListsLibrariesOutsideTheSixTypes()
    {
        var panel = Read("src/MediaEngine.Web/Components/Settings/LibraryFoldersPanel.razor");

        Assert.Contains("Other libraries", panel, StringComparison.Ordinal);
        Assert.Contains("OtherLibraries", panel, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryFoldersPanel_OffersImportFolderAndHasNoAcceptNewMediaSwitch()
    {
        var panel = Read("src/MediaEngine.Web/Components/Settings/LibraryFoldersPanel.razor");

        Assert.Contains("Import folder", panel, StringComparison.Ordinal);
        foreach (var banned in new[] { "drop-off", "Waiting for destinations", "Needs destination", "\"Ready\"" })
        {
            Assert.DoesNotContain(banned, panel, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("AcceptsNewMedia", panel, StringComparison.Ordinal);
        // The New media marker moves only on the person's say (the explicit radio, or Yes to "Should new media go here?"), never as a side effect.
        // One method, LibraryFolderAddService.WithDestination, assigns it; neither the panel nor the wizard does.
        var addService = Read("src/MediaEngine.Web/Components/Settings/Libraries/LibraryFolderAddService.cs");
        var wizard = Read("src/MediaEngine.Web/Components/Settings/Libraries/AddFolderWizard.razor");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(addService, "PrimaryDestinationSourceId = "));
        Assert.DoesNotContain("PrimaryDestinationSourceId = ", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("PrimaryDestinationSourceId = ", wizard, StringComparison.Ordinal);
        Assert.Contains("ImportFolderStatus", panel, StringComparison.Ordinal);
    }

    [Fact]
    public void LibrariesHaveOneFolderTableAndNoPerLibraryDetailPage()
    {
        var settings = Read("src/MediaEngine.Web/Components/Pages/Settings.razor");
        var libraries = Read("src/MediaEngine.Web/Components/Settings/LibrariesTab.razor");
        var panel = Read("src/MediaEngine.Web/Components/Settings/LibraryFoldersPanel.razor");

        Assert.DoesNotContain("AddLibraryWizard", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("LibraryNameChanged", settings, StringComparison.Ordinal);
        // A library's own page is the shell with its tabs (Library pages spec), not the retired per-library detail surface.
        Assert.Contains("<LibraryShell", libraries, StringComparison.Ordinal);
        Assert.DoesNotContain("LibraryDetailSurface", libraries, StringComparison.Ordinal);
        Assert.DoesNotContain("Library settings", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("/settings/libraries/{", panel, StringComparison.Ordinal);
        Assert.Contains("<LibraryFolderTable", panel, StringComparison.Ordinal);
        Assert.Contains("<LibraryAdvancedSettings", panel, StringComparison.Ordinal);
    }

    [Fact]
    public void SetupWizard_SidebarListsEnhanceStep_AndRecommendedAppearsOnce()
    {
        var stepper = Read("src/MediaEngine.Web/Components/Setup/SetupStepper.razor");
        var providers = Read("src/MediaEngine.Web/Components/Setup/SetupProvidersStage.razor");
        var picker = Read("src/MediaEngine.Web/Components/Shared/ServerFolderPicker.razor");
        var preflight = Read("src/MediaEngine.Web/Components/Setup/SetupPreflightStage.razor");

        Assert.Contains("\"providers\" => \"Enhance your library\"", stepper, StringComparison.Ordinal);
        Assert.Contains("\"language\" => \"Language\"", stepper, StringComparison.Ordinal);
        Assert.DoesNotContain("ProviderStatus(", providers, StringComparison.Ordinal);
        Assert.Contains("Media locations", picker, StringComparison.Ordinal);
        Assert.DoesNotContain("Approved folders", picker, StringComparison.Ordinal);
        Assert.DoesNotContain("Revisiting", preflight, StringComparison.Ordinal);
    }

    [Fact]
    public void Picker_RowsAreRoomyAndNeverDropBelowTheTapTarget()
    {
        var css = Read("src/MediaEngine.Web/Components/Shared/ServerFolderPicker.razor.css");

        // D43: 48px location rows (44px only for a mouse and keyboard desktop) and 56px folder rows (60px under 840px).
        Assert.Contains("@media (hover: hover) and (pointer: fine) and (min-width: 1180px)", css, StringComparison.Ordinal);
        Assert.Contains(".server-folder-picker__location { min-height: 44px; }", css, StringComparison.Ordinal);
        Assert.Contains("min-height: 48px;", css, StringComparison.Ordinal);
        Assert.Contains("min-height: 56px;", css, StringComparison.Ordinal);
        Assert.Contains(".server-folder-picker__folder { grid-template-columns: minmax(0, 1fr); min-height: 60px; }", css, StringComparison.Ordinal);
        Assert.Contains("gap: 2px;", css, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot, relativePath));
}
