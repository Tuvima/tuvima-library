using Dapper;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Tests;

// Book bookmarks live in the one bookmark table (Global bookmarks spec D13): Promote makes one Global and replaces personal ones in the
// same paragraph, Everyone's lists other people's, and film queries never see them.
public sealed class BookBookmarkStorageTests : IDisposable
{
    private const string Here = "epubcfi(/6/14!/4/2/6/1:0)";
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"book_bookmarks_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly ReaderBookmarkRepository _repository;
    private readonly Guid _asset;

    public BookBookmarkStorageTests()
    {
        _db = new DatabaseConnection(_path);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _repository = new ReaderBookmarkRepository(_db);
        _asset = AddBook();
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try { File.Delete(file); }
            catch (IOException) { /* cleaned by the operating system later */ }
        }
    }

    [Fact]
    public async Task Promote_MakesItGlobal_ReplacesPersonalBookmarksInTheSameParagraph_AndKeepsOthers()
    {
        var maya = AddProfile("Maya");
        var theo = AddProfile("Theo");
        var mine = await AddAsync(maya, Here);
        var sameParagraph = await AddAsync(theo, "epubcfi(/6/14!/4/2/6/1:250)");
        var elsewhere = await AddAsync(theo, "epubcfi(/6/20!/4/2/6/1:0)");

        var promoted = await _repository.PromoteAsync(mine.Id, "The reveal");

        Assert.True(promoted!.IsGlobal);
        Assert.Equal("The reveal", promoted.Label);
        Assert.Null(await _repository.FindByIdAsync(sameParagraph.Id));
        Assert.NotNull(await _repository.FindByIdAsync(elsewhere.Id));
        Assert.Null(await _repository.PromoteAsync(mine.Id, null)); // already Global
    }

    [Fact]
    public async Task AGlobalBookmark_ShowsForEveryProfile_AndEveryonesListsPersonalOnesWithTheirOwner()
    {
        var maya = AddProfile("Maya");
        var theo = AddProfile("Theo");
        var global = await AddAsync(maya, Here);
        await _repository.PromoteAsync(global.Id, null);
        await AddAsync(theo, "epubcfi(/6/20!/4/2/6/1:0)");

        var theoSees = await _repository.ListByAssetAsync(theo.ToString("D"), _asset);
        var everyones = await _repository.ListEveryonesByAssetAsync(_asset);

        Assert.Contains(theoSees, bookmark => bookmark.Id == global.Id && bookmark.IsGlobal);
        var personal = Assert.Single(everyones);
        Assert.Equal("Theo", personal.OwnerName);
    }

    [Fact]
    public async Task FilmQueries_NeverSeeBookBookmarks()
    {
        var maya = AddProfile("Maya");
        await AddAsync(maya, Here);
        var film = new MediaEngine.Storage.Playback.AudiobookBookmarkRepository(_db);

        using var conn = _db.CreateConnection();
        var workId = conn.QuerySingle<Guid>("SELECT work_id FROM audiobook_bookmarks LIMIT 1;");
        Assert.Empty(await film.GetByWorkAsync(maya, workId));
        Assert.Empty(await film.GetEveryonesByWorkAsync(workId));
    }

    private async Task<ReaderBookmark> AddAsync(Guid profile, string cfi)
    {
        var bookmark = new ReaderBookmark
        {
            Id = Guid.NewGuid(),
            UserId = profile.ToString("D"),
            AssetId = _asset,
            ChapterIndex = 0,
            CfiPosition = cfi,
            Label = "Spot",
            CreatedAt = DateTime.UtcNow,
        };
        await _repository.InsertAsync(bookmark);
        return bookmark;
    }

    private Guid AddProfile(string name)
    {
        var id = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute("INSERT INTO profiles (id, display_name, created_at) VALUES (@id, @name, @now);", new { id, name, now = DateTimeOffset.UtcNow.ToString("O") });
        return id;
    }

    private Guid AddBook()
    {
        var collection = Guid.NewGuid();
        var work = Guid.NewGuid();
        var edition = Guid.NewGuid();
        var asset = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute("INSERT INTO collections (id, created_at) VALUES (@collection, @now);", new { collection, now = DateTimeOffset.UtcNow.ToString("O") });
        conn.Execute("INSERT INTO works (id, collection_id, media_type, work_kind, is_catalog_only) VALUES (@work, @collection, 'Books', 'standalone', 0);", new { work, collection });
        conn.Execute("INSERT INTO editions (id, work_id) VALUES (@edition, @work);", new { edition, work });
        conn.Execute("INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status) VALUES (@asset, @edition, @hash, '/library/book.epub', 'Normal');", new { asset, edition, hash = $"hash_{asset:N}" });
        return asset;
    }
}
