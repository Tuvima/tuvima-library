using Dapper;
using MediaEngine.Api.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The album sweeps (manifest repair, MusicBrainz enrichment) run on their own timers. While a track's identity job
/// is still running they leave its album alone, so which cover or release the album ends with does not depend on
/// whether a sweep happened to run mid-identification.
/// </summary>
public sealed class MusicSweepActiveIdentityTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-music-sweep-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly Guid _album = Guid.NewGuid();
    private readonly Guid _asset = Guid.NewGuid();

    public MusicSweepActiveIdentityTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        var track = Guid.NewGuid();
        var edition = Guid.NewGuid();
        var stamp = DateTimeOffset.UtcNow.ToString("O");
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO works (id, media_type, work_kind) VALUES (@album, 'Music', 'parent');
            INSERT INTO works (id, media_type, work_kind, parent_work_id) VALUES (@track, 'Music', 'child', @album);
            INSERT INTO editions (id, work_id) VALUES (@edition, @track);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status, presented_at)
            VALUES (@asset, @edition, 'come-together', 'music/The Beatles - Come Together.flac', 'Normal', @stamp);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@asset, 'title', 'Come Together', @stamp);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@album, 'album', 'Abbey Road', @stamp);
            """,
            new { album = _album, track, edition, asset = _asset, stamp });
    }

    public void Dispose()
    {
        _database.Dispose();
        DatabaseConnection.ReleasePooledConnections(_databasePath);
        File.Delete(_databasePath);
    }

    [Theory]
    [InlineData("Queued", false)]
    [InlineData("RetailSearching", false)]
    [InlineData("BridgeSearching", false)]
    [InlineData("Ready", true)]
    [InlineData("ReadyWithoutUniverse", true)]
    public void ManifestRepair_AlbumWithATrackStillBeingIdentified_IsLeftAlone(string jobState, bool expectCandidate)
    {
        AddJob(jobState);
        var sweep = new MusicAlbumManifestHostedService(
            _database, null!, null!, null!, null!, NullLogger<MusicAlbumManifestHostedService>.Instance);

        var candidates = sweep.LoadCandidates(CancellationToken.None);

        Assert.Equal(expectCandidate, candidates.Any(candidate => candidate.RootWorkId == _album));
    }

    [Theory]
    [InlineData("Queued", false)]
    [InlineData("Hydrating", false)]
    [InlineData("Ready", true)]
    public async Task MusicBrainzEnrichment_TrackStillBeingIdentified_IsLeftAlone(string jobState, bool expectCandidate)
    {
        AddJob(jobState);
        var sweep = new MusicBrainzEnrichmentHostedService(
            _database, [], null!, null!, null!, null!, null!, null!, null!, null!, null!,
            NullLogger<MusicBrainzEnrichmentHostedService>.Instance);

        var candidates = await sweep.LoadCandidatesAsync(CancellationToken.None);

        Assert.Equal(expectCandidate, candidates.Any(candidate => candidate.AssetId == _asset));
    }

    private void AddJob(string state)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO identity_jobs (id, entity_id, entity_type, media_type, state, pass) VALUES (@id, @asset, 'MediaAsset', 'Music', @state, 'Quick')",
            new { id = Guid.NewGuid(), asset = _asset, state });
    }
}
