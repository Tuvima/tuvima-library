using Dapper;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using Xunit.Abstractions;

namespace MediaEngine.Performance.Tests;

/// <summary>
/// A person's credits and the series search once built the whole library's credit list on every visit (a person's page
/// twice, series search once for every file the search index matched: 85,000 thousand-instruction ticks for a person and
/// 3 million for a search at 4,000 works). They now work out credits only for the files that could credit the person and for
/// the 20 results shown. These checks measure the work SQLite does (the same on every run, unlike elapsed time) as a fixed
/// budget at 4,000 works, a growth check for a person's credits at twice the library, and a text check that neither read's
/// code names the whole-library credit list again. Spec <c>person-credits-and-series-search-speed.md</c> (D10).
/// </summary>
public sealed class CreditReadGuardrailTests : IClassFixture<LibraryListFixture>
{
    private readonly LibraryListFixture _fixture;
    private readonly ITestOutputHelper _output;

    public CreditReadGuardrailTests(LibraryListFixture fixture, ITestOutputHelper output)
    {
        DapperConfiguration.Configure();
        _fixture = fixture;
        _output = output;
    }

    // Measured at 4,000 works when the budgets were set (thousands of SQLite instructions, the same on every run): an
    // author's credits 989, a group member's 1,112 and a search with 40 index matches 324. The whole-library credit list put
    // them at about 85,000, 167,000 and 3,135,000. Each budget is about 1.5 times the measured cost, so going back fails by
    // about 50 to 100 times (a person) or 6,000 times (series search).
    private const long AuthorCreditsBudgetKiloInstructions = 1_500;
    private const long GroupMemberCreditsBudgetKiloInstructions = 1_700;
    private const long SeriesSearchBudgetKiloInstructions = 500;

    // Proportional growth is 2x; the candidate step reads every stored credit once and measured 1.9x per doubling.
    private const double MaxGrowthForDoubleTheWorks = 2.5;

