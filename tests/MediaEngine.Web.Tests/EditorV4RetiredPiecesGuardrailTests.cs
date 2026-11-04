using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Universal editor v4 (spec WP12): the v3 pieces the new editor replaced are deleted, not hidden. The left family tree gave way
/// to the rail of dropdowns, per-field inline editing to the Details form with lock and revert, and the old modal editor's
/// launch request, schema catalog and navigation guard to the pop-over editor. These rules keep them, and the docked artwork
/// picker, from coming back under their old names, and keep the developer maps from describing them as current.
/// </summary>
public sealed partial class EditorV4RetiredPiecesGuardrailTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static readonly string[] SourceExtensions = [".cs", ".razor", ".css", ".js", ".cshtml"];

    private static readonly string[] SkippedFolders = ["bin", "obj", "node_modules", "lib"];

    [Fact]
    public void NoRetiredEditorFileExistsInSrc()
    {
        var names = new[] { "EditorFamilyTree", "EditorDetailsInlineField", "MediaEditorNavigationGuard", "EditorExtrasMovePanel", "FloatingArtworkPicker" };
        var files = SourceRoot()
            .Where(path => names.Any(name => Path.GetFileName(path).StartsWith(name, StringComparison.Ordinal)))
            .Select(path => Path.GetRelativePath(RepoRoot, path))
            .ToList();

        Assert.True(files.Count == 0, "These retired editor files came back (the v4 editor replaced them): " + string.Join(", ", files));
    }

    [Fact]
    public void NothingInSrcNamesARetiredEditorPiece()
    {
        var offenders = SourceRoot()
            .Where(path => SourceExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(path => RetiredNames().IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(RepoRoot, path))
            .ToList();

        Assert.True(offenders.Count == 0, "These files name a retired editor piece (use the rail, the Details form and the pop-over editor): " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheDeveloperMapsDoNotPointAtRetiredEditorPieces()
    {
        foreach (var relative in new[] { "AGENTS.md", Path.Combine("src", "MediaEngine.Web", "CLAUDE.md") })
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot, relative));
            Assert.False(RetiredNames().IsMatch(text), relative + " describes a retired editor piece as current.");
        }
    }

    [Fact]
    public void TheV4EditorPiecesThatReplacedThemStillExist()
    {
        var details = Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Components", "Details");
        foreach (var name in new[] { "EditorFamilyRail", "EditorUniverseRail", "EditorItemHeader", "EditorQuickFacts", "EditorArtworkTool", "EditorMatchPanel", "EditorContentsTab", "EditorFilesTab" })
        {
            Assert.True(File.Exists(Path.Combine(details, name + ".razor")), name + " is part of the v4 editor and must stay.");
        }
    }

    private static IEnumerable<string> SourceRoot() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot, "src"), "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(RepoRoot, path)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => SkippedFolders.Contains(part, StringComparer.OrdinalIgnoreCase)));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    [GeneratedRegex(@"EditorFamilyTree|EditorDetailsInlineField|MediaEditorNavigationGuard|MediaEditorSchemaCatalog|MediaEditorLaunchRequest|MediaEditorPreviewItem|SharedMediaEditorMode|FloatingArtworkPicker|tl-editor-tree|tl-family-tree-pane")]
    private static partial Regex RetiredNames();
}
