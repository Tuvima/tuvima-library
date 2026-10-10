using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

public sealed class UiCompositionGuardrailTests
{
    private static readonly string RepoRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static readonly string[] MigratedFiles =
    [
        "src/MediaEngine.Web/Shared/MainLayout.razor",
        "src/MediaEngine.Web/Components/Pages/SearchPage.razor",
        "src/MediaEngine.Web/Components/Pages/LibraryBrowsePage.razor",
        "src/MediaEngine.Web/Components/Pages/ChronicleExplorer.razor",
        "src/MediaEngine.Web/Components/Pages/Settings.razor",
        "src/MediaEngine.Web/Components/Settings/OverviewTab.razor",
        "src/MediaEngine.Web/Components/Settings/SettingsReviewQueueTab.razor",
        "src/MediaEngine.Web/Components/Browse/MediaBrowseShell.razor",
        "src/MediaEngine.Web/Components/Pages/Collections.razor",
        "src/MediaEngine.Web/Components/Collections/CollectionEditorShell.razor",
        "src/MediaEngine.Web/Components/Library/LibraryConfigurableTable.razor",
        "src/MediaEngine.Web/Components/Library/StatusPill.razor",
        "src/MediaEngine.Web/Components/Details/DetailPage.razor",
        "src/MediaEngine.Web/Components/MediaEditor/SharedMediaBatchConfirmDialog.razor",
        "src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor",
    ];

    private static readonly string RemovedRouteSegment = "/" + "va" + "ult";
    private static readonly string RemovedProductLabel = "Va" + "ult";
    private static readonly string RemovedWorkspaceLabel = "Library " + RemovedProductLabel;
    private static readonly string RemovedCssPrefix = "va" + "ult-";
    private static readonly string RemovedPageComponent = "Library" + "Page";
    private static readonly string RemovedSurfaceType = "LibrarySurface" + "Preset";

    private static readonly string[] RemovedAllInOneWorkspaceFiles =
    [
        $"src/MediaEngine.Web/Components/Library/{RemovedPageComponent}.razor",
        $"src/MediaEngine.Web/Models/ViewDTOs/{RemovedSurfaceType}.cs",
        "src/MediaEngine.Web/Components/Library/LibraryActionCenter.razor",
        "src/MediaEngine.Web/Components/Library/LibraryActionCenterCategories.razor",
        "src/MediaEngine.Web/Components/Library/LibraryAddMediaDrawer.razor",
        "src/MediaEngine.Web/Components/Library/LibraryCollectionDrawer.razor",
        "src/MediaEngine.Web/Components/Library/LibraryOverviewPanel.razor",
        "src/MediaEngine.Web/Components/Library/LibraryPeopleTable.razor",
        "src/MediaEngine.Web/Components/Library/LibraryToolbar.razor",
        "src/MediaEngine.Web/Components/Library/LibraryUniversesTable.razor",
        "src/MediaEngine.Web/Components/Library/LibraryUniverseAssigned.razor",
        "src/MediaEngine.Web/Components/Library/LibraryUniversePendingQueue.razor",
        "src/MediaEngine.Web/Components/Library/LibraryUniverseUnlinked.razor",
        "src/MediaEngine.Web/Components/Library/LibraryEditorPanel.razor",
        "src/MediaEngine.Web/Components/Library/LibraryEditorFields.razor",
    ];

