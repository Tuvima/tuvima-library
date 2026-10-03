using Dapper;
using MediaEngine.Contracts.Playback;
using MediaEngine.Storage;
using MediaEngine.Storage.Playback;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Api.Tests;

public sealed class AudiobookListenHistoryRepositoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima-audiobook-history-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;

    public AudiobookListenHistoryRepositoryTests()
    {
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
    }

    [Fact]
    public async Task TrackHeartbeatAsync_FinalizesQualifiedAudiobookListenHistoryWithGuidBlobKeys()
    {
        var ids = await CreateProfileAssetAsync();
        var queueItemId = Guid.NewGuid();
        var item = new PlayerQueueItemDto
        {
            QueueItemId = queueItemId,
            WorkId = ids.WorkId,
            AssetId = ids.AssetId,
            MediaType = "Audiobooks",
            Title = "Test Audiobook",
            DurationSeconds = 3600,
            Chapters =
            [
                new PlaybackChapterDto
                {
                    Index = 1,
                    Title = "Chapter 1",
                    StartSeconds = 0,
                    EndSeconds = 3600,
                },
            ],
        };

        var repository = new AudiobookListenHistoryRepository(_db);

        await repository.TrackHeartbeatAsync(
            ids.ProfileId,
            item,
            new PlayerHeartbeatDto
            {
                QueueItemId = queueItemId,
                AssetId = ids.AssetId,
                IsPlaying = true,
                PositionSeconds = 65,
                DurationSeconds = 3600,
                PlaybackRate = 1.25,
            },
            qualificationSeconds: 0,
            historyLimit: 25,
            activeSegmentGapSeconds: 20,
            positionJumpToleranceSeconds: 12,
            deviceId: "web",
            client: "web");

        await repository.TrackHeartbeatAsync(
            ids.ProfileId,
            item,
            new PlayerHeartbeatDto
            {
                QueueItemId = queueItemId,
                AssetId = ids.AssetId,
                IsPlaying = false,
                PositionSeconds = 125,
                DurationSeconds = 3600,
                PlaybackRate = 1.25,
            },
            qualificationSeconds: 0,
            historyLimit: 25,
            activeSegmentGapSeconds: 20,
            positionJumpToleranceSeconds: 12,
            deviceId: "web",
            client: "web");

        var history = await repository.GetRecentAsync(ids.ProfileId, ids.WorkId);

        var entry = Assert.Single(history);
        Assert.Equal(ids.ProfileId, entry.ProfileId);
        Assert.Equal(ids.WorkId, entry.WorkId);
        Assert.Equal(ids.AssetId, entry.AssetId);
        Assert.Equal("Chapter 1", entry.ChapterTitle);
        Assert.Equal(125, entry.PositionSeconds);

        using var conn = _db.CreateConnection();
        var keyTypes = await conn.QuerySingleAsync<KeyTypeRow>(
            """
            SELECT typeof(profile_id) AS ProfileType,
                   typeof(work_id) AS WorkType,
                   typeof(asset_id) AS AssetType
            FROM audiobook_listen_history
            WHERE profile_id = @profileId;
            """,
            new { profileId = ids.ProfileId });

        Assert.Equal("blob", keyTypes.ProfileType);
        Assert.Equal("blob", keyTypes.WorkType);
        Assert.Equal("blob", keyTypes.AssetType);
    }

    [Fact]
    public async Task GetRecentAsync_IncludesActiveSegmentAndUsesHeartbeatChapterMetadata()
    {
        var ids = await CreateProfileAssetAsync();
        var queueItemId = Guid.NewGuid();
        var item = new PlayerQueueItemDto
        {
            QueueItemId = queueItemId,
            WorkId = ids.WorkId,
            AssetId = ids.AssetId,
            MediaType = "Audiobooks",
            Title = "Test Audiobook",
            DurationSeconds = 3600,
        };

        var repository = new AudiobookListenHistoryRepository(_db);

        await repository.TrackHeartbeatAsync(
            ids.ProfileId,
            item,
            new PlayerHeartbeatDto
            {
                QueueItemId = queueItemId,
                AssetId = ids.AssetId,
                IsPlaying = true,
                PositionSeconds = 925,
                DurationSeconds = 3600,
                PlaybackRate = 1.25,
                ChapterIndex = 3,
                ChapterTitle = "Chapter 4",
            },
            qualificationSeconds: 60,
            historyLimit: 25,
            activeSegmentGapSeconds: 20,
            positionJumpToleranceSeconds: 12,
            deviceId: "web",
            client: "web");

        var history = await repository.GetRecentAsync(ids.ProfileId, ids.WorkId);

        var entry = Assert.Single(history);
        Assert.Equal(Guid.Empty, entry.Id);
        Assert.Equal("Chapter 4", entry.ChapterTitle);
        Assert.Equal(3, entry.ChapterIndex);
        Assert.Equal(925, entry.PositionSeconds);
    }

    [Fact]
    public async Task AudiobookBookmarkRepository_CreatesListsAndDeletesBookmarksWithGuidBlobKeys()
    {
        var ids = await CreateProfileAssetAsync();
        var repository = new AudiobookBookmarkRepository(_db);

        var bookmark = await repository.CreateAsync(
            ids.ProfileId,
            ids.WorkId,
            new CreateAudiobookBookmarkRequestDto
            {
                AssetId = ids.AssetId,
                ChapterIndex = 7,
                ChapterTitle = "Chapter 8",
                PositionSeconds = 22917,
                DurationSeconds = 53596,
                Label = "Chapter 8 - 6:21:57",
                Note = "Remember this passage.",
            });

        var bookmarks = await repository.GetByWorkAsync(ids.ProfileId, ids.WorkId);

        var listed = Assert.Single(bookmarks);
        Assert.Equal(bookmark.Id, listed.Id);
        Assert.Equal(ids.ProfileId, listed.ProfileId);
        Assert.Equal(ids.WorkId, listed.WorkId);
        Assert.Equal(ids.AssetId, listed.AssetId);
        Assert.Equal("Chapter 8", listed.ChapterTitle);
        Assert.Equal(22917, listed.PositionSeconds);
        Assert.Equal("Chapter 8 - 6:21:57", listed.Label);
        Assert.Equal("Remember this passage.", listed.Note);

        using var conn = _db.CreateConnection();
        var keyTypes = await conn.QuerySingleAsync<KeyTypeRow>(
            """
            SELECT typeof(profile_id) AS ProfileType,
                   typeof(work_id) AS WorkType,
                   typeof(asset_id) AS AssetType
            FROM audiobook_bookmarks
            WHERE id = @id;
            """,
            new { id = bookmark.Id });

        Assert.Equal("blob", keyTypes.ProfileType);
        Assert.Equal("blob", keyTypes.WorkType);
        Assert.Equal("blob", keyTypes.AssetType);

        Assert.Empty(await repository.GetByWorkAsync(ids.ProfileId, ids.WorkId, default, new HashSet<Guid>()));
        Assert.False(await repository.DeleteAsync(ids.ProfileId, bookmark.Id, default, new HashSet<Guid>()));
        Assert.Single(await repository.GetByWorkAsync(ids.ProfileId, ids.WorkId, default, new HashSet<Guid> { ids.AssetId }));
        Assert.True(await repository.DeleteAsync(ids.ProfileId, bookmark.Id));
        Assert.Empty(await repository.GetByWorkAsync(ids.ProfileId, ids.WorkId));
    }

    [Fact]
    public async Task AudiobookBookmarkRepository_FailsClearlyWhenTheFreshNoteSchemaIsMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima-audiobook-bookmark-old-schema-{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                connection.Execute(
                    """
                    CREATE TABLE audiobook_bookmarks (
                        id BLOB NOT NULL PRIMARY KEY,
                        profile_id BLOB NOT NULL,
                        work_id BLOB NOT NULL,
                        asset_id BLOB NOT NULL,
                        chapter_index INTEGER,
                        chapter_title TEXT,
                        position_seconds REAL NOT NULL,
                        duration_seconds REAL,
                        label TEXT,
                        created_at TEXT NOT NULL
                    );
                    """);
            }

            var database = new DatabaseConnection(path);
            var repository = new AudiobookBookmarkRepository(database);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => repository.GetByWorkAsync(Guid.NewGuid(), Guid.NewGuid()));
            Assert.Contains("missing: note", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("runtime schema migration is not supported", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            using (var poolConnection = new SqliteConnection($"Data Source={path}"))
            {
                SqliteConnection.ClearPool(poolConnection);
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task AudiobookChapterTitleOverrideRepository_UpsertsListsAndDeletesDisplayNames()
    {
        var ids = await CreateProfileAssetAsync();
        var repository = new AudiobookChapterTitleOverrideRepository(_db);

        var created = await repository.UpsertAsync(
            ids.WorkId,
            new UpsertAudiobookChapterTitleOverrideRequestDto
            {
                AssetId = ids.AssetId,
                ChapterIndex = 3,
                Title = "The Crawl Begins",
                TitleSource = PlaybackChapterTitleSources.Override,
            });

        Assert.Equal(ids.WorkId, created.WorkId);
        Assert.Equal(ids.AssetId, created.AssetId);
        Assert.Equal(3, created.ChapterIndex);
        Assert.Equal("The Crawl Begins", created.Title);
        Assert.Equal(PlaybackChapterTitleSources.Override, created.TitleSource);

        var listed = Assert.Single(await repository.GetByWorkAsync(ids.WorkId, ids.AssetId));
        Assert.Equal(created.Title, listed.Title);

        var updated = await repository.UpsertAsync(
            ids.WorkId,
            new UpsertAudiobookChapterTitleOverrideRequestDto
            {
                AssetId = ids.AssetId,
                ChapterIndex = 3,
                Title = "A Manual Name",
            });

        Assert.Equal("A Manual Name", updated.Title);
        Assert.Equal(PlaybackChapterTitleSources.Override, updated.TitleSource);
        Assert.True(await repository.DeleteAsync(ids.WorkId, ids.AssetId, 3));
        Assert.Empty(await repository.GetByAssetAsync(ids.AssetId));
    }

    private async Task<(Guid ProfileId, Guid WorkId, Guid AssetId)> CreateProfileAssetAsync()
    {
        using var conn = _db.CreateConnection();
        var profileId = Guid.NewGuid();
        var collectionId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        await conn.ExecuteAsync(
            """
            INSERT INTO profiles (id, display_name, avatar_color, role, created_at)
            VALUES (@profileId, 'Owner', '#7C4DFF', 'Administrator', @now);
            INSERT INTO collections (id, created_at) VALUES (@collectionId, @now);
            INSERT INTO works (id, collection_id, media_type) VALUES (@workId, @collectionId, 'Audiobooks');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
            VALUES (@assetId, @editionId, @contentHash, '/library/Audiobooks/test.m4b', 'Normal');
            """,
            new
            {
                profileId,
                collectionId,
                workId,
                editionId,
                assetId,
                contentHash = $"asset-{assetId:N}",
                now = DateTimeOffset.UtcNow,
            });
        return (profileId, workId, assetId);
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private sealed record KeyTypeRow
    {
        public string ProfileType { get; init; } = string.Empty;
        public string WorkType { get; init; } = string.Empty;
        public string AssetType { get; init; } = string.Empty;
    }
}
