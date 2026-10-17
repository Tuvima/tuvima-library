using System.Text.Json;
using Dapper;
using MediaEngine.Domain.Models;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// The file facts the Details tab shows (size, added date, bitrate, tracks) come only from rows the
/// Engine already stores. The files named here do not exist on disk, so any fact that appears proves
/// it was read from the data store and nothing was probed.
/// </summary>
public sealed class StoredFileFactsTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;

    public StoredFileFactsTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_stored_file_facts_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { /* temp file cleanup after the test; nothing depends on it */ }
        try { File.Delete(_dbPath); } catch { /* temp file cleanup after the test; nothing depends on it */ }
    }

    private Guid AddWork(string mediaType = "Movies")
    {
        var workId = Guid.NewGuid();
        using var connection = _db.CreateConnection();
        connection.Execute("INSERT INTO works (id, media_type, work_kind, ownership) VALUES (@workId, @mediaType, 'standalone', 'Owned');", new { workId, mediaType });
        connection.Execute("INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@workId, 'title', 'Test', datetime('now'));", new { workId });
        return workId;
    }

    private Guid AddAsset(Guid workId, string fileName, string hash, string? presentedAt = null, long? renditionBitrate = null)
    {
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var connection = _db.CreateConnection();
        connection.Execute("INSERT INTO editions (id, work_id, format_label) VALUES (@editionId, @workId, NULL);", new { editionId, workId });
        connection.Execute("""
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, rendition_bitrate_bps)
            VALUES (@assetId, @editionId, @hash, @path, @presentedAt, @renditionBitrate);
            """, new { assetId, editionId, hash, path = $"/gone/{fileName}", presentedAt, renditionBitrate });
        return assetId;
    }

    private void CacheInspection(Guid assetId, string hash, long? size, double? duration, MediaProbeResult probe)
    {
        using var connection = _db.CreateConnection();
        connection.Execute("""
            INSERT INTO playback_inspection_cache (asset_id, source_hash, inspected_at, file_size, duration_secs, container, metadata_json)
            VALUES (@assetId, @hash, datetime('now'), @size, @duration, 'mkv', @json);
            """, new { assetId, hash, size, duration, json = JsonSerializer.Serialize(probe) });
    }

    private void CacheHash(string hash, long size)
    {
        using var connection = _db.CreateConnection();
        connection.Execute("""
            INSERT INTO file_hash_cache (absolute_path, size_bytes, mtime_utc, sha256, cached_at)
            VALUES (@path, @size, '2026-01-01T00:00:00Z', @hash, '2026-01-01T00:00:00Z');
            """, new { path = $"/old-place/{hash}.bin", size, hash });
    }

    private void LogIngestion(Guid assetId, string createdAt)
    {
        using var connection = _db.CreateConnection();
        connection.Execute("""
            INSERT INTO ingestion_log (id, file_path, media_asset_id, created_at)
            VALUES (@id, '/gone/x', @assetId, @createdAt);
            """, new { id = Guid.NewGuid(), assetId, createdAt });
    }

    private async Task<List<MediaEngine.Contracts.Metadata.MediaEditorAssetSelectorDto>> AssetsAsync(Guid workId)
    {
        var result = await new MediaEditorWorkVersionReadRepository(_db).GetAsync(workId, CancellationToken.None);
        return result!.Editions.SelectMany(edition => edition.Assets).ToList();
    }

    [Fact]
    public async Task OneFile_ReadsSizeBitrateTracksAndAddedDateFromTheStoredRows()
    {
        var workId = AddWork();
        var assetId = AddAsset(workId, "movie.mkv", "hash-one", presentedAt: "2026-10-16T09:15:00.0000000+00:00");
        CacheInspection(assetId, "hash-one", size: 1_500_000_000, duration: 2_000, new MediaProbeResult
        {
            VideoCodec = "hevc",
            AudioStreams = [new MediaProbeAudioStream(1, "ac3", "eng", true), new MediaProbeAudioStream(2, "aac", "jpn", false)],
            SubtitleLanguages = ["eng", "fra", "ENG"],
        });

        var asset = Assert.Single(await AssetsAsync(workId));

        Assert.Equal(1_500_000_000, asset.FileSizeBytes);
        Assert.Equal(6_000_000, asset.BitrateBps);
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 9, 15, 0, TimeSpan.Zero), asset.AddedAt);
        Assert.Equal(["eng", "jpn"], asset.AudioTracks.Select(track => track.Language));
        Assert.Equal(["ac3", "aac"], asset.AudioTracks.Select(track => track.Codec));
        Assert.True(asset.AudioTracks[0].IsDefault);
        Assert.Equal(["eng", "fra"], asset.SubtitleLanguages);
    }

    [Fact]
    public async Task SeveralFiles_EachGetTheirOwnFactsAndTheAddedDateFallsBackToTheEarliestIngestionRecord()
    {
        var workId = AddWork();
        var first = AddAsset(workId, "a.mkv", "hash-a", presentedAt: "2026-05-01T00:00:00Z");
        var second = AddAsset(workId, "b.mp4", "hash-b");
        var third = AddAsset(workId, "c.avi", "hash-c", renditionBitrate: 256_000);
        CacheInspection(first, "hash-a", size: 3_000, duration: 1, new MediaProbeResult());
        CacheHash("hash-b", 5_000);
        LogIngestion(second, "2026-07-09T12:00:00.000Z");
        LogIngestion(second, "2026-03-04T08:30:00.000Z");
        LogIngestion(first, "2025-01-01T00:00:00.000Z");

        var assets = (await AssetsAsync(workId)).ToDictionary(asset => asset.AssetId);

        Assert.Equal(3_000, assets[first].FileSizeBytes);
        Assert.Equal(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), assets[first].AddedAt);

        Assert.Equal(5_000, assets[second].FileSizeBytes);
        Assert.Equal(new DateTimeOffset(2026, 3, 4, 8, 30, 0, TimeSpan.Zero), assets[second].AddedAt);
        Assert.Null(assets[second].BitrateBps);
        Assert.Empty(assets[second].AudioTracks);

        Assert.Null(assets[third].FileSizeBytes);
        Assert.Null(assets[third].AddedAt);
        Assert.Equal(256_000, assets[third].BitrateBps);
    }

    [Fact]
    public async Task MissingOrDamagedCache_LeavesFactsOutInsteadOfFailingOrProbing()
    {
        var workId = AddWork("Music");
        var plain = AddAsset(workId, "plain.flac", "hash-plain");
        var damaged = AddAsset(workId, "damaged.flac", "hash-damaged");
        using (var connection = _db.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO playback_inspection_cache (asset_id, source_hash, inspected_at, metadata_json)
                VALUES (@damaged, 'hash-damaged', datetime('now'), '{not json');
                """, new { damaged });
        }

        var assets = (await AssetsAsync(workId)).ToDictionary(asset => asset.AssetId);

        foreach (var id in new[] { plain, damaged })
        {
            Assert.Null(assets[id].FileSizeBytes);
            Assert.Null(assets[id].BitrateBps);
            Assert.Null(assets[id].AddedAt);
            Assert.Null(assets[id].Container);
            Assert.Empty(assets[id].AudioTracks);
            Assert.Empty(assets[id].SubtitleLanguages);
        }
    }

    [Fact]
    public async Task AudioOnlyFileWithoutADuration_FallsBackToTheCachedAudioBitrate()
    {
        var workId = AddWork("Music");
        var assetId = AddAsset(workId, "song.mp3", "hash-song");
        CacheInspection(assetId, "hash-song", size: 4_000_000, duration: null, new MediaProbeResult { AudioBitrate = 256 });

        var asset = Assert.Single(await AssetsAsync(workId));

        Assert.Equal(256_000, asset.BitrateBps);
    }

    [Fact]
    public async Task Detail_FillsFileSizeFromTheInspectionCacheThenTheHashCacheAndOtherwiseLeavesItEmpty()
    {
        var withInspection = AddWork();
        var inspectedAsset = AddAsset(withInspection, "one.mkv", "hash-1");
        CacheInspection(inspectedAsset, "hash-1", size: 7_000, duration: 10, new MediaProbeResult());
        CacheHash("hash-1", 1);

        var withHashOnly = AddWork();
        AddAsset(withHashOnly, "two.mkv", "hash-2");
        CacheHash("hash-2", 9_000);

        var withNothing = AddWork();
        AddAsset(withNothing, "three.mkv", "hash-3");

        var repository = new LibraryItemRepository(_db);

        Assert.Equal(7_000, (await repository.GetDetailAsync(withInspection))!.FileSizeBytes);
        Assert.Equal(9_000, (await repository.GetDetailAsync(withHashOnly))!.FileSizeBytes);
        Assert.Null((await repository.GetDetailAsync(withNothing))!.FileSizeBytes);
    }
}
