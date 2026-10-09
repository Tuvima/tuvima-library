using Dapper;
using MediaEngine.Api.Services.Matching;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

public sealed class PairingAssetReadServiceTests
{
    [Fact]
    public void TvEpisode_UsesShowIdentityAndIgnoresUnavailableFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima_pairing_tv_{Guid.NewGuid():N}.db");
        using var database = new DatabaseConnection(path);
        try
        {
            database.InitializeSchema();
            var show = Guid.NewGuid();
            var season = Guid.NewGuid();
            var episode = Guid.NewGuid();
            var edition = Guid.NewGuid();
            var owned = Guid.NewGuid();
            var orphaned = Guid.NewGuid();
            using (var connection = database.CreateConnection())
            {
                connection.Execute("""
                    INSERT INTO works (id, media_type, work_kind, ownership) VALUES (@show, 'TV', 'parent', 'Owned');
                    INSERT INTO works (id, media_type, work_kind, parent_work_id, ownership) VALUES
                      (@season, 'TV', 'parent', @show, 'Owned'),
                      (@episode, 'TV', 'child', @season, 'Owned');
                    INSERT INTO editions (id, work_id) VALUES (@edition, @episode);
                    INSERT INTO media_assets (id, edition_id, content_hash, file_path_root) VALUES
                      (@owned, @edition, 'pairing-owned', '/tv/Show.S01E01.mkv'),
                      (@orphaned, @edition, 'pairing-orphaned', '/tv/old.mkv');
                    UPDATE media_assets SET is_orphaned = 1 WHERE id = @orphaned;
                    INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES
                      (@show, 'tvdb_id', '42', '2026-01-01'),
                      (@episode, 'tvdb_episode_id', '101', '2026-01-01');
                    INSERT INTO bridge_ids (id, entity_id, id_type, id_value) VALUES
                      (@bridge, @show, 'tvdb_id', '42');
                    """, new { show, season, episode, edition, owned, orphaned, bridge = Guid.NewGuid() });
            }

            using (var connection = database.CreateConnection())
            {
                Assert.Equal(1, connection.QuerySingle<int>("SELECT COUNT(*) FROM media_assets WHERE id = @owned", new { owned }));
                Assert.Equal(2, connection.QuerySingle<int>("SELECT COUNT(*) FROM media_assets WHERE id IN @ids", new { ids = new[] { GuidSql.ToBlob(owned), GuidSql.ToBlob(orphaned) } }));
            }

            var result = new PairingAssetReadService(database).Load([owned, orphaned], CancellationToken.None);
            var row = Assert.Single(result).Value;
            Assert.Equal(show, row.RootWorkId);
            Assert.Equal("42", row.TvdbSeriesId);
            Assert.Equal("42", row.TvdbSeriesBridgeId);
            Assert.Equal("101", row.TvdbEpisodeId);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void MusicTrack_UsesExactAlbumRatherThanArtistGrandparent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima_pairing_music_{Guid.NewGuid():N}.db");
        using var database = new DatabaseConnection(path);
        try
        {
            database.InitializeSchema();
            var artist = Guid.NewGuid();
            var album = Guid.NewGuid();
            var track = Guid.NewGuid();
            var edition = Guid.NewGuid();
            var asset = Guid.NewGuid();
            var releaseId = Guid.NewGuid().ToString("D");
            using (var connection = database.CreateConnection())
            {
                connection.Execute("""
                    INSERT INTO works (id, media_type, work_kind, ownership) VALUES (@artist, 'Music', 'parent', 'Owned');
                    INSERT INTO works (id, media_type, work_kind, parent_work_id, ownership) VALUES
                      (@album, 'Music', 'parent', @artist, 'Owned'),
                      (@track, 'Music', 'child', @album, 'Owned');
                    INSERT INTO editions (id, work_id) VALUES (@edition, @track);
                    INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                    VALUES (@asset, @edition, 'pairing-music', '/music/Disc 2/01 - Opening.flac');
                    INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                    VALUES (@album, 'musicbrainz_release_id', @releaseId, '2026-01-01');
                    INSERT INTO bridge_ids (id, entity_id, id_type, id_value)
                    VALUES (@bridge, @album, 'musicbrainz_release_id', @releaseId);
                    """, new { artist, album, track, edition, asset, releaseId, bridge = Guid.NewGuid() });
            }

            var row = Assert.Single(new PairingAssetReadService(database).Load([asset], CancellationToken.None)).Value;
            Assert.Equal(album, row.RootWorkId);
            Assert.Null(row.MusicBrainzReleaseId);
            Assert.Null(row.MusicBrainzReleaseBridgeId);
            Assert.Equal(releaseId, row.AlbumContextReleaseId);
            Assert.Equal(releaseId, row.AlbumContextReleaseBridgeId);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void MusicReleaseEvidence_MustBeEditionScopedWhenOneRecordingAppearsOnTwoReleases()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima_pairing_release_scope_{Guid.NewGuid():N}.db");
        using var database = new DatabaseConnection(path);
        try
        {
            database.InitializeSchema();
            var album = Guid.NewGuid();
            var track = Guid.NewGuid();
            var originalEdition = Guid.NewGuid();
            var deluxeEdition = Guid.NewGuid();
            var unscopedEdition = Guid.NewGuid();
            var originalAsset = Guid.NewGuid();
            var deluxeAsset = Guid.NewGuid();
            var unscopedAsset = Guid.NewGuid();
            var originalRelease = Guid.NewGuid().ToString("D");
            var deluxeRelease = Guid.NewGuid().ToString("D");
            var recording = Guid.NewGuid().ToString("D");
            using (var connection = database.CreateConnection())
            {
                connection.Execute("""
                    INSERT INTO works (id, media_type, work_kind, ownership) VALUES
                      (@album, 'Music', 'parent', 'Owned'),
                      (@track, 'Music', 'child', 'Owned');
                    UPDATE works SET parent_work_id = @album WHERE id = @track;
                    INSERT INTO editions (id, work_id) VALUES
                      (@originalEdition, @track), (@deluxeEdition, @track), (@unscopedEdition, @track);
                    INSERT INTO media_assets (id, edition_id, content_hash, file_path_root) VALUES
                      (@originalAsset, @originalEdition, 'original-release', '/music/original.flac'),
                      (@deluxeAsset, @deluxeEdition, 'deluxe-release', '/music/deluxe.flac'),
                      (@unscopedAsset, @unscopedEdition, 'unscoped-release', '/music/unscoped.flac');
                    INSERT INTO bridge_ids (id, entity_id, id_type, id_value) VALUES
                      (@albumBridge, @album, 'musicbrainz_release_id', @originalRelease),
                      (@originalBridge, @originalEdition, 'musicbrainz_release_id', @originalRelease),
                      (@deluxeBridge, @deluxeEdition, 'musicbrainz_release_id', @deluxeRelease);
                    INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES
                      (@album, 'musicbrainz_release_id', @originalRelease, '2026-01-01'),
                      (@originalEdition, 'musicbrainz_release_id', @originalRelease, '2026-01-01'),
                      (@deluxeEdition, 'musicbrainz_release_id', @deluxeRelease, '2026-01-01'),
                      (@track, 'musicbrainz_recording_id', @recording, '2026-01-01');
                    """, new
                {
                    album,
                    track,
                    originalEdition,
                    deluxeEdition,
                    unscopedEdition,
                    originalAsset,
                    deluxeAsset,
                    unscopedAsset,
                    originalRelease,
                    deluxeRelease,
                    recording,
                    albumBridge = Guid.NewGuid(),
                    originalBridge = Guid.NewGuid(),
                    deluxeBridge = Guid.NewGuid()
                });
            }

            var rows = new PairingAssetReadService(database).Load(
                [originalAsset, deluxeAsset, unscopedAsset], CancellationToken.None);
            Assert.Equal(track, rows[originalAsset].WorkId);
            Assert.Equal(track, rows[deluxeAsset].WorkId);
            Assert.Equal(recording, rows[originalAsset].RecordingId);
            Assert.Equal(recording, rows[deluxeAsset].RecordingId);
            Assert.Equal(originalRelease, rows[originalAsset].MusicBrainzReleaseBridgeId);
            Assert.Equal(deluxeRelease, rows[deluxeAsset].MusicBrainzReleaseBridgeId);
            // Parent context remains visible, but cannot establish which
            // release-track Edition this file belongs to.
            Assert.Null(rows[unscopedAsset].MusicBrainzReleaseId);
            Assert.Null(rows[unscopedAsset].MusicBrainzReleaseBridgeId);
            Assert.Equal(originalRelease, rows[unscopedAsset].AlbumContextReleaseBridgeId);
            Assert.Equal(unscopedEdition, rows[unscopedAsset].EditionId);
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
