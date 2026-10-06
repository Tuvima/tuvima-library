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
        Assert.Contains("Enter path manually", picker, StringComparison.Ordinal);
        Assert.Contains("Select this folder", picker, StringComparison.Ordinal);
        Assert.DoesNotContain("InputFile", picker, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(275)", picker, StringComparison.Ordinal);
        Assert.Contains("preserveSelection: true", picker, StringComparison.Ordinal);
        Assert.Contains("_browseRequest?.Cancel()", picker, StringComparison.Ordinal);
    }

    [Fact]
    public void Picker_IsReusedAcrossLibraryAndPersonalSpaceFlows()
    {
        var libraries = Read("src/MediaEngine.Web/Components/Settings/LibrariesTab.razor");
        var wizard = Read("src/MediaEngine.Web/Components/Settings/AddLibraryWizard.razor");
        var view = Read("src/MediaEngine.Web/Components/Settings/ViewLibrarySettings.razor");

        Assert.Contains("AppDialogParameters<ServerFolderPicker>", libraries, StringComparison.Ordinal);
        Assert.Contains("AppDialogParameters<ServerFolderPicker>", wizard, StringComparison.Ordinal);
        Assert.Contains("ServerFolderSelectionModes.ManagedLibrary", wizard, StringComparison.Ordinal);
        Assert.Contains("ServerFolderSelectionModes.ExistingLibrary", wizard, StringComparison.Ordinal);
        Assert.Contains("AppDialogParameters<ServerFolderPicker>", view, StringComparison.Ordinal);
        Assert.Contains("ServerFolderSelectionModes.PersonalSpaceManaged", Read("src/MediaEngine.Web/Components/Settings/ViewLibrarySettings.razor"), StringComparison.Ordinal);
        Assert.Contains("ServerFolderSelectionModes.PersonalSpaceExisting", view, StringComparison.Ordinal);
    }

    [Fact]
    public void StructuredWizardAndLibraryDetailsExposeCanonicalFlow()
    {
        var settings = Read("src/MediaEngine.Web/Components/Pages/Settings.razor");
        var wizard = Read("src/MediaEngine.Web/Components/Settings/AddLibraryWizard.razor");
        var libraries = Read("src/MediaEngine.Web/Components/Settings/LibrariesTab.razor");

        Assert.Contains("IsLibraryRouteSegment", settings, StringComparison.Ordinal);
        Assert.Contains("<AddLibraryWizard />", settings, StringComparison.Ordinal);

        Assert.Contains("Add folders", wizard, StringComparison.Ordinal);
        Assert.Contains("Organization", wizard, StringComparison.Ordinal);
        Assert.Contains("Review", wizard, StringComparison.Ordinal);
        Assert.Contains("PrimaryDestinationSourceId", wizard, StringComparison.Ordinal);
        Assert.Contains("Choose type", wizard, StringComparison.Ordinal);
        Assert.Contains("Personal Media", wizard, StringComparison.Ordinal);
        Assert.Contains("Save View storage", wizard, StringComparison.Ordinal);
        Assert.Contains("LibraryNameChanged", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("library-detail__tabs", libraries, StringComparison.Ordinal);
        Assert.Contains("File Handling", libraries, StringComparison.Ordinal);
        Assert.DoesNotContain("<NavigationLock", libraries, StringComparison.Ordinal);
        Assert.Contains("MutateLibraryAsync", libraries, StringComparison.Ordinal);
        Assert.Contains("RequiresPrimaryDestination", wizard, StringComparison.Ordinal);
        Assert.Contains("Existing files will remain unchanged", wizard, StringComparison.Ordinal);
        Assert.Contains("Folders", libraries, StringComparison.Ordinal);
        Assert.Contains("Organization", libraries, StringComparison.Ordinal);
        Assert.Contains("Advanced", libraries, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot, relativePath));
}
