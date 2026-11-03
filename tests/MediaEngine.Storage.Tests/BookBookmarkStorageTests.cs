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

    [Fact]
    public async Task ApplySet_AsAdministrator_AddsGlobal_ReplacingPersonalBookmarksInTheSameParagraph_InOneWrite()
    {
        var maya = AddProfile("Maya");
        var theo = AddProfile("Theo");
        var mine = await AddAsync(maya, Here);
        var theirs = await AddAsync(theo, "epubcfi(/6/14!/4/2/6/1:250)");
        var elsewhere = await AddAsync(maya, "epubcfi(/6/20!/4/2/6/1:0)");
        var oldGlobal = await AddAsync(maya, "epubcfi(/6/30!/4/2/6/1:0)", global: true);

        var outcome = await _repository.ApplySetAsync(maya, _asset,
            [new ReaderSetBookmark("epubcfi(/6/14!/4/2/6/1:90)", 6, "From the file")],
            new[] { oldGlobal.Id }, addAsGlobal: true, replaceGlobalSet: false);

        Assert.Equal(1, outcome.Added);
        var list = await _repository.ListByAssetAsync(maya.ToString("D"), _asset);
        Assert.Contains(list, item => item.IsGlobal && item.CfiPosition == "epubcfi(/6/14!/4/2/6/1:90)");
        Assert.DoesNotContain(list, item => item.Id == mine.Id || item.Id == oldGlobal.Id);
        Assert.Null(await _repository.FindByIdAsync(theirs.Id));
        Assert.NotNull(await _repository.FindByIdAsync(elsewhere.Id));
    }

    [Fact]
    public async Task ApplySet_ReplaceWholeGlobalSet_RemovesOldGlobal_ButNeverPersonalBookmarks()
    {
        var maya = AddProfile("Maya");
        var personal = await AddAsync(maya, "epubcfi(/6/20!/4/2/6/1:0)");
        var oldGlobal = await AddAsync(maya, "epubcfi(/6/30!/4/2/6/1:0)", global: true);

        await _repository.ApplySetAsync(maya, _asset,
            [new ReaderSetBookmark("epubcfi(/6/40!/4/2/6/1:0)", 19, null)],
            [], addAsGlobal: true, replaceGlobalSet: true);

        Assert.Null(await _repository.FindByIdAsync(oldGlobal.Id));
        Assert.NotNull(await _repository.FindByIdAsync(personal.Id));
    }

    [Fact]
    public async Task ApplySet_ForAPerson_AddsOnlyToTheirOwn_AndCannotDeleteGlobalOrOthersBookmarks()
    {
        var maya = AddProfile("Maya");
        var theo = AddProfile("Theo");
        var global = await AddAsync(maya, "epubcfi(/6/30!/4/2/6/1:0)", global: true);
        var others = await AddAsync(theo, "epubcfi(/6/20!/4/2/6/1:0)");

        await _repository.ApplySetAsync(theo, _asset,
            [new ReaderSetBookmark("epubcfi(/6/40!/4/2/6/1:0)", 19, "Mine")],
            new[] { global.Id, others.Id }, addAsGlobal: false, replaceGlobalSet: false);

        Assert.NotNull(await _repository.FindByIdAsync(global.Id));
        Assert.Null(await _repository.FindByIdAsync(others.Id)); // Theo's own bookmark may be replaced
        var mine = await _repository.ListByAssetAsync(theo.ToString("D"), _asset);
        Assert.Contains(mine, item => !item.IsGlobal && item.Label == "Mine");
    }

    private async Task<ReaderBookmark> AddAsync(Guid profile, string cfi, bool global = false)
    {
        var bookmark = new ReaderBookmark
        {
            Id = Guid.NewGuid(),
            UserId = profile.ToString("D"),
            AssetId = _asset,
            ChapterIndex = 0,
            CfiPosition = cfi,
            Label = "Spot",
            IsGlobal = global,
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
