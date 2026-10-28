using MediaEngine.Web.Services.Ui;
namespace MediaEngine.Web.Tests;

public sealed class ViewProfileSettingsUiTests
{
    [Fact]
    public void UsersSettings_UsesSharedControlsAndTruthfulViewManagement()
    {
        var users = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/ManagedAccessUsers.razor");
        var view = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/Libraries/ViewFoldersTab.razor");
        var access = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/Libraries/ViewAccessTab.razor");

        Assert.Contains("GetViewProfileSourcesAsync", ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/Libraries/ViewKindPage.razor"), StringComparison.Ordinal);
        Assert.Contains("AppDialogParameters<ServerFolderPicker>", view, StringComparison.Ordinal);
        Assert.Contains("ServerFolderSelectionModes.PersonalSpaceExisting", view, StringComparison.Ordinal);
        Assert.Contains("Show in Photos timeline", view, StringComparison.Ordinal);
        Assert.Contains("Shared Library access, contribution submission and review", access, StringComparison.Ordinal);
        // What a person may use (including View) is set on their profile, not on the sign-in.
        Assert.DoesNotContain("ManagedAccessUserPermissions", users, StringComparison.Ordinal);
        Assert.DoesNotContain("MudSwitch", users, StringComparison.Ordinal);
        Assert.DoesNotContain("quota", users, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DocumentsSettings_IsAPlainListWithTruthfulTextRecognitionControls()
    {
        var view = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/Libraries/ViewFileHandlingTab.razor");
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
