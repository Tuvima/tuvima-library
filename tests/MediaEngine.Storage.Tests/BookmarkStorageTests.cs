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
            INSERT INTO profiles(id,display_name,avatar_color,role,created_at) VALUES(@admin,'Admin','#7C4DFF','Administrator',@now);
            INSERT INTO profiles(id,display_name,avatar_color,role,created_at) VALUES(@viewer,'Viewer','#7C4DFF','StandardUser',@now);
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

    // ---------------------------------------------------------------- Everyone's, Promote and editing (player-controls-v3 D10)

    [Fact]
    public async Task APersonalBookmark_IsSavedForThePersonWhoMadeIt_AndTheirOwnListShowsIt_WhateverProfileIdTheDashboardHolds()
    {
        // Private bookmarks: the Engine scopes by the session profile, so the saved bookmark comes straight back to its owner.
        var saved = await SaveAsync(_viewer, 125, "private");

        var listed = Assert.Single(await _bookmarks.GetByWorkAsync(_viewer, _workId));
        Assert.Equal(saved.Id, listed.Id);
        Assert.False(listed.IsGlobal);
        Assert.Equal(125, listed.PositionSeconds);
        Assert.Equal(_assetId, listed.AssetId);
    }

    [Fact]
    public async Task Everyones_ListsEveryProfilesPersonalBookmarks_WithTheOwnersName_AndLeavesGlobalOnesOut()
    {
        await SaveAsync(_viewer, 300, "viewer note");
        await SaveAsync(_admin, 100, "admin note");
        await SaveAsync(_admin, 200, "for all", global: true);

        var everyones = await _bookmarks.GetEveryonesByWorkAsync(_workId);

        Assert.Equal([100d, 300d], everyones.Select(item => item.PositionSeconds).ToArray());
        Assert.Equal(new[] { "Admin", "Viewer" }, everyones.Select(item => item.OwnerName ?? string.Empty).ToArray());
        Assert.All(everyones, item => Assert.False(item.IsGlobal));
    }

    [Fact]
    public async Task Everyones_LeavesOutAFileTheCallerCannotSee()
    {
        await SaveAsync(_viewer, 300);

        Assert.Empty(await _bookmarks.GetEveryonesByWorkAsync(_workId, authorizedAssetIds: new HashSet<Guid>()));
    }

    [Fact]
    public async Task Promote_MakesItGlobalAtOnce_NamesIt_AndReplacesPersonalBookmarksWithinThreeSeconds()
    {
        var chosen = await SaveAsync(_viewer, 300, "viewer note");
        await SaveAsync(_admin, 302, "admin note");        // within 3 seconds: the same moment, replaced.
        var farther = await SaveAsync(_admin, 310, "later"); // outside the window: kept.

        var promoted = await _bookmarks.PromoteAsync(chosen.Id, "The reveal", 3d);

        Assert.NotNull(promoted);
        Assert.True(promoted!.IsGlobal);
        Assert.Equal("The reveal", promoted.Label);
        var forAdmin = await _bookmarks.GetByWorkAsync(_admin, _workId);
        Assert.Equal([310d, 300d], forAdmin.OrderByDescending(item => item.PositionSeconds).Select(item => item.PositionSeconds).ToArray());
        Assert.Contains(forAdmin, item => item.Id == chosen.Id && item.IsGlobal);
        Assert.Contains(forAdmin, item => item.Id == farther.Id && !item.IsGlobal);
        Assert.DoesNotContain(await _bookmarks.GetEveryonesByWorkAsync(_workId), item => item.PositionSeconds is >= 299 and <= 303);
    }

    [Fact]
    public async Task Promote_OfAGlobalOrMissingBookmark_ChangesNothing()
    {
        var global = await SaveAsync(_admin, 50, global: true);

        Assert.Null(await _bookmarks.PromoteAsync(global.Id, "x", 3d));
        Assert.Null(await _bookmarks.PromoteAsync(Guid.NewGuid(), "x", 3d));
    }

    [Fact]
    public async Task UpdateGlobal_ChangesTheNameNoteAndDescription_OnlyOfAGlobalBookmark()
    {
        var global = await SaveAsync(_admin, 50, "old", global: true);
        var personal = await SaveAsync(_viewer, 60, "mine");

        var updated = await _bookmarks.UpdateGlobalAsync(global.Id, "New name", "New note", "Longer words.");

        Assert.NotNull(updated);
        Assert.Equal("New name", updated!.Label);
        Assert.Equal("New note", updated.Note);
        Assert.Equal("Longer words.", updated.Description);
        Assert.Null(await _bookmarks.UpdateGlobalAsync(personal.Id, "Nope", null, null));
        Assert.Equal("mine", Assert.Single(await _bookmarks.GetByWorkAsync(_viewer, _workId), item => item.Id == personal.Id).Note);
    }
}
