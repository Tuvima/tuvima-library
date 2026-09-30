using Dapper;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

public sealed class MediaEditorOwnedChildScaleTests
{
    [Fact]
    public async Task ThousandOwnedEpisodes_ReturnsOneStablePageAndSearchesOriginalFilename()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima_owned_children_{Guid.NewGuid():N}.db");
        using var database = new DatabaseConnection(path);
        try
        {
            database.InitializeSchema();
            var showId = Guid.NewGuid();
            var libraryId = Guid.NewGuid().ToString("D");
            using (var connection = database.CreateConnection())
            using (var transaction = connection.BeginTransaction())
            {
                connection.Execute("INSERT INTO works (id, media_type, work_kind, ownership) VALUES (@showId, 'TV', 'parent', 'Owned');", new { showId }, transaction);
                for (var index = 1; index <= 1000; index++)
                {
                    var episodeId = Guid.NewGuid();
                    var editionId = Guid.NewGuid();
                    var assetId = Guid.NewGuid();
                    connection.Execute("""
                        INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ownership)
                        VALUES (@episodeId, 'TV', 'child', @showId, @index, 'Owned');
                        INSERT INTO editions (id, work_id, format_label) VALUES (@editionId, @episodeId, 'MKV');
                        INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, library_id)
                        VALUES (@assetId, @editionId, @hash, @filePath, @libraryId);
                        """, new { episodeId, showId, index, editionId, assetId, hash = $"hash-{index}", filePath = $"/fixtures/original_episode_{index:D4}.mkv", libraryId }, transaction);
                }
                transaction.Commit();
            }

            var reader = new MediaEditorOwnedChildReadService(database);
            var segment = Assert.Single(await reader.GetAccessSegmentsAsync(showId, CancellationToken.None));
            var first = await reader.SearchAsync(showId, null, 1, 50, null, null, null, null, null, CancellationToken.None, [segment.Key]);
            var found = await reader.SearchAsync(showId, "original_episode_0999", 1, 50, null, null, null, null, null, CancellationToken.None);
            var denied = await reader.SearchAsync(showId, null, 1, 50, null, null, null, null, null, CancellationToken.None, ["another-library|TV"]);

            Assert.NotNull(first);
            Assert.Equal(1000, first.TotalCount);
            Assert.Equal(50, first.Items.Count);
            Assert.Equal(0, denied!.TotalCount);
            Assert.Single(found!.Items);
            Assert.Equal("original_episode_0999.mkv", found.Items[0].SourceFileName);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
