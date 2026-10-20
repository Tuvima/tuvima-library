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
        Assert.True(outcome.IsRefusal);
        Assert.Equal(LibraryMoveReasons.UserManagedMerge, outcome.ConflictReason);
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
        // The queue row also asks for the file to be renamed and moved to fit its new place (D19).
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM media_file_write_intents WHERE trigger = 'library_move';"));
    }

    [Fact]
    public async Task AWholeContainer_IsRefused_AndNothingCanBeMovedIntoItselfOrWhatIsInsideIt()
    {
        var show = Parent("TV", "Northwatch");
        var season = Child(show, "TV", "Season one", 1, hasFile: false, kind: "parent");
        var episode = Child(season, "TV", "Pilot", 1);
        var items = await _repository.GetItemsAsync([show, episode]);

        Assert.True(items.Single(item => item.ItemId == show).IsContainer);
        Assert.False(items.Single(item => item.ItemId == episode).IsContainer);
        Assert.True(await _repository.IsSameOrBeneathAsync(season, [show]));
        Assert.True(await _repository.IsSameOrBeneathAsync(show, [show]));
        Assert.False(await _repository.IsSameOrBeneathAsync(show, [season]));
        Assert.False(await _repository.IsSameOrBeneathAsync(season, [episode]));

        var outcome = await _repository.CommitAsync(
            new LibraryMovePlan(season, LibraryMoveKinds.Season, [Row(items, show, LibraryMoveMode.AddAnyway, new LibraryMoveTarget())], "Shy"),
            null);

        Assert.False(outcome.Committed);
        Assert.Null(ParentOf(show));
        Assert.Equal(show, ParentOf(season));
    }

    [Fact]
    public async Task AnEntryCoveredByAnotherFile_IsTakenInTheTransactionToo()
    {
        var album = Parent("Music", "OK Computer");
        var source = Parent("Music", "The Best Of");
        var song = Child(source, "Music", "Airbag", 1);
        var host = Child(album, "Music", "Omnibus", 5);
        var covered = Child(album, "Music", "Airbag", 1, hasFile: false);
        var hostAsset = Scalar<Guid>("SELECT ma.id FROM media_assets ma JOIN editions e ON e.id = ma.edition_id WHERE e.work_id = @host;", new { host });
        Execute("INSERT INTO media_asset_coverage (asset_id, work_id, position, source) VALUES (@hostAsset, @covered, 1, 'filename');", new { hostAsset, covered });
        var items = await _repository.GetItemsAsync([song]);

        var outcome = await _repository.CommitAsync(
            new LibraryMovePlan(album, LibraryMoveKinds.Album, [Row(items, song, LibraryMoveMode.Match, new LibraryMoveTarget(ExistingWorkId: covered))], "Shy"),
            null);

        Assert.False(outcome.Committed);
        Assert.Contains("already has a file", outcome.ConflictReason, StringComparison.Ordinal);
        Assert.Equal(source, ParentOf(song));
    }

    [Fact]
    public async Task AllThreeFilesOfARenamedAudiobookCannotBeMovedInOneBatch()
    {
        var (target, _) = Audiobook("Dune (audiobook)", 1);
        var (source, editions) = Audiobook("Dune disc", 3);
        Execute("UPDATE works SET display_overrides_json = '{\"title\":\"Mine\"}' WHERE id = @source;", new { source });
        var items = await _repository.GetItemsAsync(editions);

        var outcome = await _repository.CommitAsync(
            new LibraryMovePlan(target, LibraryMoveKinds.Audiobook, [.. editions.Select(edition => Row(items, edition, LibraryMoveMode.AddAnyway, new LibraryMoveTarget()))], "Shy"),
            null);

        Assert.False(outcome.Committed);
        Assert.Contains("last file can't be moved", outcome.ConflictReason, StringComparison.Ordinal);
        Assert.Equal(3, Scalar("SELECT COUNT(*) FROM editions WHERE work_id = @source;", new { source }));
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

    private Guid Child(Guid parent, string mediaType, string title, int ordinal, bool hasFile = true, bool catalog = false, string kind = "child")
    {
        var work = Guid.NewGuid();
        var edition = Guid.NewGuid();
        Execute(
            """
            INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ordinal_sort, is_catalog_only, ownership)
            VALUES (@work, @mediaType, @kind, @parent, @ordinal, @ordinal, @catalog, @ownership);
            INSERT INTO editions (id, work_id) VALUES (@edition, @work);
            """,
            new { work, edition, mediaType, parent, ordinal, kind = catalog ? "catalog" : kind, catalog = catalog ? 1 : 0, ownership = catalog ? "Unowned" : "Owned" });
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

    private (Guid Work, List<Guid> Editions) Audiobook(string title, int parts)
    {
        var work = Guid.NewGuid();
        Execute("INSERT INTO works (id, media_type, work_kind) VALUES (@work, 'Audiobooks', 'standalone');", new { work });
        Put(work, "title", title);
        var editions = new List<Guid>();
        for (var part = 1; part <= parts; part++)
        {
            var edition = Guid.NewGuid();
            var asset = Guid.NewGuid();
            Execute(
                "INSERT INTO editions (id, work_id) VALUES (@edition, @work); INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at) VALUES (@asset, @edition, @hash, @path, CURRENT_TIMESTAMP);",
                new { edition, work, asset, hash = Guid.NewGuid().ToString("N"), path = $"C:/library/{asset:N}.mp3" });
            editions.Add(edition);
        }

        return (work, editions);
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