    private static readonly Regex RetiredCustomUiRegex =
        new(@"<(?:AppSurfaceCard|TopBar|ProfileDropdown|LibraryToolbar)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RawInteractiveHtmlRegex =
        new(@"<(?:button|input|select|textarea|table|dialog|details|summary)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RazorCommentRegex =
        new(@"@\*.*?\*@", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex LineCommentRegex =
        new(@"(?m)^\s*///?.*$", RegexOptions.Compiled);

    public static IEnumerable<object[]> MigratedFilesData() =>
        MigratedFiles.Select(path => new object[] { path });

    public static IEnumerable<object[]> RemovedAllInOneWorkspaceFilesData() =>
        RemovedAllInOneWorkspaceFiles.Select(path => new object[] { path });

    [Theory]
    [MemberData(nameof(MigratedFilesData))]
    public void MigratedFiles_DoNotUseRetiredCustomUiOrRawInteractiveHtml(string relativePath)
    {
        var filePath = Path.Combine(RepoRoot, relativePath);
        Assert.True(File.Exists(filePath), $"Guardrail target is missing: {relativePath}.");
        var contents = Sanitize(File.ReadAllText(filePath));

        Assert.False(
            RetiredCustomUiRegex.IsMatch(contents),
            $"Retired custom UI wrapper found in {relativePath}.");

        Assert.False(
            RawInteractiveHtmlRegex.IsMatch(contents),
            $"Raw interactive HTML found in {relativePath}.");
    }

    [Theory]
    [MemberData(nameof(RemovedAllInOneWorkspaceFilesData))]
    public void RemovedAllInOneWorkspaceFiles_DoNotExist(string relativePath)
    {
        Assert.False(
            File.Exists(Path.Combine(RepoRoot, relativePath)),
            $"{relativePath} belongs to the removed all-in-one workspace and should not be restored.");
    }

    [Fact]
    public void ActiveDashboardFiles_DoNotReintroduceRemovedWorkspace()
    {
        var roots = new[]
        {
            Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Components"),
            Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Shared"),
            Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Models", "ViewDTOs"),
        };

        var offenders = roots
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            .Where(path => path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
            .Where(path => !RemovedAllInOneWorkspaceFiles.Contains(ToRelativePath(path), StringComparer.OrdinalIgnoreCase))
            .Where(path => ContainsRemovedWorkspaceLanguage(Sanitize(File.ReadAllText(path))))
            .Select(ToRelativePath)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void RepositoryText_DoesNotUseRemovedWorkspaceVocabulary()
    {
        var roots = new List<string>
        {
            Path.Combine(RepoRoot, "AGENTS.md"),
            Path.Combine(RepoRoot, "CLAUDE.md"),
            Path.Combine(RepoRoot, "README.md"),
            Path.Combine(RepoRoot, "src"),
            Path.Combine(RepoRoot, "tests"),
            Path.Combine(RepoRoot, "docs"),
            Path.Combine(RepoRoot, ".agent"),
        };

        var offenders = roots
            .Where(root => File.Exists(root) || Directory.Exists(root))
            .SelectMany(EnumerateTextFiles)
            .Where(path => !IsGeneratedOrBuildOutput(path))
            .Where(path => ContainsRemovedWorkspaceLanguage(File.ReadAllText(path)))
            .Select(ToRelativePath)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void ActiveNavigationResources_ExposeOnlyCurrentDashboardLabels()
    {
        var resourceRoot = Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Resources");
        var offenders = Directory.EnumerateFiles(resourceRoot, "*.resx", SearchOption.AllDirectories)
            .Where(path => ContainsRemovedWorkspaceLanguage(File.ReadAllText(path)))
            .Select(ToRelativePath)
            .ToList();

        Assert.Empty(offenders);
    }

    private static IEnumerable<string> EnumerateTextFiles(string root)
    {
        if (File.Exists(root))
        {
            return [root];
        }

        return Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(path =>
            {
                var extension = Path.GetExtension(path);
                return extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".razor", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".css", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".resx", StringComparison.OrdinalIgnoreCase);
            });
    }

    private static bool ContainsRemovedWorkspaceLanguage(string contents)
    {
        return contents.Contains(RemovedRouteSegment, StringComparison.OrdinalIgnoreCase)
            || contents.Contains(RemovedProductLabel, StringComparison.OrdinalIgnoreCase)
            || contents.Contains(RemovedWorkspaceLabel, StringComparison.OrdinalIgnoreCase)
            || contents.Contains(RemovedCssPrefix, StringComparison.OrdinalIgnoreCase)
            || contents.Contains(RemovedPageComponent, StringComparison.OrdinalIgnoreCase)
            || contents.Contains(RemovedSurfaceType, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGeneratedOrBuildOutput(string path)
    {
        return path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SharedMediaEditorLauncher_RemainsCanonicalEditingPath()
    {
        var detailPage = File.ReadAllText(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Components", "Details", "DetailPage.razor"));
        var reviewTab = File.ReadAllText(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Components", "Settings", "SettingsReviewQueueTab.razor"));
        var launcher = File.ReadAllText(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Services", "Editing", "MediaEditorLauncherService.cs"));

        Assert.Contains("MediaEditorLauncher.OpenAsync", detailPage);
        Assert.DoesNotContain("MediaEditorLauncher.BeginInline", detailPage);
        Assert.Contains("MediaEditorLauncher.OpenAsync", reviewTab);
        Assert.DoesNotContain("MediaEditorInlineSession", launcher);
        Assert.Contains("SharedMediaEditorMode.Normal", detailPage);
        Assert.Contains("SharedMediaEditorMode.Review", reviewTab);
        Assert.Contains("SharedMediaEditorMode.Batch", launcher);
    }

    [Fact]
    public void InlineEditingSurfaces_RefreshOnlyAfterSuccessfulEditorResult()
    {
        var detailPage = File.ReadAllText(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Components", "Details", "DetailPage.razor"));
        var reviewTab = File.ReadAllText(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Components", "Settings", "SettingsReviewQueueTab.razor"));
        var listenPage = File.ReadAllText(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Components", "Pages", "ListenPage.razor.cs"));

        Assert.Matches(@"if\s*\(\s*applied\s*\)\s*\{[^}]*RefreshCurrentDetailAsync", detailPage);
        Assert.Contains("GetDetailPageAsync", detailPage);
        Assert.Matches(@"if\s*\(\s*applied\s*\)\s*\{[^}]*LoadAsync", reviewTab);
        Assert.Matches(@"if\s*\(\s*applied\s*\)\s*\{[^}]*LoadAsync", listenPage);
    }

    [Fact]
    public void MediaEditorLauncher_RejectsEmptyAndMultiItemLaunches()
    {
        var launcher = File.ReadAllText(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Services", "Editing", "MediaEditorLauncherService.cs"));

        Assert.Contains("request.EntityIds.Count == 0", launcher);
        Assert.Contains("return false", launcher);
        Assert.Contains("request.Mode == SharedMediaEditorMode.Batch || request.EntityIds.Count > 1", launcher);
        Assert.DoesNotContain("SharedMediaBatchConfirmDialog", launcher);
    }

    private static string Sanitize(string contents)
    {
        var withoutRazorComments = RazorCommentRegex.Replace(contents, string.Empty);
        return LineCommentRegex.Replace(withoutRazorComments, string.Empty);
    }

    private static string ToRelativePath(string path) =>
        Path.GetRelativePath(RepoRoot, path).Replace('\\', '/');
}