    [Fact]
    [Trait("Category", "Performance")]
    public async Task PersonCredits_OfAnAuthorStayWithinBudgetAtFourThousandWorks()
    {
        var author = PersonIdWithTheMostAuthorCredits(_fixture.Small);

        var work = await MeasurePersonCreditsAsync(_fixture.Small, author);

        _output.WriteLine("Author credits: {0:N0} thousand SQLite instructions (budget {1:N0})", work, AuthorCreditsBudgetKiloInstructions);
        Assert.True(work < AuthorCreditsBudgetKiloInstructions,
            $"A person's credits took {work:N0} thousand SQLite instructions at {LibraryListFixture.SmallLeafWorks:N0} works; the budget is {AuthorCreditsBudgetKiloInstructions:N0}. "
            + "A breach usually means the read works out credits for the whole library again instead of only the files that could credit the person.");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task PersonCredits_OfAGroupMemberStayWithinBudgetAtFourThousandWorks()
    {
        var member = PersonIdByName(_fixture.Small, "Person 71");

        var work = await MeasurePersonCreditsAsync(_fixture.Small, member);

        _output.WriteLine("Group member credits: {0:N0} thousand SQLite instructions (budget {1:N0})", work, GroupMemberCreditsBudgetKiloInstructions);
        Assert.True(work < GroupMemberCreditsBudgetKiloInstructions,
            $"The credits of a group member took {work:N0} thousand SQLite instructions at {LibraryListFixture.SmallLeafWorks:N0} works; the budget is {GroupMemberCreditsBudgetKiloInstructions:N0}. "
            + "A breach usually means the read works out credits for the whole library again for the person's own, the pen-name check or the group's credits.");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task PersonCredits_GrowInProportionToTheLibrary()
    {
        var small = await MeasurePersonCreditsAsync(_fixture.Small, PersonIdByName(_fixture.Small, "Person 71"));
        var large = await MeasurePersonCreditsAsync(_fixture.Large, PersonIdByName(_fixture.Large, "Person 71"));

        var ratio = (double)large / small;
        _output.WriteLine("Group member credits: {0:N0} -> {1:N0} thousand instructions, {2:F2}x the work for 2x the works", small, large, ratio);
        Assert.True(ratio <= MaxGrowthForDoubleTheWorks,
            $"A person's credits did {ratio:F2} times the work for twice the works ({small:N0} -> {large:N0} thousand SQLite instructions); at most {MaxGrowthForDoubleTheWorks} is allowed.");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task SeriesSearch_StaysWithinBudgetWhateverNumberOfFilesTheIndexMatches()
    {
        var audiobooks = AudiobookWorkIds(_fixture.Small, 40);
        Assert.Equal(40, audiobooks.Count);
        var counter = new SqliteWorkCounter(_fixture.Small);

        var found = await new CollectionSearchReadService(counter, new FixedSearchIndex(audiobooks))
            .SearchAsync("Audiobooks", CancellationToken.None);

        var work = counter.KiloInstructions;
        _output.WriteLine("Series search with 40 index matches: {0} rows, {1:N0} thousand SQLite instructions (budget {2:N0})", found.Count, work, SeriesSearchBudgetKiloInstructions);
        Assert.Equal(20, found.Count);
        Assert.True(work < SeriesSearchBudgetKiloInstructions,
            $"The series search took {work:N0} thousand SQLite instructions for 40 index matches at {LibraryListFixture.SmallLeafWorks:N0} works; the budget is {SeriesSearchBudgetKiloInstructions:N0}. "
            + "A breach usually means credits are worked out for every file the search index matched instead of only the 20 results shown.");
    }

    [Fact]
    public void PersonCreditsAndSeriesSearch_NeverNameTheWholeLibraryCreditList()
    {
        var root = RepositoryRoot();
        var files = new[]
        {
            "src/MediaEngine.Api/Services/ReadServices/PersonCreditReadService.cs",
            "src/MediaEngine.Api/Services/ReadServices/CollectionSearchReadService.cs",
        };

        foreach (var file in files)
        {
            var text = File.ReadAllText(Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar)));
            if (file.EndsWith("PersonCreditReadService.cs", StringComparison.Ordinal))
            {
                // Only the library credits read (GetLibraryCreditsCoreAsync) is covered; the file's other reads are the follow-up remediation R2.
                var start = text.IndexOf("Task<List<PersonLibraryCreditDto>> GetLibraryCreditsCoreAsync(", StringComparison.Ordinal);
                var end = text.IndexOf("NormalizeLibraryCreditRows(", start, StringComparison.Ordinal);
                Assert.True(start >= 0 && end > start, "GetLibraryCreditsCoreAsync was not found in PersonCreditReadService.cs.");
                text = text[start..end];
            }

            Assert.DoesNotContain("primary_person_media_credits", text);
        }
    }

    private async Task<long> MeasurePersonCreditsAsync(DatabaseConnection database, Guid personId)
    {
        var counter = new SqliteWorkCounter(database);
        var service = new PersonCreditReadService(
            new CanonicalValueArrayRepository(counter),
            new PersonRepository(counter),
            counter);

        var credits = await service.GetLibraryCreditsAsync(personId, CancellationToken.None);

        Assert.NotEmpty(credits);
        return counter.KiloInstructions;
    }

    private static Guid PersonIdWithTheMostAuthorCredits(DatabaseConnection database)
    {
        using var connection = database.CreateConnection();
        var id = connection.QuerySingle<byte[]>(
            """
            SELECT p.id
            FROM persons p
            INNER JOIN canonical_value_arrays cva ON cva.key = 'author' AND cva.value = p.name
            GROUP BY p.id
            ORDER BY COUNT(*) DESC, p.id
            LIMIT 1;
            """);
        return GuidSql.FromDb(id);
    }

    private static Guid PersonIdByName(DatabaseConnection database, string name)
    {
        using var connection = database.CreateConnection();
        return GuidSql.FromDb(connection.QuerySingle<byte[]>("SELECT id FROM persons WHERE name = @name;", new { name }));
    }

    private static List<Guid> AudiobookWorkIds(DatabaseConnection database, int count)
    {
        using var connection = database.CreateConnection();
        return connection
            .Query<byte[]>(
                "SELECT id FROM works WHERE media_type = 'Audiobooks' AND work_kind = 'standalone' ORDER BY id LIMIT @count;",
                new { count })
            .Select(blob => GuidSql.FromDb(blob))
            .ToList();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("MediaEngine.slnx was not found above the test output folder.");
    }

    /// <summary>A search index that answers every query with the same works, as the full-text index would for a matching word.</summary>
    private sealed class FixedSearchIndex(IReadOnlyList<Guid> workIds) : ISearchIndexRepository
    {
        public Task UpsertByEntityIdAsync(Guid entityId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<Guid>> SearchAsync(string query, int limit = 50, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Guid>>(workIds.Take(limit).ToList());

        public Task RebuildAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
