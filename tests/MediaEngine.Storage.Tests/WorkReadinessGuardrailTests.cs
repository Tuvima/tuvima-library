using System.Text.RegularExpressions;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// "Ready" has one definition (<c>WorkReadinessSql</c>) and one writer (<c>WorkReadinessRepository</c>). These tests fail
/// when a display query defines its own visibility rule or when anything else decides Ready.
/// </summary>
public sealed class WorkReadinessGuardrailTests
{
    // Files that read works but are not a display listing of ready items. Each needs a one-line reason.
    private static readonly IReadOnlyDictionary<string, string> NotDisplayQueries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ActivityBatchReadService.cs"] = "Activity log: lists what the Library did, including items that are not ready.",
        ["ArtworkLibraryReadService.cs"] = "Artwork workspace for administrators: must reach items whose artwork is still being settled.",
        ["CatalogueResourceAuthorization.cs"] = "Authorization: works out which works a grant covers, by id, whether or not they are ready.",
        ["EditorSuggestionReadService.cs"] = "Editor suggestions for one item the administrator already opened.",
        ["HierarchyAlignmentService.cs"] = "Maintenance that realigns parents and children for every work, ready or not.",
        ["IngestionPresentationReadService.cs"] = "The live ingestion page: it exists to show items that are not ready yet.",
        ["JourneyReadService.cs"] = "A profile's own progress rows; they exist only for items that were played.",
        ["LibraryCurationReadService.cs"] = "Administrator curation list: must show items that are not ready.",
        ["LibraryItemCurationRepository.cs"] = "Administrator curation writes and lookups by id, including items that are not ready.",
        ["LibraryItemRepository.cs"] = "The Library list: it gates on the stored ready mark itself through library_visibility.",
        ["MediaEditorNavigationReadService.cs"] = "Editor navigation between the parts of one item the administrator opened.",
        ["MediaEditorOwnedChildReadService.cs"] = "Editor listing of the children of one item the administrator opened.",
        ["MediaEditorSelectionHistoryReadService.cs"] = "Editor selection history for one item the administrator opened.",
        ["OrphanImageReferenceReadService.cs"] = "Maintenance that finds image references with no owner.",
        ["PersonReadServices.cs"] = "Membership lookups from a person or collection id to files, not a listing.",
        ["ReviewQueueReadService.cs"] = "Needs Review: lists items that are by definition not ready.",
        ["UniversalSearchReadService.cs"] = "Searches only the works the gated display projection already returned for the caller.",
        ["WorkDetailReadService.cs"] = "Lookup of one work by id for the editor.",
    };

    [Fact]
    public void DisplayQueries_UseTheSharedReadyRule()
    {
        var repoRoot = FindRepositoryRoot();
        var apiDisplay = Path.Combine(repoRoot, "src", "MediaEngine.Api", "Services", "Display");
        var apiReadServices = Path.Combine(repoRoot, "src", "MediaEngine.Api", "Services", "ReadServices");
        var apiSecurity = Path.Combine(repoRoot, "src", "MediaEngine.Api", "Security");
        var storage = Path.Combine(repoRoot, "src", "MediaEngine.Storage");

        var candidates = SourceFiles(apiDisplay)
            .Concat(SourceFiles(apiReadServices))
            .Concat(SourceFiles(apiSecurity))
            .Concat(SourceFiles(storage).Where(IsStorageDisplayCandidate));
        var readsWorks = new Regex(@"\b(?:FROM|JOIN)\s+works\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var offenders = candidates
            .Where(path => readsWorks.IsMatch(File.ReadAllText(path)))
            .Where(path => !File.ReadAllText(path).Contains("WorkReadinessSql.", StringComparison.Ordinal))
            .Where(path => !NotDisplayQueries.ContainsKey(Path.GetFileName(path)))
            .Select(path => Path.GetRelativePath(repoRoot, path))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Display queries over works must filter with WorkReadinessSql (ReadyWorkPredicate or ReadyGroupPredicate), "
            + "or be listed in NotDisplayQueries with a reason: " + string.Join(", ", offenders));
    }

    [Fact]
    public void NotDisplayQueriesAllowlist_OnlyNamesFilesThatStillReadWorks()
    {
        var repoRoot = FindRepositoryRoot();
        var all = SourceFiles(Path.Combine(repoRoot, "src")).ToList();
        var readsWorks = new Regex(@"\b(?:FROM|JOIN)\s+works\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var stale = NotDisplayQueries.Keys
            .Where(name => !all.Any(path => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase)
                && readsWorks.IsMatch(File.ReadAllText(path))))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(stale.Count == 0, "Remove from NotDisplayQueries: " + string.Join(", ", stale));
    }

    [Fact]
    public void OnlyTheReadinessRuleDecidesReady()
    {
        var repoRoot = FindRepositoryRoot();
        var assigns = new Regex(@"\bready_at\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var tests = new Regex(@"\bready_at\s+IS\s+(?:NOT\s+)?NULL", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // The repository writes the mark; the SQL rule and the Library list (which reads it) may test it.
        string[] mayTest = ["WorkReadinessSql.cs", "WorkReadinessRepository.cs", "LibraryItemRepository.cs"];
        string[] mayWrite = ["WorkReadinessRepository.cs"];

        var offenders = new List<string>();
        foreach (var path in SourceFiles(Path.Combine(repoRoot, "src")))
        {
            var name = Path.GetFileName(path);
            var text = File.ReadAllText(path);
            if (assigns.IsMatch(text) && !mayWrite.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                offenders.Add($"{Path.GetRelativePath(repoRoot, path)} assigns ready_at");
            }

            if (tests.IsMatch(text) && !mayTest.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                offenders.Add($"{Path.GetRelativePath(repoRoot, path)} tests ready_at");
            }
        }

        Assert.True(offenders.Count == 0, "Only WorkReadinessSql and WorkReadinessRepository decide Ready: " + string.Join("; ", offenders));
    }

    [Fact]
    public void RetiredVisibilityRules_AreNotDefinedAgain()
    {
        var repoRoot = FindRepositoryRoot();
        string[] retired = ["VisibleWorkPredicate", "LatestNeedsReviewStateExclusion", "LatestTerminalIdentityStateExclusion", "IsIdentityReady"];

        var offenders = SourceFiles(Path.Combine(repoRoot, "src"))
            .SelectMany(path => retired
                .Where(name => File.ReadAllText(path).Contains(name, StringComparison.Ordinal))
                .Select(name => $"{Path.GetRelativePath(repoRoot, path)}: {name}"))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Empty(offenders);
    }

    private static bool IsStorageDisplayCandidate(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith("ReadService.cs", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Collection", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Library", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Display", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
