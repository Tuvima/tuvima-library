namespace MediaEngine.Web.Tests;

/// <summary>
/// After Save the editor window closes and the item shows the app's normal Updating state until the Engine finishes (editor v7 spec D12,
/// issue 995 option B). The "Saved N changes" ribbon, its named-step list, its glow script and the client call that fed it are gone and
/// must not come back. (The View editor's own ViewSavedRibbon belongs to the Photos and Documents library, not the universal editor.)
/// </summary>
public sealed class EditorSavedBoxRetiredGuardrailTests
{
    private static readonly string WebRoot = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");

    private static readonly string[] RetiredTokens =
    [
        "EditSavedRibbon",
        "EditSaveSummary",
        "ClearLastSave",
        "editor-glow",
        "tl-saved-ribbon__step",
        "tl-saved-ribbon__reason",
        "tl-saved-ribbon__history",
        "data-steps",
        "GetEditorFollowUpAsync",
        "EditorFollowUpView",
    ];

    [Fact]
    public void TheEditorHasNoSavedRibbonComponent_AndNoScriptToGlowTheChangedParts()
    {
        Assert.False(File.Exists(Path.Combine(WebRoot, "Components", "Editor", "EditSavedRibbon.razor")));
        Assert.False(File.Exists(Path.Combine(WebRoot, "wwwroot", "js", "editor-glow.js")));
    }

    [Fact]
    public void NoDashboardFileMentionsTheRetiredSavedBox()
    {
        var offenders = Directory.EnumerateFiles(WebRoot, "*", SearchOption.AllDirectories)
            .Where(path => new[] { ".cs", ".razor", ".css", ".js", ".resx" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(path => !IsSkipped(path))
            .Select(path => (Path: path, Source: File.ReadAllText(path)))
            .SelectMany(file => RetiredTokens
                .Where(token => file.Source.Contains(token, StringComparison.Ordinal))
                .Select(token => $"{Path.GetRelativePath(WebRoot, file.Path).Replace('\\', '/')}: {token}"))
            .ToList();

        Assert.True(offenders.Count == 0, "The editor's saved box is retired (v7 D12); after Save the item shows Updating: " + string.Join("; ", offenders));
    }

    private static bool IsSkipped(string path) =>
        Path.GetRelativePath(WebRoot, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part is "bin" or "obj" or "node_modules" or "lib" or "vendor");

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
