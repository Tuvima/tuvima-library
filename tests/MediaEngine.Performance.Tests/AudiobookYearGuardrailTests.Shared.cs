using Dapper;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Xunit.Abstractions;

namespace MediaEngine.Performance.Tests;

// TEMPORARY half of the class (WP3 of spec "Audiobook year on the remaining screens"): the fixture constructor and helpers
// the collection guardrails need, so that packet builds and runs on its own. WP2 writes AudiobookYearGuardrailTests.cs with
// the same members; the coordinator merges the two halves and deletes this file.

/// <summary>
/// An audiobook without an original year of its own borrows the year of the book with its title and author. Each read that
/// shows that year is measured twice on the 4,000-work library, once with audiobooks that need a borrowed year and once with
/// the same number that have their own; the difference is the year's own cost (spec D7), which must stay under a fixed budget.
/// </summary>
public sealed partial class AudiobookYearGuardrailTests : IClassFixture<LibraryListFixture>
{
    private readonly LibraryListFixture _fixture;
    private readonly ITestOutputHelper _output;

    public AudiobookYearGuardrailTests(LibraryListFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    /// <summary>Runs a read through a work counter and returns the thousands of SQLite instructions it took.</summary>
    private async Task<long> MeasureAsync(
        string label,
        IDatabaseConnection database,
        Func<IDatabaseConnection, Task<int>> read)
    {
        var work = new SqliteWorkCounter(database);

        var rows = await read(work);

        _output.WriteLine("{0}: {1:N0} rows, {2:N0} thousand SQLite instructions", label, rows, work.KiloInstructions);
        return work.KiloInstructions;
    }

    /// <summary>The works a listener has played that are audiobooks, in the fixture's data store.</summary>
    private static async Task<List<Guid>> ListenerAudiobookWorkIdsAsync(IDatabaseConnection database, Guid listenerId)
    {
        using var connection = database.CreateConnection();
        var rows = await connection.QueryAsync<Guid>(
            """
            SELECT w.id
            FROM user_states us
            INNER JOIN media_assets ma ON ma.id = us.asset_id
            INNER JOIN editions e ON e.id = ma.edition_id
            INNER JOIN works w ON w.id = e.work_id
            WHERE us.user_id = @listenerId
              AND LOWER(COALESCE(w.media_type, '')) LIKE '%audio%'
            ORDER BY w.id;
            """,
            new { listenerId });
        return rows.ToList();
    }

    private void AssertYearCostWithinBudget(string read, long needsYear, long ownYear, long budget)
    {
        var yearCost = needsYear - ownYear;
        _output.WriteLine(
            "{0}: with audiobooks needing a borrowed year {1:N0}, with their own year {2:N0}; the year costs {3:N0} thousand SQLite instructions (budget {4:N0})",
            read,
            needsYear,
            ownYear,
            yearCost,
            budget);
        Assert.True(yearCost < budget,
            $"The borrowed audiobook year cost {yearCost:N0} thousand SQLite instructions in the {read} at {LibraryListFixture.SmallLeafWorks:N0} works "
            + $"({needsYear:N0} with audiobooks that need it, {ownYear:N0} with their own year); the budget is {budget:N0}. "
            + "A breach usually means the year step is fed the whole library's audiobooks, or builds the whole library's credit list, instead of the rows the read returns.");
    }
}
