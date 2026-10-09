using Dapper;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

public sealed class MediaEditorOwnedChildScaleTests
{
    [Fact]
    public async Task WorkWithSeveralEditions_KeepsEditionAndAssetIdentitySeparate()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima_editor_editions_{Guid.NewGuid():N}.db");
        using var database = new DatabaseConnection(path);
        try
        {
            database.InitializeSchema();
            var workId = Guid.NewGuid();
            var theatricalId = Guid.NewGuid();
            var imaxId = Guid.NewGuid();
            var firstAssetId = Guid.NewGuid();
            var secondAssetId = Guid.NewGuid();
            var imaxAssetId = Guid.NewGuid();
            using (var connection = database.CreateConnection())
            {
                connection.Execute("INSERT INTO works (id, media_type, work_kind, ownership) VALUES (@workId, 'Movies', 'standalone', 'Owned');", new { workId });
                connection.Execute("INSERT INTO editions (id, work_id, format_label) VALUES (@theatricalId, @workId, 'Theatrical Cut'), (@imaxId, @workId, 'IMAX Version');", new { theatricalId, imaxId, workId });
                connection.Execute("""
                    INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                    VALUES (@firstAssetId, @theatricalId, 'movie-4k', '/movies/theatrical-4k.mkv'),
                           (@secondAssetId, @theatricalId, 'movie-1080', '/movies/theatrical-1080.mkv'),
                           (@imaxAssetId, @imaxId, 'movie-imax', '/movies/imax.mkv');
                    """, new { firstAssetId, secondAssetId, imaxAssetId, theatricalId, imaxId });
            }

            var reader = new MediaEditorOwnedChildReadService(database);
            var result = await reader.SearchAsync(workId, null, 1, 50, null, null, null, null, null, CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal(3, result.TotalCount);
            Assert.Equal(2, result.Items.Count(item => item.EditionId == theatricalId));
            Assert.All(result.Items.Where(item => item.EditionId == theatricalId), item =>
            {
                Assert.Equal(2, item.EditionAssetCount);
                Assert.Equal(2, item.WorkEditionCount);
                Assert.False(item.CollapseEdition);
                Assert.Equal(workId, item.IdentityOwnerEntityId);
                Assert.Equal(workId, item.ArtworkOwnerEntityId);
            });
            Assert.Equal(imaxId, result.Items.Single(item => item.AssetId == imaxAssetId).EditionId);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task TwoEpisodeAssetsWithSameWork_AreBothOwnedChildren()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima_editor_episode_versions_{Guid.NewGuid():N}.db");
        using var database = new DatabaseConnection(path);
        try
        {
            database.InitializeSchema();
            var showId = Guid.NewGuid();
            var episodeId = Guid.NewGuid();
            var editionId = Guid.NewGuid();
            using (var connection = database.CreateConnection())
            {
                connection.Execute("""
                    INSERT INTO works (id, media_type, work_kind, ownership) VALUES (@showId, 'TV', 'parent', 'Owned');
                    INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ownership)
                    VALUES (@episodeId, 'TV', 'child', @showId, 1, 'Owned');
                    INSERT INTO editions (id, work_id, format_label) VALUES (@editionId, @episodeId, 'MKV');
                    INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                    VALUES (@firstAssetId, @editionId, 'episode-4k', '/tv/s01e01-4k.mkv'),
                           (@secondAssetId, @editionId, 'episode-1080', '/tv/s01e01-1080.mkv');
                    """, new { showId, episodeId, editionId, firstAssetId = Guid.NewGuid(), secondAssetId = Guid.NewGuid() });
            }

            var result = await new MediaEditorOwnedChildReadService(database)
                .SearchAsync(showId, null, 1, 50, null, null, null, null, null, CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal(2, result.TotalCount);
            Assert.All(result.Items, item =>
            {
                Assert.Equal(episodeId, item.WorkId);
                Assert.Equal(editionId, item.EditionId);
                Assert.Equal(2, item.EditionAssetCount);
                Assert.True(item.CollapseEdition);
            });
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task MusicArtwork_InheritsAlbumWorkUntilEditionOverrideExists()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima_editor_music_art_{Guid.NewGuid():N}.db");
        using var database = new DatabaseConnection(path);
        try
        {
            database.InitializeSchema();
            var albumId = Guid.NewGuid();
            var trackId = Guid.NewGuid();
            var editionId = Guid.NewGuid();
            var artworkId = Guid.NewGuid();
            using (var connection = database.CreateConnection())
            {
                connection.Execute("""
                    INSERT INTO works (id, media_type, work_kind, ownership) VALUES (@albumId, 'Music', 'parent', 'Owned');
                    INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ownership)
                    VALUES (@trackId, 'Music', 'child', @albumId, 1, 'Owned');
                    INSERT INTO editions (id, work_id, format_label) VALUES (@editionId, @trackId, 'FLAC');
                    INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                    VALUES (@assetId, @editionId, 'music-track', '/music/track.flac');
                    """, new { albumId, trackId, editionId, assetId = Guid.NewGuid() });

                var reader = new MediaEditorOwnedChildReadService(database);
                var inherited = await reader.SearchAsync(albumId, null, 1, 50, null, null, null, null, null, CancellationToken.None);
                Assert.Equal(albumId, Assert.Single(inherited!.Items).ArtworkOwnerEntityId);

                connection.Execute("""
                    INSERT INTO artwork_assets (id, content_hash) VALUES (@artworkId, 'edition-cover');
                    INSERT INTO entity_artwork_links (id, entity_id, entity_type, artwork_asset_id, role)
                    VALUES (@linkId, @editionId, 'Edition', @artworkId, 'Primary');
                    """, new { artworkId, linkId = Guid.NewGuid(), editionId });

                var overridden = await reader.SearchAsync(albumId, null, 1, 50, null, null, null, null, null, CancellationToken.None);
                var item = Assert.Single(overridden!.Items);
                Assert.Equal(editionId, item.ArtworkOwnerEntityId);
                Assert.False(item.CollapseEdition);
            }
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task SelectionRevision_TracksIdentityAndMembershipButNotFileHousekeeping()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima_editor_revision_{Guid.NewGuid():N}.db");
        using var database = new DatabaseConnection(path);
        try
        {
            database.InitializeSchema();
            var showId = Guid.NewGuid();
            var seasonId = Guid.NewGuid();
            var episodeId = Guid.NewGuid();
            var editionId = Guid.NewGuid();
            var otherEditionId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var initialLibraryId = Guid.NewGuid().ToString("D");
            using var connection = database.CreateConnection();
            connection.Execute("""
                INSERT INTO works (id, media_type, work_kind, ownership)
                VALUES (@showId, 'TV', 'parent', 'Owned');
                INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ownership)
                VALUES (@seasonId, 'TV', 'parent', @showId, 1, 'Owned'),
                       (@episodeId, 'TV', 'child', @seasonId, 1, 'Owned');
                INSERT INTO editions (id, work_id) VALUES (@editionId, @episodeId), (@otherEditionId, @episodeId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, library_id)
                VALUES (@assetId, @editionId, 'revision-fixture', '/tv/original.mkv', @initialLibraryId);
                """, new { showId, seasonId, episodeId, editionId, otherEditionId, assetId, initialLibraryId });

            var reader = new MediaEditorOwnedChildReadService(database);
            async Task<MediaEngine.Application.ReadModels.MediaEditorOwnedChildEnvelope> ReadAsync() =>
                Assert.Single((await reader.SearchAsync(showId, null, 1, 50, null, null, null,
                    null, null, CancellationToken.None))!.Items);

            var original = await ReadAsync();
            Assert.Equal(seasonId, original.StructuralParentId);
            Assert.StartsWith("v1:", original.SelectionRevision);
            Assert.Equal(original.SelectionRevision, (await ReadAsync()).SelectionRevision);
            var arbitraryAssetRevisions = await reader.GetSelectionRevisionsForAssetsAsync(
                showId, [assetId], CancellationToken.None);
            Assert.Equal(original.SelectionRevision, arbitraryAssetRevisions[assetId]);

            connection.Execute("""
                UPDATE media_assets SET file_path_root = '/tv/renamed.mkv', writeback_status = 'Pending'
                WHERE id = @assetId;
                """, new { assetId });
            Assert.Equal(original.SelectionRevision, (await ReadAsync()).SelectionRevision);

            connection.Execute("""
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@episodeId, 'identity_revision', 'reviewed-2', datetime('now'));
                """, new { episodeId });
            var revised = (await ReadAsync()).SelectionRevision;
            Assert.NotEqual(original.SelectionRevision, revised);
            Assert.Equal(revised, (await reader.GetSelectionRevisionsForAssetsAsync(
                showId, [assetId], CancellationToken.None))[assetId]);

            connection.Execute("""
                INSERT INTO bridge_ids (id, entity_id, id_type, id_value)
                VALUES (@bridgeId, @episodeId, 'tvdb_episode_id', '12345');
                """, new { bridgeId = Guid.NewGuid(), episodeId });
            var bridged = (await ReadAsync()).SelectionRevision;
            Assert.NotEqual(revised, bridged);

            connection.Execute("UPDATE media_assets SET library_id = @newLibraryId WHERE id = @assetId;",
                new { newLibraryId = Guid.NewGuid().ToString("D"), assetId });
            var movedLibrary = (await ReadAsync()).SelectionRevision;
            Assert.NotEqual(bridged, movedLibrary);

            connection.Execute("UPDATE media_assets SET edition_id = @otherEditionId WHERE id = @assetId;",
                new { otherEditionId, assetId });
            var movedEdition = (await ReadAsync()).SelectionRevision;
            Assert.NotEqual(movedLibrary, movedEdition);
            Assert.Equal(movedEdition, (await reader.GetSelectionRevisionsForAssetsAsync(
                showId, [assetId], CancellationToken.None))[assetId]);

            var otherShowId = Guid.NewGuid();
            var otherSeasonId = Guid.NewGuid();
            connection.Execute("""
                INSERT INTO works (id, media_type, work_kind, ownership)
                VALUES (@otherShowId, 'TV', 'parent', 'Owned');
                INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ownership)
                VALUES (@otherSeasonId, 'TV', 'parent', @otherShowId, 2, 'Owned');
                UPDATE works SET parent_work_id = @otherSeasonId WHERE id = @episodeId;
                """, new { otherShowId, otherSeasonId, episodeId });
            var movedParent = (await reader.GetSelectionRevisionsForAssetsAsync(
                showId, [assetId], CancellationToken.None))[assetId];
            Assert.NotEqual(movedEdition, movedParent);
            var newParentSearch = await reader.SearchAsync(otherShowId, null, 1, 50,
                null, null, null, null, null, CancellationToken.None);
            Assert.Equal(Assert.Single(newParentSearch!.Items).SelectionRevision, movedParent);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

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
            {
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
            }

            var reader = new MediaEditorOwnedChildReadService(database);
            var segment = Assert.Single(await reader.GetAccessSegmentsAsync(showId, CancellationToken.None));
            var snapshot = await reader.SnapshotMatchingAsync(showId, null, null, null, null,
                null, null, CancellationToken.None, [segment.Key]);
            var filteredSnapshot = await reader.SnapshotMatchingAsync(showId, "original_episode_0999",
                null, null, null, null, null, CancellationToken.None, [segment.Key]);
            var deniedSnapshot = await reader.SnapshotMatchingAsync(showId, null, null, null, null,
                null, null, CancellationToken.None, ["another-library|TV"]);
            var first = await reader.SearchAsync(showId, null, 1, 50, null, null, null, null, null, CancellationToken.None, [segment.Key]);
            var found = await reader.SearchAsync(showId, "original_episode_0999", 1, 50, null, null, null, null, null, CancellationToken.None);
            var denied = await reader.SearchAsync(showId, null, 1, 50, null, null, null, null, null, CancellationToken.None, ["another-library|TV"]);

            Assert.NotNull(first);
            Assert.Equal(1000, first.TotalCount);
            Assert.NotNull(snapshot);
            Assert.False(snapshot.ExceedsLimit);
            Assert.Equal(1000, snapshot.Items.Count);
            Assert.Equal(1000, snapshot.Items.Select(item => item.AssetId).Distinct().Count());
            Assert.All(snapshot.Items, item => Assert.StartsWith("v1:", item.SelectionRevision));
            Assert.Single(filteredSnapshot!.Items);
            Assert.Empty(deniedSnapshot!.Items);
            Assert.Equal(50, first.Items.Count);
            Assert.All(first.Items, item => Assert.True(item.CollapseEdition));
            Assert.Equal(0, denied!.TotalCount);
            Assert.Single(found!.Items);
            Assert.Equal("original_episode_0999.mkv", found.Items[0].SourceFileName);

            var laterAssetId = Guid.NewGuid();
            using (var connection = database.CreateConnection())
            {
                var editionId = connection.QueryFirst<Guid>("SELECT id FROM editions LIMIT 1;");
                connection.Execute("""
                    INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, library_id)
                    VALUES (@laterAssetId, @editionId, 'later-hash', '/fixtures/later.mkv', @libraryId);
                    """, new { laterAssetId, editionId, libraryId });
            }
            Assert.DoesNotContain(snapshot.Items, item => item.AssetId == laterAssetId);
            var overflow = await reader.SnapshotMatchingAsync(showId, null, null, null, null,
                null, null, CancellationToken.None, [segment.Key]);
            Assert.NotNull(overflow);
            Assert.True(overflow.ExceedsLimit);
            Assert.Empty(overflow.Items);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
