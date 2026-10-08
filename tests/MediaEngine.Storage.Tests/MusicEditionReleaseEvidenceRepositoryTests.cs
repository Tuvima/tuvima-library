using System.Text.Json;
using Dapper;
using MediaEngine.Domain;

namespace MediaEngine.Storage.Tests;

public sealed class MusicEditionReleaseEvidenceRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(),
        $"tuvima_release_evidence_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly Guid _album = Guid.NewGuid();
    private readonly Guid _track = Guid.NewGuid();
    private readonly Guid _edition = Guid.NewGuid();
    private readonly Guid _asset = Guid.NewGuid();
    private readonly Guid _original = Guid.NewGuid();
    private readonly Guid _deluxe = Guid.NewGuid();

    public MusicEditionReleaseEvidenceRepositoryTests()
    {
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        using var db = _database.CreateConnection();
        db.Execute("""
            INSERT INTO works(id, media_type, work_kind) VALUES(@album, 'Music', 'parent');
            INSERT INTO works(id, media_type, work_kind, parent_work_id)
            VALUES(@track, 'Music', 'child', @album);
            INSERT INTO editions(id, work_id) VALUES(@edition, @track);
            INSERT INTO media_assets(id, edition_id, content_hash, file_path_root)
            VALUES(@asset, @edition, @hash, '/music/track.flac');
            """, new { album = _album, track = _track, edition = _edition,
            asset = _asset, hash = Guid.NewGuid().ToString("N") });
    }

    [Fact]
    public void AcceptedMusicBrainzContextDoesNotBecomeEditionIdentity()
    {
        SeedAcceptedCandidate(_asset, _original);
        using (var db = _database.CreateConnection())
        {
            db.Execute("""
                    INSERT INTO canonical_values(entity_id, key, value, last_scored_at)
                    VALUES(@album, @key, @release, @now);
                    """, new { album = _album, key = BridgeIdKeys.MusicBrainzReleaseId,
                    release = _original.ToString("D"), now = DateTimeOffset.UtcNow.ToString("O") });
        }

        var assessment = new MusicEditionReleaseEvidenceRepository(_database).Assess(_asset);
        Assert.Equal(MusicEditionReleaseEvidenceStatus.ProviderContextOnly, assessment.Status);
        Assert.Equal(_original.ToString("D"), assessment.ObservedProviderReleaseId);
        Assert.False(assessment.CanPromoteToEdition);
        AssertEditionUnchanged();
    }

    [Fact]
    public void OriginalAndDeluxeAcceptedForOneAlbumRemainDistinctAndAmbiguous()
    {
        SeedAcceptedCandidate(_asset, _original);
        var otherTrack = Guid.NewGuid();
        var otherEdition = Guid.NewGuid();
        var otherAsset = Guid.NewGuid();
        using (var db = _database.CreateConnection())
        {
            db.Execute("""
                    INSERT INTO works(id, media_type, work_kind, parent_work_id)
                    VALUES(@track, 'Music', 'child', @album);
                    INSERT INTO editions(id, work_id) VALUES(@edition, @track);
                    INSERT INTO media_assets(id, edition_id, content_hash, file_path_root)
                    VALUES(@asset, @edition, @hash, '/music/deluxe.flac');
                    """, new { track = otherTrack, album = _album, edition = otherEdition,
                    asset = otherAsset, hash = Guid.NewGuid().ToString("N") });
        }
        SeedAcceptedCandidate(otherAsset, _deluxe);

        var repo = new MusicEditionReleaseEvidenceRepository(_database);
        Assert.Equal(MusicEditionReleaseEvidenceStatus.MixedAlbumReleases,
            repo.Assess(_asset).Status);
        Assert.Equal(MusicEditionReleaseEvidenceStatus.MixedAlbumReleases,
            repo.Assess(otherAsset).Status);
        using var verify = _database.CreateConnection();
        Assert.Equal(2, verify.QuerySingle<int>("""
            SELECT COUNT(DISTINCT e.id) FROM editions e JOIN works w ON w.id=e.work_id
            WHERE w.parent_work_id=@album;
            """, new { album = _album }));
        AssertEditionUnchanged();
    }

    [Fact]
    public void TwoEncodesOnSameTrackCannotBeReconciledByAlbumRelease()
    {
        SeedAcceptedCandidate(_asset, _original);
        var secondEdition = Guid.NewGuid();
        using (var db = _database.CreateConnection())
        {
            db.Execute("""
                    INSERT INTO editions(id, work_id) VALUES(@edition, @track);
                    INSERT INTO media_assets(id, edition_id, content_hash, file_path_root)
                    VALUES(@asset, @edition, @hash, '/music/track.mp3');
                    """, new { edition = secondEdition, track = _track,
                    asset = Guid.NewGuid(), hash = Guid.NewGuid().ToString("N") });
        }
        Assert.Equal(MusicEditionReleaseEvidenceStatus.MultipleEncodes,
            new MusicEditionReleaseEvidenceRepository(_database).Assess(_asset).Status);
        AssertEditionUnchanged();
    }

    [Fact]
    public void TwoFilesAlreadyUnderOneEditionAreNotAssignedAReleaseByGuess()
    {
        SeedAcceptedCandidate(_asset, _original);
        using (var db = _database.CreateConnection())
        {
            db.Execute("""
                    INSERT INTO media_assets(id, edition_id, content_hash, file_path_root)
                    VALUES(@asset, @edition, @hash, '/music/track-second-encode.mp3');
                    """, new { asset = Guid.NewGuid(), edition = _edition,
                    hash = Guid.NewGuid().ToString("N") });
        }
        Assert.Equal(MusicEditionReleaseEvidenceStatus.SharedEdition,
            new MusicEditionReleaseEvidenceRepository(_database).Assess(_asset).Status);
        AssertEditionUnchanged();
    }

    [Fact]
    public void ExistingEditionScopedIdIsReadWithoutCopyingRootIdentity()
    {
        using (var db = _database.CreateConnection())
        {
            db.Execute("""
                    INSERT INTO canonical_values(entity_id, key, value, last_scored_at)
                    VALUES(@edition, @key, @release, @now),
                          (@album, @key, @otherRelease, @now);
                    INSERT INTO bridge_ids(id, entity_id, id_type, id_value)
                    VALUES(@id, @edition, @key, @release);
                    """, new { edition = _edition, album = _album,
                    key = BridgeIdKeys.MusicBrainzReleaseId,
                    release = _original.ToString("D"), otherRelease = _deluxe.ToString("D"),
                    now = DateTimeOffset.UtcNow.ToString("O"), id = Guid.NewGuid() });
        }
        var result = new MusicEditionReleaseEvidenceRepository(_database).Assess(_asset);
        Assert.Equal(MusicEditionReleaseEvidenceStatus.ExistingEditionIdentity, result.Status);
        Assert.Equal(_original.ToString("D"), result.ExistingEditionReleaseId);
        Assert.False(result.CanPromoteToEdition);
    }

    private void SeedAcceptedCandidate(Guid assetId, Guid releaseId)
    {
        var job = Guid.NewGuid();
        var candidate = Guid.NewGuid();
        using var db = _database.CreateConnection();
        db.Execute("""
            INSERT INTO identity_jobs(id, entity_id, entity_type, media_type,
                state, pass, selected_candidate_id)
            VALUES(@job, @assetId, 'MediaAsset', 'Music',
                'RetailMatched', 'Retail', @candidate);
            INSERT INTO retail_match_candidates(id, job_id, provider_id,
                provider_name, title, bridge_ids_json, outcome)
            VALUES(@candidate, @job, @provider, 'musicbrainz', 'Track',
                @bridgeJson, 'AutoAccepted');
            """, new { job, assetId, candidate, provider = Guid.NewGuid(),
            bridgeJson = JsonSerializer.Serialize(new Dictionary<string, string>
            { [BridgeIdKeys.MusicBrainzReleaseId] = releaseId.ToString("D") }) });
    }

    private void AssertEditionUnchanged()
    {
        using var db = _database.CreateConnection();
        Assert.Equal(0, db.QuerySingle<int>("""
            SELECT COUNT(*) FROM canonical_values
            WHERE entity_id=@edition AND key=@key;
            """, new { edition = _edition, key = BridgeIdKeys.MusicBrainzReleaseId }));
        Assert.Equal(0, db.QuerySingle<int>("""
            SELECT COUNT(*) FROM bridge_ids
            WHERE entity_id=@edition AND id_type=@key;
            """, new { edition = _edition, key = BridgeIdKeys.MusicBrainzReleaseId }));
    }

    public void Dispose()
    {
        _database.Dispose();
        try { File.Delete(_path); } catch { }
    }
}
