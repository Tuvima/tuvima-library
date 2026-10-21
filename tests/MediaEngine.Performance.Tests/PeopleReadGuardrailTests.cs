using Dapper;
using MediaEngine.Storage;
using Xunit.Abstractions;

namespace MediaEngine.Performance.Tests;

/// <summary>
/// The "what the library holds for these people" count and the people search once read the whole-library credit list
/// (about 82,000 thousand SQLite instructions at 4,000 works) for every call. They now work out credits only for the
/// files that could credit the people asked about. These checks pin that as a fixed work budget at 4,000 works (the same
/// on every run, unlike elapsed time) and a text check that neither read names the whole-library credit list again.
/// </summary>
public sealed class PeopleReadGuardrailTests : IClassFixture<LibraryListFixture>
{
    private readonly LibraryListFixture _fixture;
    private readonly ITestOutputHelper _output;

    public PeopleReadGuardrailTests(LibraryListFixture fixture, ITestOutputHelper output)
    {
        DapperConfiguration.Configure();
        _fixture = fixture;
        _output = output;
    }

    // Measured at 4,000 works for the 20 people with the most author credits: 4,976 thousand SQLite instructions; the
    // whole-library credit list cost about 82,000 for one call. The budget is about 1.5 times the measured cost.
    private const long PresenceBudgetKiloInstructions = 7_500;

    [Fact]
    [Trait("Category", "Performance")]
    public async Task PresenceCounts_ForAPageOfPeopleStayWithinBudgetAtFourThousandWorks()
    {
        var people = PersonIds(_fixture.Small, 20);
        Assert.Equal(20, people.Count);
        var counter = new SqliteWorkCounter(_fixture.Small);

        var presence = await new PersonRepository(counter).GetPresenceBatchAsync(people, CancellationToken.None);

        var work = counter.KiloInstructions;
        _output.WriteLine("Presence for 20 people: {0} with credits, {1:N0} thousand SQLite instructions (budget {2:N0})", presence.Count, work, PresenceBudgetKiloInstructions);
        Assert.NotEmpty(presence);
        Assert.True(work < PresenceBudgetKiloInstructions,
            $"Presence counts for 20 people took {work:N0} thousand SQLite instructions at {LibraryListFixture.SmallLeafWorks:N0} works; the budget is {PresenceBudgetKiloInstructions:N0}. "
            + "A breach usually means the read works out credits for the whole library again instead of only the files that could credit these people.");
    }

    [Fact]
    public void PresenceCountsAndPeopleSearch_NeverNameTheWholeLibraryCreditList()
    {
        var root = RepositoryRoot();
        var presence = Slice(Read(root, "src/MediaEngine.Storage/PersonRepository.cs"), "GetPresenceBatchAsync(", "// Alias (pseudonym) management");
        var search = Slice(Read(root, "src/MediaEngine.Api/Services/ReadServices/UniversalSearchReadService.cs"), "SearchPeopleAsync(", "SearchCollectionsAsync(");

        Assert.DoesNotContain("primary_person_media_credits", presence);
        Assert.DoesNotContain("primary_person_media_credits", search);
    }

    private static string Read(string root, string file) =>
        File.ReadAllText(Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar)));

    private static string Slice(string text, string startMarker, string endMarker)
    {
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        var end = start < 0 ? -1 : text.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"{startMarker} was not found.");
        return text[start..end];
    }

    private static List<Guid> PersonIds(DatabaseConnection database, int count)
    {
        using var connection = database.CreateConnection();
        return connection
            .Query<byte[]>(
                """
                SELECT p.id
                FROM persons p
                INNER JOIN canonical_value_arrays cva ON cva.key = 'author' AND cva.value = p.name
                GROUP BY p.id
                ORDER BY COUNT(*) DESC, p.id
                LIMIT @count;
                """,
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
}
