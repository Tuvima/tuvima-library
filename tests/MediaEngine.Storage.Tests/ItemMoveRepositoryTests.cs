using Dapper;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// Match first, then move (details overlay spec D12 to D16): a song joins another album only by taking an entry of its list, or by
/// being added anyway with no number and a "Not matched" tag. Real temp SQLite.
/// </summary>
public sealed class ItemMoveRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly ItemMoveRepository _repository;

    public ItemMoveRepositoryTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_itemmove_test_{Guid.NewGuid():N}.db");
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

    [Fact]
    public async Task Destinations_OnlyOfferParentsOfTheSameKind_AndNotTheCurrentOne()
    {
        var best = Album("The Best Of", out var song, ownedTrack: ("Paranoid Android", 1));
        var ok = Album("OK Computer");
        var tvShow = Parent("TV", "A Show", parent: null);

        var source = await _repository.GetSourceAsync(song);
        var found = await _repository.FindDestinationsAsync(source!, null, 25);

        Assert.Equal([ok], found.Select(d => d.Id));
        Assert.DoesNotContain(found, d => d.Id == best || d.Id == tvShow);
    }

    [Fact]
    public async Task Match_TakesTheEmptyEntry_SoTheSongGetsThatTrackNumber_AndOldPlaceKeepsAnEmptyEntry()
    {
        Album("The Best Of", out var song, ownedTrack: ("Paranoid Android", 1));
        var ok = Album("OK Computer");
        var entry = CatalogTrack(ok, "Paranoid Android", 2);

        var outcome = await _repository.CommitAsync(new ItemMoveCommand(song, ok, entry, ItemMoveMode.MatchEntry, 1, false));

        Assert.Equal(ItemMoveOutcome.Moved, outcome);
        using var conn = _db.CreateConnection();
        Assert.Equal(entry, conn.ExecuteScalar<Guid>("SELECT work_id FROM editions WHERE work_id = @entry;", new { entry }));
        Assert.Equal((2, "child", "Owned"), conn.QuerySingle<(int Ordinal, string Kind, string Owned)>(
            "SELECT ordinal AS Ordinal, work_kind AS Kind, ownership AS Owned FROM works WHERE id = @entry;", new { entry }));
        Assert.Equal("catalog", conn.ExecuteScalar<string>("SELECT work_kind FROM works WHERE id = @song;", new { song }));
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM media_file_write_intents;"));
    }

    [Fact]
    public async Task Match_RefusesATakenEntry_AndNothingMoves()
    {
        Album("The Best Of", out var song, ownedTrack: ("Paranoid Android", 1));
        var ok = Album("OK Computer", out _, ownedTrack: ("Paranoid Android", 2));
        var taken = EntriesOf(ok).Single();

        var outcome = await _repository.CommitAsync(new ItemMoveCommand(song, ok, taken, ItemMoveMode.MatchEntry, 1, false));

        Assert.Equal(ItemMoveOutcome.Changed, outcome);
        using var conn = _db.CreateConnection();
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM editions WHERE work_id = @song;", new { song }));
    }

    [Fact]
    public async Task AnotherVersion_AddsTheFileAsASecondEditionOfTheTakenEntry()
    {
        Album("The Best Of", out var song, ownedTrack: ("Paranoid Android", 1));
        var ok = Album("OK Computer", out _, ownedTrack: ("Paranoid Android", 2));
        var taken = EntriesOf(ok).Single();

        var outcome = await _repository.CommitAsync(new ItemMoveCommand(song, ok, taken, ItemMoveMode.AnotherVersion, 1, true));

        Assert.Equal(ItemMoveOutcome.Moved, outcome);
        using var conn = _db.CreateConnection();
        Assert.Equal(2, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM editions WHERE work_id = @taken;", new { taken }));
    }

    [Fact]
    public async Task AddAnyway_JoinsTheAlbumWithNoNumberAndANotMatchedTag_BorrowingNothing()
    {
        Album("The Best Of", out var song, ownedTrack: ("Paranoid Android", 1));
        var ok = Album("OK Computer");

        var outcome = await _repository.CommitAsync(new ItemMoveCommand(song, ok, null, ItemMoveMode.AddAnyway, 1, false));

        Assert.Equal(ItemMoveOutcome.Moved, outcome);
        using var conn = _db.CreateConnection();
        var row = conn.QuerySingle<(Guid Parent, int? Ordinal, double? Sort)>(
            "SELECT parent_work_id AS Parent, ordinal AS Ordinal, ordinal_sort AS Sort FROM works WHERE id = @song;", new { song });
        Assert.Equal(ok, row.Parent);
        Assert.Null(row.Ordinal);
        Assert.Null(row.Sort);
        Assert.Equal(ItemMoveRepository.UnmatchedValue, conn.ExecuteScalar<string>(
            "SELECT value FROM canonical_values WHERE entity_id = @song AND key = 'match_state';", new { song }));

        var entries = await _repository.GetEntriesAsync(ok);
        Assert.Equal(song, Assert.Single(entries).Id);
        Assert.Null(entries[0].Ordinal);
    }

    [Fact]
    public async Task WrongKind_IsBlocked_ASongCannotGoIntoAShow()
    {
        Album("The Best Of", out var song, ownedTrack: ("Paranoid Android", 1));
        var season = Parent("TV", "Season 1", parent: Parent("TV", "A Show", parent: null));

        var outcome = await _repository.CommitAsync(new ItemMoveCommand(song, season, null, ItemMoveMode.AddAnyway, 1, false));

        Assert.Equal(ItemMoveOutcome.WrongKind, outcome);
    }

    [Fact]
    public async Task Episodes_CanOnlyMoveToASeason_NotStraightOntoAShow()
    {
        var show = Parent("TV", "A Show", parent: null);
        var s1 = Parent("TV", "S1", show, ordinal: 1);
        var s2 = Parent("TV", "S2", show, ordinal: 2);
        var episode = OwnedChild("TV", s1, "Pilot", 1);

        var source = await _repository.GetSourceAsync(episode);
        var found = await _repository.FindDestinationsAsync(source!, null, 25);

        Assert.Equal([s2], found.Select(d => d.Id));
        Assert.Equal(ItemMoveOutcome.WrongKind,
            await _repository.CommitAsync(new ItemMoveCommand(episode, show, null, ItemMoveMode.AddAnyway, 1, false)));
    }

    private Guid[] EntriesOf(Guid parent)
    {
        using var conn = _db.CreateConnection();
        return conn.Query<Guid>("SELECT id FROM works WHERE parent_work_id = @parent;", new { parent }).ToArray();
    }

    private Guid Album(string title) => Album(title, out _);

    private Guid Album(string title, out Guid song, (string Title, int Number)? ownedTrack = null)
    {
        var album = Parent("Music", title, parent: null);
        song = ownedTrack is { } track ? OwnedChild("Music", album, track.Title, track.Number) : Guid.Empty;
        return album;
    }

    private Guid Parent(string mediaType, string title, Guid? parent, int? ordinal = null)
    {
        var id = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO works(id, media_type, work_kind, parent_work_id, ordinal, ownership, wikidata_status)
            VALUES(@id, @mediaType, 'parent', @parent, @ordinal, 'Owned', 'pending');
            """, new { id, mediaType, parent, ordinal });
        Title(conn, id, title);
        return id;
    }

    private Guid CatalogTrack(Guid album, string title, int number)
    {
        var id = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO works(id, media_type, work_kind, parent_work_id, ordinal, ordinal_sort, is_catalog_only, ownership, wikidata_status)
            VALUES(@id, 'Music', 'catalog', @album, @number, @number, 1, 'Unowned', 'pending');
            """, new { id, album, number });
        Title(conn, id, title);
        return id;
    }

    private Guid OwnedChild(string mediaType, Guid parent, string title, int number)
    {
        var id = Guid.NewGuid();
        var edition = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO works(id, media_type, work_kind, parent_work_id, ordinal, ordinal_sort, ownership, wikidata_status)
            VALUES(@id, @mediaType, 'child', @parent, @number, @number, 'Owned', 'pending');
            INSERT INTO editions(id, work_id, format_label) VALUES(@edition, @id, 'FLAC');
            INSERT INTO media_assets(id, edition_id, content_hash, file_path_root, status)
            VALUES(@asset, @edition, @hash, @path, 'Normal');
            """, new { id, mediaType, parent, number, edition, asset = Guid.NewGuid(), hash = Guid.NewGuid().ToString("N"), path = $"/music/{title}.flac" });
        Title(conn, id, title);
        return id;
    }

    private static void Title(Microsoft.Data.Sqlite.SqliteConnection conn, Guid id, string title) =>
        conn.Execute("""
            INSERT INTO canonical_values(entity_id, key, value, last_scored_at) VALUES(@id, 'title', @title, '2026-01-01T00:00:00Z');
            """, new { id, title });
}
