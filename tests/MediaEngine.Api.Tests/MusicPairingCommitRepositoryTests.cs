using Dapper;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Tests;

public sealed class MusicPairingCommitRepositoryTests
{
    [Fact]
    public async Task ExactReleaseTracks_PersistSeparately_AndReplay()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima_music_pairing_{Guid.NewGuid():N}.db");
        using var database = new DatabaseConnection(path);
        try
        {
            database.InitializeSchema();
            var album = Guid.NewGuid();
            var first = Seed(database, album, "original.flac");
            var second = Seed(database, album, "deluxe.flac");
            var originalRelease = Guid.NewGuid().ToString("D");
            var deluxeRelease = Guid.NewGuid().ToString("D");
            var repeatedRecording = Guid.NewGuid().ToString("D");
            using (var connection = database.CreateConnection())
                connection.Execute("""
                    INSERT INTO canonical_values(entity_id,key,value,last_scored_at) VALUES
                      (@firstWork,'musicbrainz_recording_id',@recording,datetime('now')),
                      (@secondWork,'musicbrainz_recording_id',@recording,datetime('now'));
                    """, new { firstWork = first.Work, secondWork = second.Work, recording = repeatedRecording });
            var operation = Guid.NewGuid().ToString("D");
            var plan = new[]
            {
                Pair(operation, first, album, originalRelease, Guid.NewGuid().ToString("D")),
                Pair(operation, second, album, deluxeRelease, Guid.NewGuid().ToString("D")),
            };
            var repository = new MusicPairingCommitRepository(database);
            Assert.Equal(MediaEditorCommitOutcome.Committed, (await repository.CommitAsync(plan)).Outcome);
            using (var verify = database.CreateConnection())
            {
                Assert.Equal(originalRelease, verify.QuerySingle<string>(
                    "SELECT id_value FROM bridge_ids WHERE entity_id=@id AND id_type='musicbrainz_release_id'", new { id = first.Edition }));
                Assert.Equal(deluxeRelease, verify.QuerySingle<string>(
                    "SELECT id_value FROM bridge_ids WHERE entity_id=@id AND id_type='musicbrainz_release_id'", new { id = second.Edition }));
                Assert.Equal(2, verify.ExecuteScalar<int>(
                    "SELECT COUNT(DISTINCT id_value) FROM bridge_ids WHERE id_type='musicbrainz_release_track_id'"));
                Assert.Equal(2, verify.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM canonical_values WHERE key='musicbrainz_recording_id' AND value=@recording",
                    new { recording = repeatedRecording }));
            }
            Assert.Equal(MediaEditorCommitOutcome.Replayed, (await repository.CommitAsync(plan)).Outcome);
            Assert.Equal(MediaEditorCommitOutcome.Conflict, (await repository.CommitAsync(
                [plan[0] with { ReleaseTrackId = Guid.NewGuid().ToString("D") }])).Outcome);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public async Task ConflictingRelease_RollsBackWholePlan()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima_music_pairing_rollback_{Guid.NewGuid():N}.db");
        using var database = new DatabaseConnection(path);
        try
        {
            database.InitializeSchema();
            var album = Guid.NewGuid();
            var valid = Seed(database, album, "valid.flac");
            var conflicting = Seed(database, album, "conflict.flac");
            using (var connection = database.CreateConnection())
                connection.Execute("""
                    INSERT INTO bridge_ids(id,entity_id,id_type,id_value)
                    VALUES(@id,@edition,'musicbrainz_release_id',@release);
                    """, new { id = Guid.NewGuid(), edition = conflicting.Edition, release = Guid.NewGuid().ToString("D") });
            var operation = Guid.NewGuid().ToString("D");
            var requestedRelease = Guid.NewGuid().ToString("D");
            var result = await new MusicPairingCommitRepository(database).CommitAsync([
                Pair(operation, valid, album, requestedRelease, Guid.NewGuid().ToString("D")),
                Pair(operation, conflicting, album, requestedRelease, Guid.NewGuid().ToString("D"))]);
            Assert.Equal(MediaEditorCommitOutcome.Conflict, result.Outcome);
            using var verify = database.CreateConnection();
            Assert.Equal(0, verify.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM bridge_ids WHERE entity_id=@work AND id_type='musicbrainz_release_track_id'", new { work = valid.Work }));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public async Task UnselectedFileSharingTrackIdentity_PreventsAnyIdentityWrite()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima_music_pairing_sibling_{Guid.NewGuid():N}.db");
        using var database = new DatabaseConnection(path);
        try
        {
            database.InitializeSchema();
            var album = Guid.NewGuid();
            var selected = Seed(database, album, "selected.flac");
            using (var connection = database.CreateConnection())
                connection.Execute("""
                    INSERT INTO media_assets(id,edition_id,content_hash,file_path_root,library_id)
                    VALUES(@id,@edition,@hash,'unselected.mp3',@library);
                    """, new { id = Guid.NewGuid(), edition = selected.Edition,
                        hash = Guid.NewGuid().ToString("N"), library = selected.Library.ToString("D") });

            var result = await new MusicPairingCommitRepository(database).CommitAsync([
                Pair(Guid.NewGuid().ToString("D"), selected, album,
                    Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"))]);

            Assert.Equal(MediaEditorCommitOutcome.Conflict, result.Outcome);
            using var verify = database.CreateConnection();
            Assert.Equal(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM bridge_ids"));
            Assert.Equal(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM media_editor_music_pairing_commits"));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    private static (Guid Asset, Guid Edition, Guid Work, Guid Library) Seed(DatabaseConnection database,
        Guid album, string file)
    {
        var value = (Asset: Guid.NewGuid(), Edition: Guid.NewGuid(), Work: Guid.NewGuid(), Library: Guid.NewGuid());
        using var connection = database.CreateConnection();
        connection.Execute("""
            INSERT OR IGNORE INTO works(id,media_type,work_kind,ownership) VALUES(@album,'Music','parent','Owned');
            INSERT INTO works(id,media_type,work_kind,parent_work_id,ownership) VALUES(@Work,'Music','child',@album,'Owned');
            INSERT INTO editions(id,work_id) VALUES(@Edition,@Work);
            INSERT INTO media_assets(id,edition_id,content_hash,file_path_root,library_id)
            VALUES(@Asset,@Edition,@hash,@file,@library);
            """, new { album, value.Work, value.Edition, value.Asset, hash = Guid.NewGuid().ToString("N"), file,
                library = value.Library.ToString("D") });
        return value;
    }

    private static VerifiedMusicReleaseTrackPairing Pair(string operation,
        (Guid Asset, Guid Edition, Guid Work, Guid Library) value, Guid album,
        string release, string track) => new(operation, value.Asset, value.Edition, value.Work,
            album, value.Library, "", release, track);
}
