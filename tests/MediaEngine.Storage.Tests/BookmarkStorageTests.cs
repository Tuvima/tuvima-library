using Dapper;
using MediaEngine.Contracts.Playback;
using MediaEngine.Storage.Playback;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

// Bookmarks in videos and audiobooks (Videos spec WP15, D43): a personal bookmark belongs to the profile that made it, a bookmark
// shared with everyone is seen by every profile that can see the title, and only an administrator may delete one. A file the
// caller cannot see (a Vault file with Vault mode off) contributes nothing, whoever made the bookmark.
public sealed class BookmarkStorageTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_bookmarks_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly AudiobookBookmarkRepository _bookmarks;
    private readonly Guid _admin = Guid.NewGuid();
    private readonly Guid _viewer = Guid.NewGuid();
    private readonly Guid _workId = Guid.NewGuid();
    private readonly Guid _assetId = Guid.NewGuid();

    public BookmarkStorageTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _bookmarks = new AudiobookBookmarkRepository(_database);
        var now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero).ToString("O");
        var editionId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO profiles(id,display_name,avatar_color,role,content_limit,created_at) VALUES(@admin,'Admin','#7C4DFF','Administrator',NULL,@now);
            INSERT INTO profiles(id,display_name,avatar_color,role,content_limit,created_at) VALUES(@viewer,'Viewer','#7C4DFF','StandardUser',NULL,@now);
            INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@workId, 'Videos', 'standalone', 'accepted');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, library_id)
            VALUES (@assetId, @editionId, 'hash-bookmarks', 'C:/videos/film.mkv', CURRENT_TIMESTAMP, @libraryId);
            """,
            new { admin = _admin, viewer = _viewer, workId = _workId, editionId, assetId = _assetId, now, libraryId = Guid.NewGuid().ToString("D") });
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

    private Task<AudiobookBookmarkDto> SaveAsync(Guid profile, double seconds, string? note = null, string? description = null, bool global = false) =>
        _bookmarks.CreateAsync(profile, _workId, new CreateAudiobookBookmarkRequestDto
        {
            AssetId = _assetId,
            PositionSeconds = seconds,
            DurationSeconds = 7200,
            Note = note,
            Description = description,
            IsGlobal = global,
        });

    [Fact]
    public async Task ABookmarkAtTwelveThirty_KeepsItsShortNoteAndLongerDescription_AndBelongsToTheProfileThatMadeIt()
    {
        var saved = await SaveAsync(_viewer, 750, "  The reveal  ", "  Watch the reflection in the window.  ");

        var listed = Assert.Single(await _bookmarks.GetByWorkAsync(_viewer, _workId));
        Assert.Equal(saved.Id, listed.Id);
        Assert.Equal(750, listed.PositionSeconds);
        Assert.Equal("The reveal", listed.Note);
        Assert.Equal("Watch the reflection in the window.", listed.Description);
        Assert.False(listed.IsGlobal);
        Assert.Equal(_viewer, listed.ProfileId);
    }

    [Fact]
    public async Task APersonalBookmark_IsNeverSeenOrDeletedByAnotherProfile()
    {
        var saved = await SaveAsync(_viewer, 60, "mine");

        Assert.Empty(await _bookmarks.GetByWorkAsync(_admin, _workId));
        Assert.False(await _bookmarks.DeleteAsync(_admin, saved.Id, canChangeGlobal: true));
        Assert.Single(await _bookmarks.GetByWorkAsync(_viewer, _workId));
    }

    [Fact]
    public async Task AGlobalBookmark_IsSeenByEveryProfile_ButDeletedOnlyWithAdministratorPermission()
    {
        var shared = await SaveAsync(_admin, 300, "for everyone", global: true);

        var seenByViewer = Assert.Single(await _bookmarks.GetByWorkAsync(_viewer, _workId));
        Assert.True(seenByViewer.IsGlobal);
        Assert.Equal(shared.Id, seenByViewer.Id);
        Assert.False(await _bookmarks.DeleteAsync(_viewer, shared.Id));
        Assert.False(await _bookmarks.DeleteAsync(_viewer, shared.Id, canChangeGlobal: false));
        Assert.Single(await _bookmarks.GetByWorkAsync(_admin, _workId));

        // A different administrator than the one who made it may remove it.
        Assert.True(await _bookmarks.DeleteAsync(_viewer, shared.Id, canChangeGlobal: true));
        Assert.Empty(await _bookmarks.GetByWorkAsync(_admin, _workId));
    }

    [Fact]
    public async Task AnAdministrator_CanDeleteAGlobalBookmark_OnAFileTheyCanSee()
    {
        var shared = await SaveAsync(_admin, 300, "for everyone", global: true);
        var visible = new HashSet<Guid> { _assetId };

        Assert.False(await _bookmarks.DeleteAsync(_viewer, shared.Id, default, visible));
        Assert.True(await _bookmarks.DeleteAsync(_viewer, shared.Id, default, visible, canChangeGlobal: true));
        Assert.Empty(await _bookmarks.GetByWorkAsync(_admin, _workId));
    }

    [Fact]
    public async Task TheListHoldsPersonalAndGlobalBookmarksNewestFirst()
    {
        await SaveAsync(_viewer, 10, "first");
        await Task.Delay(5);
        await SaveAsync(_admin, 20, "shared", global: true);
        await Task.Delay(5);
        await SaveAsync(_viewer, 30, "last");

        var listed = await _bookmarks.GetByWorkAsync(_viewer, _workId);

        Assert.Equal(new[] { "last", "shared", "first" }, listed.Select(bookmark => bookmark.Note ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task ABookmarkOnAFileTheCallerCannotSee_IsAbsentAndCannotBeDeleted_PersonalOrGlobal()
    {
        var personal = await SaveAsync(_viewer, 15, "vault note");
        var shared = await SaveAsync(_admin, 25, "vault shared", global: true);
        var nothingVisible = new HashSet<Guid>();

        Assert.Empty(await _bookmarks.GetByWorkAsync(_viewer, _workId, default, nothingVisible));
        Assert.Empty(await _bookmarks.GetByWorkAsync(_admin, _workId, default, nothingVisible));
        Assert.False(await _bookmarks.DeleteAsync(_viewer, personal.Id, default, nothingVisible));
        Assert.False(await _bookmarks.DeleteAsync(_admin, shared.Id, default, nothingVisible, canChangeGlobal: true));

        var visible = new HashSet<Guid> { _assetId };
        Assert.Equal(2, (await _bookmarks.GetByWorkAsync(_viewer, _workId, default, visible)).Count);
    }

    [Fact]
    public async Task ABookmarkSavedBeforeSharingExisted_StaysPersonalWithItsTextAsTheShortNote()
    {
        var id = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                """
                INSERT INTO audiobook_bookmarks (id, profile_id, work_id, asset_id, position_seconds, note, created_at)
                VALUES (@id, @profile, @work, @asset, 90, 'old note', @now);
                """,
                new { id, profile = _viewer, work = _workId, asset = _assetId, now = DateTimeOffset.UtcNow.ToString("O") });
        }

        var listed = Assert.Single(await _bookmarks.GetByWorkAsync(_viewer, _workId));
        Assert.False(listed.IsGlobal);
        Assert.Equal("old note", listed.Note);
        Assert.Null(listed.Description);
        Assert.Empty(await _bookmarks.GetByWorkAsync(_admin, _workId));
    }
}
