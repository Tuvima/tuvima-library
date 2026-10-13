using MediaEngine.Web.Services.Ui;
namespace MediaEngine.Web.Tests;

public sealed class ViewProfileSettingsUiTests
{
    [Fact]
    public void UsersSettings_UsesSharedControlsAndTruthfulViewManagement()
    {
        var users = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/ManagedAccessUsers.razor");
        var view = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/ViewLibrarySettings.razor");

        Assert.Contains("GetViewProfileSourcesAsync", view, StringComparison.Ordinal);
        Assert.Contains("AppDialogParameters<ServerFolderPicker>", view, StringComparison.Ordinal);
        Assert.Contains("ServerFolderSelectionModes.PersonalSpaceExisting", view, StringComparison.Ordinal);
        Assert.Contains("Show in Photos timeline", view, StringComparison.Ordinal);
        Assert.Contains("Shared Library access, contribution submission and review", view, StringComparison.Ordinal);
        Assert.Contains("ManagedAccessUserPermissions", users, StringComparison.Ordinal);
        Assert.DoesNotContain("MudSwitch", users, StringComparison.Ordinal);
        Assert.DoesNotContain("quota", users, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DocumentsSettings_IsAPlainListWithTruthfulTextRecognitionControls()
    {
        var view = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/ViewLibrarySettings.razor");
        var documents = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/DocumentTextSettings.razor");

        Assert.Contains("<DocumentTextSettings", view, StringComparison.Ordinal);
        Assert.Contains("Text recognition is not installed", documents, StringComparison.Ordinal);
        Assert.Contains("Read all documents again", documents, StringComparison.Ordinal);
        Assert.Contains("the only time text recognition uses the internet", documents, StringComparison.Ordinal);
        Assert.Contains("<AppSwitchRow", documents, StringComparison.Ordinal);
        Assert.Contains("<AppCheckboxRow", documents, StringComparison.Ordinal);
        Assert.DoesNotContain("<input", documents, StringComparison.Ordinal);
        Assert.DoesNotContain("style=", documents, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(string relativePath) =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            relativePath)));
}
