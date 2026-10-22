using Dapper;
using MediaEngine.Domain.Models;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

// The data readers that sit under the shared screens (Videos spec D17, D27, D33, WP17 part A): anything in a Vault library is
// absent from search, dynamic collections, library counts, delete look-ups and the batch jobs that feed Local AI, whoever asks.
public sealed class VaultLeakAuditStorageTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_vault_audit_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly string _vaultLibrary = Guid.NewGuid().ToString("D");
    private readonly string _normalLibrary = Guid.NewGuid().ToString("D");
    private readonly DateTimeOffset _now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    public VaultLeakAuditStorageTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        var admin = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO profiles(id,display_name,avatar_color,role,content_limit,created_at) VALUES(@admin,'Admin','#7C4DFF','Administrator',NULL,@now);",
            new { admin, now = _now.ToString("O") });
        connection.Execute(
            "INSERT INTO vault_libraries(library_id, lookup_source, lookup_server_id, adults_confirmed_at, created_by_profile_id, created_at, updated_at) VALUES(@id,'none',NULL,@now,@admin,@now,@now);",
            new { id = _vaultLibrary, admin, now = _now.ToString("O") });
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

    private (Guid Work, Guid Asset) AddVideo(string title, bool vault)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO works (id, media_type, work_kind, curator_state, ready_at) VALUES (@workId, 'Videos', 'standalone', 'accepted', @now);
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, library_id, is_orphaned)
            VALUES (@assetId, @editionId, @hash, @path, CURRENT_TIMESTAMP, @libraryId, 0);
            INSERT INTO canonical_values(entity_id, key, value, last_scored_at) VALUES(@assetId,'title',@title,@now);
            """,
            new
            {
                workId,
                editionId,
                assetId,
                title,
                libraryId = vault ? _vaultLibrary : _normalLibrary,
                path = "/media/" + assetId.ToString("N") + ".mkv",
                hash = "hash-" + assetId.ToString("N"),
                now = _now.ToString("O"),
            });
        return (workId, assetId);
    }

    [Fact]
    public async Task SharedSearch_NeverIndexesOrReturnsAVaultWork()
    {
        var vault = AddVideo("Zebra Crossing Vaultword", vault: true);
        var normal = AddVideo("Zebra Crossing Normalword", vault: false);
        var search = new SearchIndexRepository(_database);

        await search.UpsertByEntityIdAsync(vault.Work);
        await search.UpsertByEntityIdAsync(normal.Work);
        await search.RebuildAsync();

        var found = await search.SearchAsync("Zebra");
        Assert.Contains(normal.Work, found);
        Assert.DoesNotContain(vault.Work, found);
        using var connection = _database.CreateConnection();
        Assert.Equal(0, connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM search_index WHERE entity_id = @id;", new { id = vault.Work.ToString("D") }));
    }

    [Fact]
    public void DynamicCollectionRules_NeverMatchOrCountAVaultWork()
    {
        var vault = AddVideo("Vault scene", vault: true);
        var normal = AddVideo("Normal movie", vault: false);
        var definition = CollectionRuleDefinition.SingleGroup(
            [new CollectionRulePredicate { Field = "media_type", Op = "eq", Value = "Videos" }]);
        var evaluator = new CollectionRuleEvaluator(_database);

        var works = evaluator.Evaluate(definition);

        Assert.Contains(normal.Work, works);
        Assert.DoesNotContain(vault.Work, works);
        Assert.Equal(1, evaluator.Count(definition));
    }

    [Fact]
    public async Task OwnedMediaTypeCounts_LeaveVaultFilesOut()
    {
        AddVideo("Vault scene one", vault: true);
        AddVideo("Vault scene two", vault: true);
        AddVideo("Normal movie", vault: false);

        var counts = await new LibraryItemRepository(_database).GetOwnedMediaTypeCountsAsync();

        Assert.Equal(1, counts["Videos"]);
    }

    [Fact]
    public async Task OwnedMediaTypeCounts_HaveNoEntryWhenOnlyTheVaultOwnsAType()
    {
        AddVideo("Vault scene", vault: true);

        var counts = await new LibraryItemRepository(_database).GetOwnedMediaTypeCountsAsync();

        Assert.False(counts.ContainsKey("Videos"));
    }

    [Fact]
    public async Task DeleteLookup_AnswersAVaultWorkLikeAnUnknownId()
    {
        var vault = AddVideo("Vault scene", vault: true);
        var normal = AddVideo("Normal movie", vault: false);

        var works = await new LibraryItemDeletionRepository(_database).GetWorksAsync([vault.Work, normal.Work]);

        Assert.Single(works);
        Assert.Equal(normal.Work, works[0].WorkId);
    }

    [Fact]
    public async Task BatchEnrichmentCandidates_NeverIncludeAVaultEntity()
    {
        var vault = AddVideo("Vault scene", vault: true);
        var normal = AddVideo("Normal movie", vault: false);

        var ids = await new CanonicalValueRepository(_database).GetEntitiesNeedingEnrichmentAsync("title", "description", 50);

        Assert.Contains(normal.Asset, ids);
        Assert.DoesNotContain(vault.Asset, ids);
    }
}
