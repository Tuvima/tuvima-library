using System.Globalization;
using Dapper;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Search;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

/// <summary>
/// An audiobook without an original year of its own borrows the year of the book with the same title and author. The
/// collection editor's details and "add media" search, a person's credits and the series search all show that year; these
/// tests pin the value each of the four reads gives, including a page past the first and the newest-first order of a
/// person's credits.
/// </summary>
public sealed class AudiobookYearCollectionReadsTests : IDisposable
{
    private const string Author = "Frank Herbert";

    private readonly string _dbPath;
    private readonly DatabaseConnection _db;

    public AudiobookYearCollectionReadsTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_audiobook_year_collection_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        ReadyWorks.TreatEveryWorkAsReady(_db);
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
    public async Task CollectionEditorDetails_AudiobookWithoutAYear_ShowsTheMatchingBooksYear()
    {
        await AddWorkAsync("Books", "Dune", "1965", Author);
        var audiobook = await AddWorkAsync("Audiobooks", "Dune", null, Author);

        var items = await new CollectionMediaLookupReadService(_db)
            .ResolveMetadataAsync([audiobook], CancellationToken.None);

        Assert.Equal("1965", Assert.Single(items).Year);
    }

    [Fact]
    public async Task CollectionEditorDetails_AudiobookWithItsOwnYear_KeepsIt()
    {
        await AddWorkAsync("Books", "Dune", "1965", Author);
        var audiobook = await AddWorkAsync("Audiobooks", "Dune", "2007", Author);

        var items = await new CollectionMediaLookupReadService(_db)
            .ResolveMetadataAsync([audiobook], CancellationToken.None);

        Assert.Equal("2007", Assert.Single(items).Year);
    }

    [Fact]
    public async Task CollectionEditorDetails_AudiobookWithNoMatchingBook_HasNoYear()
    {
        await AddWorkAsync("Books", "Dune Messiah", "1969", Author);
        var audiobook = await AddWorkAsync("Audiobooks", "Dune", null, Author);

        var items = await new CollectionMediaLookupReadService(_db)
            .ResolveMetadataAsync([audiobook], CancellationToken.None);

        Assert.Null(Assert.Single(items).Year);
    }

    [Fact]
    public async Task AddMediaSearch_AudiobookWithoutAYear_ShowsTheMatchingBooksYear()
    {
        await AddWorkAsync("Books", "Dune", "1965", Author);
        var audiobook = await AddWorkAsync("Audiobooks", "Dune", null, Author);

        var found = await LookupAsync("Audiobooks", offset: 0, limit: 24, audiobook);

        var item = Assert.Single(found);
        Assert.Equal(audiobook, item.WorkId);
        Assert.Equal("1965", item.Year);
    }

    [Fact]
    public async Task AddMediaSearch_AudiobookWithItsOwnYear_KeepsIt()
    {
        await AddWorkAsync("Books", "Dune", "1965", Author);
        var audiobook = await AddWorkAsync("Audiobooks", "Dune", "2007", Author);

        var found = await LookupAsync("Audiobooks", offset: 0, limit: 24, audiobook);

        Assert.Equal("2007", Assert.Single(found).Year);
    }

    [Fact]
    public async Task AddMediaSearch_AudiobookOnTheSecondPage_GetsItsBorrowedYear()
    {
        await AddWorkAsync("Books", "Beta", "1950", Author);
        await AddWorkAsync("Books", "Gamma", "1960", Author);
        var alpha = await AddWorkAsync("Audiobooks", "Alpha", null, Author);
        var beta = await AddWorkAsync("Audiobooks", "Beta", null, Author);
        var gamma = await AddWorkAsync("Audiobooks", "Gamma", null, Author);

        var secondPage = await LookupAsync("Audiobooks", offset: 2, limit: 2, alpha, beta, gamma);

        var item = Assert.Single(secondPage);
        Assert.Equal(gamma, item.WorkId);
        Assert.Equal("1960", item.Year);
    }

    [Fact]
    public async Task AddMediaSearch_PagesStayInTitleOrderAndEachAudiobookBorrowsItsOwnBooksYear()
    {
        await AddWorkAsync("Books", "Beta", "1950", Author);
        await AddWorkAsync("Books", "Gamma", "1960", Author);
        var alpha = await AddWorkAsync("Audiobooks", "Alpha", null, Author);
        var beta = await AddWorkAsync("Audiobooks", "beta", null, Author);
        var gamma = await AddWorkAsync("Audiobooks", "Gamma", null, Author);

        var firstPage = await LookupAsync("Audiobooks", offset: 0, limit: 2, alpha, beta, gamma);

        Assert.Equal([alpha, beta], firstPage.Select(item => item.WorkId));
        Assert.Equal([null, "1950"], firstPage.Select(item => item.Year));
    }

    [Fact]
    public async Task AddMediaSearch_BookKeepsItsOwnYear()
    {
        var book = await AddWorkAsync("Books", "Dune", "1965", Author);
        await AddWorkAsync("Audiobooks", "Dune", null, Author);

        var found = await LookupAsync("Books", offset: 0, limit: 24, book);

        Assert.Equal("1965", Assert.Single(found).Year);
    }

    [Fact]
    public async Task PersonCredits_AudiobookWithoutAYear_ShowsTheMatchingBooksYear()
    {
        await AddWorkAsync("Books", "Dune", "1965", Author);
        await AddWorkAsync("Audiobooks", "Dune", null, Author);
        var personId = await PersonIdAsync(Author);

        var credits = await CreateCreditService().GetLibraryCreditsAsync(personId, CancellationToken.None);

        var audiobook = Assert.Single(credits, credit => credit.MediaType == "Audiobooks");
        Assert.Equal("1965", audiobook.Year);
    }

