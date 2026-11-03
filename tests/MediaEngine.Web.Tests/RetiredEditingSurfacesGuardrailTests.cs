using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Universal editor v3 (spec WP5): Edit opens the pop-over editor at once. The sticky editing bar, per-field pencils, the
/// page edit-mode toggle, the on-page live preview and the floating artwork picker are deleted, and these rules keep them from
/// coming back under their old names, classes or settings.
/// </summary>
public sealed partial class RetiredEditingSurfacesGuardrailTests
{
    private static readonly string SrcRoot = Path.Combine(FindRepoRoot(), "src");

    private static readonly string[] SourceExtensions = [".cs", ".razor", ".css", ".js", ".cshtml", ".resx"];

    private static readonly string[] SkippedFolders = ["bin", "obj", "node_modules", "lib"];

    [Fact]
    public void NothingInSrcNamesTheRetiredEditingBarPencilsOrPreview()
    {
        var offenders = SourceFiles()
            .Where(file => RetiredNames().IsMatch(file.Source))
            .Select(file => file.RelativePath)
            .ToList();

        Assert.True(offenders.Count == 0, "These retired editing surfaces came back (use the pop-over editor): " + string.Join(", ", offenders));
    }

    [Fact]
    public void NoRetiredEditingComponentFileExistsInSrc()
    {
        var names = new[] { "EditingBar", "EditableFieldPencil", "EditingHeroArtworkButtons", "EditingLibraryFields", "EditingTemporaryArtworkView", "FloatingArtworkPicker", "ReviewChangesDialog", "EditPendingPreview", "DetailsEditResume" };
        var files = Directory.EnumerateFiles(SrcRoot, "*", SearchOption.AllDirectories)
            .Where(path => !IsSkipped(path))
            .Where(path => names.Any(name => Path.GetFileName(path).StartsWith(name, StringComparison.Ordinal)))
            .Select(path => Path.GetRelativePath(SrcRoot, path))
            .ToList();

        Assert.True(files.Count == 0, "These retired editing files came back: " + string.Join(", ", files));
    }

    private static IEnumerable<(string RelativePath, string Source)> SourceFiles() =>
        Directory.EnumerateFiles(SrcRoot, "*", SearchOption.AllDirectories)
            .Where(path => SourceExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(path => !IsSkipped(path))
            .Select(path => (Path.GetRelativePath(SrcRoot, path).Replace('\\', '/'), File.ReadAllText(path)));

    private static bool IsSkipped(string path) =>
        Path.GetRelativePath(SrcRoot, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => SkippedFolders.Contains(part, StringComparer.OrdinalIgnoreCase));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    [GeneratedRegex(@"\bEditingBar\b|EditableFieldPencil|FloatingArtworkPicker|EditPendingPreview|ShowsPencils|UseStagedEditor|DetailsEditResume|OfferEditResume|EditingTemporaryArtworkView|tl-editing-bar|tl-editing-slot|tl-temporary-view|tl-editing-banner")]
    private static partial Regex RetiredNames();
}
