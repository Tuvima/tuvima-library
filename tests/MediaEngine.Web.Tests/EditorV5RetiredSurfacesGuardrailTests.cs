using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Universal editor v5 (spec WP12, acceptance A11): the Contents tab, the Files tab, the family rail and "Change placement" are gone from the
/// library-item editor for good. The header's navigation row replaced the rail and the two tabs; the Match tab replaced the placement picker.
/// These rules read the real configuration and the real sources, so a tab, a rail or a placement control cannot come back under any name.
/// </summary>
public sealed partial class EditorV5RetiredSurfacesGuardrailTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    // The tabs the editor may offer, by id (config/editor.json "kinds.*.tabs"). Preview belongs to a supporting file, Bookmarks to the
    // administrators' tab, Pictures to a Universe and its things; a Series has Details and History only.
    private static readonly string[] AllowedTabs = ["details", "artwork", "match", "bookmarks", "history", "preview", "pictures"];

    private static readonly string[] SkippedFolders = ["bin", "obj", "node_modules", "lib"];

    [Fact]
    public void NoKindInTheEditorConfiguration_OffersAContentsOrFilesTab_OrAnyTabOutsideTheV5List()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "config", "editor.json")));
        var kinds = document.RootElement.GetProperty("kinds");
        var offenders = new List<string>();
        foreach (var kind in kinds.EnumerateObject())
        {
            foreach (var tab in kind.Value.GetProperty("tabs").EnumerateArray().Select(element => element.GetString() ?? string.Empty))
            {
                if (!AllowedTabs.Contains(tab, StringComparer.Ordinal))
                {
                    offenders.Add($"{kind.Name}: {tab}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "These editor tabs are not part of editor v5 (no Contents tab, no Files tab): " + string.Join(", ", offenders));
    }

    [Fact]
    public void ASeriesHasOnlyDetailsAndHistory_AndASupportingFileOnlyDetailsPreviewAndHistory()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "config", "editor.json")));
        var kinds = document.RootElement.GetProperty("kinds");

        Assert.Equal(["details", "history"], TabsOf(kinds, "series"));
        Assert.Equal(["details", "preview", "history"], TabsOf(kinds, "supporting_file"));
    }

    [Fact]
    public void OnlyAdministratorsTabsAndAFilesPreview_AreOfferedBeyondDetailsArtworkMatchAndHistory_OnALibraryItem()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "config", "editor.json")));
        var kinds = document.RootElement.GetProperty("kinds");
        var libraryKinds = new[] { "item", "tv_show", "tv_season", "tv_episode", "movie", "album", "song", "book", "audiobook", "comic", "course", "course_section", "video", "person" };

        foreach (var kind in libraryKinds)
        {
            Assert.Empty(TabsOf(kinds, kind).Except(["details", "artwork", "match", "bookmarks", "history"]));
        }
    }

    [Fact]
    public void NoLibraryItemEditorSource_DrawsAFamilyRailAContentsOrFilesTabOrChangePlacement()
    {
        // The View editor's files are WP14's; every other editor source is checked.
        var offenders = EditorSources()
            .Where(path => RetiredText().IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(RepoRoot, path))
            .ToList();

        Assert.True(offenders.Count == 0, "These editor sources bring back a surface editor v5 retired: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheFamilyStateKeepsNoRailOrContentsTabReads()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Services", "Editing", "EditorFamilyState.cs"));

        Assert.DoesNotMatch(@"\b(RailLevel|RailOption|ContentsRow|ContentsPage|ContentsOf|EnsureChildrenAsync)\b", source);
        Assert.DoesNotMatch(@"\bLevels\(", source);
    }

    [Fact]
    public void NoEditorStringSaysContentsFilesOrChangePlacementAsAnEditorLabel()
    {
        var resx = File.ReadAllText(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Resources", "SharedStrings.resx"));
        var offenders = EditorKeyValue().Matches(resx)
            .Select(match => (Key: match.Groups["key"].Value, Value: match.Groups["value"].Value.Trim()))
            .Where(pair => pair.Value is "Contents" or "Files" or "Change placement")
            .Select(pair => pair.Key)
            .ToList();

        Assert.True(offenders.Count == 0, "These editor strings label a retired tab or control: " + string.Join(", ", offenders));
    }

    private static List<string> TabsOf(JsonElement kinds, string kind) =>
        [.. kinds.GetProperty(kind).GetProperty("tabs").EnumerateArray().Select(element => element.GetString() ?? string.Empty)];

    private static IEnumerable<string> EditorSources()
    {
        var web = Path.Combine(RepoRoot, "src", "MediaEngine.Web");
        var folders = new[]
        {
            Path.Combine(web, "Components", "Details"),
            Path.Combine(web, "Services", "Editing"),
        };

        return folders
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            .Where(path => Path.GetExtension(path) is ".cs" or ".razor" or ".css")
            .Where(path => !Path.GetRelativePath(RepoRoot, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => SkippedFolders.Contains(part)))
            // The View editor (photos, videos, documents, galleries) moves onto the v5 frame in WP14 and has its own guardrail.
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                return !name.StartsWith("ViewEdit", StringComparison.Ordinal) && !name.StartsWith("ViewGallery", StringComparison.Ordinal)
                    && !name.StartsWith("ViewEditor", StringComparison.Ordinal);
            })
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}ViewEdit{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    [GeneratedRegex(@"\b(EditorContentsTab|EditorFilesTab|EditorFamilyRail|EditorPlacementPicker|EditorLineUp)\b|Change placement|Levels\(Guid|RailLevel|RailOption", RegexOptions.CultureInvariant)]
    private static partial Regex RetiredText();

    [GeneratedRegex(@"<data name=""(?<key>(?:Editor|Details_Editor)[A-Za-z0-9_]*)""[^>]*>\s*<value>(?<value>[^<]*)</value>", RegexOptions.CultureInvariant)]
    private static partial Regex EditorKeyValue();

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
