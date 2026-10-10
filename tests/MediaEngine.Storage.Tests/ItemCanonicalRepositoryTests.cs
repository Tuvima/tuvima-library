using Dapper;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Storage.Tests;

public sealed class ItemCanonicalRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly ItemCanonicalRepository _repository;

    public ItemCanonicalRepositoryTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_item_canonical_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _repository = new ItemCanonicalRepository(_db, NullLogger<ItemCanonicalRepository>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        TestTemp.DeleteDatabase(_dbPath);
    }

    [Fact]
    public async Task ResolveWorkAssetContextAsync_SupportsTypedAssetAndWorkLookups()
    {
        var (workId, assetId) = SeedWorkAndAsset();
        using (var conn = _db.CreateConnection())
        {
            conn.Execute("""
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@assetId, 'title', 'Dune', @now),
                       (@assetId, 'year', '1965', @now);
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
                VALUES (@assetId, 'author', 0, 'Frank Herbert');
                """, new { assetId, now = DateTimeOffset.UtcNow.ToString("O") });
        }

        var byAsset = await _repository.ResolveWorkAssetContextAsync(assetId);
        var byWork = await _repository.ResolveWorkAssetContextAsync(workId);

        Assert.NotNull(byAsset);
        Assert.NotNull(byWork);
        Assert.Equal(assetId, byAsset.AssetId);
        Assert.Equal(assetId, byWork.AssetId);
        Assert.Equal("Books", byAsset.MediaType);
        Assert.Equal("Dune", byWork.WorkTitle);
        Assert.Equal("Frank Herbert", byWork.PrimaryCreator);
        Assert.Equal("1965", byWork.Year);
    }

    [Fact]
    public async Task DisplayOverrides_RoundTripAndDistinguishMissingWork()
    {
        var (workId, _) = SeedWorkAndAsset();

        var missing = await _repository.LoadDisplayOverridesAsync(Guid.NewGuid());
        var saved = await _repository.SaveDisplayOverridesAsync(
            workId,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["title"] = "Preferred Dune",
            });
        var loaded = await _repository.LoadDisplayOverridesAsync(workId);

        Assert.False(missing.WorkExists);
        Assert.True(saved);
        Assert.True(loaded.WorkExists);
        Assert.Equal("Preferred Dune", loaded.Values["TITLE"]);
    }

    [Fact]
    public async Task DeleteIdentityArtifactsAsync_RemovesOnlyTheTargetKeyFromAllStores()
    {
        var (workId, _) = SeedWorkAndAsset();
        var providerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");
        using (var conn = _db.CreateConnection())
        {
            conn.Execute("""
                INSERT INTO metadata_providers (id, name, version) VALUES (@providerId, 'test-provider', '1');
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                    VALUES (@workId, 'tmdb_id', '123', @now), (@workId, 'title', 'Dune', @now);
                INSERT INTO metadata_claims
                    (id, entity_id, provider_id, claim_key, claim_value, confidence, claimed_at)
                    VALUES (@claimId, @workId, @providerId, 'tmdb_id', '123', 1, @now);
                INSERT INTO bridge_ids (id, entity_id, id_type, id_value, provider_id, created_at)
                    VALUES (@bridgeId, @workId, 'tmdb_id', '123', 'test-provider', @now);
                """, new
            {
                providerId,
                workId,
                now,
                claimId = Guid.NewGuid(),
                bridgeId = Guid.NewGuid(),
            });
        }

        await _repository.DeleteIdentityArtifactsAsync(
            [new ItemCanonicalIdentityArtifact(workId, "tmdb_id")]);

        using var verify = _db.CreateConnection();
        Assert.Equal(0, verify.QuerySingle<int>(
            "SELECT COUNT(*) FROM canonical_values WHERE entity_id = @workId AND key = 'tmdb_id';",
            new { workId }));
        Assert.Equal(1, verify.QuerySingle<int>(
            "SELECT COUNT(*) FROM canonical_values WHERE entity_id = @workId AND key = 'title';",
            new { workId }));
        Assert.Equal(0, verify.QuerySingle<int>(
            "SELECT COUNT(*) FROM metadata_claims WHERE entity_id = @workId AND claim_key = 'tmdb_id';",
            new { workId }));
        Assert.Equal(0, verify.QuerySingle<int>(
            "SELECT COUNT(*) FROM bridge_ids WHERE entity_id = @workId AND id_type = 'tmdb_id';",
            new { workId }));
    }

    [Fact]
    public async Task WorkIdentityAndRejectedQids_RoundTripWithoutStringifyingInternalIds()
    {
        var (workId, assetId) = SeedWorkAndAsset();

        await _repository.UpdateWorkIdentityAsync(workId, "Q123");
        var resolvedWorkId = await _repository.ResolveWorkIdForAssetAsync(assetId);
        var state = await _repository.LoadWorkWikidataStateAsync(workId);
        var rejected = await _repository.AppendRejectedQidAsync(workId, "Q999");

        Assert.Equal(workId, resolvedWorkId);
        Assert.NotNull(state);
        Assert.Equal("Q123", state.Qid);
        Assert.Equal("registered", ReadScalar<string>("SELECT curator_state FROM works WHERE id = @workId;", new { workId }));
        Assert.Equal("[\"Q999\"]", rejected);
    }

    [Fact]
    public async Task ReadsHonorPreCancelledTokens()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _repository.ResolveWorkAssetContextAsync(Guid.NewGuid(), cts.Token));
    }

    private (Guid WorkId, Guid AssetId) SeedWorkAndAsset()
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO works (id, media_type) VALUES (@workId, 'Books');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                VALUES (@assetId, @editionId, @hash, @path);
            """, new
        {
            workId,
            editionId,
            assetId,
            hash = $"hash-{assetId:N}",
            path = $"C:/library/{assetId:N}.epub",
        });
        return (workId, assetId);
    }

    private T ReadScalar<T>(string sql, object parameters)
    {
        using var conn = _db.CreateConnection();
        return conn.QuerySingle<T>(sql, parameters);
    }
}
