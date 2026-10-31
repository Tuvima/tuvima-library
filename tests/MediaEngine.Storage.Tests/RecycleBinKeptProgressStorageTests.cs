using Dapper;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Models;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

// Progress survives a trip through the Recycle bin (delete groups D21): deleting a title that goes to the bin keeps every profile's
// progress by the file's fingerprint, and the same file coming back gets it again, even when it lands on a different title.
public sealed class RecycleBinKeptProgressStorageTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_bin_progress_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly Guid _profile = Guid.NewGuid();

    public RecycleBinKeptProgressStorageTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO profiles(id,display_name,avatar_color,role,created_at) VALUES(@id,'Sam','#7C4DFF','Administrator',@now);",
            new { id = _profile, now = DateTimeOffset.UtcNow.ToString("O") });
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

    private (Guid Work, Guid Edition, Guid Asset) AddFile(string hash)
    {
        var work = Guid.NewGuid();
        var edition = Guid.NewGuid();
        var asset = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO works (id, media_type, work_kind, curator_state, ready_at) VALUES (@work, 'Books', 'standalone', 'accepted', @now);
            INSERT INTO editions (id, work_id) VALUES (@edition, @work);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, library_id, is_orphaned)
            VALUES (@asset, @edition, @hash, @path, CURRENT_TIMESTAMP, 'lib', 0);
            """,
            new { work, edition, asset, hash, path = "/media/" + asset.ToString("N") + ".epub", now = DateTimeOffset.UtcNow.ToString("O") });
        return (work, edition, asset);
    }

    private void AddProgress(Guid asset, Guid work)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO user_states (user_id, asset_id, content_hash, progress_pct, last_accessed) VALUES (@profile, @asset, 'h', 42.5, '2026-10-30T10:00:00Z');
            INSERT INTO audiobook_bookmarks (id, profile_id, work_id, asset_id, position_seconds, label, created_at)
            VALUES (@id, @profile, @work, @asset, 91.5, 'Chapter two', '2026-10-30T10:00:00Z');
            """,
            new { profile = _profile, asset, work, id = Guid.NewGuid() });
    }

    private long Count(string table)
    {
        using var connection = _database.CreateConnection();
        return connection.ExecuteScalar<long>($"SELECT COUNT(*) FROM {table};");
    }

    [Fact]
    public async Task DeletingToTheRecycleBin_KeepsProgress_AndTheSameFileComingBackGetsItBackOnItsNewTitle()
    {
        var first = AddFile("fingerprint-1");
        AddProgress(first.Asset, first.Work);

        await new LibraryItemDeletionRepository(_database).DeleteWorksAsync([first.Work], keepPersonalState: true);

        Assert.Equal(0, Count("user_states"));
        Assert.Equal(2, Count("recycle_bin_personal_state"));

        // The restored file is found again and becomes a new title with a new asset id.
        var second = AddFile("fingerprint-2");
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("DELETE FROM media_assets WHERE id=@a;", new { a = second.Asset });
        }

        var inserted = await new MediaAssetRepository(_database).InsertAsync(new MediaAsset
        {
            Id = Guid.NewGuid(),
            EditionId = second.Edition,
            ContentHash = "fingerprint-1",
            FilePathRoot = "/media/restored.epub",
            LibraryId = "lib",
        });

        Assert.True(inserted);
        using var check = _database.CreateConnection();
        var state = check.QuerySingle<(Guid AssetId, double Progress)>("SELECT asset_id AS AssetId, progress_pct AS Progress FROM user_states;");
        var newAsset = check.ExecuteScalar<Guid>("SELECT id FROM media_assets WHERE content_hash='fingerprint-1';");
        Assert.Equal(newAsset, state.AssetId);
        Assert.Equal(42.5, state.Progress);
        var bookmarkWork = check.ExecuteScalar<Guid>("SELECT work_id FROM audiobook_bookmarks;");
        Assert.Equal(second.Work, bookmarkWork);
        Assert.Equal(0, Count("recycle_bin_personal_state"));
    }

    [Fact]
    public async Task Deleting_ForgetsTheFilesIntakeRecord_SoAPutBackFileIsPickedUpByTheNextScan()
    {
        var file = AddFile("fingerprint-7");
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                """
                INSERT INTO media_operations (id, operation_type, operation_kind, source_path, status, position_key, created_at, updated_at, idempotency_key)
                SELECT @id, 'ingestion.file', 'ingestion', file_path_root, 'no_result', 1, '2026-10-31T00:00:00Z', '2026-10-31T00:00:00Z', 'ingestion:file:x:1:1'
                FROM media_assets WHERE id = @asset;
                """,
                new { id = Guid.NewGuid(), asset = file.Asset });
        }

        await new LibraryItemDeletionRepository(_database).DeleteWorksAsync([file.Work], keepPersonalState: true);

        Assert.Equal(0, Count("media_operations WHERE operation_type = 'ingestion.file'"));
    }

    [Fact]
    public async Task DeletingWithoutTheRecycleBin_KeepsNothing()
    {
        var file = AddFile("fingerprint-3");
        AddProgress(file.Asset, file.Work);

        await new LibraryItemDeletionRepository(_database).DeleteWorksAsync([file.Work]);

        Assert.Equal(0, Count("recycle_bin_personal_state"));
        Assert.Equal(0, Count("user_states"));
    }

    [Fact]
    public async Task KeptProgress_IsDropped_WhenTheBinIsEmptied_OrWhenItIsOlderThanTheCutoff()
    {
        var file = AddFile("fingerprint-4");
        AddProgress(file.Asset, file.Work);
        var repository = new LibraryItemDeletionRepository(_database);
        await repository.DeleteWorksAsync([file.Work], keepPersonalState: true);

        Assert.Equal(0, await repository.DiscardKeptProgressAsync(DateTimeOffset.UtcNow.AddDays(-1)));
        Assert.Equal(2, Count("recycle_bin_personal_state"));
        Assert.Equal(2, await repository.DiscardKeptProgressAsync(null));
        Assert.Equal(0, Count("recycle_bin_personal_state"));
    }

    [Fact]
    public async Task ProgressOfAProfileThatWasDeletedMeanwhile_IsNotPutBack_AndDoesNotBreakTheInsert()
    {
        var first = AddFile("fingerprint-5");
        AddProgress(first.Asset, first.Work);
        await new LibraryItemDeletionRepository(_database).DeleteWorksAsync([first.Work], keepPersonalState: true);
        var other = AddFile("fingerprint-6");
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("DELETE FROM profiles WHERE id=@p;", new { p = _profile });
            connection.Execute("DELETE FROM media_assets WHERE id=@a;", new { a = other.Asset });
        }

        var inserted = await new MediaAssetRepository(_database).InsertAsync(new MediaAsset
        {
            Id = Guid.NewGuid(),
            EditionId = other.Edition,
            ContentHash = "fingerprint-5",
            FilePathRoot = "/media/again.epub",
            LibraryId = "lib",
        });

        Assert.True(inserted);
        Assert.Equal(0, Count("audiobook_bookmarks"));
    }
}
