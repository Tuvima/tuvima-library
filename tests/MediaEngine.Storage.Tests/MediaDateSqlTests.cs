using Dapper;
using MediaEngine.Domain.Models;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// An audiobook with no original year of its own borrows the year of the matching book: a book (not an audiobook) with
/// the same title and an author in common. These tests drive the set-based form (<c>RelatedBookOriginalYearCte</c>)
/// through the library list and the item detail, which must agree.
/// </summary>
public sealed class MediaDateSqlTests : IDisposable
{
    private const string Author = "Frank Herbert";

    private readonly string _dbPath;
    private readonly DatabaseConnection _db;

    public MediaDateSqlTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima-media-date-sql-{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // A pooled connection can still hold the temp file; the OS temp folder clears it later.
        }
    }

    [Fact]
    public async Task Audiobook_WithoutAYear_BorrowsTheMatchingBooksYear()
    {
        var book = await AddBookAsync("Dune", year: "1965", authors: [Author]);
        var audiobook = await AddAudiobookAsync("Dune", authors: [Author]);

        await AssertYearAsync(audiobook, "1965");
        await AssertYearAsync(book, "1965");
    }

    [Fact]
    public async Task Audiobook_MatchesTheBookIgnoringCaseAndSurroundingSpaces()
    {
        await AddBookAsync("The Hobbit", year: "1937", authors: ["J. R. R. Tolkien"]);
        var audiobook = await AddAudiobookAsync("  THE HOBBIT  ", authors: ["J. R. R. Tolkien"]);

        await AssertYearAsync(audiobook, "1937");
    }

    [Fact]
    public async Task Audiobook_DoesNotBorrowFromABookWithTheSameTitleByAnotherAuthor()
    {
        await AddBookAsync("Dune", year: "1965", authors: ["Someone Else"]);
        var audiobook = await AddAudiobookAsync("Dune", authors: [Author]);

        await AssertYearAsync(audiobook, null);
    }

    [Fact]
    public async Task Audiobook_DoesNotBorrowFromABookByTheSameAuthorWithAnotherTitle()
    {
        await AddBookAsync("Dune Messiah", year: "1969", authors: [Author]);
        var audiobook = await AddAudiobookAsync("Dune", authors: [Author]);

        await AssertYearAsync(audiobook, null);
    }

    [Fact]
    public async Task Audiobook_WithItsOwnOriginalYear_KeepsIt()
    {
        await AddBookAsync("Dune", year: "1965", authors: [Author]);
        var audiobook = await AddAudiobookAsync("Dune", authors: [Author], ownYear: "2007");

        await AssertYearAsync(audiobook, "2007");
    }

    [Fact]
    public async Task Audiobook_WithTwoMatchingBooks_TakesTheYearOfTheLowerWorkId()
    {
        var first = await AddBookAsync("Dune", year: "1965", authors: [Author]);
        var second = await AddBookAsync("Dune", year: "1984", authors: [Author]);
        var lower = await LowerWorkIdAsync(first.WorkId, second.WorkId);
        var expected = lower == first.WorkId ? "1965" : "1984";
        var audiobook = await AddAudiobookAsync("Dune", authors: [Author]);

        await AssertYearAsync(audiobook, expected);
    }

    [Fact]
    public async Task Audiobook_WhenTheLowerWorkIdBookHasNoYear_BorrowsNothing()
    {
        var first = await AddBookAsync("Dune", year: "1965", authors: [Author]);
        var second = await AddBookAsync("Dune", year: "1984", authors: [Author]);
        var lower = await LowerWorkIdAsync(first.WorkId, second.WorkId);
        // Only the first match counts, so a later match with a year does not rescue it.
        await SetYearAsync(lower, null);
        var audiobook = await AddAudiobookAsync("Dune", authors: [Author]);

        await AssertYearAsync(audiobook, null);
    }

    [Fact]
    public async Task Audiobook_IsNeverGivenTheYearOfAnotherAudiobookWithTheSameTitleAndAuthor()
    {
        var audiobook = await AddAudiobookAsync("Dune", authors: [Author]);
        await AddAudiobookAsync("Dune", authors: [Author], ownYear: "1999");

        await AssertYearAsync(audiobook, null);
    }

    [Fact]
    public async Task Audiobook_MatchesWhetherTheAuthorCreditSitsOnTheWorkOrOnTheFile()
    {
        await AddBookAsync("Dune", year: "1965", authors: [Author], creditsOnFile: false);
        var fileCredit = await AddAudiobookAsync("Dune", authors: [Author], creditsOnFile: true);

        await AssertYearAsync(fileCredit, "1965");

        await AddBookAsync("Emma", year: "1815", authors: ["Jane Austen"], creditsOnFile: true);
        var workCredit = await AddAudiobookAsync("Emma", authors: ["Jane Austen"], creditsOnFile: false);

        await AssertYearAsync(workCredit, "1815");
    }

    [Fact]
    public async Task Audiobooks_ListedTogether_EachBorrowTheirOwnBooksYear()
    {
        await AddBookAsync("Dune", year: "1965", authors: [Author]);
        await AddBookAsync("Emma", year: "1815", authors: ["Jane Austen"]);
        await AddBookAsync("Persuasion", year: "1817", authors: ["Jane Austen"]);
        var dune = await AddAudiobookAsync("Dune", authors: [Author]);
        var emma = await AddAudiobookAsync("emma", authors: ["Jane Austen"]);
        var unmatched = await AddAudiobookAsync("Northanger Abbey", authors: ["Jane Austen"]);

        var page = await new LibraryItemRepository(_db).GetPageAsync(new LibraryItemQuery(IncludeAll: true));

        Assert.Equal("1965", YearOf(page, dune));
        Assert.Equal("1815", YearOf(page, emma));
        Assert.Null(YearOf(page, unmatched));
    }

    private async Task AssertYearAsync(SeededWork work, string? expected)
    {
        var repository = new LibraryItemRepository(_db);

        var page = await repository.GetPageAsync(new LibraryItemQuery(IncludeAll: true));
        Assert.Equal(expected, YearOf(page, work));

        var detail = await repository.GetDetailAsync(work.WorkId);
        Assert.NotNull(detail);
        Assert.Equal(expected, detail.Year);
    }

    private static string? YearOf(LibraryItemsPage page, SeededWork work) =>
        page.Items.Single(item => item.EntityId == work.WorkId).Year;

    private Task<SeededWork> AddBookAsync(string title, string? year, string[] authors, bool creditsOnFile = false) =>
        AddWorkAsync("Books", title, year, authors, creditsOnFile);

    private Task<SeededWork> AddAudiobookAsync(
        string title,
        string[] authors,
        string? ownYear = null,
        bool creditsOnFile = false) =>
        AddWorkAsync("Audiobooks", title, ownYear, authors, creditsOnFile);

    private async Task<SeededWork> AddWorkAsync(
        string mediaType,
        string title,
        string? year,
        string[] authors,
        bool creditsOnFile)
    {
        using var conn = _db.CreateConnection();
        var collectionId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        await conn.ExecuteAsync("""
            INSERT INTO collections (id, created_at) VALUES (@collectionId, datetime('now'));
            INSERT INTO works (id, collection_id, media_type) VALUES (@workId, @collectionId, @mediaType);
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
                VALUES (@assetId, @editionId, @hash, @filePath, 'Normal');
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@assetId, 'title', @title, datetime('now'));
            """,
            new
            {
                collectionId,
                workId,
                editionId,
                assetId,
                mediaType,
                title,
                hash = $"hash_{assetId:N}",
                filePath = $"/library/{assetId:N}.bin",
            });

        var work = new SeededWork(workId, assetId);
        if (year is not null)
        {
            await SetYearAsync(workId, year);
        }

        var creditEntity = creditsOnFile ? assetId : workId;
        for (var ordinal = 0; ordinal < authors.Length; ordinal++)
        {
            await conn.ExecuteAsync("""
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
                VALUES (@creditEntity, 'author', @ordinal, @author);
                INSERT INTO persons (id, name, created_at)
                SELECT @personId, @author, datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM persons WHERE name = @author COLLATE NOCASE);
                """,
                new { creditEntity, ordinal, author = authors[ordinal], personId = Guid.NewGuid() });
        }

        return work;
    }

    private async Task SetYearAsync(Guid workId, string? year)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "DELETE FROM canonical_values WHERE entity_id = @workId AND key = 'original_publication_year';",
            new { workId });
        if (year is not null)
        {
            await conn.ExecuteAsync("""
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@workId, 'original_publication_year', @year, datetime('now'));
                """,
                new { workId, year });
        }
    }

    private async Task<Guid> LowerWorkIdAsync(Guid first, Guid second)
    {
        using var conn = _db.CreateConnection();
        var firstIsLower = await conn.ExecuteScalarAsync<long>(
            "SELECT CASE WHEN @first < @second THEN 1 ELSE 0 END;",
            new { first, second });
        return firstIsLower == 1 ? first : second;
    }

    private sealed record SeededWork(Guid WorkId, Guid AssetId);
}
