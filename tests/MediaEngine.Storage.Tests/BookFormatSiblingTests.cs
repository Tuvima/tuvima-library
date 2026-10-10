using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage.Services;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// One book in several formats must resolve to one Work with several Editions, but only for the
/// same folder + title + author (or a shared calibre UUID), and never across media types.
/// Real temp SQLite, same chain factory the ingestion pipeline uses.
/// </summary>
public sealed class BookFormatSiblingTests : IDisposable
{
    private const string Folder = @"C:\import\books\Anand Giridharadas\Winners Take All (81)\";
    private const string Title = "Winners Take All: The Elite Charade of Changing the World";
    private const string Author = "Anand Giridharadas";

    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly MediaEntityChainFactory _factory;

    public BookFormatSiblingTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_booksib_test_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();

        var resolver = new HierarchyResolver(new WorkRepository(_db), null, new BookFormatSiblingFinder(_db));
        _factory = new MediaEntityChainFactory(_db, resolver);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task EpubAndAzw3InSameFolder_ShareOneWork_WithTwoEditions()
    {
        var epub = await IngestAsync(Folder + "Winners Take All.epub");
        var azw3 = await IngestAsync(Folder + "Winners Take All.azw3");

        Assert.Equal(epub.WorkId, azw3.WorkId);
        Assert.NotEqual(epub.EditionId, azw3.EditionId);
        Assert.Equal(1, Count("SELECT COUNT(*) FROM works"));
        Assert.Equal(2, Count("SELECT COUNT(*) FROM editions WHERE work_id = @w", new { w = epub.WorkId }));
    }

    [Fact]
    public async Task TitlePunctuationAndAuthorOrder_DoNotPreventFormatMatch()
    {
        var epub = await IngestAsync(Folder + "a.epub");
        var azw3 = await IngestAsync(Folder + "a.azw3", "Winners Take All_ The Elite Charade of Changing the World", "Giridharadas, Anand");

        Assert.Equal(epub.WorkId, azw3.WorkId);
    }

