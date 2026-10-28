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
        "src/MediaEngine.Web/Components/Settings/LibrarySummary.razor",
        "src/MediaEngine.Web/Components/Browse/MediaBrowseShell.razor",
        "src/MediaEngine.Web/Components/Pages/Collections.razor",
        "src/MediaEngine.Web/Components/Collections/CollectionEditorShell.razor",
        "src/MediaEngine.Web/Components/Library/LibraryConfigurableTable.razor",
        "src/MediaEngine.Web/Components/Library/StatusPill.razor",
        "src/MediaEngine.Web/Components/Details/DetailPage.razor",
        "src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor",
    ];

    // Only the old workspace name (the word Library before the area name) stays retired; the plain area name is the adult-video area (Videos spec D15).
    private static readonly string RemovedWorkspaceLabel = "Library " + "Va" + "ult";
    private static readonly string RemovedPageComponent = "Library" + "Page";
    private static readonly string RemovedSurfaceType = "LibrarySurface" + "Preset";

    // The old all-in-one screen's name is retired entirely (Registry retirement D2). Only the bare word is refused:
    // technical identifiers such as PermissionRegistry or CapabilityRegistry, and lowercase "registry" (a Docker
    // image registry), stay legal.
    private static readonly string RetiredRegistryLabel = "Reg" + "istry";

    private static readonly Regex RetiredRegistryLabelRegex =
        new($@"(?<!\w){RetiredRegistryLabel}(?!\w)", RegexOptions.Compiled);

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

    // A C# string literal: raw ("""..."""), verbatim (@"..." / $@"..."), or regular ("..." / $"...").
    private static readonly Regex StringLiteralRegex =
        new(@"""""""[\s\S]*?""""""|[$@]{1,2}""(?:[^""]|"""")*""|""(?:[^""\\\r\n]|\\.)*""", RegexOptions.Compiled);

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

    [Fact]
    public void UserFacingText_DoesNotUseTheRetiredRegistryLabel()
    {
        // A missing folder would silently scan nothing, so the scan proves it saw each source it claims to cover.
        Assert.True(Directory.Exists(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Components")), "src/MediaEngine.Web/Components must exist.");
        Assert.True(Directory.Exists(Path.Combine(RepoRoot, "docs")), "docs must exist.");
        var scanned = RegistryScanFiles(RepoRoot);
        foreach (var extension in new[] { ".razor", ".resx", ".md", ".cs" })
        {
            Assert.Contains(scanned, path => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
        }

        Assert.Contains(scanned, path => path.Contains($"{Path.DirectorySeparatorChar}MediaEngine.Api{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));

        var offenders = FindRetiredRegistryLabels(RepoRoot);

        Assert.True(
            offenders.Count == 0,
            "The retired all-in-one screen name must not return in Dashboard markup, Dashboard resources or docs; say Needs Review or Editing Mode instead: "
            + string.Join("; ", offenders));
    }

    [Theory]
    [InlineData("<AppTab Label=\"{0}\" />")]
    [InlineData("<h2>{0}</h2>")]
    [InlineData("<value>{0}</value>")]
    [InlineData("Open the {0} tab to see the item's state.")]
    [InlineData("<a href=\"/details/book/1\">{0}</a>")]
    public void RetiredRegistryLabelMatcher_FlagsAPlantedLabel(string template)
    {
        Assert.Matches(RetiredRegistryLabelRegex, string.Format(template, RetiredRegistryLabel));
    }

    [Theory]
    [InlineData("Permission")]
    [InlineData("Capability")]
    [InlineData("OpenScreen")]
    [InlineData("IntercomAudience")]
    public void RetiredRegistryLabelMatcher_AllowsTechnicalRegistryTypeNames(string prefix)
    {
        Assert.DoesNotMatch(RetiredRegistryLabelRegex, $"@inject {prefix}{RetiredRegistryLabel} {prefix}Source");
        Assert.DoesNotMatch(RetiredRegistryLabelRegex, $"I{prefix}{RetiredRegistryLabel}");
    }

    [Theory]
    [InlineData("Start without pulling the registry image.")]
    [InlineData("var registry = new ApplicationEventRegistry(permissions);")]
    [InlineData("{0}Tab")]
    [InlineData("{0}_CloseWhere")]
    public void RetiredRegistryLabelMatcher_AllowsLowercaseWordsAndCodeOnlyIdentifiers(string template)
    {
        Assert.DoesNotMatch(RetiredRegistryLabelRegex, string.Format(template, RetiredRegistryLabel));
    }

    [Fact]
    public void RegistryLabelScan_FailsOnAPlantedLabelAndPassesOnACleanTree()
    {
        var planted = Path.Combine(Path.GetTempPath(), "tuvima-registry-label-" + Guid.NewGuid().ToString("N"));
        var clean = Path.Combine(Path.GetTempPath(), "tuvima-registry-clean-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteFile(planted, "src/MediaEngine.Web/Components/Pages/Sample.razor", $"<h1>{RetiredRegistryLabel}</h1>");
            WriteFile(planted, "src/MediaEngine.Web/Resources/Sample.resx", $"<data name=\"Nav\"><value>{RetiredRegistryLabel}</value></data>");
            WriteFile(planted, "docs/guides/sample.md", $"Open the {RetiredRegistryLabel} to fix an item.");
            WriteFile(planted, "src/MediaEngine.Web/Services/Sample.cs", $"class A {{ string Label = \"Open {RetiredRegistryLabel}\"; }}");
            WriteFile(planted, "src/MediaEngine.Api/Services/Sample.cs", $"class B {{ string Title = $@\"The {RetiredRegistryLabel} {{x}}\"; }}");

            WriteFile(clean, "src/MediaEngine.Web/Components/Pages/Sample.razor", $"@inject Permission{RetiredRegistryLabel} Permissions\r\n<h1>Needs Review</h1>");
            WriteFile(clean, "src/MediaEngine.Web/Resources/Sample.resx", "<data name=\"Nav\"><value>Needs Review</value></data>");
            WriteFile(clean, "docs/guides/sample.md", "Start without pulling the registry image.");
            WriteFile(clean, "src/MediaEngine.Web/Services/Sample.cs", $"// the old {RetiredRegistryLabel} screen\r\nclass A {{ IPermission{RetiredRegistryLabel} Source; string Label = \"capability registry\"; }}");
            WriteFile(clean, "src/MediaEngine.Api/Services/Sample.cs", "class B { string Title = \"Needs Review\"; }");

            Assert.Equal(
                [
                    "docs/guides/sample.md",
                    "src/MediaEngine.Api/Services/Sample.cs",
                    "src/MediaEngine.Web/Components/Pages/Sample.razor",
                    "src/MediaEngine.Web/Resources/Sample.resx",
                    "src/MediaEngine.Web/Services/Sample.cs",
                ],
                FindRetiredRegistryLabels(planted).Order(StringComparer.Ordinal));
            Assert.Empty(FindRetiredRegistryLabels(clean));
        }
        finally
        {
            DeleteDirectory(planted);
            DeleteDirectory(clean);
        }
    }

    // Everything a person can read: Dashboard markup and resources, docs, and the C# string literals of the Dashboard
    // and the Engine (labels, messages and titles built in code).
    private static List<string> RegistryScanFiles(string repoRoot)
    {
        var webRoot = Path.Combine(repoRoot, "src", "MediaEngine.Web");
        var files = new List<string>();
        foreach (var folder in new[] { Path.Combine(webRoot, "Components"), Path.Combine(webRoot, "Shared") })
        {
            files.AddRange(EnumerateFiles(folder, "*.razor"));
        }

        files.AddRange(EnumerateFiles(Path.Combine(webRoot, "Resources"), "*.resx"));
        files.AddRange(EnumerateFiles(Path.Combine(repoRoot, "docs"), "*.md"));
        files.AddRange(EnumerateFiles(Path.Combine(repoRoot, "docs"), "*.mdx"));
        files.AddRange(EnumerateFiles(webRoot, "*.cs"));
        files.AddRange(EnumerateFiles(Path.Combine(repoRoot, "src", "MediaEngine.Api"), "*.cs"));

        return files.Where(path => !IsGeneratedOrBuildOutput(path)).ToList();
    }

    private static List<string> FindRetiredRegistryLabels(string repoRoot)
    {
        return RegistryScanFiles(repoRoot)
            .Where(path =>
            {
                var contents = File.ReadAllText(path);
                if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    return StringLiteralRegex.Matches(LineCommentRegex.Replace(contents, string.Empty))
                        .Any(literal => RetiredRegistryLabelRegex.IsMatch(literal.Value));
                }

                return RetiredRegistryLabelRegex.IsMatch(
                    path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) ? Sanitize(contents) : contents);
            })
            .Select(path => Path.GetRelativePath(repoRoot, path).Replace('\\', '/'))
            .ToList();
    }

    private static IEnumerable<string> EnumerateFiles(string folder, string pattern) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, pattern, SearchOption.AllDirectories)
            : [];

    private static void WriteFile(string root, string relativePath, string contents)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
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
        return contents.Contains(RemovedWorkspaceLabel, StringComparison.OrdinalIgnoreCase)
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
        var reviewTab = File.ReadAllText(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Components", "Settings", "NeedsReviewPageContent.razor"));
        var launcher = File.ReadAllText(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Services", "Editing", "MediaEditorLauncherService.cs"));

        // The pop-up stays for people (and the Universe editor); a single item edits in place and Needs Review opens its page.
        Assert.Contains("MediaEditorLauncher.OpenAsync", detailPage);
        Assert.DoesNotContain("MediaEditorLauncher.BeginInline", detailPage);
        Assert.DoesNotContain("MediaEditorLauncher", reviewTab);
        Assert.DoesNotContain("MediaEditorInlineSession", launcher);
        Assert.Contains("SharedMediaEditorMode.Normal", detailPage);
        Assert.Contains("EditingSection.Match", reviewTab);
        Assert.Contains("SharedMediaEditorMode.Batch", launcher);
    }

    // No route opens the old pop-up for a single item (universal editor spec D1 and D17): the only callers left are the person
    // editor on the detail page and the Universe editor, and the launcher itself refuses any other request.
    [Fact]
    public void TheOldPopUpOpensOnlyForAPersonOrAUniverse()
    {
        var webSource = Path.Combine(RepoRoot, "src", "MediaEngine.Web");
        var callers = Directory.EnumerateFiles(webSource, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".razor", StringComparison.Ordinal) || path.EndsWith(".cs", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("MediaEditorLauncher.OpenAsync", StringComparison.Ordinal)
                || File.ReadAllText(path).Contains("MediaEditorLauncher.TryOpenAsync", StringComparison.Ordinal))
            .Select(ToRelativePath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
        [
            "src/MediaEngine.Web/Components/Details/DetailPage.razor",
            "src/MediaEngine.Web/Components/Pages/ChronicleExplorer.razor",
        ], callers);

        var launcher = File.ReadAllText(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Services", "Editing", "MediaEditorLauncherService.cs"));
        Assert.Contains("if (sharedTarget is null && !IsPersonRequest(request))", launcher);
    }

    [Fact]
    public void InlineEditingSurfaces_RefreshOnlyAfterSuccessfulEditorResult()
    {
        var detailPage = File.ReadAllText(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Components", "Details", "DetailPage.razor"));

        Assert.Matches(@"if\s*\(\s*applied\s*\)\s*\{[^}]*RefreshCurrentDetailAsync", detailPage);
        Assert.Contains("GetDetailPageAsync", detailPage);
    }

    [Fact]
    public void MediaEditorLauncher_RejectsEmptyAndMultiItemLaunches()
    {
        var launcher = File.ReadAllText(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Services", "Editing", "MediaEditorLauncherService.cs"));

        Assert.Contains("request.EntityIds.Count == 0", launcher);
        Assert.Contains("return false", launcher);
        Assert.Contains("request.Mode == SharedMediaEditorMode.Batch || request.EntityIds.Count > 1", launcher);
    }

    private static string Sanitize(string contents)
    {
        var withoutRazorComments = RazorCommentRegex.Replace(contents, string.Empty);
        return LineCommentRegex.Replace(withoutRazorComments, string.Empty);
    }

    private static string ToRelativePath(string path) =>
        Path.GetRelativePath(RepoRoot, path).Replace('\\', '/');
}
