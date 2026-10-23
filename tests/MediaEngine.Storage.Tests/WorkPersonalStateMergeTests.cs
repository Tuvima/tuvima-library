using Dapper;
using MediaEngine.Domain.Aggregates;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// A move that merges a title into another carries what people did with it over. These tests hold the list of tables that point at a
/// title to the data structure, so a table added later cannot quietly be left out.
/// </summary>
public sealed class WorkPersonalStateMergeTests : IDisposable
{
    private static readonly Guid Person = Profile.SeedProfileId;

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_merge_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;

    public WorkPersonalStateMergeTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
    }

    public void Dispose()
    {
        TestTemp.ReleaseSqlitePool(_databasePath);
        try { _database.Dispose(); } catch (ObjectDisposedException) { /* Already closed by the pool sweep. */ }
        try { File.Delete(_databasePath); } catch (IOException) { /* The OS temp sweep removes a handle SQLite releases late. */ }
    }

    [Fact]
    public void EveryTableThatPointsAtATitle_IsEitherCarriedOverOrDealtWithByTheMove()
    {
        using var connection = _database.CreateConnection();
        var pointingAtWorks = connection.Query<string>(
            """
            SELECT DISTINCT m.name
            FROM sqlite_master m, pragma_foreign_key_list(m.name) f
            WHERE m.type = 'table' AND f."table" = 'works';
            """).ToList();

        Assert.NotEmpty(pointingAtWorks);
        Assert.All(pointingAtWorks, table => Assert.Contains(table, WorkPersonalStateMerge.HandledTables));
    }

    [Fact]
    public void EveryPerPersonTableThatNamesATitleByIdWithNoForeignKey_IsCarriedOver()
    {
        using var connection = _database.CreateConnection();
        var perPersonByEntityId = connection.Query<string>(
            """
            SELECT m.name
            FROM sqlite_master m
            WHERE m.type = 'table'
              AND EXISTS (SELECT 1 FROM pragma_table_info(m.name) c WHERE c.name = 'entity_id')
              AND EXISTS (SELECT 1 FROM pragma_table_info(m.name) c WHERE c.name = 'profile_id');
            """).ToList();

        // The activity log records who did what and when; it is history, not something a person owns, so a move leaves it as it was.
        var records = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "system_activity" };
        var personal = perPersonByEntityId.Where(table => !records.Contains(table)).ToList();

        Assert.NotEmpty(personal);
        Assert.All(personal, table => Assert.Contains(table, WorkPersonalStateMerge.HandledEntityIdTables));
        Assert.All(WorkPersonalStateMerge.HandledEntityIdTables, table => Assert.Contains(table, personal));
    }

    [Fact]
    public void QueueItemsAndDeviceStatesPointAtTheSurvivingTitle_AndListenHistoryAndBookmarksAreAllKept()
    {
        var source = Work("Audiobooks");
        var target = Work("Audiobooks");
        var asset = Asset(source);
        Execute(
            """
            INSERT INTO player_sessions (profile_id, session_id, device_id, client) VALUES (@person, @session, 'd', 'web');
            INSERT INTO player_queue_items (id, profile_id, position, work_id, media_type, title, added_at)
            VALUES (@queue, @person, 0, @source, 'Audiobooks', 'Chapter', '2026-10-01T00:00:00Z');
            INSERT INTO player_device_states (profile_id, device_id, client, asset_id, work_id, playback_state, updated_at)
            VALUES (@person, 'd', 'web', @asset, @source, 'paused', '2026-10-01T00:00:00Z');
            INSERT INTO audiobook_bookmarks (id, profile_id, work_id, asset_id, position_seconds, created_at)
            VALUES (@bookmark1, @person, @source, @asset, 1, '2026-10-01T00:00:00Z'), (@bookmark2, @person, @target, @asset, 2, '2026-10-02T00:00:00Z');
            """,
            new { person = Person, session = Guid.NewGuid(), queue = Guid.NewGuid(), source, target, asset, bookmark1 = Guid.NewGuid(), bookmark2 = Guid.NewGuid() });

        Carry(source, target);

        Assert.Equal(target, Scalar<Guid>("SELECT work_id FROM player_queue_items;"));
        Assert.Equal(target, Scalar<Guid>("SELECT work_id FROM player_device_states;"));
        Assert.Equal(2, Scalar<long>("SELECT COUNT(*) FROM audiobook_bookmarks WHERE work_id = @target;", new { target }));
        Assert.Equal(0, Scalar<long>("SELECT COUNT(*) FROM audiobook_bookmarks WHERE work_id = @source;", new { source }));
    }

    [Fact]
    public void ASavedItemEitherTitleHad_IsKept_AndHandPickedCollectionsHoldTheSurvivorOnce()
    {
        var source = Work("Music");
        var target = Work("Music");
        var collection = Guid.NewGuid();
        Execute(
            """
            INSERT INTO profile_saved_items (profile_id, entity_kind, entity_id, saved_at) VALUES (@person, 'Song', @source, '2026-10-01T00:00:00Z');
            INSERT INTO collections (id, display_name, collection_type, membership_mode, resolution) VALUES (@collection, 'Mine', 'Custom', 'Manual', 'materialized');
            INSERT INTO collection_items (id, collection_id, work_id, sort_order) VALUES (@one, @collection, @source, 0), (@two, @collection, @target, 1);
            """,
            new { person = Person, source, target, collection, one = Guid.NewGuid(), two = Guid.NewGuid() });

        Carry(source, target);

        Assert.Equal(1, Scalar<long>("SELECT COUNT(*) FROM profile_saved_items WHERE entity_id = @target;", new { target }));
        Assert.Equal(1, Scalar<long>("SELECT COUNT(*) FROM collection_items WHERE collection_id = @collection AND work_id = @target;", new { collection, target }));
        Assert.Equal(0, Scalar<long>("SELECT COUNT(*) FROM collection_items WHERE work_id = @source;", new { source }));
    }

    // A file that was Unmatched and is later identified onto an existing title keeps what its person did with it (Library setup spec D64):
    // the carry is the same for every media type, so each one is checked with a row in every work-keyed table.
    [Theory]
    [InlineData("Movies", "Movie")]
    [InlineData("TV", "TvShow")]
    [InlineData("Music", "Song")]
    [InlineData("Audiobooks", "Audiobook")]
    [InlineData("Books", "Book")]
    [InlineData("Comics", "Comic")]
    public void EveryKindOfWorkKeyedPersonalState_FollowsTheTitleItIsMergedInto_ForEveryMediaType(string mediaType, string entityKind)
    {
        var source = Work(mediaType);
        var target = Work(mediaType);
        var asset = Asset(source);
        Execute(
            """
            INSERT INTO profile_saved_items (profile_id, entity_kind, entity_id, saved_at) VALUES (@person, @entityKind, @source, '2026-10-01T00:00:00Z');
            INSERT INTO profile_reactions (profile_id, entity_kind, entity_id, reaction, updated_at) VALUES (@person, @entityKind, @source, 'Love', '2026-10-01T00:00:00Z');
            INSERT INTO profile_work_preferences (profile_id, work_id, local_tags_json, revision, updated_at) VALUES (@person, @source, '["watch later"]', 1, '2026-10-01T00:00:00Z');
            INSERT INTO music_play_stats (profile_id, work_id, play_count, last_played_at) VALUES (@person, @source, 3, '2026-10-01T00:00:00Z');
            INSERT INTO audiobook_bookmarks (id, profile_id, work_id, asset_id, position_seconds, created_at) VALUES (@bookmark, @person, @source, @asset, 12, '2026-10-01T00:00:00Z');
            INSERT INTO audiobook_listen_history (id, profile_id, work_id, asset_id, title, position_seconds, device_id, client, started_at, ended_at)
            VALUES (@history, @person, @source, @asset, 'Heard', 12, 'd', 'web', '2026-10-01T00:00:00Z', '2026-10-01T00:10:00Z');
            """,
            new { person = Person, entityKind, source, target, asset, bookmark = Guid.NewGuid(), history = Guid.NewGuid() });

        Carry(source, target);

        Assert.Equal(1, Scalar<long>("SELECT COUNT(*) FROM profile_saved_items WHERE entity_id = @target AND entity_kind = @entityKind;", new { target, entityKind }));
        Assert.Equal("Love", Scalar<string>("SELECT reaction FROM profile_reactions WHERE entity_id = @target AND entity_kind = @entityKind;", new { target, entityKind }));
        Assert.Equal("""["watch later"]""", Scalar<string>("SELECT local_tags_json FROM profile_work_preferences WHERE work_id = @target;", new { target }));
        Assert.Equal(3, Scalar<long>("SELECT play_count FROM music_play_stats WHERE work_id = @target;", new { target }));
        Assert.Equal(1, Scalar<long>("SELECT COUNT(*) FROM audiobook_bookmarks WHERE work_id = @target;", new { target }));
        Assert.Equal(1, Scalar<long>("SELECT COUNT(*) FROM audiobook_listen_history WHERE work_id = @target;", new { target }));
        Assert.Equal(0, Scalar<long>("SELECT COUNT(*) FROM profile_saved_items WHERE entity_id = @source;", new { source }));
        Assert.Equal(0, Scalar<long>("SELECT COUNT(*) FROM profile_work_preferences WHERE work_id = @source;", new { source }));
    }

    private void Carry(Guid source, Guid target)
    {
        using var connection = _database.CreateConnection();
        using var transaction = connection.BeginTransaction();
        WorkPersonalStateMerge.Carry(connection, transaction, source, target);
        transaction.Commit();
    }

    private Guid Work(string mediaType)
    {
        var id = Guid.NewGuid();
        Execute("INSERT INTO works (id, media_type, work_kind) VALUES (@id, @mediaType, 'standalone');", new { id, mediaType });
        return id;
    }

    private Guid Asset(Guid work)
    {
        var edition = Guid.NewGuid();
        var asset = Guid.NewGuid();
        Execute(
            """
            INSERT INTO editions (id, work_id) VALUES (@edition, @work);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at) VALUES (@asset, @edition, @hash, @path, CURRENT_TIMESTAMP);
            """,
            new { edition, work, asset, hash = Guid.NewGuid().ToString("N"), path = $"C:/library/{asset:N}.mp3" });
        return asset;
    }

    private void Execute(string sql, object? args = null)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(sql, args);
    }

    private T Scalar<T>(string sql, object? args = null)
    {
        using var connection = _database.CreateConnection();
        return connection.ExecuteScalar<T>(sql, args)!;
    }
}
