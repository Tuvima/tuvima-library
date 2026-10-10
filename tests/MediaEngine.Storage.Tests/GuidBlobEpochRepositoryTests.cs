using Dapper;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

public sealed class GuidBlobEpochRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;

    public GuidBlobEpochRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_guid_blob_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
    }

    [Fact]
    public void GuidSql_RejectsEveryNonBlobRepresentation()
    {
        var id = Guid.NewGuid();

        Assert.Equal(id, GuidSql.FromDb(GuidSql.ToBlob(id)));
        Assert.Throws<InvalidCastException>(() => GuidSql.FromDb(id.ToString("D")));
        Assert.Throws<InvalidCastException>(() => GuidSql.FromDb(id));
        Assert.Throws<InvalidCastException>(() => GuidSql.FromDb(new byte[15]));
        Assert.Throws<InvalidCastException>(() => GuidSql.FromDb(new byte[17]));
    }

    [Fact]
    public async Task MediaFeatureRepositories_RoundTripOnlyBlobGuids()
    {
        var collectionId = CreateCollection();
        var ebookAssetId = CreateAsset(collectionId, "Books", "/library/book.epub");
        var audioAssetId = CreateAsset(collectionId, "Audiobooks", "/library/book.m4b");

        var alignment = new AlignmentJob
        {
            Id = Guid.NewGuid(),
            EbookAssetId = ebookAssetId,
            AudiobookAssetId = audioAssetId,
            Status = AlignmentJobStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };
        var alignmentRepo = new AlignmentJobRepository(_db);
        await alignmentRepo.InsertAsync(alignment);
        Assert.Equal(alignment.Id, (await alignmentRepo.FindByIdAsync(alignment.Id))?.Id);

        var fingerprintRepo = new AudioFingerprintRepository(_db);
        await fingerprintRepo.UpsertAsync(audioAssetId, [1, 2, 3], 42.5);
        Assert.True(await fingerprintRepo.ExistsAsync(audioAssetId));
        Assert.Contains(await fingerprintRepo.GetAllAsync(), row => row.AssetId == audioAssetId);

        var placement = new CollectionPlacement
        {
            Id = Guid.NewGuid(),
            CollectionId = collectionId,
            Location = "home",
            Position = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var placementRepo = new CollectionPlacementRepository(_db);
        await placementRepo.UpsertAsync(placement);
        Assert.Contains(await placementRepo.GetByCollectionIdAsync(collectionId), row => row.Id == placement.Id);

        var segment = new PlaybackSegment
        {
            Id = Guid.NewGuid(),
            AssetId = audioAssetId,
            Kind = "intro",
            StartSeconds = 0,
            EndSeconds = 12,
            Confidence = 0.9,
            Source = "test",
            IsSkippable = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var segmentRepo = new PlaybackSegmentRepository(_db);
        await segmentRepo.UpsertBatchAsync(audioAssetId, [segment]);
        Assert.Equal(segment.Id, (await segmentRepo.FindByIdAsync(segment.Id))?.Id);

        var bookmark = new ReaderBookmark
        {
            Id = Guid.NewGuid(),
            UserId = "reader",
            AssetId = ebookAssetId,
            ChapterIndex = 1,
            CreatedAt = DateTime.UtcNow,
        };
        var bookmarkRepo = new ReaderBookmarkRepository(_db);
        await bookmarkRepo.InsertAsync(bookmark);
        Assert.Equal(bookmark.Id, (await bookmarkRepo.FindByIdAsync(bookmark.Id))?.Id);

        var highlight = new ReaderHighlight
        {
            Id = Guid.NewGuid(),
            UserId = "reader",
            AssetId = ebookAssetId,
            ChapterIndex = 1,
            StartOffset = 2,
            EndOffset = 8,
            SelectedText = "selected",
            CreatedAt = DateTime.UtcNow,
        };
        var highlightRepo = new ReaderHighlightRepository(_db);
        await highlightRepo.InsertAsync(highlight);
        Assert.Equal(highlight.Id, (await highlightRepo.FindByIdAsync(highlight.Id))?.Id);

        var statistics = new ReaderStatistics
        {
            Id = Guid.NewGuid(),
            UserId = "reader",
            AssetId = ebookAssetId,
            ChaptersRead = 1,
            SessionsCount = 1,
        };
        var statisticsRepo = new ReaderStatisticsRepository(_db);
        await statisticsRepo.UpsertAsync(statistics);
        Assert.Equal(statistics.Id, (await statisticsRepo.GetAsync("reader", ebookAssetId))?.Id);

        var track = new TextTrack
        {
            Id = Guid.NewGuid(),
            AssetId = audioAssetId,
            Kind = TextTrackKind.Subtitles,
            Language = "en",
            Provider = "test",
            SourceFormat = "srt",
            NormalizedFormat = "vtt",
            LocalPath = "/managed/subtitles.vtt",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var trackRepo = new TextTrackRepository(_db);
        await trackRepo.UpsertAsync(track);
        await trackRepo.SetPreferredAsync(track.Id);
        Assert.Equal(track.Id, (await trackRepo.FindByIdAsync(track.Id))?.Id);

        using var conn = _db.CreateConnection();
        AssertBlob(conn, "alignment_jobs", "id", "id", alignment.Id);
        AssertBlob(conn, "alignment_jobs", "ebook_asset_id", "id", alignment.Id);
        AssertBlob(conn, "alignment_jobs", "audiobook_asset_id", "id", alignment.Id);
        AssertBlob(conn, "audio_fingerprints", "asset_id", "asset_id", audioAssetId);
        AssertBlob(conn, "collection_placements", "id", "id", placement.Id);
        AssertBlob(conn, "collection_placements", "collection_id", "id", placement.Id);
        AssertBlob(conn, "playback_segments", "id", "id", segment.Id);
        AssertBlob(conn, "playback_segments", "asset_id", "id", segment.Id);
        AssertBlob(conn, "reader_bookmarks", "id", "id", bookmark.Id);
        AssertBlob(conn, "reader_bookmarks", "asset_id", "id", bookmark.Id);
        AssertBlob(conn, "reader_highlights", "id", "id", highlight.Id);
        AssertBlob(conn, "reader_highlights", "asset_id", "id", highlight.Id);
        AssertBlob(conn, "reader_statistics", "id", "id", statistics.Id);
        AssertBlob(conn, "reader_statistics", "asset_id", "id", statistics.Id);
        AssertBlob(conn, "text_tracks", "id", "id", track.Id);
        AssertBlob(conn, "text_tracks", "asset_id", "id", track.Id);
    }

    [Fact]
    public async Task CollectionAndBatchOperations_MatchBlobIdentifiers()
    {
        var keepCollectionId = CreateCollection();
        var mergeCollectionId = CreateCollection();
        var workId = CreateWork(mergeCollectionId, "Movies", "standalone");

        var collections = new CollectionRepository(_db);
        await collections.MergeCollectionsAsync(keepCollectionId, mergeCollectionId);
        await collections.SetUniverseMismatchAsync(workId);
        await collections.UpdateWorkWikidataStatusAsync(workId, "resolved");
        await collections.UpdateWorkWikidataMatchStateAsync(workId, "resolved", source: "test", locked: true, wikidataQid: "Q1");
        await collections.UpdateMatchLevelAsync(workId, "edition");
        var edition = await collections.CreateEditionAsync(workId, "Digital", null);

        using (var conn = _db.CreateConnection())
        {
            Assert.Equal(keepCollectionId, conn.QuerySingle<Guid>("SELECT collection_id FROM works WHERE id = @workId;", new { workId }));
            Assert.Equal(1, conn.QuerySingle<int>("SELECT universe_mismatch FROM works WHERE id = @workId;", new { workId }));
            AssertBlob(conn, "editions", "id", "id", edition.Id);
            AssertBlob(conn, "editions", "work_id", "id", edition.Id);
        }

        var batchId = Guid.NewGuid();
        using (var conn = _db.CreateConnection())
        {
            conn.Execute("""
                INSERT INTO ingestion_batches
                    (id, files_registered, files_review, files_no_match, files_failed)
                VALUES (@batchId, 7, 2, 1, 1);
                """, new { batchId });
        }

        var counts = await new LibraryItemRepository(_db).GetFourStateCountsAsync(batchId);
        Assert.Equal(7, counts.Identified);
        Assert.Equal(4, counts.InReview);
    }

    [Fact]
    public async Task RelationshipJournalAndSearch_PreserveBlobIdentityEndToEnd()
    {
        var edge = new EntityRelationship
        {
            Id = Guid.NewGuid(),
            SubjectQid = "Q1",
            RelationshipTypeValue = "member_of",
            ObjectQid = "Q2",
            DiscoveredAt = DateTimeOffset.UtcNow,
        };
        var relationships = new EntityRelationshipRepository(_db);
        await relationships.CreateAsync(edge);
        Assert.Equal(edge.Id, Assert.Single(await relationships.GetBySubjectAsync(edge.SubjectQid)).Id);

        var journalEntityId = Guid.NewGuid();
        new TransactionJournal(_db).Log("UPDATED", "Work", journalEntityId);

        var collectionId = CreateCollection();
        var workId = CreateWork(collectionId, "Books", "standalone");
        var assetId = CreateAssetForWork(workId, "/library/search.epub");
        await new CanonicalValueRepository(_db).UpsertBatchAsync([
            new CanonicalValue
            {
                EntityId = assetId,
                Key = "title",
                Value = "Dune",
                LastScoredAt = DateTimeOffset.UtcNow,
            },
        ]);

        var search = new SearchIndexRepository(_db);
        await search.UpsertByEntityIdAsync(assetId);
        Assert.Contains(workId, await search.SearchAsync("Dune"));
        Assert.Contains(workId, await search.SearchAsync("Du"));
        await search.RebuildAsync();
        Assert.Contains(workId, await search.SearchAsync("Dune"));

        using var conn = _db.CreateConnection();
        AssertBlob(conn, "entity_relationships", "id", "id", edge.Id);
        AssertBlob(conn, "transaction_log", "entity_id", "entity_id", journalEntityId);
        AssertBlob(conn, "search_index", "entity_id", "entity_id", workId);
    }

    public void Dispose()
    {
        _db.Dispose();
        TestTemp.DeleteDatabase(_dbPath);
    }

    private Guid CreateCollection()
    {
        var id = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute("INSERT INTO collections (id, created_at) VALUES (@id, @createdAt);", new { id, createdAt = DateTimeOffset.UtcNow.ToString("O") });
        return id;
    }

    private Guid CreateWork(Guid collectionId, string mediaType, string workKind)
    {
        var id = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO works (id, collection_id, media_type, work_kind, is_catalog_only)
            VALUES (@id, @collectionId, @mediaType, @workKind, 0);
            """, new { id, collectionId, mediaType, workKind });
        return id;
    }

    private Guid CreateAsset(Guid collectionId, string mediaType, string path)
    {
        var workId = CreateWork(collectionId, mediaType, "standalone");
        return CreateAssetForWork(workId, path);
    }

    private Guid CreateAssetForWork(Guid workId, string path)
    {
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute("INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);", new { editionId, workId });
        conn.Execute("""
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
            VALUES (@assetId, @editionId, @contentHash, @path, 'Normal');
            """, new { assetId, editionId, contentHash = $"hash_{assetId:N}", path });
        return assetId;
    }

    private static void AssertBlob(
        System.Data.IDbConnection conn,
        string table,
        string column,
        string keyColumn,
        Guid key)
    {
        var storage = conn.QuerySingle<(string StorageType, int Length)>(
            $"SELECT typeof([{column}]) AS StorageType, length([{column}]) AS Length FROM [{table}] WHERE [{keyColumn}] = @key;",
            new { key });
        Assert.Equal("blob", storage.StorageType);
        Assert.Equal(16, storage.Length);
    }
}
