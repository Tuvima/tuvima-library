using System.Text.RegularExpressions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Documents are private to the Documents page: their words never feed Home, global Search or Recommendations,
/// and every route that serves a document file asks the Documents permission first.
/// </summary>
public sealed class ViewDocumentsGuardrailTests
{
    private static readonly string[] LocalItemTokens =
    [
        "local_items", "local_item_search", "local_item_metadata", "ILocalAssetRepository", "LocalAssetRepository",
    ];

    // Words in a path that mark the surfaces that must never read View documents.
    private static readonly string[] ForbiddenSurfaces = ["Search", "Recommend", "Home", "Recent", "Spotlight", "Suggest"];

    [Fact]
    public void GlobalSearchHomeAndRecommendationsNeverReadLocalItems()
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
                     .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".razor", StringComparison.Ordinal))
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                    && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            if (!LocalItemTokens.Any(token => text.Contains(token, StringComparison.Ordinal)))
            {
                continue;
            }

            var name = Path.GetFileName(file);
            if (ForbiddenSurfaces.Any(surface => name.Contains(surface, StringComparison.Ordinal)))
            {
                offenders.Add(Path.GetRelativePath(root, file));
            }
        }

        Assert.True(offenders.Count == 0, "These Search, Home or Recommendation files read View items: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheGlobalSearchIndexNeverIndexesViewItems()
    {
        var root = FindRepoRoot();
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src", "MediaEngine.Storage"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.EndsWith("SchemaMigrator.cs", StringComparison.Ordinal))
            .Select(path => (path, text: File.ReadAllText(path)))
            .Where(file => Regex.IsMatch(file.text, @"\bsearch_index\b") && file.text.Contains("local_item", StringComparison.Ordinal))
            .Select(file => Path.GetFileName(file.path))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void EveryRouteThatServesADocumentFileChecksTheDocumentsPermission()
    {
        var endpoint = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Api", "Endpoints", "ViewEndpoints.cs"));

        foreach (var route in new[]
                 {
                     "group.MapGet(\"/items/{id:guid}\"",
                     "group.MapGet(\"/items/{id:guid}/text\"",
                     "group.MapGet(\"/items/{id:guid}/content\"",
                     "group.MapGet(\"/items/{id:guid}/thumbnail\"",
                     "group.MapGet(\"/items/{id:guid}/preview\"",
                 })
        {
            var start = endpoint.IndexOf(route, StringComparison.Ordinal);
            Assert.True(start >= 0, $"{route} is missing.");
            var end = endpoint.IndexOf(".WithName(", start, StringComparison.Ordinal);
            Assert.Contains("DocumentsBlockedAsync", endpoint[start..end], StringComparison.Ordinal);
        }

        var folderStart = endpoint.IndexOf("group.MapGet(\"/folders\"", StringComparison.Ordinal);
        var folderEnd = endpoint.IndexOf(".WithName(\"GetViewFolders\")", folderStart, StringComparison.Ordinal);
        Assert.Contains("MayUseDocumentsAsync", endpoint[folderStart..folderEnd], StringComparison.Ordinal);

        var listStart = endpoint.IndexOf("group.MapGet(\"/assets\"", StringComparison.Ordinal);
        var listEnd = endpoint.IndexOf(".WithName(\"GetViewAssets\")", listStart, StringComparison.Ordinal);
        Assert.Contains("KindsWithoutBlockedDocumentsAsync", endpoint[listStart..listEnd], StringComparison.Ordinal);

        // The timeline index searches and counts the same rows as the list.
        var indexStart = endpoint.IndexOf("group.MapGet(\"/assets/timeline-index\"", StringComparison.Ordinal);
        var indexEnd = endpoint.IndexOf(".WithName(\"GetViewTimelineIndex\")", indexStart, StringComparison.Ordinal);
        Assert.Contains("KindsWithoutBlockedDocumentsAsync", endpoint[indexStart..indexEnd], StringComparison.Ordinal);

        // Every action that answers with the whole item (notes, field values, suggestions) asks the permission first.
        foreach (var route in new[]
                 {
                     "group.MapPut(\"/items/{id:guid}/description\"",
                     "group.MapPut(\"/items/{id:guid}/captured-at\"",
                     "group.MapPut(\"/items/{id:guid}/people\"",
                     "group.MapPut(\"/items/{id:guid}/location\"",
                 })
        {
            var start = endpoint.IndexOf(route, StringComparison.Ordinal);
            Assert.True(start >= 0, $"{route} is missing.");
            var end = endpoint.IndexOf(".WithName(", start, StringComparison.Ordinal);
            Assert.Contains("DocumentsBlockedAsync", endpoint[start..end], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void HomeRecentlyAddedAsksViewForPhotosAndVideosOnly()
    {
        var composer = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Api", "Services", "Display", "DisplayRecentComposerService.cs"));

        Assert.Contains("MediaKinds: HomeMediaKinds", composer, StringComparison.Ordinal);
        Assert.Contains("[LocalAssetMediaKinds.Image, LocalAssetMediaKinds.Video]", composer, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalAssetMediaKinds.Document", composer, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
