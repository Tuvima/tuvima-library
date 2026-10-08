using Dapper;
using MediaEngine.Domain;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Tests;

public sealed class MusicTrackRelocationRepositoryTests
{
    [Fact]
    public async Task MoveIsAtomicAndKeepsSiblingAndUserOverrideWithSelectedFile()
    {
        using var fixture = new Fixture();
        using (var conn = fixture.Database.CreateConnection())
        {
            conn.Execute("""
                INSERT INTO canonical_values(entity_id,key,value,last_scored_at,winning_provider_id)
                VALUES(@work,'composer','My composer correction',datetime('now'),@manual);
                UPDATE works SET wikidata_qid='Q123' WHERE id=@album;
                INSERT INTO bridge_ids(id,entity_id,id_type,id_value) VALUES(@id,@album,'musicbrainz_release_id',@oldRelease);
                INSERT INTO bridge_ids(id,entity_id,id_type,id_value) VALUES(@assetBridge,@asset,'musicbrainz_release_id',@oldRelease);
                INSERT INTO canonical_values(entity_id,key,value,last_scored_at,winning_provider_id)
                VALUES(@work,'description','Source album description',datetime('now'),@manual);
                """, new { work = fixture.Work, album = fixture.Album, manual = WellKnownProviders.UserManual,
                    id = Guid.NewGuid(), asset = fixture.Asset, assetBridge = Guid.NewGuid(), oldRelease = Guid.NewGuid().ToString("D") });
        }
        var repository = new MusicTrackRelocationRepository(fixture.Database);
        var result = await repository.CommitAsync(fixture.Move);
        Assert.Equal(MediaEditorCommitOutcome.Committed, result.Outcome);
        var target = result.Items.Single().TargetWorkId;
        Assert.NotEqual(fixture.Work, target);
        using var verify = fixture.Database.CreateConnection();
        Assert.Equal(3d, verify.QuerySingle<double>("SELECT ordinal_sort FROM works WHERE id=@target", new { target }));
        Assert.Equal(fixture.Edition, verify.QuerySingle<Guid>("SELECT edition_id FROM media_assets WHERE id=@id", new { id = fixture.Sibling }));
        Assert.NotEqual(fixture.Edition, verify.QuerySingle<Guid>("SELECT edition_id FROM media_assets WHERE id=@id", new { id = fixture.Asset }));
        Assert.Equal("My composer correction", verify.QuerySingle<string>("SELECT value FROM canonical_values WHERE entity_id=@target AND key='composer'", new { target }));
        Assert.Equal("Q123", verify.QuerySingle<string>("SELECT wikidata_qid FROM works WHERE id=@id", new { id = fixture.Album }));
        Assert.Null(verify.QuerySingle<string?>("SELECT wikidata_qid FROM works WHERE id=@target", new { target }));
        Assert.Equal(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM canonical_values WHERE entity_id=@target AND key='description'", new { target }));
        Assert.Equal(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM bridge_ids WHERE entity_id=@asset", new { asset = fixture.Asset }));
        Assert.Equal(1, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM identity_jobs WHERE entity_id=@id AND state='RetailMatched'", new { id = fixture.Asset }));
        Assert.Equal(1, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM media_file_write_intents WHERE asset_id=@id AND status='pending'", new { id = fixture.Asset }));
        Assert.Equal(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM media_file_write_intents WHERE asset_id=@id", new { id = fixture.Sibling }));
        Assert.Equal(MediaEditorCommitOutcome.Replayed, (await repository.CommitAsync(fixture.Move)).Outcome);
        Assert.Equal(1, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM system_activity"));
    }

    [Fact]
    public async Task ExistingOwnedDestinationCannotBeOverwritten()
    {
        using var fixture = new Fixture();
        await new MusicTrackRelocationRepository(fixture.Database).CommitAsync(fixture.Move);
        var siblingMove = fixture.Move with { AssetId = fixture.Sibling, OperationToken = Guid.NewGuid().ToString("D") };
        var result = await new MusicTrackRelocationRepository(fixture.Database).CommitAsync(siblingMove);
        Assert.Equal(MediaEditorCommitOutcome.Conflict, result.Outcome);
        using var verify = fixture.Database.CreateConnection();
        Assert.Equal(fixture.Edition, verify.QuerySingle<Guid>("SELECT edition_id FROM media_assets WHERE id=@id", new { id = fixture.Sibling }));
        Assert.Equal(1, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM media_editor_music_pairing_commits"));
    }

    [Fact]
    public async Task ChangedSourceIdentityDoesNotCreateDestinationOrJobs()
    {
        using var fixture = new Fixture();
        var result = await new MusicTrackRelocationRepository(fixture.Database).CommitAsync(fixture.Move with { IdentityRevision = "stale" });
        Assert.Equal(MediaEditorCommitOutcome.Conflict, result.Outcome);
        using var verify = fixture.Database.CreateConnection();
        Assert.Equal(2, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM works"));
        Assert.Equal(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM identity_jobs"));
        Assert.Equal(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM media_file_write_intents"));
    }

    [Fact]
    public async Task LeasedEnrichmentCannotRaceTheSelectedFileMove()
    {
        using var fixture = new Fixture();
        using (var conn = fixture.Database.CreateConnection())
        {
            conn.Execute("""
                    INSERT INTO identity_jobs(id,entity_id,entity_type,media_type,state,pass,lease_owner,lease_expires_at)
                    VALUES(@id,@asset,'MediaAsset','Music','RetailMatched','Quick','worker',@expires);
                    """, new { id = Guid.NewGuid(), asset = fixture.Asset, expires = DateTimeOffset.UtcNow.AddMinutes(5).ToString("O") });
        }
        var result = await new MusicTrackRelocationRepository(fixture.Database).CommitAsync(fixture.Move);
        Assert.Equal(MediaEditorCommitOutcome.Conflict, result.Outcome);
        using var verify = fixture.Database.CreateConnection();
        Assert.Equal(fixture.Edition, verify.QuerySingle<Guid>("SELECT edition_id FROM media_assets WHERE id=@asset", new { asset = fixture.Asset }));
        Assert.Equal(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM media_editor_music_pairing_commits"));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima_music_move_{Guid.NewGuid():N}.db");
        public DatabaseConnection Database { get; }
        public Guid Album { get; } = Guid.NewGuid(); public Guid Work { get; } = Guid.NewGuid();
        public Guid Edition { get; } = Guid.NewGuid(); public Guid Asset { get; } = Guid.NewGuid();
        public Guid Sibling { get; } = Guid.NewGuid(); public Guid Library { get; } = Guid.NewGuid();
        public VerifiedMusicTrackRelocation Move { get; }
        public Fixture()
        {
            Database = new DatabaseConnection(_path); Database.InitializeSchema();
            using var conn = Database.CreateConnection();
            conn.Execute("""
                INSERT INTO works(id,media_type,work_kind,ownership) VALUES(@Album,'Music','parent','Owned');
                INSERT INTO works(id,media_type,work_kind,parent_work_id,ownership) VALUES(@Work,'Music','child',@Album,'Owned');
                INSERT INTO editions(id,work_id,format_label) VALUES(@Edition,@Work,'FLAC');
                INSERT INTO media_assets(id,edition_id,content_hash,file_path_root,library_id) VALUES
                  (@Asset,@Edition,'selected-hash','selected.flac',@library),(@Sibling,@Edition,'sibling-hash','sibling.mp3',@library);
                """, new { Album, Work, Edition, Asset, Sibling, library = Library.ToString("D") });
            Move = new(Guid.NewGuid().ToString("D"), Asset, Edition, Work, Library, "",
                Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
                "Original album", "Artist", "Original track", 1, 3, "{}");
        }
        public void Dispose() { Database.Dispose(); try { File.Delete(_path); } catch { } }
    }
}
