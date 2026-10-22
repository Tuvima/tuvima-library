using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

public sealed class MetadataClaimHistoryReadServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly MetadataClaimRepository _claimRepo;

    public MetadataClaimHistoryReadServiceTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_claim_history_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _claimRepo = new MetadataClaimRepository(_db);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task GetClaimHistoryAsync_ReturnsDirectEntityClaims()
    {
        var entityId = Guid.NewGuid();
        await AddClaimAsync(entityId, "title", "Direct", 0.7, DateTimeOffset.UtcNow.AddMinutes(-2));

        var service = new MetadataClaimHistoryReadService(_claimRepo, _db);

        var result = await service.GetClaimHistoryAsync(entityId, CancellationToken.None);

        var claim = Assert.Single(result);
        Assert.Equal("title", claim.ClaimKey);
        Assert.Equal("Direct", claim.ClaimValue);
    }

    [Fact]
    public async Task GetClaimHistoryAsync_ResolvesWorkIdToAssetClaimsAndDeduplicates()
    {
        var workId = Guid.NewGuid();
        var firstAsset = SeedAsset(workId);
        var secondAsset = SeedAsset(workId);
        var duplicateProviderId = Guid.NewGuid();
        var older = DateTimeOffset.UtcNow.AddMinutes(-10);
        var newer = DateTimeOffset.UtcNow.AddMinutes(-1);
        await AddClaimAsync(firstAsset, "title", "Shared", 0.5, newer, duplicateProviderId);
        await AddClaimAsync(secondAsset, "title", "Shared", 0.9, older, duplicateProviderId);
        await AddClaimAsync(secondAsset, "author", "Author", 0.8, newer);

        var service = new MetadataClaimHistoryReadService(_claimRepo, _db);

        var result = await service.GetClaimHistoryAsync(workId, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal("title", result[0].ClaimKey);
        Assert.Equal(0.9, result[0].Confidence);
        Assert.Equal("author", result[1].ClaimKey);
    }

    [Fact]
    public async Task GetClaimHistoryAsync_ReturnsEmptyForUnknownEntity()
    {
        var service = new MetadataClaimHistoryReadService(_claimRepo, _db);

        var result = await service.GetClaimHistoryAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Empty(result);
    }

    // WP17 part C: the claims of a Vault file or of a work with a file in the Vault are never shown; the answer is the one an unknown id gets.
    [Fact]
    public async Task GetClaimHistoryAsync_AnswersAVaultAssetAndItsWorkLikeAnUnknownId()
    {
        var vaultWork = Guid.NewGuid();
        var vaultAsset = SeedVaultAsset(vaultWork);
        await AddClaimAsync(vaultAsset, "title", "Secret title", 0.9, DateTimeOffset.UtcNow);
        var ordinaryWork = Guid.NewGuid();
        var ordinaryAsset = SeedAsset(ordinaryWork);
        await AddClaimAsync(ordinaryAsset, "title", "Ordinary title", 0.9, DateTimeOffset.UtcNow);
        var service = new MetadataClaimHistoryReadService(_claimRepo, _db);

        Assert.Empty(await service.GetClaimHistoryAsync(vaultAsset, CancellationToken.None));
        Assert.Empty(await service.GetClaimHistoryAsync(vaultWork, CancellationToken.None));
        Assert.Empty(await service.GetClaimHistoryAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Equal("Ordinary title", Assert.Single(await service.GetClaimHistoryAsync(ordinaryAsset, CancellationToken.None)).ClaimValue);
        Assert.Equal("Ordinary title", Assert.Single(await service.GetClaimHistoryAsync(ordinaryWork, CancellationToken.None)).ClaimValue);
    }

    [Fact]
    public async Task GetClaimHistoryAsync_AWorkWithOneFileInTheVault_IsHiddenAsAWhole()
    {
        var mixedWork = Guid.NewGuid();
        var ordinaryAsset = SeedAsset(mixedWork);
        var vaultAsset = SeedVaultAsset(mixedWork);
        await AddClaimAsync(ordinaryAsset, "title", "Shared work title", 0.9, DateTimeOffset.UtcNow);
        await AddClaimAsync(vaultAsset, "title", "Secret file title", 0.9, DateTimeOffset.UtcNow);
        var service = new MetadataClaimHistoryReadService(_claimRepo, _db);

        Assert.Empty(await service.GetClaimHistoryAsync(mixedWork, CancellationToken.None));
        Assert.Empty(await service.GetClaimHistoryAsync(vaultAsset, CancellationToken.None));
    }

    private const string VaultLibraryId = "vault-library-for-claims";

    private void EnsureVaultLibrary()
    {
        const string vaultLibrary = VaultLibraryId;
        using var conn = _db.CreateConnection();
        Dapper.SqlMapper.Execute(conn,
            """
            INSERT OR IGNORE INTO vault_libraries (library_id, lookup_source, adults_confirmed_at, created_at, updated_at)
            VALUES (@vaultLibrary, 'none', '2026-10-21T12:00:00Z', '2026-10-21T12:00:00Z', '2026-10-21T12:00:00Z');
            """,
            new { vaultLibrary });
    }

    private Guid SeedVaultAsset(Guid workId)
    {
        EnsureVaultLibrary();
        return SeedAsset(workId, VaultLibraryId);
    }

    private Guid SeedAsset(Guid workId, string? libraryId = null)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        cmd.CommandText = """
            INSERT OR IGNORE INTO works (id, media_type, work_kind)
            VALUES ($workId, 'Books', 'standalone');
            INSERT INTO editions (id, work_id)
            VALUES ($editionId, $workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, library_id)
            VALUES ($assetId, $editionId, $hash, $path, $library);
            """;
        AddGuid(cmd, "$workId", workId);
        AddGuid(cmd, "$editionId", editionId);
        AddGuid(cmd, "$assetId", assetId);
        cmd.Parameters.AddWithValue("$hash", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("$path", $"C:/library/{assetId:N}.epub");
        cmd.Parameters.AddWithValue("$library", (object?)libraryId ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return assetId;
    }

    private async Task AddClaimAsync(Guid entityId, string key, string value, double confidence, DateTimeOffset claimedAt, Guid? providerId = null)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        providerId ??= Guid.NewGuid();
        cmd.CommandText = """
            INSERT OR IGNORE INTO metadata_providers (id, name, version, is_enabled)
            VALUES ($providerId, $providerName, '1.0', 1);
            INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value, confidence, claimed_at)
            VALUES ($claimId, $entityId, $providerId, $key, $value, $confidence, $claimedAt);
            """;
        AddGuid(cmd, "$providerId", providerId.Value);
        cmd.Parameters.AddWithValue("$providerName", $"test-{providerId.Value:N}");
        AddGuid(cmd, "$claimId", Guid.NewGuid());
        AddGuid(cmd, "$entityId", entityId);
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.Parameters.AddWithValue("$confidence", confidence);
        cmd.Parameters.AddWithValue("$claimedAt", claimedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    private static void AddGuid(Microsoft.Data.Sqlite.SqliteCommand command, string name, Guid value)
    {
        command.Parameters.AddWithValue(name, GuidSql.ToBlob(value));
    }
}

