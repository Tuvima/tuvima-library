using Dapper;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// Which kinds of title can take part in a Match-first move (details overlay spec, per-type pass). A song, an episode and an audiobook
/// part can; a movie, a book, a comic issue, a video, a whole show, season, album or audiobook cannot, and the Engine says no instead of
/// offering a place. Real temp SQLite.
/// </summary>
public sealed class ItemMovePerTypeTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly ItemMoveRepository _repository;

    public ItemMovePerTypeTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_itemmove_types_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _repository = new ItemMoveRepository(_db);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { /* temp database cleanup is best effort */ }
        try { File.Delete(_dbPath); } catch { /* temp database cleanup is best effort */ }
    }

    [Theory]
    [InlineData("Music")]
    [InlineData("TV")]
    [InlineData("Audiobooks")]
    public async Task APartOfAListedPlace_CanBeMoved_ASongAnEpisodeAnAudiobookPart(string mediaType)
    {
        var parent = Insert(mediaType, "parent", parent: null);
        var part = Insert(mediaType, "child", parent);

        Assert.NotNull(await _repository.GetSourceAsync(part));
    }

    [Theory]
    [InlineData("Movies")]
    [InlineData("Books")]
    [InlineData("Comics")]
    [InlineData("Videos")]
    public async Task AStandaloneTitle_IsNeverOfferedAMove_MovieBookComicVideo(string mediaType)
    {
        var standalone = Insert(mediaType, "standalone", parent: null);

        Assert.Null(await _repository.GetSourceAsync(standalone));
    }

    [Theory]
    [InlineData("Music")]
    [InlineData("TV")]
    [InlineData("Audiobooks")]
    public async Task AWholeShowSeasonAlbumOrAudiobook_IsNeverMoved(string mediaType)
    {
        var parent = Insert(mediaType, "parent", parent: null);
        var nested = Insert(mediaType, "parent", parent);

        Assert.Null(await _repository.GetSourceAsync(parent));
        Assert.Null(await _repository.GetSourceAsync(nested));
    }

    [Fact]
    public async Task ABookInASeries_IsNotMovedByMatchYet_ItStaysWhereTheSeriesRuleSetIt()
    {
        // Series positions come from the Series manifest (spec D12 for books, comics, movies); a manual move into one is held back
        // until a hand-made link can survive the next manifest refresh. See the inline-media-editing doc.
        var book = Insert("Books", "child", Insert("Books", "parent", parent: null));

        Assert.Null(await _repository.GetSourceAsync(book));
    }

    private Guid Insert(string mediaType, string kind, Guid? parent)
    {
        var id = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO works(id, media_type, work_kind, parent_work_id, ordinal, ordinal_sort, ownership, wikidata_status)
            VALUES(@id, @mediaType, @kind, @parent, 1, 1, 'Owned', 'pending');
            INSERT INTO editions(id, work_id, format_label) VALUES(@edition, @id, 'FLAC');
            """, new { id, mediaType, kind, parent, edition = Guid.NewGuid() });
        return id;
    }
}
