using Dapper;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Storage;
using MediaEngine.Storage.Services;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Api.Tests;

public sealed class WorkDetailReadServiceTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly WorkDetailReadService _service;

    public WorkDetailReadServiceTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_work_detail_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _service = new WorkDetailReadService(_database);
    }

    [Fact]
    public async Task GetAsync_ReturnsWorkEditionsNormalAssetsAndCanonicalValues()
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var normalAssetId = Guid.NewGuid();
        var missingAssetId = Guid.NewGuid();
        var providerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");

        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync("""
                INSERT INTO works (id, media_type, work_kind, ordinal, is_catalog_only, wikidata_qid)
                VALUES (@workId, 'Books', 'standalone', 2, 0, 'Q123');

                INSERT INTO editions (id, work_id, format_label, wikidata_qid)
                VALUES (@editionId, @workId, 'EPUB', 'Q456');

                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
                VALUES (@normalAssetId, @editionId, 'normal-hash', 'C:/library/book.epub', 'Normal');

                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
                VALUES (@missingAssetId, @editionId, 'conflicted-hash', 'C:/library/conflicted.epub', 'Conflicted');

                INSERT INTO canonical_values (entity_id, key, value, last_scored_at, winning_provider_id)
                VALUES (@workId, 'title', 'Test Work', @now, @providerId);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@editionId, 'format', 'ebook', @now);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@normalAssetId, 'file_label', 'Owned Copy', @now);
                """, new { workId, editionId, normalAssetId, missingAssetId, providerId, now });
        }

        var detail = await _service.GetAsync(workId);

        Assert.NotNull(detail);
        Assert.Equal("Books", detail.MediaType);
        Assert.Equal("standalone", detail.WorkKind);
        Assert.Equal("Q123", detail.WikidataQid);
        Assert.Contains(detail.CanonicalValues, value => value.Key == "title"
            && value.Value == "Test Work" && value.WinningProviderId == providerId.ToString("D"));
        var edition = Assert.Single(detail.Editions);
        Assert.Equal("EPUB", edition.FormatLabel);
        Assert.Contains(edition.CanonicalValues, value => value.Key == "format" && value.Value == "ebook");
        var asset = Assert.Single(edition.Assets);
        Assert.Equal(normalAssetId, asset.Id);
        Assert.Contains(asset.CanonicalValues, value => value.Key == "file_label" && value.Value == "Owned Copy");
    }

    [Fact]
    public async Task GetAsync_ReturnsNullForUnknownWork()
    {
        Assert.Null(await _service.GetAsync(Guid.NewGuid()));
    }

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
    }
}