    [Fact]
    public async Task PersonCredits_AudiobookWithItsOwnYear_KeepsIt()
    {
        await AddWorkAsync("Books", "Dune", "1965", Author);
        await AddWorkAsync("Audiobooks", "Dune", "2007", Author);
        var personId = await PersonIdAsync(Author);

        var credits = await CreateCreditService().GetLibraryCreditsAsync(personId, CancellationToken.None);

        var audiobook = Assert.Single(credits, credit => credit.MediaType == "Audiobooks");
        Assert.Equal("2007", audiobook.Year);
    }

    [Fact]
    public async Task PersonCredits_WithABorrowedYearAmongTheRows_ListNewestYearFirst()
    {
        await AddWorkAsync("Books", "Dune", "1965", Author);
        await AddWorkAsync("Books", "Emma", "1815", Author);
        await AddWorkAsync("Books", "Chapterhouse", "1985", Author);
        await AddWorkAsync("Audiobooks", "Dune", null, Author);
        await AddWorkAsync("Audiobooks", "Emma", "1999", Author);
        await AddWorkAsync("Audiobooks", "Chapterhouse", null, Author);
        var personId = await PersonIdAsync(Author);

        var credits = await CreateCreditService().GetLibraryCreditsAsync(personId, CancellationToken.None);

        // Rows with the same year are ordered by title; the same title lists in whichever order the library gives it.
        Assert.Equal(
            ["1999", "1985", "1985", "1965", "1965", "1815"],
            credits.Select(credit => credit.Year).ToList());
        Assert.Equal(
            ["Emma", "Chapterhouse", "Chapterhouse", "Dune", "Dune", "Emma"],
            credits.Select(credit => credit.Title).ToList());
    }

    [Fact]
    public async Task SeriesSearch_AudiobookWithoutAYear_ShowsTheMatchingBooksYear()
    {
        await AddWorkAsync("Books", "Dune", "1965", Author);
        var audiobook = await AddWorkAsync("Audiobooks", "Dune", null, Author);

        var results = await SearchThroughTheIndexAsync("Dune");

        Assert.Equal("1965", Assert.Single(results, result => result.WorkId == audiobook).Year);
    }

    [Fact]
    public async Task SeriesSearch_AudiobookWithItsOwnYear_KeepsIt()
    {
        var book = await AddWorkAsync("Books", "Dune", "1965", Author);
        var audiobook = await AddWorkAsync("Audiobooks", "Dune", "2007", Author);

        var results = await SearchThroughTheIndexAsync("Dune");

        Assert.Equal("2007", Assert.Single(results, result => result.WorkId == audiobook).Year);
        Assert.Equal("1965", Assert.Single(results, result => result.WorkId == book).Year);
    }

    [Fact]
    public async Task SeriesSearch_AudiobookAtTheEndOfAFullPage_ShowsTheMatchingBooksYear()
    {
        for (var number = 1; number <= 25; number++)
        {
            await AddWorkAsync("Books", $"Saga {number:00}", (1900 + number).ToString(CultureInfo.InvariantCulture), Author);
        }

        // Sorted by title, the book and the audiobook "Saga 19" are the 19th and 20th of 26 matches: the last rows of the page.
        var audiobook = await AddWorkAsync("Audiobooks", "Saga 19", null, Author);

        var results = await SearchThroughTheIndexAsync("Saga");

        Assert.Equal(20, results.Count);
        Assert.Equal("1919", Assert.Single(results, result => result.WorkId == audiobook).Year);
    }

    private async Task<List<SearchResultDto>> SearchThroughTheIndexAsync(string query)
    {
        var index = new SearchIndexRepository(_db);
        await index.RebuildAsync(CancellationToken.None);
        return await new CollectionSearchReadService(_db, index).SearchAsync(query, CancellationToken.None);
    }

    private async Task<List<CollectionMediaLookupDto>> LookupAsync(
        string mediaType,
        int offset,
        int limit,
        params Guid[] allowedWorkIds) =>
        await new CollectionMediaLookupReadService(_db).LookupAsync(
            null,
            mediaType,
            new HashSet<Guid>(),
            allowedWorkIds.ToHashSet(),
            offset,
            limit,
            CancellationToken.None);

    private PersonCreditReadService CreateCreditService() =>
        new(new CanonicalValueArrayRepository(_db), new PersonRepository(_db), _db);

    private async Task<Guid> PersonIdAsync(string name)
    {
        using var conn = _db.CreateConnection();
        return await conn.ExecuteScalarAsync<Guid>(
            "SELECT id FROM persons WHERE name = @name COLLATE NOCASE;",
            new { name });
    }

    private async Task<Guid> AddWorkAsync(string mediaType, string title, string? year, string author)
    {
        using var conn = _db.CreateConnection();
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        await conn.ExecuteAsync("""
            INSERT INTO works (id, media_type, work_kind) VALUES (@workId, @mediaType, 'standalone');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
                VALUES (@assetId, @editionId, @hash, @filePath, 'Normal');
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@assetId, 'title', @title, datetime('now'));
            INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
                VALUES (@workId, 'author', 0, @author);
            INSERT INTO persons (id, name, created_at)
                SELECT @personId, @author, datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM persons WHERE name = @author COLLATE NOCASE);
            """,
            new
            {
                workId,
                editionId,
                assetId,
                mediaType,
                title,
                author,
                personId = Guid.NewGuid(),
                hash = $"hash_{assetId:N}",
                filePath = $"/library/{assetId:N}.bin",
            });

        if (year is not null)
        {
            await conn.ExecuteAsync("""
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@workId, 'original_publication_year', @year, datetime('now'));
                """,
                new { workId, year });
        }

        return workId;
    }
}
