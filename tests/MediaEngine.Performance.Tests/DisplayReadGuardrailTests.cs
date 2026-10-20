using System.Reflection;
using System.Text.RegularExpressions;
using Dapper;
using MediaEngine.Api.Services.Display;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Xunit.Abstractions;

namespace MediaEngine.Performance.Tests;

/// <summary>
/// Home and the detail pages read their rows through <c>DisplayWorkProjectionReader.LoadAsync</c>. The reader once built
/// credits for the whole library for every read (Home 101,588 thousand SQLite instructions at 4,000 works, an audiobook page
/// 84,943, a person page 85,257) and looked each of about 280 fields up separately for every row. These checks measure the
/// work SQLite does for one Home read of 1,000 items, one audiobook page and one person page at two sizes: a fixed budget at
/// 4,000 works, and a growth check that doubling the library at most about doubles the work. A second group reads the query
/// plan, because the one-pass read of every item's values barely changes the instruction count (index seeks are single
/// expensive instructions) while its plan shape is what keeps it fast.
/// </summary>
public sealed partial class DisplayReadGuardrailTests : IClassFixture<LibraryListFixture>
{
    private const int HomeItems = 1_000;

    private readonly LibraryListFixture _fixture;
    private readonly ITestOutputHelper _output;

    public DisplayReadGuardrailTests(LibraryListFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task Home_ThousandItemsStayWithinBudgetAtFourThousandWorks()
    {
        var work = await MeasureHomeAsync(_fixture.Small);

        Assert.True(work < HomeBudgetKiloInstructions,
            $"The Home read of {HomeItems:N0} items took {work:N0} thousand SQLite instructions at {LibraryListFixture.SmallLeafWorks:N0} works; the budget is {HomeBudgetKiloInstructions:N0}. "
            + "A breach usually means credits are being worked out for more than the returned rows, or a per-row look-up has stopped using its index.");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task Home_GrowsInProportionToTheLibrary()
    {
        var small = await MeasureHomeAsync(_fixture.Small);
        var large = await MeasureHomeAsync(_fixture.Large);

        AssertProportionalGrowth("Home read", small, large);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task Detail_OneAudiobookStaysWithinBudgetAtFourThousandWorks()
    {
        var work = await MeasureAudiobookDetailAsync(_fixture.Small);

        Assert.True(work < AudiobookDetailBudgetKiloInstructions,
            $"The audiobook detail read took {work:N0} thousand SQLite instructions at {LibraryListFixture.SmallLeafWorks:N0} works; the budget is {AudiobookDetailBudgetKiloInstructions:N0}. "
            + "A breach usually means credits are being worked out for the whole library instead of the returned rows.");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task Detail_OneAudiobookGrowsInProportionToTheLibrary()
    {
        var small = await MeasureAudiobookDetailAsync(_fixture.Small);
        var large = await MeasureAudiobookDetailAsync(_fixture.Large);

        AssertProportionalGrowth("audiobook detail read", small, large);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task Detail_OnePersonStaysWithinBudgetAtFourThousandWorks()
    {
        var work = await MeasurePersonDetailAsync(_fixture.Small);

        Assert.True(work < PersonDetailBudgetKiloInstructions,
            $"The person detail read took {work:N0} thousand SQLite instructions at {LibraryListFixture.SmallLeafWorks:N0} works; the budget is {PersonDetailBudgetKiloInstructions:N0}. "
            + "A breach usually means credits are being worked out for the whole library, or the files that credit the person are no longer found by their stored credits.");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task Detail_OnePersonGrowsInProportionToTheLibrary()
    {
        var small = await MeasurePersonDetailAsync(_fixture.Small);
        var large = await MeasurePersonDetailAsync(_fixture.Large);

        AssertProportionalGrowth("person detail read", small, large);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Home_ReadsEachItemsValuesInOnePass()
    {
        AssertValuesAreReadInOnePass("Home", hasDetail: false, StoredValueLookupBudgetForHome);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Detail_ReadsEachItemsValuesInOnePass()
    {
        AssertValuesAreReadInOnePass("Detail", hasDetail: true, StoredValueLookupBudgetForDetail);
    }

    private async Task<long> MeasureHomeAsync(IDatabaseConnection database)
    {
        var work = new SqliteWorkCounter(database);

        var rows = await new DisplayWorkProjectionReader(work).LoadAsync(CancellationToken.None, HomeItems);

        _output.WriteLine("Home ({0:N0} items): {1:N0} rows, {2:N0} thousand SQLite instructions", HomeItems, rows.Count, work.KiloInstructions);
        Assert.Equal(HomeItems, rows.Count);
        // The tracks on Home name their artist through the album's credit, which is what the fixture's album artists exercise.
        Assert.Contains(rows, row => row.ArtistPersonId.HasValue);
        return work.KiloInstructions;
    }

    private async Task<long> MeasureAudiobookDetailAsync(IDatabaseConnection database)
    {
        var work = new SqliteWorkCounter(database);

        var rows = await new DisplayWorkProjectionReader(work).LoadAsync(CancellationToken.None, detailId: LibraryListFixture.AudiobookWorkId);

        _output.WriteLine("Audiobook detail: {0:N0} rows, {1:N0} thousand SQLite instructions", rows.Count, work.KiloInstructions);
        // The audiobook comes back with its series mates, and takes the year of the book with its title and author.
        Assert.True(rows.Count >= 2);
        var audiobook = Assert.Single(rows, row => row.WorkId == LibraryListFixture.AudiobookWorkId);
        Assert.False(string.IsNullOrEmpty(audiobook.Year));
        return work.KiloInstructions;
    }

    private async Task<long> MeasurePersonDetailAsync(IDatabaseConnection database)
    {
        var work = new SqliteWorkCounter(database);

        var rows = await new DisplayWorkProjectionReader(work).LoadAsync(CancellationToken.None, detailId: LibraryListFixture.FeaturedPersonId);

        _output.WriteLine("Person detail: {0:N0} rows, {1:N0} thousand SQLite instructions", rows.Count, work.KiloInstructions);
        // 24 books and the first album's 10 tracks credit the person.
        Assert.True(rows.Count >= 10);
        return work.KiloInstructions;
    }

    private void AssertProportionalGrowth(string read, long small, long large)
    {
        var ratio = (double)large / small;
        _output.WriteLine("{0}: {1:F2}x the work for 2x the works", read, ratio);
        Assert.True(ratio <= MaxGrowthForDoubleTheWorks,
            $"The {read} did {ratio:F2} times the work for twice the works ({small:N0} -> {large:N0} thousand SQLite instructions); "
            + $"at most {MaxGrowthForDoubleTheWorks} is allowed. A cost that grows faster than the library usually means a per-item look-up has stopped using its index.");
    }

    // The instruction count cannot see a return of the per-field look-ups, so the plan is read instead. A field chosen with
    // its own look-up for every row shows up as a correlated scalar subquery that reads the stored values; the one-pass read
    // keeps only the few that are not fields (the borrowed audiobook year, which tries each date name). Neither stored-value
    // table may be scanned: both are read by entity (and name) through their primary index.
    private void AssertValuesAreReadInOnePass(string read, bool hasDetail, int budget)
    {
        var plan = ReadPlan(hasDetail, out var sql);

        var lookups = plan
            .Where(step => step.Detail.StartsWith("CORRELATED SCALAR SUBQUERY", StringComparison.Ordinal))
            .Count(step => Descendants(plan, step).Any(inner => ReadsStoredValues(inner.Detail) && !inner.Detail.StartsWith("SEARCH date_value ", StringComparison.Ordinal)));
        _output.WriteLine("{0}: {1:N0} correlated look-ups of stored values in a plan of {2:N0} steps", read, lookups, plan.Count);
        Assert.True(lookups <= budget,
            $"The {read} plan has {lookups:N0} correlated look-ups of stored values; the budget is {budget:N0}. "
            + "A count in the hundreds or thousands means the display fields are being looked up one by one for every row again "
            + "instead of chosen from the one-pass value and array pivots.");

        var storedValueNames = StoredValueTableNames(sql);
        var scans = plan.Where(step => IsScanOf(step.Detail, storedValueNames)).Select(step => step.Detail).ToList();
        Assert.True(scans.Count == 0,
            $"The {read} plan scans the stored values ({string.Join("; ", scans)}); both tables must be read by entity through their primary index.");
    }

    private List<PlanStep> ReadPlan(bool hasDetail, out string sql)
    {
        var method = typeof(DisplayWorkProjectionReader).GetMethod("BuildSql", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("DisplayWorkProjectionReader.BuildSql was not found.");
        sql = (string)method.Invoke(null, [hasDetail, false, false])!;

        using var connection = _fixture.Small.CreateConnection();
        var rows = connection.Query(
            "EXPLAIN QUERY PLAN " + sql,
            new { limit = HomeItems, detailId = GuidSql.ToBlob(LibraryListFixture.AudiobookWorkId), personAssetIds = "[]" });
        return rows
            .Cast<IDictionary<string, object>>()
            .Select(row => new PlanStep((long)row["id"], (long)row["parent"], (string)row["detail"]))
            .ToList();
    }

    private static IEnumerable<PlanStep> Descendants(List<PlanStep> plan, PlanStep step)
    {
        var pending = new Stack<long>();
        pending.Push(step.Id);
        while (pending.Count > 0)
        {
            var parent = pending.Pop();
            foreach (var child in plan.Where(candidate => candidate.Parent == parent))
            {
                yield return child;
                pending.Push(child.Id);
            }
        }
    }

    private static bool ReadsStoredValues(string detail) =>
        detail.StartsWith("SEARCH", StringComparison.Ordinal)
        && (detail.Contains("canonical_values", StringComparison.Ordinal) || detail.Contains("canonical_value_arrays", StringComparison.Ordinal));

    // The table names and every alias the statement gives them, because the plan names a table by its alias.
    private static HashSet<string> StoredValueTableNames(string sql)
    {
        var names = new HashSet<string>(StringComparer.Ordinal) { "canonical_values", "canonical_value_arrays" };
        foreach (Match match in StoredValueAlias().Matches(sql))
        {
            names.Add(match.Groups["alias"].Value);
        }

        return names;
    }

    private static bool IsScanOf(string detail, HashSet<string> names)
    {
        if (!detail.StartsWith("SCAN ", StringComparison.Ordinal))
        {
            return false;
        }

        var end = detail.IndexOf(' ', 5);
        return names.Contains(end < 0 ? detail[5..] : detail[5..end]);
    }

    [GeneratedRegex(@"\b(?:FROM|JOIN)\s+canonical_value(?:s|_arrays)\s+(?:AS\s+)?(?!(?:WHERE|ON|INNER|LEFT|RIGHT|CROSS|JOIN|GROUP|ORDER|UNION|LIMIT|USING)\b)(?<alias>[A-Za-z_]\w*)", RegexOptions.IgnoreCase)]
    private static partial Regex StoredValueAlias();

    private sealed record PlanStep(long Id, long Parent, string Detail);

    // Proportional growth is 2x, as in the library list guardrails.
    private const double MaxGrowthForDoubleTheWorks = 2.5;

    // Measured on this fixture when the budgets were set, the same on every run: Home 9,634 thousand instructions for 1,000
    // items at 4,000 works (11,189 at 8,000, so 1.16x for 2x the works: only the fixed share grows), an audiobook page 920
    // (1,805 at 8,000, 1.96x; four rows, the audiobook and its three series mates) and a person page 1,803 (3,133 at 8,000,
    // 1.74x; 34 rows). Each budget is about one and a half times the fixed cost. With credits worked out for the whole
    // library again (instead of the returned rows) the same reads took 15,972, 10,233 and 10,892.
    private const long HomeBudgetKiloInstructions = 14_500;
    private const long AudiobookDetailBudgetKiloInstructions = 1_400;
    private const long PersonDetailBudgetKiloInstructions = 2_700;

    // Measured: 7 correlated look-ups of stored values in each plan (the owner, series and edition fall-backs that are not
    // display fields), and another 146 under the borrowed audiobook year, which tries each date name once per matched
    // audiobook and is left out of the count. With the display fields looked up one by one for every row the same count was
    // 245 (about 1,450 in all before the credits change), and the Home read still took only 11,584 thousand instructions,
    // which is why the plan is checked as well as the instruction count.
    private const int StoredValueLookupBudgetForHome = 10;
    private const int StoredValueLookupBudgetForDetail = 10;
}
