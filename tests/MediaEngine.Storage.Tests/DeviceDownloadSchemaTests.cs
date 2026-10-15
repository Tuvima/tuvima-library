using Dapper;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

/// <summary>The <c>device_downloads</c> table: created by the schema and the startup migration, safe to run again, and tied to its variant.</summary>
public sealed class DeviceDownloadSchemaTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima_device_downloads_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;

    public DeviceDownloadSchemaTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _database.RunStartupChecks();
    }

    [Fact]
    public void StartupChecks_AreIdempotent_AndLeaveTheTableAndItsIndexes()
    {
        // A second startup (an existing data store) changes nothing and does not fail.
        _database.RunStartupChecks();

        using var connection = _database.CreateConnection();
        var columns = connection.Query<string>("SELECT name FROM pragma_table_info('device_downloads');").ToHashSet();
        Assert.Equal(
            new HashSet<string> { "device_id", "profile_id", "asset_id", "variant_id", "stored_bytes", "stored_at", "updated_at" },
            columns);
        var indexes = connection.Query<string>("SELECT name FROM pragma_index_list('device_downloads');").ToHashSet();
        Assert.Contains("idx_device_downloads_device_profile", indexes);
        Assert.Contains("idx_device_downloads_variant", indexes);
    }

    [Fact]
    public void StartupChecks_AreIdempotent_AndLeaveThePhotoTableAndItsIndexes()
    {
        _database.RunStartupChecks();

        using var connection = _database.CreateConnection();
        var columns = connection.Query<string>("SELECT name FROM pragma_table_info('device_photo_downloads');").ToHashSet();
        Assert.Equal(
            new HashSet<string> { "device_id", "profile_id", "item_id", "stored_bytes", "stored_at", "updated_at" },
            columns);
        var indexes = connection.Query<string>("SELECT name FROM pragma_index_list('device_photo_downloads');").ToHashSet();
        Assert.Contains("idx_device_photo_downloads_device_profile", indexes);
        Assert.Contains("idx_device_photo_downloads_item", indexes);
    }

    [Fact]
    public void APhotoRowFollowsItsItem_AndRefusesNegativeBytes()
    {
        using var connection = _database.CreateConnection();
        var seed = SeedVariant(connection);
        var item = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");
        // A Shared item must sit in a household's Shared library.
        connection.Execute(
            """
            INSERT OR IGNORE INTO households (id, name, created_at)
            SELECT @household, 'Test household', @now WHERE NOT EXISTS (SELECT 1 FROM households);
            INSERT OR IGNORE INTO view_shared_library (household_id, library_id, created_at, updated_at)
            SELECT id, @library, @now, @now FROM households ORDER BY rowid LIMIT 1;
            INSERT INTO local_items (id, scope_kind, library_id, media_kind, primary_file_name, primary_mime_type, created_at, updated_at)
            VALUES (@item, 'shared', (SELECT library_id FROM view_shared_library LIMIT 1), 'image', 'photo.jpg', 'image/jpeg', @now, @now);
            """,
            new { item, household = Guid.NewGuid(), library = Guid.NewGuid(), now });
        const string insert = """
            INSERT INTO device_photo_downloads (device_id, profile_id, item_id, stored_bytes, stored_at, updated_at)
            VALUES (@DeviceId, @ProfileId, @item, @bytes, @now, @now);
            """;

        Assert.Throws<SqliteException>(() => connection.Execute(insert, new { seed.DeviceId, seed.ProfileId, item, bytes = -1, now }));
        connection.Execute(insert, new { seed.DeviceId, seed.ProfileId, item, bytes = 10, now });
        Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM device_photo_downloads;"));

        connection.Execute("DELETE FROM local_items WHERE id = @item;", new { item });

        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM device_photo_downloads;"));
    }

    [Fact]
    public void NegativeStoredBytes_AreRefusedByTheTable()
    {
        using var connection = _database.CreateConnection();
        var variant = SeedVariant(connection);

        Assert.Throws<SqliteException>(() => InsertDownload(connection, variant, storedBytes: -1));
    }

    [Fact]
    public void ARowFollowsItsVariant_WhenTheVariantIsRemoved()
    {
        using var connection = _database.CreateConnection();
        var variant = SeedVariant(connection);
        InsertDownload(connection, variant, storedBytes: 10);

        connection.Execute("DELETE FROM offline_variants WHERE id = @id;", new { id = variant.VariantId });

        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM device_downloads;"));
    }

    private static (Guid VariantId, Guid AssetId, Guid DeviceId, Guid ProfileId) SeedVariant(SqliteConnection connection)
    {
        var work = Guid.NewGuid();
        var edition = Guid.NewGuid();
        var asset = Guid.NewGuid();
        var variant = Guid.NewGuid();
        var profile = MediaEngine.Domain.Aggregates.Profile.SeedProfileId;
        var device = Guid.NewGuid();
        var account = Guid.NewGuid();
        connection.Execute(
            """
            INSERT INTO works (id, media_type) VALUES (@work, 'Movies');
            INSERT INTO editions (id, work_id) VALUES (@edition, @work);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status) VALUES (@asset, @edition, 'hash', '/library/film.mp4', 'Normal');
            INSERT INTO offline_variants (id, asset_id, profile_key, source_hash, display_name, status, created_at)
            VALUES (@variant, @asset, 'mobile-small', 'hash', 'Phone', 'ready', @now);
            """,
            new { work, edition, asset, variant, now = DateTimeOffset.UtcNow.ToString("O") });
        // Foreign keys are enforced on every connection, so the device row needs a real account and application.
        connection.Execute("PRAGMA foreign_keys = OFF;");
        connection.Execute(
            """
            INSERT INTO client_devices (id, application_id, account_id, profile_id, device_name, device_class, client_id, client_name, client_version, scopes, created_at, last_seen_at)
            SELECT @device, id, @account, @profile, 'Phone', 'mobile', 'client', 'Client', '1', 'downloads.read', @now, @now FROM applications LIMIT 1;
            """,
            new { device, account, profile, now = DateTimeOffset.UtcNow.ToString("O") });
        connection.Execute("PRAGMA foreign_keys = ON;");
        return (variant, asset, device, profile);
    }

    private static void InsertDownload(SqliteConnection connection, (Guid VariantId, Guid AssetId, Guid DeviceId, Guid ProfileId) seed, long storedBytes) =>
        connection.Execute(
            """
            INSERT INTO device_downloads (device_id, profile_id, asset_id, variant_id, stored_bytes, stored_at, updated_at)
            VALUES (@DeviceId, @ProfileId, @AssetId, @VariantId, @storedBytes, @now, @now);
            """,
            new { seed.DeviceId, seed.ProfileId, seed.AssetId, seed.VariantId, storedBytes, now = DateTimeOffset.UtcNow.ToString("O") });

    public void Dispose()
    {
        _database.Dispose();
        foreach (var path in new[] { _path, $"{_path}-wal", $"{_path}-shm" })
        {
            try { File.Delete(path); } catch (IOException) { /* the temp file is disposable */ }
        }
    }
}
