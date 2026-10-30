namespace MediaEngine.Web.Tests;

/// <summary>
/// The Chronicle explorer edits the Universe, and one character, place or organisation in it, on the page with the universal editor
/// (the editing bar, side panel, floating artwork picker and Review changes). The old pop-up editor and its launcher are gone.
/// </summary>
public sealed class ChronicleExplorerEditingGuardrailTests
{
    [Fact]
    public void TheExplorer_UsesTheUniversalEditorInPlace_AndNoPopUp()
    {
        var explorer = Read("src/MediaEngine.Web/Components/Pages/ChronicleExplorer.razor");

        Assert.Contains("<EditingBar", explorer, StringComparison.Ordinal);
        Assert.Contains("<EditorPanelHost", explorer, StringComparison.Ordinal);
        Assert.Contains("Picker=\"_artworkPicker\"", explorer, StringComparison.Ordinal);
        Assert.Contains("LoadSubjectAsync", explorer, StringComparison.Ordinal);
        Assert.Contains("LoadUniverseSubjectAsync", explorer, StringComparison.Ordinal);
        Assert.Contains("EditorDeviceRule.TryRefuse", explorer, StringComparison.Ordinal);
        Assert.DoesNotContain("MediaEditorLauncher", explorer, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOldPopUpEditor_IsRetired()
    {
        var root = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");

        Assert.False(File.Exists(Path.Combine(root, "Services", "Editing", "MediaEditorLauncherService.cs")));
        Assert.False(File.Exists(Path.Combine(root, "Components", "MediaEditor", "SharedEntityEditorWorkspace.razor")));
        Assert.DoesNotContain("MediaEditorLauncherService", Read("src/MediaEngine.Web/Program.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain("SharedEntityTarget", Read("src/MediaEngine.Web/Services/Editing/MediaEditorModels.cs"), StringComparison.Ordinal);
    }

    private static string Read(string relativePath) => File.ReadAllText(Path.Combine(FindRepoRoot(), relativePath));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
