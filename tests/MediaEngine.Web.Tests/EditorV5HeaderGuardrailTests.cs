using System.Text.RegularExpressions;
using MediaEngine.Web.Services.Editing;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Universal editor v5 (spec WP5, acceptance A11): the header carries the way around the family, so the old family rail and the Contents and
/// Files tabs are deleted for good, and the Dashboard holds none of the numbers the spec moved into configuration (D34): how many supporting
/// files show inline, how many rows a page holds, how strong the header gradient is. Those arrive in the Engine's header (config/editor.json,
/// config/ui/palette.json).
/// </summary>
public sealed partial class EditorV5HeaderGuardrailTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static readonly string[] SkippedFolders = ["bin", "obj", "node_modules", "lib"];

    private static readonly string[] HeaderSources =
    [
        "src/MediaEngine.Web/Components/Details/EditorHeaderNav.razor",
        "src/MediaEngine.Web/Components/Details/EditorItemHeader.razor",
        "src/MediaEngine.Web/Components/Details/EditorWhereLine.razor",
        "src/MediaEngine.Web/Services/Editing/EditorHeaderView.cs",
        "src/MediaEngine.Web/Services/Editing/EditorFooterRules.cs",
        "src/MediaEngine.Web/Services/Editing/EditorHeaderHooks.cs",
    ];

    [Fact]
    public void TheFamilyRailAndTheContentsAndFilesTabs_StayDeleted_AndNothingNamesThem()
    {
        var offenders = Directory.EnumerateFiles(Path.Combine(RepoRoot, "src"), "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(RepoRoot, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => SkippedFolders.Contains(part)))
            .Where(path => Path.GetExtension(path) is ".cs" or ".razor" or ".css" or ".js")
            .Where(path => RetiredPieces().IsMatch(File.ReadAllText(path)) || RetiredPieces().IsMatch(Path.GetFileName(path)))
            .Select(path => Path.GetRelativePath(RepoRoot, path))
            .ToList();

        Assert.True(offenders.Count == 0, "These files bring back a piece universal editor v5 deleted (the header's nav row took its job): " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheEditorOffersOnlyDetailsArtworkMatchAndHistory_NeverAContentsOrFilesTool()
    {
        Assert.Equal(["Details", "Artwork", "Match", "History"], Enum.GetNames<EditTool>());
        Assert.DoesNotContain("contents", EditorTabRules.Standard);
        Assert.DoesNotContain("files", EditorTabRules.Standard);
    }

    [Fact]
    public void NoTabLabelInTheEditorSaysContentsOrFiles()
    {
        foreach (var relative in new[]
        {
            "src/MediaEngine.Web/Services/Editing/EditorHeaderView.cs",
            "src/MediaEngine.Web/Components/Details/EditorPanelHost.razor",
            "src/MediaEngine.Web/Components/Details/EditorWindowChrome.razor",
        })
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            Assert.DoesNotMatch("\"(Contents|Files)\"", source);
        }
    }

    [Fact]
    public void TheHeadersSource_HoldsNoNumberFromTheConfigTable()
    {
        // D34: the inline supporting-file count, the page sizes, the days a missing file is kept and the gradient's strength come from the
        // Engine's header and the palette config. A number written beside one of those words, or into Take/Skip, is a number in code.
        foreach (var relative in HeaderSources)
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            Assert.DoesNotMatch(@"(?i)(inline|threshold|page_?size|per_?page|cards?_?per|missing_?days|primary_?percent|secondary_?percent)\w*\s*[=:]\s*\d", source);
            Assert.DoesNotMatch(@"\b(Take|Skip)\(\s*(8|12|25|30|50)\s*\)", source);
        }
    }

    [Fact]
    public void TheHeaderReadsItsThresholdsFromTheEngineConfig_AndTheGradientFromThePaletteConfig()
    {
        var nav = File.ReadAllText(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Components", "Details", "EditorHeaderNav.razor"));
        var header = File.ReadAllText(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Components", "Details", "EditorItemHeader.razor"));

        Assert.Contains("GetEditorNavLevelAsync", nav, StringComparison.Ordinal);
        Assert.Contains("PaletteProvider.Current.EditorHeader", header, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEditorsNarrowLayouts_AnswerToTheScreen_NotToAContainerThatIsAlwaysWideEnough()
    {
        // The editor window is at most 1280px wide, so a container query that switches below 1279px is true at every size and the layout never
        // widens. The editor's own style sheets use the 1180px screen breakpoint, which is where the window becomes full screen.
        var offenders = Directory.EnumerateFiles(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Components", "Details"), "Editor*.css", SearchOption.AllDirectories)
            .Where(path => ContainerQuery().IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(RepoRoot, path))
            .ToList();

        Assert.Empty(offenders);
    }

    [GeneratedRegex(@"@container\b")]
    private static partial Regex ContainerQuery();

    [GeneratedRegex(@"\b(EditorContentsTab|EditorFilesTab|EditorFamilyRail)\b")]
    private static partial Regex RetiredPieces();

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The solution folder was not found above the test output.");
    }
}
