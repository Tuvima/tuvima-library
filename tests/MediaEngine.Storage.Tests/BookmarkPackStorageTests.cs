using Dapper;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Playback;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

// Bookmark packs in the data store (Videos spec D46): a pack belongs to the profile that imported it, is invisible on a file the caller
// cannot see, is not stored twice on a title, switches off without being removed, and goes when its title goes.
public sealed class BookmarkPackStorageTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_bookmark_packs_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly BookmarkPackRepository _packs;
    private readonly AudiobookBookmarkRepository _bookmarks;
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly Guid _workId = Guid.NewGuid();
    private readonly Guid _editionId = Guid.NewGuid();
    private readonly Guid _assetId = Guid.NewGuid();

    public BookmarkPackStorageTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _packs = new BookmarkPackRepository(_database);
        _bookmarks = new AudiobookBookmarkRepository(_database);
        var now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero).ToString("O");
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO profiles(id,display_name,avatar_color,role,content_limit,created_at) VALUES(@owner,'Owner','#7C4DFF','StandardUser',NULL,@now);
            INSERT INTO profiles(id,display_name,avatar_color,role,content_limit,created_at) VALUES(@other,'Other','#7C4DFF','StandardUser',NULL,@now);
            INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@workId, 'Videos', 'standalone', 'accepted');
            INSERT INTO editions (id, work_id, format_label) VALUES (@editionId, @workId, 'Extended');
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, library_id)
            VALUES (@assetId, @editionId, 'hash-packs', 'C:/videos/film.mkv', CURRENT_TIMESTAMP, @libraryId);
            INSERT INTO bridge_ids(id, entity_id, id_type, id_value) VALUES (@b1, @workId, 'imdb_id', 'tt0120737');
            INSERT INTO bridge_ids(id, entity_id, id_type, id_value) VALUES (@b2, @workId, 'tmdb_id', '120');
            INSERT INTO bridge_ids(id, entity_id, id_type, id_value) VALUES (@b3, @workId, 'goodreads_id', 'not-for-packs');
            """,
            new
            {
                owner = _owner,
                other = _other,
                workId = _workId,
                editionId = _editionId,
                assetId = _assetId,
                now,
                libraryId = Guid.NewGuid().ToString("D"),
                b1 = Guid.NewGuid(),
                b2 = Guid.NewGuid(),
                b3 = Guid.NewGuid(),
            });
    }

    public void Dispose()
    {
        try { _database.Dispose(); } catch (ObjectDisposedException) { }
        using (var pool = new SqliteConnection($"Data Source={_databasePath}"))
        {
            SqliteConnection.ClearPool(pool);
        }

        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static BookmarkPackBookmark[] Items() =>
    [
        new BookmarkPackBookmark(300, 360, "Opening", 0, "late note", "a longer description"),
        new BookmarkPackBookmark(60, null, null, null, "early note", null),
    ];

    private Task<BookmarkPackDto?> AddAsync(Guid profile, string hash = "hash-a", string name = "Gondor") =>
        _packs.AddAsync(profile, _workId, _assetId, name, "A fan", hash, 7200, "Extended", false, Items());

    [Fact]
    public async Task APack_KeepsEveryBookmarkField_InTimeOrder()
    {
        var added = await AddAsync(_owner);

        Assert.NotNull(added);
        var listed = Assert.Single(await _packs.GetByWorkAsync(_owner, _workId));
        Assert.Equal("Gondor", listed.Name);
        Assert.Equal("A fan", listed.Author);
        Assert.True(listed.IsEnabled);
        Assert.Equal([60d, 300d], listed.Bookmarks.Select(item => item.PositionSeconds));
        var scene = listed.Bookmarks[1];
        Assert.Equal(360, scene.EndSeconds);
        Assert.Equal("Opening", scene.ChapterTitle);
        Assert.Equal("late note", scene.Note);
        Assert.Equal("a longer description", scene.Description);
    }

    [Fact]
    public async Task APack_BelongsToTheProfileThatImportedIt_AndNoOneElseCanSeeSwitchOrRemoveIt()
    {
        var added = await AddAsync(_owner);

        Assert.Empty(await _packs.GetByWorkAsync(_other, _workId));
        Assert.False(await _packs.SetEnabledAsync(_other, added!.Id, false));
        Assert.False(await _packs.DeleteAsync(_other, added.Id));
        Assert.True(Assert.Single(await _packs.GetByWorkAsync(_owner, _workId)).IsEnabled);
    }

    [Fact]
    public async Task ThePackOfOneProfile_DoesNotStopAnotherProfileImportingTheSamePack()
    {
        Assert.NotNull(await AddAsync(_owner));
        Assert.NotNull(await AddAsync(_other));
        Assert.Null(await AddAsync(_owner));
        Assert.True(await _packs.ExistsAsync(_owner, _workId, "hash-a"));
    }

    [Fact]
    public async Task APackOnAFileTheCallerCannotSee_IsInvisible_AndCannotBeChanged()
    {
        var added = await AddAsync(_owner);
        var nothingVisible = new HashSet<Guid>();
        var visible = new HashSet<Guid> { _assetId };

        Assert.Empty(await _packs.GetByWorkAsync(_owner, _workId, default, nothingVisible));
        Assert.False(await _packs.SetEnabledAsync(_owner, added!.Id, false, default, nothingVisible));
        Assert.False(await _packs.DeleteAsync(_owner, added.Id, default, nothingVisible));
        Assert.Single(await _packs.GetByWorkAsync(_owner, _workId, default, visible));
    }

    [Fact]
    public async Task SwitchingAPackOff_KeepsItsBookmarks_AndRemovingItTakesThemAway()
    {
        var added = await AddAsync(_owner);

        Assert.True(await _packs.SetEnabledAsync(_owner, added!.Id, false));
        var off = Assert.Single(await _packs.GetByWorkAsync(_owner, _workId));
        Assert.False(off.IsEnabled);
        Assert.Equal(2, off.Bookmarks.Count);

        Assert.True(await _packs.DeleteAsync(_owner, added.Id));
        Assert.Empty(await _packs.GetByWorkAsync(_owner, _workId));
        using var connection = _database.CreateConnection();
        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM bookmark_pack_items;"));
    }

    [Fact]
    public async Task ATitlesPacks_GoWithTheTitle()
    {
        await AddAsync(_owner);

        using (var connection = _database.CreateConnection())
        {
            connection.Execute("DELETE FROM media_assets WHERE id = @assetId; DELETE FROM editions WHERE id = @editionId; DELETE FROM works WHERE id = @workId;",
                new { assetId = _assetId, editionId = _editionId, workId = _workId });
            Assert.Equal(0, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM bookmark_packs;"));
            Assert.Equal(0, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM bookmark_pack_items;"));
        }
    }

    [Fact]
    public async Task Recognition_ListsOnlyTheIdsAPackMayCarry_AndTheEditionLabel()
    {
        var recognition = await _packs.GetRecognitionAsync(_assetId);

        Assert.NotNull(recognition);
        Assert.Equal(_workId, recognition!.WorkId);
        Assert.Equal("Extended", recognition.EditionLabel);
        Assert.Equal("tt0120737", recognition.Ids["imdb_id"]);
        Assert.Equal("120", recognition.Ids["tmdb_id"]);
        Assert.DoesNotContain("goodreads_id", recognition.Ids.Keys);
        Assert.Null(await _packs.GetRecognitionAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task ABookmarkWithAnEnd_KeepsIt_AndAnEndBeforeTheStartIsRefusedByTheDataStore()
    {
        var scene = await _bookmarks.CreateAsync(_owner, _workId, new CreateAudiobookBookmarkRequestDto
        {
            AssetId = _assetId,
            PositionSeconds = 100,
            EndSeconds = 160,
            DurationSeconds = 7200,
        });

        Assert.Equal(160, Assert.Single(await _bookmarks.GetByWorkAsync(_owner, _workId)).EndSeconds);
        Assert.Equal(scene.Id, (await _bookmarks.GetByWorkAsync(_owner, _workId))[0].Id);
        await Assert.ThrowsAnyAsync<Exception>(() => _bookmarks.CreateAsync(_owner, _workId, new CreateAudiobookBookmarkRequestDto
        {
            AssetId = _assetId,
            PositionSeconds = 100,
            EndSeconds = 90,
        }));
    }
}
