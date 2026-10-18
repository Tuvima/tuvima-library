using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

public sealed class LibraryItemCurationRepositoryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly LibraryItemCurationRepository _repository;

    public LibraryItemCurationRepositoryTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-library-item-store-{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _repository = new LibraryItemCurationRepository(_database);
    }

    [Fact]
    public async Task RemovalReads_AreSetBased_AndDeleteUsesOneTransactionalBoundary()
    {
        var first = await SeedWorkAsync("First", "C:/library/a.epub", "Books");
        var secondAssetId = Guid.NewGuid();
        var second = await SeedWorkAsync("Second", "C:/library/c.epub", "Movies");
        var managedPath = "C:/library/.data/assets/work/cover.jpg";

        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync("""
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
                VALUES (@secondAssetId, @editionId, @hash, 'C:/library/b.epub', 'Normal');
                INSERT INTO entity_assets (id, entity_id, entity_type, asset_type, local_image_path)
                VALUES (@entityAssetId, @workId, 'Work', 'CoverArt', @managedPath);
                INSERT INTO review_queue (id, entity_id, entity_type, trigger, status)
                VALUES (@reviewId, @assetId, 'MediaAsset', 'LowConfidence', 'Pending');
                """, new
            {
                secondAssetId,
                editionId = first.EditionId,
                hash = Guid.NewGuid().ToString("N"),
                entityAssetId = Guid.NewGuid(),
                workId = first.WorkId,
                managedPath,
                reviewId = Guid.NewGuid(),
                assetId = first.AssetId,
            });
        }

        var removals = await _repository.GetRemovalTargetsAsync([first.WorkId, second.WorkId]);

        Assert.Equal(2, removals.Count);
        Assert.Equal("First", removals[first.WorkId].Title);
        var removal = removals[first.WorkId];
        Assert.Equal(2, removal.FilePaths.Count);
        Assert.Contains(managedPath, removal.ManagedAssetPaths);

        await _repository.DeleteWorkRecordsAsync(removal);

        using var verify = _database.CreateConnection();
        Assert.Equal(0, await verify.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM works WHERE id = @workId", new { workId = first.WorkId }));
        Assert.Equal(0, await verify.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM review_queue WHERE entity_id = @assetId", new { assetId = first.AssetId }));
        Assert.Equal(0, await verify.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM entity_assets WHERE entity_id = @workId", new { workId = first.WorkId }));
    }

    [Fact]
    public async Task History_IsTypedForAWorkWithAssetActivity()
    {
        var seeded = await SeedWorkAsync("History", "C:/library/history.epub", "Books");
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync("""
                INSERT INTO system_activity (occurred_at, action_type, entity_id, entity_type, detail)
                VALUES (@now, 'FileDetected', @assetId, 'MediaAsset', 'Detected for test');
                """, new
            {
                now = DateTimeOffset.UtcNow.ToString("O"),
                assetId = seeded.AssetId,
            });
        }

        var history = await _repository.GetHistoryAsync(seeded.WorkId);

        var entry = Assert.Single(history);
        Assert.Equal("File detected", entry.Label);
        Assert.Equal(seeded.AssetId, entry.EntityId);
    }

    [Fact]
    public async Task History_HumanizesAndCollapsesRepeatedNarrativeRootEvents()
    {
        var seeded = await SeedWorkAsync("Episode", "C:/library/show/episode.mkv", "TV");
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync("""
                INSERT INTO system_activity (occurred_at, action_type, entity_id, entity_type, detail)
                VALUES (@older, 'NarrativeRootResolved', @workId, 'Work', 'Resolved narrative root from technical metadata'),
                       (@newer, 'NarrativeRootResolved', @workId, 'Work', 'Resolved narrative root from technical metadata');
                """, new
            {
                older = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"),
                newer = DateTimeOffset.UtcNow.ToString("O"),
                workId = seeded.WorkId,
            });
        }

        var history = await _repository.GetHistoryAsync(seeded.WorkId);

        var entry = Assert.Single(history);
        Assert.Equal("TV show relationship confirmed", entry.Label);
        Assert.Equal("Connected this episode to its TV show using the library's identified series metadata.", entry.Detail);
        Assert.Equal("match", entry.Category);
        Assert.Equal("System", entry.ActorLabel);
    }

    [Fact]
    public async Task History_ForEpisodeDoesNotIncludeSiblingEpisodeActivity()
    {
        var selectedEpisode = await SeedWorkAsync("Selected episode", "C:/library/show/s01e01.mkv", "TV");
        var siblingEpisode = await SeedWorkAsync("Sibling episode", "C:/library/show/s01e02.mkv", "TV");
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync("""
                INSERT INTO system_activity (occurred_at, action_type, entity_id, entity_type, detail)
                VALUES (@now, 'ArtworkUpdated', @selectedId, 'Work', 'Selected still changed'),
                       (@now, 'ArtworkUpdated', @siblingId, 'Work', 'Sibling still changed');
                """, new
            {
                now = DateTimeOffset.UtcNow.ToString("O"),
                selectedId = selectedEpisode.WorkId,
                siblingId = siblingEpisode.WorkId,
            });
        }

        var history = await _repository.GetHistoryAsync(selectedEpisode.WorkId);

        var entry = Assert.Single(history);
        Assert.Equal(selectedEpisode.WorkId, entry.EntityId);
        Assert.Equal("Selected still changed", entry.Detail);
        Assert.Equal("artwork", entry.Category);
    }

    [Fact]
    public async Task History_ForAlbumIncludesEachTrackWithTrackContext()
    {
        var album = await SeedWorkAsync("The Marshall Mathers LP", "C:/library/eminem/album.flac", "Music");
        var track = await SeedWorkAsync("Stan", "C:/library/eminem/03-stan.flac", "Music");
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync("""
                UPDATE works
                SET work_kind = 'parent'
                WHERE id = @albumId;
                UPDATE works
                SET work_kind = 'child', parent_work_id = @albumId, ordinal = 3, ordinal_sort = 3
                WHERE id = @trackId;
                INSERT INTO system_activity (occurred_at, action_type, entity_id, entity_type, detail)
                VALUES (@now, 'HydrationEnqueued', @trackAssetId, 'MediaAsset', 'Queued for the full enrichment cycle.');
                """, new
            {
                now = DateTimeOffset.UtcNow.ToString("O"),
                albumId = album.WorkId,
                trackId = track.WorkId,
                trackAssetId = track.AssetId,
            });
        }

        var history = await _repository.GetHistoryAsync(album.WorkId);

        var entry = Assert.Single(history);
        Assert.Equal(track.AssetId, entry.EntityId);
        Assert.Equal("Queued for enrichment", entry.Label);
        Assert.Equal("Track 3: Stan — Queued for the full enrichment cycle.", entry.Detail);
    }

    [Fact]
    public async Task History_ForShowIncludesEpisodeWithSeasonAndEpisodeContext()
    {
        var show = await SeedWorkAsync("Northwatch", "C:/library/northwatch/show.mkv", "TV");
        var season = await SeedWorkAsync("Season 2", "C:/library/northwatch/season.mkv", "TV");
        var episode = await SeedWorkAsync("Echoes in the Frost", "C:/library/northwatch/s02e01.mkv", "TV");
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync("""
                UPDATE works SET work_kind = 'parent' WHERE id = @showId;
                UPDATE works
                SET work_kind = 'parent', parent_work_id = @showId, ordinal = 2, ordinal_sort = 2
                WHERE id = @seasonId;
                UPDATE works
                SET work_kind = 'child', parent_work_id = @seasonId, ordinal = 1, ordinal_sort = 1
                WHERE id = @episodeId;
                INSERT INTO system_activity (occurred_at, action_type, entity_id, entity_type, detail)
                VALUES (@now, 'NarrativeRootResolved', @episodeId, 'Work', 'NarrativeRootResolved');
                """, new
            {
                now = DateTimeOffset.UtcNow.ToString("O"),
                showId = show.WorkId,
                seasonId = season.WorkId,
                episodeId = episode.WorkId,
            });
        }

        var history = await _repository.GetHistoryAsync(show.WorkId);

        var entry = Assert.Single(history);
        Assert.Equal(episode.WorkId, entry.EntityId);
        Assert.Equal("TV show relationship confirmed", entry.Label);
        Assert.Equal(
            "S2 E1: Echoes in the Frost — Connected this episode to its TV show using the library's identified series metadata.",
            entry.Detail);
    }

    [Fact]
    public async Task Reads_PropagateCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _repository.GetHistoryAsync(Guid.NewGuid(), cancellation.Token));
    }

    private async Task<SeededWork> SeedWorkAsync(string title, string filePath, string mediaType)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        await connection.ExecuteAsync("""
            INSERT INTO works (id, media_type, work_kind, ownership)
            VALUES (@workId, @mediaType, 'standalone', 'Owned');
            INSERT INTO editions (id, work_id, format_label)
            VALUES (@editionId, @workId, 'Test');
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
            VALUES (@assetId, @editionId, @contentHash, @filePath, 'Normal');
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@assetId, 'title', @title, @now);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@assetId, 'media_type', @mediaType, @now);
            """, new
        {
            workId,
            editionId,
            assetId,
            mediaType,
            contentHash = Guid.NewGuid().ToString("N"),
            filePath,
            title,
            now = DateTimeOffset.UtcNow.ToString("O"),
        });
        return new SeededWork(workId, editionId, assetId);
    }

    public void Dispose()
    {
        _database.Dispose();
        using (var pool = new SqliteConnection($"Data Source={_databasePath}"))
        {
            SqliteConnection.ClearPool(pool);
        }

        File.Delete(_databasePath);
    }

    private sealed record SeededWork(Guid WorkId, Guid EditionId, Guid AssetId);
}
