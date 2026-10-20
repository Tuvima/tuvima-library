using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Editing is a change in how the page renders (details overlay spec D3): there is no drawer anywhere. These rules keep the
/// retired Editing Mode drawer from coming back under its old type, tab enum, stylesheet classes or shell mode.
/// </summary>
public sealed partial class EditingDrawerRetiredGuardrailTests
{
    private static readonly string SrcRoot = Path.Combine(FindRepoRoot(), "src");

    private static readonly string[] SourceExtensions = [".cs", ".razor", ".css", ".js", ".cshtml"];

    private static readonly string[] SkippedFolders = ["bin", "obj", "node_modules", "lib"];

    [Fact]
    public void NoEditingDrawerTypeExistsInSrc()
    {
        var offenders = SourceFiles()
            .Where(file => EditingDrawerType().IsMatch(file.Source))
            .Select(file => file.RelativePath)
            .ToList();

        Assert.True(offenders.Count == 0, "The Editing Mode drawer is retired (spec D3); use EditingBanner and EditingSectionSlot: " + string.Join(", ", offenders));
    }

    [Fact]
    public void NoEditingDrawerComponentFileExistsInSrc()
    {
        var files = Directory.EnumerateFiles(SrcRoot, "*", SearchOption.AllDirectories)
            .Where(path => !IsSkipped(path))
            .Where(path => Path.GetFileName(path).StartsWith("EditingDrawer", StringComparison.Ordinal)
                           || Path.GetFileName(path).StartsWith("EditingModeBar", StringComparison.Ordinal)
                           || Path.GetFileName(path).StartsWith("EditingModeToggle", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(SrcRoot, path))
            .ToList();

        Assert.True(files.Count == 0, "These retired Editing Mode components came back: " + string.Join(", ", files));
    }

    [Fact]
    public void NoEditingDrawerSelectorOrShellDrawerModeExistsInSrc()
    {
        var offenders = SourceFiles()
            .Where(file => DrawerSelector().IsMatch(file.Source))
            .Select(file => file.RelativePath)
            .ToList();

        Assert.True(offenders.Count == 0, "No .tl-editing-drawer class, drawer-width variable or shell drawer mode may exist: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheEditorShellHasASectionModeAndNoDrawerParameter()
    {
        var shell = File.ReadAllText(Path.Combine(SrcRoot, "MediaEngine.Web", "Components", "MediaEditor", "SharedMediaEditorShell.Section.cs"));

        Assert.Contains("[Parameter] public string? Section { get; set; }", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("public bool Drawer", shell, StringComparison.Ordinal);
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

    [GeneratedRegex(@"\bEditingDrawer\w*")]
    private static partial Regex EditingDrawerType();

    [GeneratedRegex(@"tl-editing-drawer|editing-drawer-width|sme-shell--drawer|sme-section-nav--drawer|universe-page--editing-drawer")]
    private static partial Regex DrawerSelector();
}
