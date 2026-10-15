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
