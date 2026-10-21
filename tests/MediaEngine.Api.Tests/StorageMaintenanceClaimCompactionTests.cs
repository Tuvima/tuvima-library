using Dapper;
using MediaEngine.Api.Services;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

public sealed class StorageMaintenanceClaimCompactionTests : IDisposable
{
    private const string Older = "2026-10-19T19:00:00.0000000+00:00";
    private const string Newer = "2026-10-19T19:05:00.0000000+00:00";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-claim-compaction-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly Guid _entity = Guid.NewGuid();
    private readonly Guid _tmdb = Guid.NewGuid();

    public StorageMaintenanceClaimCompactionTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO metadata_providers (id, name, version, is_enabled) VALUES (@id, 'compaction-test', '1', 1)",
            new { id = _tmdb });
    }

    public void Dispose()
    {
        _database.Dispose();
        DatabaseConnection.ReleasePooledConnections(_databasePath);
        File.Delete(_databasePath);
    }

    [Fact]
    public void Compaction_RepeatedValueInsideNewestObservation_KeepsEveryCopy()
    {
        using var connection = _database.CreateConnection();
        Insert(connection, "composer", "Hans Zimmer", Newer);
        Insert(connection, "composer", "Pedro Eustache", Newer);
        Insert(connection, "composer", "Hans Zimmer", Newer);

        var removed = StorageMaintenanceService.CompactDuplicateClaims(connection, batchSize: 100, dryRun: false);

        Assert.Equal(0, removed);
        Assert.Equal(["Hans Zimmer", "Pedro Eustache", "Hans Zimmer"], ComposerValues(connection));
    }

    [Fact]
    public void Compaction_CopyFromOlderObservation_IsRemovedAndNewestObservationKeepsItsOrder()
    {
        using var connection = _database.CreateConnection();
        Insert(connection, "composer", "Hans Zimmer", Older);
        Insert(connection, "composer", "Hans Zimmer", Newer);
        Insert(connection, "composer", "Pedro Eustache", Newer);
        Insert(connection, "composer", "Hans Zimmer", Newer);

        var removed = StorageMaintenanceService.CompactDuplicateClaims(connection, batchSize: 100, dryRun: false);

        Assert.Equal(1, removed);
        Assert.Equal(["Hans Zimmer", "Pedro Eustache", "Hans Zimmer"], ComposerValues(connection));
        Assert.Equal(0, connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM metadata_claims WHERE claimed_at = @Older", new { Older }));
    }

    [Fact]
    public void Compaction_SupersededCopy_IsRemovedButCurrentCopyStays()
    {
        using var connection = _database.CreateConnection();
        Insert(connection, "composer", "Hans Zimmer", Newer, isCurrent: false);
        Insert(connection, "composer", "Hans Zimmer", Newer);

        var removed = StorageMaintenanceService.CompactDuplicateClaims(connection, batchSize: 100, dryRun: false);

        Assert.Equal(1, removed);
        Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM metadata_claims WHERE is_current = 1"));
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM metadata_claims WHERE is_current = 0"));
    }

    private void Insert(System.Data.IDbConnection connection, string key, string value, string claimedAt, bool isCurrent = true) =>
        connection.Execute(
            """
            INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value, confidence, claimed_at, is_current)
            VALUES (@id, @entity, @provider, @key, @value, 0.88, @claimedAt, @current);
            """,
            new { id = Guid.NewGuid(), entity = _entity, provider = _tmdb, key, value, claimedAt, current = isCurrent ? 1 : 0 });

    private static List<string> ComposerValues(System.Data.IDbConnection connection) =>
        connection.Query<string>(
            "SELECT claim_value FROM metadata_claims WHERE claim_key = 'composer' AND is_current = 1 ORDER BY claimed_at, rowid")
            .AsList();
}
