using Dapper;
using MediaEngine.Contracts.Playback;
using MediaEngine.Storage.Playback;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

// Applying a bookmark set in the data store (Global bookmarks spec D3, D8): one write, Global when an administrator applies it, an
// administrator's Global add replaces any profile's personal bookmark at the same spot, and nobody can delete another person's bookmark.
public sealed class BookmarkSetStorageTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_bookmark_sets_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly AudiobookBookmarkRepository _bookmarks;
    private readonly Guid _admin = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly Guid _workId = Guid.NewGuid();
    private readonly Guid _assetId = Guid.NewGuid();

    public BookmarkSetStorageTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _bookmarks = new AudiobookBookmarkRepository(_database);
        var editionId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO profiles(id,display_name,avatar_color,role,created_at) VALUES(@admin,'Admin','#7C4DFF','Administrator','2026-11-03T12:00:00.0000000+00:00');
            INSERT INTO profiles(id,display_name,avatar_color,role,created_at) VALUES(@other,'Other','#7C4DFF','StandardUser','2026-11-03T12:00:00.0000000+00:00');
            INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@workId, 'Videos', 'standalone', 'accepted');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, library_id)
            VALUES (@assetId, @editionId, 'hash-sets', 'C:/videos/film.mkv', CURRENT_TIMESTAMP, 'lib');
            """,
            new { admin = _admin, other = _other, workId = _workId, editionId, assetId = _assetId });
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

    private Task<AudiobookBookmarkDto> SaveAsync(Guid profile, double at, bool global = false) =>
        _bookmarks.CreateAsync(profile, _workId, new CreateAudiobookBookmarkRequestDto
        {
            AssetId = _assetId,
            PositionSeconds = at,
            DurationSeconds = 7200,
            Note = $"at {at}",
            IsGlobal = global,
        });

    private static AudiobookBookmarkRepository.SetBookmark Incoming(double at) => new(at, null, null, $"incoming {at}", null);

    [Fact]
    public async Task AnAdministratorsApply_AddsGlobal_AndReplacesEveryProfilesPersonalBookmarkAtTheSameSpot()
    {
        var adminsOwn = await SaveAsync(_admin, 100);
        var othersOwn = await SaveAsync(_other, 102);
        var farAway = await SaveAsync(_other, 500);

        var outcome = await _bookmarks.ApplySetAsync(_admin, _workId, _assetId, 7200, [Incoming(101)], [adminsOwn.Id],
            addAsGlobal: true, replaceGlobalSet: false, sameSpotSeconds: 3);

        Assert.Equal(1, outcome.Added);
        var rows = await _bookmarks.GetEveryonesByWorkAsync(_workId);
        Assert.DoesNotContain(rows, row => row.Id == othersOwn.Id);
        Assert.Contains(rows, row => row.Id == farAway.Id);
        var global = Assert.Single(await _bookmarks.GetByWorkAsync(_admin, _workId), row => row.IsGlobal);
        Assert.Equal(101, global.PositionSeconds);
        Assert.Equal("incoming 101", global.Note);
    }

    [Fact]
    public async Task AnOrdinaryApply_AddsPersonalBookmarks_AndCanNeitherDeleteGlobalNorSomeoneElses()
    {
        var global = await SaveAsync(_admin, 100, global: true);
        var admins = await SaveAsync(_admin, 200);

        await _bookmarks.ApplySetAsync(_other, _workId, _assetId, 7200, [Incoming(101)], [global.Id, admins.Id],
            addAsGlobal: false, replaceGlobalSet: true, sameSpotSeconds: 3);

        var seenByOther = await _bookmarks.GetByWorkAsync(_other, _workId);
        Assert.Contains(seenByOther, row => row.Id == global.Id && row.IsGlobal);
        Assert.Contains(seenByOther, row => row.PositionSeconds == 101 && !row.IsGlobal && row.ProfileId == _other);
        Assert.Contains(await _bookmarks.GetEveryonesByWorkAsync(_workId), row => row.Id == admins.Id);
    }

    [Fact]
    public async Task ReplacingTheWholeGlobalSet_RemovesEveryGlobalBookmarkOnTheFile_BeforeAdding()
    {
        await SaveAsync(_admin, 100, global: true);
        await SaveAsync(_admin, 900, global: true);

        await _bookmarks.ApplySetAsync(_admin, _workId, _assetId, 7200, [Incoming(400), Incoming(401)], [],
            addAsGlobal: true, replaceGlobalSet: true, sameSpotSeconds: 3);

        var global = (await _bookmarks.GetByWorkAsync(_admin, _workId)).Where(row => row.IsGlobal).Select(row => row.PositionSeconds).Order();
        Assert.Equal([400d, 401d], global);
    }
}
