using Dapper;
using MediaEngine.Domain.Models;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// The move's one transaction: it checks everything again before and while it writes, and a failed check leaves the data store
/// exactly as it was, however far the move had got.
/// </summary>
public sealed class LibraryMoveRepositoryTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_move_repo_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly LibraryMoveRepository _repository;

    public LibraryMoveRepositoryTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _repository = new LibraryMoveRepository(_database);
    }

    public void Dispose()
    {
        TestTemp.ReleaseSqlitePool(_databasePath);
        try { _database.Dispose(); } catch (ObjectDisposedException) { /* Already closed by the pool sweep. */ }
        try { File.Delete(_databasePath); } catch (IOException) { /* The OS temp sweep removes a handle SQLite releases late. */ }
    }

    [Fact]
    public async Task AFailedCheckPartWayThroughTheBatch_UndoesTheRowsAlreadyMoved()
    {
        var album = Parent("Music", "OK Computer");
        var source = Parent("Music", "The Best Of");
        var first = Child(source, "Music", "Airbag", 1);
        var second = Child(source, "Music", "Karma Police", 2);
        var items = await _repository.GetItemsAsync([first, second]);

        var outcome = await _repository.CommitAsync(
            new LibraryMovePlan(album, LibraryMoveKinds.Album,
            [
                Row(items, first, LibraryMoveMode.Match, new LibraryMoveTarget(Ordinal: 1, OrdinalSort: 1)),
                // The second row's slot is the first row's: the transaction sees the slot taken once the first is in place.
                Row(items, second, LibraryMoveMode.Match, new LibraryMoveTarget(Ordinal: 1, OrdinalSort: 1)),
            ], "Shy"),
            null);

        Assert.False(outcome.Committed);
        Assert.Contains("no longer free", outcome.ConflictReason, StringComparison.Ordinal);
        Assert.Equal(source, ParentOf(first));
        Assert.Equal(source, ParentOf(second));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM works WHERE id = @source;", new { source }));
    }

    [Fact]
    public async Task AnItemWhoseIdentityRevisionChangedSinceThePreview_IsRefusedAndNothingMoves()
    {
        var album = Parent("Music", "OK Computer");
        var source = Parent("Music", "The Best Of");
        var song = Child(source, "Music", "Airbag", 1);
        var items = await _repository.GetItemsAsync([song]);
        Put(song, "identity_revision", "somebody-else-changed-it");

        var outcome = await _repository.CommitAsync(
            new LibraryMovePlan(album, LibraryMoveKinds.Album, [Row(items, song, LibraryMoveMode.AddAnyway, new LibraryMoveTarget())], "Shy"),
            null);

        Assert.False(outcome.Committed);
        Assert.Equal(source, ParentOf(song));
    }

    [Fact]
    public async Task AnItemWithAnActiveJob_IsRefusedAndNothingMoves()
    {
        var album = Parent("Music", "OK Computer");
        var source = Parent("Music", "The Best Of");
        var song = Child(source, "Music", "Airbag", 1);
        var items = await _repository.GetItemsAsync([song]);
        var asset = Scalar<Guid>("SELECT ma.id FROM media_assets ma JOIN editions e ON e.id = ma.edition_id WHERE e.work_id = @song;", new { song });
        Execute("INSERT INTO identity_jobs (id, entity_id, entity_type, media_type, state, pass) VALUES (@id, @asset, 'MediaAsset', 'Music', 'Hydrating', 'Quick');",
            new { id = Guid.NewGuid(), asset });

        var outcome = await _repository.CommitAsync(
            new LibraryMovePlan(album, LibraryMoveKinds.Album, [Row(items, song, LibraryMoveMode.AddAnyway, new LibraryMoveTarget())], "Shy"),
            null);

        Assert.False(outcome.Committed);
        Assert.Contains("processed", outcome.ConflictReason, StringComparison.Ordinal);
        Assert.Equal(source, ParentOf(song));
    }

    [Fact]
    public async Task MergingAnItemWithDetailsThePersonSetIntoAnEntry_IsRefusedSoNothingTheyChangedIsLost()
    {
        var album = Parent("Music", "OK Computer");
        var source = Parent("Music", "The Best Of");
        var song = Child(source, "Music", "Airbag", 1);
        var entry = Child(album, "Music", "Airbag", 1, hasFile: false, catalog: true);
        Execute(
            """
            INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value, confidence, is_user_locked, is_current)
            VALUES (@id, @song, (SELECT id FROM metadata_providers LIMIT 1), 'title', 'My own title', 1.0, 1, 1);
            """,
            new { id = Guid.NewGuid(), song });
        var items = await _repository.GetItemsAsync([song]);

        var outcome = await _repository.CommitAsync(
            new LibraryMovePlan(album, LibraryMoveKinds.Album, [Row(items, song, LibraryMoveMode.Match, new LibraryMoveTarget(ExistingWorkId: entry))], "Shy"),
            null);

        Assert.False(outcome.Committed);
        Assert.Contains("changed details", outcome.ConflictReason, StringComparison.Ordinal);
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM works WHERE id = @song;", new { song }));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM editions WHERE work_id = @song;", new { song }));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM editions WHERE work_id = @entry;", new { entry }));
    }

    [Fact]
    public async Task AMoveIsRecordedOnceInTheActivityLog_AndAsksTheFilesTagsToCatchUp()
    {
        var album = Parent("Music", "OK Computer");
        var source = Parent("Music", "The Best Of");
        var song = Child(source, "Music", "Airbag", 1);
        var items = await _repository.GetItemsAsync([song]);

        var outcome = await _repository.CommitAsync(
            new LibraryMovePlan(album, LibraryMoveKinds.Album, [Row(items, song, LibraryMoveMode.AddAnyway, new LibraryMoveTarget())], "Shy"),
            null);

        Assert.True(outcome.Committed);
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM system_activity WHERE action_type = 'MediaUpdated' AND entity_id = @album;", new { album }));
        Assert.Equal("pending", Scalar<string>("SELECT writeback_status FROM media_assets;"));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM media_file_write_intents WHERE status = 'pending';"));
    }

    // ----- helpers ---------------------------------------------------------------------------------------------------------------

    private static LibraryMovePlanRow Row(IReadOnlyList<LibraryMoveItem> items, Guid id, LibraryMoveMode mode, LibraryMoveTarget target)
    {
        var item = items.Single(candidate => candidate.ItemId == id);
        return new LibraryMovePlanRow(id, mode, target, item.ParentWorkId, item.IdentityRevision);
    }

    private Guid Parent(string mediaType, string title)
    {
        var id = Guid.NewGuid();
        Execute("INSERT INTO works (id, media_type, work_kind) VALUES (@id, @mediaType, 'parent');", new { id, mediaType });
        Put(id, "album", title);
        return id;
    }

    private Guid Child(Guid parent, string mediaType, string title, int ordinal, bool hasFile = true, bool catalog = false)
    {
        var work = Guid.NewGuid();
        var edition = Guid.NewGuid();
        Execute(
            """
            INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ordinal_sort, is_catalog_only, ownership)
            VALUES (@work, @mediaType, @kind, @parent, @ordinal, @ordinal, @catalog, @ownership);
            INSERT INTO editions (id, work_id) VALUES (@edition, @work);
            """,
            new { work, edition, mediaType, parent, ordinal, kind = catalog ? "catalog" : "child", catalog = catalog ? 1 : 0, ownership = catalog ? "Unowned" : "Owned" });
        Put(work, "title", title);
        if (hasFile)
        {
            var asset = Guid.NewGuid();
            Execute(
                "INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at) VALUES (@asset, @edition, @hash, @path, CURRENT_TIMESTAMP);",
                new { asset, edition, hash = Guid.NewGuid().ToString("N"), path = $"C:/library/{asset:N}.mp3" });
        }

        return work;
    }

    private void Put(Guid entity, string key, string value) =>
        Execute(
            """
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@entity, @key, @value, CURRENT_TIMESTAMP)
            ON CONFLICT(entity_id, key) DO UPDATE SET value = excluded.value;
            """,
            new { entity, key, value });

    private Guid? ParentOf(Guid work)
    {
        using var connection = _database.CreateConnection();
        return connection.QueryFirstOrDefault<Guid?>("SELECT parent_work_id FROM works WHERE id = @work;", new { work });
    }

    private void Execute(string sql, object? args = null)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(sql, args);
    }

    private long Scalar(string sql, object? args = null) => Scalar<long>(sql, args);

    private T Scalar<T>(string sql, object? args = null)
    {
        using var connection = _database.CreateConnection();
        return connection.ExecuteScalar<T>(sql, args)!;
    }
}