    [Fact]
    public async Task ThreeFormatsResolvedUnderAFolderLock_ConvergeOnOneWork()
    {
        // The pipeline serialises siblings with a folder lock (see DurablePipelineTests for the
        // real concurrent run); emulate it here for three formats at once.
        var gate = new SemaphoreSlim(1, 1);
        var results = await Task.WhenAll(new[] { "epub", "azw3", "mobi" }.Select(ext => Task.Run(async () =>
        {
            await gate.WaitAsync();
            try { return await IngestAsync($"{Folder}Book.{ext}"); }
            finally { gate.Release(); }
        })));

        Assert.Single(results.Select(r => r.WorkId).Distinct());
        Assert.Equal(3, Count("SELECT COUNT(*) FROM editions"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM works"));
    }

    [Fact]
    public async Task SameBookInDifferentFolders_StaysTwoWorks()
    {
        var first = await IngestAsync(Folder + "Winners Take All.epub");
        var second = await IngestAsync(@"C:\import\books\Elsewhere\Winners Take All.azw3");

        Assert.NotEqual(first.WorkId, second.WorkId);
        Assert.Equal(2, Count("SELECT COUNT(*) FROM works"));
    }

    [Fact]
    public async Task SameFormatTwice_StaysTwoWorks()
    {
        var first = await IngestAsync(Folder + "Winners Take All.epub");
        var second = await IngestAsync(Folder + "Winners Take All (copy).epub");

        Assert.NotEqual(first.WorkId, second.WorkId);
    }

    [Fact]
    public async Task SecondEpub_DoesNotJoinWorkThatAlreadyHasAnEpub()
    {
        var epub = await IngestAsync(Folder + "Winners Take All.epub");
        var azw3 = await IngestAsync(Folder + "Winners Take All.azw3");
        var epub2 = await IngestAsync(Folder + "Winners Take All v2.epub");

        Assert.Equal(epub.WorkId, azw3.WorkId);
        Assert.NotEqual(epub.WorkId, epub2.WorkId);
    }

    [Fact]
    public async Task DifferentAuthorOrTitle_StaysSeparate()
    {
        var epub = await IngestAsync(Folder + "x.epub");
        var otherAuthor = await IngestAsync(Folder + "x.azw3", Title, "Someone Else");
        var otherTitle = await IngestAsync(Folder + "x.mobi", "A Different Book", Author);

        Assert.Equal(3, new[] { epub.WorkId, otherAuthor.WorkId, otherTitle.WorkId }.Distinct().Count());
    }

    [Fact]
    public async Task BookAndAudiobookInSameFolder_StaySeparateWorks()
    {
        var book = await IngestAsync(Folder + "Winners Take All.epub");
        var audio = await IngestAsync(Folder + "Winners Take All.m4b", mediaType: MediaType.Audiobooks);

        Assert.NotEqual(book.WorkId, audio.WorkId);
        Assert.Equal(1, Count("SELECT COUNT(*) FROM works WHERE media_type = 'Audiobooks'"));
    }

    [Fact]
    public async Task SharedCalibreUuid_JoinsWorkEvenInAnotherFolderWithDifferentMetadata()
    {
        const string uuid = "4a40febf-b65d-4ea7-810d-8317f0706a88";
        var epub = await IngestAsync(@"C:\lib\A\Book.epub", "Original Title", "Author One", uuid);
        var azw3 = await IngestAsync(@"C:\lib\B\Book.azw3", "Retitled", "Author Two", uuid);
        var unrelated = await IngestAsync(@"C:\lib\C\Book.mobi", "Original Title", "Author One",
            "11111111-1111-4111-8111-111111111111");

        Assert.Equal(epub.WorkId, azw3.WorkId);
        Assert.NotEqual(epub.WorkId, unrelated.WorkId);
    }

    [Fact]
    public async Task BooksInASeries_StillResolveThroughSeriesParent()
    {
        var first = await IngestAsync(Folder + "One.epub", series: "Saga", position: "1");
        var second = await IngestAsync(Folder + "One.azw3", series: "Saga", position: "1");

        Assert.Equal(first.WorkId, second.WorkId);
        Assert.NotNull(first.ParentWorkId);
    }

    // Mirrors the pipeline: claims + canonical values first, then the chain, then the asset row.
    private async Task<Ingested> IngestAsync(
        string path,
        string title = Title,
        string author = Author,
        string? calibreUuid = null,
        MediaType mediaType = MediaType.Books,
        string? series = null,
        string? position = null)
    {
        var assetId = Guid.NewGuid();
        var metadata = new Dictionary<string, string> { ["title"] = title, ["author"] = author };
        if (calibreUuid is not null) { metadata["calibre_uuid"] = calibreUuid; }
        if (series is not null) { metadata["series"] = series; metadata["series_position"] = position!; }

        using (var conn = _db.CreateConnection())
        {
            conn.Execute(
                """
                INSERT OR IGNORE INTO metadata_providers (id, name, version, is_enabled)
                VALUES (@provider, 'local_processor', '1', 1);
                INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value, confidence, claimed_at)
                VALUES (@c1, @asset, @provider, 'title', @title, 1, '2026-01-01'),
                       (@c2, @asset, @provider, 'author', @author, 1, '2026-01-01');
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@asset, 'title', @title, '2026-01-01');
                """,
                new
                {
                    provider = WellKnownProviders.LocalProcessor,
                    c1 = Guid.NewGuid(),
                    c2 = Guid.NewGuid(),
                    asset = assetId,
                    title,
                    author,
                });
            if (calibreUuid is not null)
            {
                conn.Execute(
                    "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@asset, 'calibre_uuid', @uuid, '2026-01-01');",
                    new { asset = assetId, uuid = calibreUuid });
            }
        }

        var editionId = await _factory.EnsureEntityChainAsync(mediaType, metadata, path);

        using var conn2 = _db.CreateConnection();
        conn2.Execute(
            "INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status) VALUES (@asset, @edition, @hash, @path, 'Normal');",
            new { asset = assetId, edition = editionId, hash = assetId.ToString("N"), path });
        var work = conn2.QuerySingle<(Guid WorkId, Guid? ParentId)>(
            "SELECT w.id AS WorkId, w.parent_work_id AS ParentId FROM editions e JOIN works w ON w.id = e.work_id WHERE e.id = @edition;",
            new { edition = editionId });
        return new Ingested(work.WorkId, editionId, work.ParentId);
    }

    private int Count(string sql, object? args = null)
    {
        using var conn = _db.CreateConnection();
        return conn.ExecuteScalar<int>(sql, args);
    }

    private sealed record Ingested(Guid WorkId, Guid EditionId, Guid? ParentWorkId);
}
