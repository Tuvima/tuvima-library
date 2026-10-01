using Dapper;
using MediaEngine.Storage;

namespace MediaEngine.Storage.Tests;

public sealed class MediaEditorWorkVersionReadRepositoryTests
{
    [Fact]
    public async Task DirectAssetLaunch_PreservesIdentityAndReturnsPersistedEditionAndAssetLevels()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima_work_versions_{Guid.NewGuid():N}.db");
        using var database = new DatabaseConnection(path);
        try
        {
            database.InitializeSchema();
            var workId = Guid.NewGuid();
            var editionId = Guid.NewGuid();
            var secondEditionId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var secondAssetId = Guid.NewGuid();
            using (var connection = database.CreateConnection())
            {
                connection.Execute("INSERT INTO works (id, media_type, work_kind, ownership) VALUES (@workId, 'Books', 'standalone', 'Owned');", new { workId });
                connection.Execute("INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@workId, 'title', 'Owned Book', datetime('now'));", new { workId });
                connection.Execute("INSERT INTO editions (id, work_id, format_label) VALUES (@editionId, @workId, 'EPUB'), (@secondEditionId, @workId, NULL);", new { editionId, secondEditionId, workId });
                connection.Execute("INSERT INTO media_assets (id, edition_id, content_hash, file_path_root) VALUES (@assetId, @editionId, 'one', '/books/owned.epub'), (@secondAssetId, @secondEditionId, 'two', '/books/scan.pdf');", new { assetId, secondAssetId, editionId, secondEditionId });
            }

            var result = await new MediaEditorWorkVersionReadRepository(database).GetAsync(assetId, CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal(workId, result.WorkId);
            Assert.Equal(assetId, result.SelectedEntityId);
            Assert.Equal("Asset", result.SelectedEntityType);
            Assert.Equal("Owned Book", result.WorkTitle);
            Assert.Equal(2, result.Editions.Count);
            Assert.Equal("EPUB", result.Editions.Single(item => item.EditionId == editionId).Label);
            Assert.Null(result.Editions.Single(item => item.EditionId == secondEditionId).Label);
            Assert.Equal("PDF", result.Editions.Single(item => item.EditionId == secondEditionId).Assets.Single().TechnicalLabel);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public async Task SingleTechnicalEdition_UsesCollapseHintWithoutInventingACut()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima_work_version_collapse_{Guid.NewGuid():N}.db");
        using var database = new DatabaseConnection(path);
        try
        {
            database.InitializeSchema();
            var workId = Guid.NewGuid();
            var editionId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            using (var connection = database.CreateConnection())
            {
                connection.Execute("INSERT INTO works (id, media_type, work_kind, ownership) VALUES (@workId, 'Movies', 'standalone', 'Owned');", new { workId });
                connection.Execute("INSERT INTO editions (id, work_id, format_label) VALUES (@editionId, @workId, 'MKV');", new { editionId, workId });
                connection.Execute("INSERT INTO media_assets (id, edition_id, content_hash, file_path_root) VALUES (@assetId, @editionId, 'movie', '/movies/movie.mkv');", new { assetId, editionId });
            }

            var result = await new MediaEditorWorkVersionReadRepository(database).GetAsync(workId, CancellationToken.None);
            var edition = Assert.Single(result!.Editions);

            Assert.True(edition.Collapse);
            Assert.Equal("MKV", edition.Label);
            Assert.Equal("MKV", Assert.Single(edition.Assets).TechnicalLabel);
            Assert.DoesNotContain("cut", edition.Label, StringComparison.OrdinalIgnoreCase);
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
