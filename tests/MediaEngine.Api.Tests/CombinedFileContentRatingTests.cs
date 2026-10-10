using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A combined file can be played from every episode it covers, so a profile's content limit has to
/// be checked against the strictest of them, not only the episode the file is attached to.
/// </summary>
public sealed class CombinedFileContentRatingTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DatabaseConnection _database;

    public CombinedFileContentRatingTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_combined_rating_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
    }

    [Fact]
    public async Task RatingsIncludeEveryCoveredEpisode_NotJustTheHost()
    {
        var host = Guid.NewGuid();
        var covered = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        using var conn = _database.CreateConnection();
        conn.Execute("""
            INSERT INTO works (id, media_type, work_kind) VALUES (@host, 'TV', 'standalone'), (@covered, 'TV', 'standalone');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @host);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
            VALUES (@assetId, @editionId, @hash, '/library/combined.mkv', 'Normal');
            INSERT INTO media_asset_coverage (asset_id, work_id, position, source)
            VALUES (@assetId, @host, 1, 'filename'), (@assetId, @covered, 2, 'filename');
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@host, 'content_rating', 'TV-PG', CURRENT_TIMESTAMP),
                   (@covered, 'content_rating', 'TV-MA', CURRENT_TIMESTAMP);
            """, new { host, covered, editionId, assetId, hash = $"hash_{assetId:N}" });

        var ratings = await CatalogueResourceAuthorizationService.ReadContentRatingsAsync(conn, assetId, CancellationToken.None);

        Assert.Contains("TV-PG", ratings);
        Assert.Contains("TV-MA", ratings);
    }

    [Fact]
    public async Task AFileWithoutCoverage_StillReportsOnlyItsOwnRating()
    {
        var host = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        using var conn = _database.CreateConnection();
        conn.Execute("""
            INSERT INTO works (id, media_type, work_kind) VALUES (@host, 'TV', 'standalone');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @host);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
            VALUES (@assetId, @editionId, @hash, '/library/single.mkv', 'Normal');
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@host, 'content_rating', 'TV-PG', CURRENT_TIMESTAMP);
            """, new { host, editionId, assetId, hash = $"hash_{assetId:N}" });

        var ratings = await CatalogueResourceAuthorizationService.ReadContentRatingsAsync(conn, assetId, CancellationToken.None);

        Assert.Equal(["TV-PG"], ratings);
    }

    public void Dispose()
    {
        try { _database.Dispose(); } catch { /* Test cleanup is best effort. */ }
        try { File.Delete(_databasePath); } catch { /* Test cleanup is best effort. */ }
    }
}
