using Dapper;
using MediaEngine.Api.Services;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The album sweeps pick their own files from the data store, with no identity job to stop them. Files in a library that never
/// looks anything up (Other audio) are left out of what they pick, so MusicBrainz is never asked about them and no cover is
/// downloaded (Library setup D38).
/// </summary>
public sealed class MusicSweepPlainKindTests : IDisposable
{
    private const string PlainLibrary = "dddddddd-0000-4000-8000-000000000001";
    private const string MusicLibrary = "dddddddd-0000-4000-8000-000000000002";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-music-plain-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly Guid _plainAlbum = Guid.NewGuid();
    private readonly Guid _plainAsset = Guid.NewGuid();
    private readonly Guid _musicAlbum = Guid.NewGuid();
    private readonly Guid _musicAsset = Guid.NewGuid();

    public MusicSweepPlainKindTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        Seed(_plainAlbum, _plainAsset, PlainLibrary, "Recording one");
        Seed(_musicAlbum, _musicAsset, MusicLibrary, "Come Together");
    }

    public void Dispose()
    {
        _database.Dispose();
        DatabaseConnection.ReleasePooledConnections(_databasePath);
        File.Delete(_databasePath);
    }

    [Fact]
    public void ManifestRepair_LeavesAlbumsInAPlainKindLibraryOut_ButStillRepairsOthers()
    {
        var sweep = new MusicAlbumManifestHostedService(
            _database, null!, null!, null!, null!, NullLogger<MusicAlbumManifestHostedService>.Instance, new BlockingGuard(PlainLibrary));

        var albums = sweep.LoadCandidates(CancellationToken.None).Select(candidate => candidate.RootWorkId).ToArray();

        Assert.DoesNotContain(_plainAlbum, albums);
        Assert.Contains(_musicAlbum, albums);
    }

    [Fact]
    public async Task MusicBrainzEnrichment_LeavesTracksInAPlainKindLibraryOut_ButStillPicksOthers()
    {
        var sweep = new MusicBrainzEnrichmentHostedService(
            _database, [], null!, null!, null!, null!, null!, null!, null!, null!, null!,
            NullLogger<MusicBrainzEnrichmentHostedService>.Instance, new BlockingGuard(PlainLibrary));

        var assets = (await sweep.LoadCandidatesAsync(CancellationToken.None)).Select(candidate => candidate.AssetId).ToArray();

        Assert.DoesNotContain(_plainAsset, assets);
        Assert.Contains(_musicAsset, assets);
    }

    [Fact]
    public async Task APlainKindLibraryDoesNotCrowdTheBatch_SoOtherMusicIsStillReached()
    {
        // 30 plain tracks (more than the batch of 25) sort ahead of the music track by album id; the music track must still be picked.
        for (var index = 0; index < 30; index++)
        {
            Seed(Guid.Parse($"00000000-0000-4000-8000-{index:D12}"), Guid.NewGuid(), PlainLibrary, $"Take {index}");
        }

        var sweep = new MusicBrainzEnrichmentHostedService(
            _database, [], null!, null!, null!, null!, null!, null!, null!, null!, null!,
            NullLogger<MusicBrainzEnrichmentHostedService>.Instance, new BlockingGuard(PlainLibrary));

        var assets = (await sweep.LoadCandidatesAsync(CancellationToken.None)).Select(candidate => candidate.AssetId).ToArray();

        Assert.Contains(_musicAsset, assets);
    }

    [Fact]
    public async Task WithNoGuardEveryTrackIsPicked_AsBefore()
    {
        var sweep = new MusicBrainzEnrichmentHostedService(
            _database, [], null!, null!, null!, null!, null!, null!, null!, null!, null!,
            NullLogger<MusicBrainzEnrichmentHostedService>.Instance);

        var assets = (await sweep.LoadCandidatesAsync(CancellationToken.None)).Select(candidate => candidate.AssetId).ToArray();

        Assert.Contains(_plainAsset, assets);
        Assert.Contains(_musicAsset, assets);
    }

    private void Seed(Guid album, Guid asset, string libraryId, string title)
    {
        var track = Guid.NewGuid();
        var edition = Guid.NewGuid();
        var stamp = DateTimeOffset.UtcNow.ToString("O");
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO works (id, media_type, work_kind) VALUES (@album, 'Music', 'parent');
            INSERT INTO works (id, media_type, work_kind, parent_work_id) VALUES (@track, 'Music', 'child', @album);
            INSERT INTO editions (id, work_id) VALUES (@edition, @track);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status, presented_at, library_id)
            VALUES (@asset, @edition, @hash, @path, 'Normal', @stamp, @libraryId);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@asset, 'title', @title, @stamp);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@album, 'album', 'Album', @stamp);
            """,
            new { album, track, edition, asset, stamp, libraryId, title, hash = asset.ToString("N"), path = $"music/{asset:N}.flac" });
    }

    private sealed class BlockingGuard(params string[] blockedLibraries) : IExternalLookupGuard
    {
        public bool BlocksLibrary(string? libraryId) =>
            libraryId is not null && blockedLibraries.Contains(libraryId, StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<string> GetBlockedLibraryIds() => blockedLibraries;

        public Task<bool> BlocksEntityAsync(Guid entityId, CancellationToken ct = default) =>
            throw new NotSupportedException("Choosing candidates only uses the list of blocked libraries.");
    }
}
