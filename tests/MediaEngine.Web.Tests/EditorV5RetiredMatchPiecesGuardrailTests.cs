using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Universal editor v5 (spec WP9): the Match tab has no placement controls of its own. "Change placement", the placement picker,
/// the line-up table, the connected-show panel and the placement rules are deleted, not hidden; a person finds the record that fits
/// the file and chooses it, and applying it re-parents the item (and moves a Managed file) through the staged Relationship and file-move
/// changes every move uses, shown in Review. These rules keep the old pieces from coming back under their old names, in code, styles or strings.
/// </summary>
public sealed partial class EditorV5RetiredMatchPiecesGuardrailTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static readonly string[] SourceExtensions = [".cs", ".razor", ".css", ".js", ".cshtml", ".resx"];

    private static readonly string[] SkippedFolders = ["bin", "obj", "node_modules", "lib"];

    [Fact]
    public void NoRetiredMatchPlacementFileExistsInSrc()
    {
        var names = new[] { "EditorPlacementPicker", "EditorLineUp", "EditorPlacementRules", "EditorSearchNoise" };
        var files = SourceRoot()
            .Where(path => names.Any(name => Path.GetFileName(path).StartsWith(name, StringComparison.Ordinal)))
            .Select(path => Path.GetRelativePath(RepoRoot, path))
            .ToList();

        Assert.True(files.Count == 0, "These retired Match placement files came back (the v5 Match tab replaced them): " + string.Join(", ", files));
    }

    [Fact]
    public void NothingInSrcNamesARetiredMatchPlacementPiece()
    {
        var offenders = SourceRoot()
            .Where(path => SourceExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(path => RetiredNames().IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(RepoRoot, path))
            .ToList();

        Assert.True(offenders.Count == 0,
            "These files name a retired Match placement piece (the Match tab finds a record and applies it; the move is staged for it, not chosen on the tab): " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheMatchTabStillHasItsSearchAndItsResults()
    {
        var details = Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Components", "Editor");
        foreach (var name in new[] { "EditorMatchPanel", "EditorMatchResults" })
        {
            Assert.True(File.Exists(Path.Combine(details, name + ".razor")), name + " is the v5 Match tab and must stay.");
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

    [GeneratedRegex(@"EditorPlacementPicker|EditorLineUp|EditorPlacementRules|EditorSearchNoise|Change placement|tl-lineup")]
    private static partial Regex RetiredNames();
}
