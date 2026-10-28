using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

/// <summary>A Vault work never joins a shared Series (Vault sections D35); an ordinary work with the same claims still does.</summary>
public sealed class VaultSeriesAssignmentTests : IDisposable
{
    private const string VaultLibraryId = "11111111-1111-1111-1111-111111111111";
    private const string OrdinaryLibraryId = "22222222-2222-2222-2222-222222222222";
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_vault_series_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly CollectionAssignmentService _assignment;

    public VaultSeriesAssignmentTests()
    {
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        var vault = new VaultLibraryRepository(_db);
        var now = DateTimeOffset.UtcNow;
        vault.AddAsync(new VaultLibrary(VaultLibraryId, VaultLookupSources.None, null, now, null, now, now)).GetAwaiter().GetResult();
        _assignment = new CollectionAssignmentService(
            new CollectionRepository(_db),
            new CanonicalValueRepository(_db),
            new WorkRepository(_db),
            NullLogger<CollectionAssignmentService>.Instance,
            new CanonicalValueArrayRepository(_db),
            vaultLibraries: vault);
    }

    public void Dispose()
    {
        _db.Dispose();
        TestTemp.DeleteDatabase(_dbPath);
    }

    [Fact]
    public async Task AVaultWork_IsNotPlacedInAnySeries_AndCreatesNoShelf()
    {
        var (workId, assetId) = AddWork(VaultLibraryId, "Private Film");

        var result = await _assignment.AssignAsync(assetId);

        Assert.Equal(CollectionAssignmentOutcome.SkippedNoShelfIdentity, result.Outcome);
        Assert.Null(result.CollectionId);
        using var connection = _db.CreateConnection();
        Assert.Null(connection.ExecuteScalar<byte[]?>("SELECT collection_id FROM works WHERE id = @workId;", new { workId }));
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM collections;"));
    }

    [Fact]
    public async Task AnOrdinaryWorkWithTheSameClaims_IsStillPlacedInItsSeries()
    {
        var (_, assetId) = AddWork(OrdinaryLibraryId, "Ordinary Film");

        var result = await _assignment.AssignAsync(assetId);

        Assert.True(result.HasCollection);
    }

    private (Guid WorkId, Guid AssetId) AddWork(string libraryId, string title)
    {
        var workId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var connection = _db.CreateConnection();
        connection.Execute("INSERT INTO works (id, collection_id, media_type, work_kind) VALUES (@workId, NULL, 'Movies', 'standalone');", new { workId });
        connection.Execute(
            """
            INSERT INTO editions (id, work_id, format_label) VALUES (@editionId, @workId, 'Edition');
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, library_id)
            VALUES (@assetId, @editionId, @hash, @path, @libraryId);
            """,
            new { editionId = Guid.NewGuid(), workId, assetId, hash = Guid.NewGuid().ToString("N"), path = $"{title}-{assetId:N}.bin", libraryId });
        foreach (var (key, value) in new Dictionary<string, string> { ["title"] = title, ["media_type"] = "Movies", ["series_qid"] = "QSeries", ["series"] = "Some Series" })
        {
            connection.Execute(
                "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@workId, @key, @value, @now);",
                new { workId, key, value, now = DateTimeOffset.UtcNow.ToString("O") });
        }

        return (workId, assetId);
    }
}
