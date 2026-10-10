using Dapper;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage.Services;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

public sealed class WritebackUnverifiedStateTests
{
    [Fact]
    public async Task UnverifiedAttempt_IsSkippedForSameHash_AndEligibleAfterConfigChange()
    {
        DapperConfiguration.Configure();
        var dbPath = Path.Combine(Path.GetTempPath(), $"tuvima-writeback-{Guid.NewGuid():N}.db");
        try
        {
            using var db = new DatabaseConnection(dbPath);
            db.InitializeSchema();
            db.RunStartupChecks();
            var collectionId = Guid.NewGuid();
            var workId = Guid.NewGuid();
            var editionId = Guid.NewGuid();
            using (var connection = db.CreateConnection())
            {
                connection.Execute("""
                    INSERT INTO collections (id, created_at) VALUES (@collectionId, datetime('now'));
                    INSERT INTO works (id, collection_id, media_type) VALUES (@workId, @collectionId, 'Music');
                    INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
                    """, new { collectionId, workId, editionId });
            }

            var repository = new MediaAssetRepository(db);
            var assetId = Guid.NewGuid();
            await repository.InsertAsync(new MediaAsset
            {
                Id = assetId,
                EditionId = editionId,
                ContentHash = Guid.NewGuid().ToString("N"),
                FilePathRoot = "/library/song.flac",
                Status = AssetStatus.Normal,
            });
            using (var connection = db.CreateConnection())
            {
                connection.Execute("UPDATE media_assets SET writeback_fields_hash='old' WHERE id=@assetId", new { assetId });
            }

            var current = new Dictionary<string, string> { ["Music"] = "config-v1" };
            Assert.Contains(await repository.GetStaleForRetagAsync(current, 10, 0), row => row.AssetId == assetId);
            Assert.Empty(await repository.GetStaleForRetagAsync(current, 10, 0,
                afterAssetId: assetId));

            await repository.MarkWritebackUnverifiedAsync(assetId, "config-v1");
            Assert.DoesNotContain(await repository.GetStaleForRetagAsync(current, 10, 0), row => row.AssetId == assetId);
            using (var connection = db.CreateConnection())
            {
                var state = connection.QuerySingle<(string Status, string Hash)>(
                    "SELECT writeback_status AS Status, writeback_fields_hash AS Hash FROM media_assets WHERE id=@assetId",
                    new { assetId });
                Assert.Equal("unverified", state.Status);
                Assert.Equal("unverified:config-v1", state.Hash);
            }

            Assert.Contains(await repository.GetStaleForRetagAsync(
                new Dictionary<string, string> { ["Music"] = "config-v2" }, 10, 0),
                row => row.AssetId == assetId);

            await repository.MarkWritebackUnsupportedAsync(assetId, "config-v2", "Container cannot write musicbrainz_id");
            Assert.DoesNotContain(await repository.GetStaleForRetagAsync(
                new Dictionary<string, string> { ["Music"] = "config-v2" }, 10, 0),
                row => row.AssetId == assetId);
            using (var connection = db.CreateConnection())
            {
                var status = connection.QuerySingle<string>(
                    "SELECT writeback_status FROM media_assets WHERE id=@assetId", new { assetId });
                Assert.Equal("unsupported", status);
            }
            Assert.Contains(await repository.GetStaleForRetagAsync(
                new Dictionary<string, string> { ["Music"] = "config-v3" }, 10, 0),
                row => row.AssetId == assetId);
        }
        finally
        {
            TestTemp.DeleteDatabase(dbPath);
        }
    }
}
