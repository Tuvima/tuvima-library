using System.Text.RegularExpressions;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// "Ready" has one definition (<c>WorkReadinessSql</c>) and one writer (<c>WorkReadinessRepository</c>). These tests fail
/// when a display query defines its own visibility rule or when anything else decides Ready.
/// </summary>
public sealed class WorkReadinessGuardrailTests
{
    // Files that are wholly not a display listing of ready items. Each needs a one-line reason. A file that mixes display
    // queries with lookups is not listed here; its lookups carry a "-- readiness: reason" comment inside the SQL instead.
    private static readonly IReadOnlyDictionary<string, string> NotDisplayFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ArtworkLibraryReadService.cs"] = "Artwork workspace for administrators: must reach items whose artwork is still being settled.",
        ["CatalogueResourceAuthorization.cs"] = "Authorization: works out which works a grant covers, by id, whether or not they are ready.",
        ["EditorSuggestionReadService.cs"] = "Editor suggestions for one item the administrator already opened.",
        ["HierarchyAlignmentService.cs"] = "Maintenance that realigns parents and children for every work, ready or not.",
        ["IngestionPresentationReadService.cs"] = "The live ingestion page: it exists to show items that are not ready yet.",
        ["LibraryCurationReadService.cs"] = "Administrator curation list: must show items that are not ready.",
        ["LibraryItemCurationRepository.cs"] = "Administrator curation writes and lookups by id, including items that are not ready.",
        ["MediaEditorNavigationReadService.cs"] = "Editor navigation between the parts of one item the administrator opened.",
        ["MediaEditorOwnedChildReadService.cs"] = "Editor listing of the children of one item the administrator opened.",
        ["MediaEditorSelectionHistoryReadService.cs"] = "Editor selection history for one item the administrator opened.",
        ["OrphanImageReferenceReadService.cs"] = "Maintenance that finds image references with no owner.",
        ["ReviewQueueReadService.cs"] = "Needs Review: lists items that are by definition not ready.",
    };

    private static readonly Regex ReadsWorks = new(@"\b(?:FROM|JOIN)\s+works\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // A raw string literal ("""..."""), with any number of leading $ signs.
    private static readonly Regex RawLiteral = new("\\$*\"\"\"[\\s\\S]*?\"\"\"", RegexOptions.CultureInvariant);

    // An ordinary single-line string literal.
    private static readonly Regex PlainLiteral = new("@?\\$?\"(?:[^\"\\\\\\r\\n]|\\\\.)*\"", RegexOptions.CultureInvariant);

    private static readonly Regex ReadinessAssignment = new(@"\b(\w+)\s*=[^;]*WorkReadinessSql\.", RegexOptions.CultureInvariant);

    [Fact]
    public void DisplayQueries_UseTheSharedReadyRule()
    {
        var repoRoot = FindRepositoryRoot();
        var api = Path.Combine(repoRoot, "src", "MediaEngine.Api");
        var candidates = new[] { "Services/Display", "Services/ReadServices", "Services/Details", "Security", "Endpoints" }
            .SelectMany(folder => SourceFiles(Path.Combine(api, folder)))
            .Concat(SourceFiles(Path.Combine(repoRoot, "src", "MediaEngine.Storage")).Where(IsStorageDisplayCandidate))
            .Where(path => !NotDisplayFiles.ContainsKey(Path.GetFileName(path)));

        var offenders = candidates
            .SelectMany(path => FindUnfilteredWorkQueries(File.ReadAllText(path))
                .Select(excerpt => $"{Path.GetRelativePath(repoRoot, path)}: {excerpt}"))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Every query that reads works must filter with WorkReadinessSql, read ready_at, or carry a "
            + "'-- readiness: reason' comment inside the SQL (an id lookup, an administrator tool). Found:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void TheQueryCheck_FlagsAnUnfilteredLiteral_AndAcceptsFilteredOrMarkedOnes()
    {
        const string unfiltered = "var rows = conn.Query(\"\"\"\n    SELECT w.id\n    FROM works w\n    WHERE w.media_type = 'Movies'\n    \"\"\");";
        Assert.Single(FindUnfilteredWorkQueries(unfiltered));

        const string plain = "var rows = conn.Query(\"SELECT w.id FROM works w WHERE w.media_type = 'Movies'\");";
        Assert.Single(FindUnfilteredWorkQueries(plain));

        const string joined = "var rows = conn.Query($\"\"\"\n    SELECT w.id FROM catalog c JOIN works w ON w.id = c.work_id\n    \"\"\");";
        Assert.Single(FindUnfilteredWorkQueries(joined));

        const string filtered = "var rows = conn.Query($\"\"\"\n    SELECT w.id FROM works w WHERE {WorkReadinessSql.ReadyWorkPredicate(\"w\")}\n    \"\"\");";
        Assert.Empty(FindUnfilteredWorkQueries(filtered));

        const string viaVariable =
            "var ready = WorkReadinessSql.ReadyWorkPredicate(\"w\");\nvar rows = conn.Query($\"\"\"\n    SELECT w.id FROM works w WHERE {ready}\n    \"\"\");";
        Assert.Empty(FindUnfilteredWorkQueries(viaVariable));

        const string readsMark = "var rows = conn.Query(\"\"\"\n    SELECT w.id FROM works w WHERE w.ready_at IS NOT NULL\n    \"\"\");";
        Assert.Empty(FindUnfilteredWorkQueries(readsMark));

        const string marked = "var row = conn.Query(\"\"\"\n    -- readiness: lookup of one work by id\n    SELECT w.id FROM works w WHERE w.id = @id\n    \"\"\");";
        Assert.Empty(FindUnfilteredWorkQueries(marked));

        const string noWorks = "var rows = conn.Query(\"SELECT id FROM collections\");";
        Assert.Empty(FindUnfilteredWorkQueries(noWorks));
    }

    [Fact]
    public void NotDisplayFilesAllowlist_OnlyNamesFilesThatStillReadWorks()
    {
        var repoRoot = FindRepositoryRoot();
        var all = SourceFiles(Path.Combine(repoRoot, "src")).ToList();

        var stale = NotDisplayFiles.Keys
            .Where(name => !all.Any(path => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase)
                && ReadsWorks.IsMatch(File.ReadAllText(path))))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(stale.Count == 0, "Remove from NotDisplayFiles: " + string.Join(", ", stale));
    }

    [Fact]
    public void OnlyTheReadinessRuleDecidesReady()
    {
        var repoRoot = FindRepositoryRoot();
        var assigns = new Regex(@"\b(?:first_)?ready_at\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var tests = new Regex(@"\b(?:first_)?ready_at\s+IS\s+(?:NOT\s+)?NULL", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
    public void TheLiveIngestionPage_DecidesReadyWithTheSharedRule_NotFromIdentityJobs()
    {
        var path = Path.Combine(FindRepositoryRoot(), "src", "MediaEngine.Api", "Services", "ReadServices", "IngestionPresentationReadService.cs");
        var text = File.ReadAllText(path);

        Assert.DoesNotContain("IdentityReadySql", text, StringComparison.Ordinal);
        Assert.Contains("WorkReadinessSql.ReadyMarkPredicate", text, StringComparison.Ordinal);
        Assert.Contains("WorkReadinessSql.MatchedPredicate", text, StringComparison.Ordinal);
        Assert.Contains("WorkReadinessSql.PlacedPredicate", text, StringComparison.Ordinal);
        Assert.Contains("WorkReadinessSql.ArtworkPresentOrSettledPredicate", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RetiredVisibilityRules_AreNotDefinedAgain()
    {
        var repoRoot = FindRepositoryRoot();
        string[] retired = ["VisibleWorkPredicate", "LatestNeedsReviewStateExclusion", "LatestTerminalIdentityStateExclusion", "IsIdentityReady", "ReadyGroupPredicate", "IdentityReadySql"];

        var offenders = SourceFiles(Path.Combine(repoRoot, "src"))
            .SelectMany(path => retired
                .Where(name => File.ReadAllText(path).Contains(name, StringComparison.Ordinal))
                .Select(name => $"{Path.GetRelativePath(repoRoot, path)}: {name}"))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The SQL string literals in <paramref name="source"/> that read the works table without using the shared Ready rule:
    /// a literal that mentions <c>FROM works</c> or <c>JOIN works</c> must interpolate <c>WorkReadinessSql.</c> (directly or
    /// through a variable assigned from it), read <c>ready_at</c>, or carry a <c>-- readiness: reason</c> comment.
    /// </summary>
    private static IReadOnlyList<string> FindUnfilteredWorkQueries(string source)
    {
        var literals = new List<string>();
        foreach (Match match in RawLiteral.Matches(source))
        {
            literals.Add(match.Value);
        }

        var rest = RawLiteral.Replace(source, " ");
        foreach (Match match in PlainLiteral.Matches(rest))
        {
            literals.Add(match.Value);
        }

        var readinessVariables = ReadinessAssignment.Matches(source)
            .Select(match => match.Groups[1].Value)
            .Distinct()
            .ToList();

        return literals
            .Where(literal => ReadsWorks.IsMatch(literal))
            .Where(literal => !literal.Contains("WorkReadinessSql.", StringComparison.Ordinal))
            .Where(literal => !literal.Contains("ready_at", StringComparison.OrdinalIgnoreCase))
            .Where(literal => !literal.Contains("-- readiness:", StringComparison.Ordinal))
            .Where(literal => !readinessVariables.Any(name => literal.Contains("{" + name + "}", StringComparison.Ordinal)))
            .Select(literal => Regex.Replace(literal, @"\s+", " ").Trim())
            .Select(literal => literal.Length > 110 ? literal[..110] + "..." : literal)
            .ToList();
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
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            : [];

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
