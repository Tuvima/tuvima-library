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
        Assert.Contains("Label=\"Add\"", picker, StringComparison.Ordinal);
        Assert.DoesNotContain("InputFile", picker, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(275)", picker, StringComparison.Ordinal);
        Assert.Contains("preserveSelection: true", picker, StringComparison.Ordinal);
        Assert.Contains("_browseRequest?.Cancel()", picker, StringComparison.Ordinal);
    }

    [Fact]
    public void Picker_IsReusedAcrossLibraryAndPersonalSpaceFlows()
    {
        var wizard = Read("src/MediaEngine.Web/Components/Settings/LibraryFoldersPanel.razor");
        var view = Read("src/MediaEngine.Web/Components/Settings/ViewLibrarySettings.razor");

        Assert.Contains("AppDialogParameters<ServerFolderPicker>", wizard, StringComparison.Ordinal);
        Assert.Contains("ServerFolderSelectionModes.ManagedLibrary", wizard, StringComparison.Ordinal);
        Assert.Contains("ServerFolderSelectionModes.ExistingLibrary", wizard, StringComparison.Ordinal);
        Assert.Contains("AppDialogParameters<ServerFolderPicker>", view, StringComparison.Ordinal);
        Assert.Contains("ServerFolderSelectionModes.PersonalSpaceManaged", Read("src/MediaEngine.Web/Components/Settings/ViewLibrarySettings.razor"), StringComparison.Ordinal);
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
        Assert.Contains("Waiting for destinations", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("AcceptsNewMedia", panel, StringComparison.Ordinal);
        // The New media marker moves only through the explicit radio (SetDestinationAsync), never as a side effect.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(panel, "PrimaryDestinationSourceId = "));
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
        Assert.Contains("Guid.TryParse(Subsection, out _)", settings, StringComparison.Ordinal);
        Assert.Contains("Nav.NavigateTo(\"/settings/libraries\", replace: true)", settings, StringComparison.Ordinal);
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
        Assert.Contains("Approved folders", picker, StringComparison.Ordinal);
        Assert.DoesNotContain("Revisiting", preflight, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot, relativePath));
}
