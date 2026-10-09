using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Storage;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Api.Tests;

public sealed class ActivityBatchReadServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;

    public ActivityBatchReadServiceTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_activity_batch_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task ActivityBatchReadService_ReturnsBatchGroupsItemsDetailsAndPeopleAudit()
    {
        var seed = SeedActivityBatch();
        var service = new ActivityBatchReadService(_db);
        var query = new ActivityBatchQuery(
            Search: "Dune",
            MediaType: "Movies",
            Status: "completed",
            Source: null,
            EventType: null,
            Start: null,
            End: null,
            Offset: 0,
            Limit: 10);

        var batches = await service.GetBatchesAsync(query);
        var exactBatch = await service.GetBatchAsync(seed.BatchId);
        var groups = await service.GetGroupsAsync(seed.BatchId);
        var insights = await service.GetInsightsAsync(seed.BatchId);
        var items = await service.GetItemsAsync(seed.BatchId, "Needs Review", 0, 10, "title", "asc");
        var movieItems = await service.GetItemsAsync(seed.BatchId, "Movies", 0, 10, "title", "asc");
        var searchedItems = await service.GetItemsAsync(seed.BatchId, "Movies", "Dune", "review", "watch/movies", 0, 10, "title", "asc");
        var events = await service.GetEventsAsync(seed.BatchId, "Dune", "Media", "completed", 0, 25);
        var detail = await service.GetItemDetailAsync(seed.BatchId, seed.AssetId);
        var people = await service.GetPeopleAsync(new ActivityBatchQuery(
            Search: $"batch:{seed.BatchId:D}",
            MediaType: "Movies",
            Status: null,
            Source: null,
            EventType: null,
            Start: null,
            End: null,
            Offset: 0,
            Limit: 10));

        var batch = Assert.Single(batches.Items);
        Assert.NotNull(exactBatch);
        Assert.Equal(seed.BatchId, exactBatch.BatchId);
        Assert.Equal(seed.BatchId, batch.BatchId);
        Assert.Equal("completed", batch.Status);
        Assert.Equal(1, batch.MediaTypeCount);
        Assert.Equal(1, batch.TitleCount);
        Assert.Equal(1, batch.ItemCount);
        Assert.Equal(3, batch.EventCount);
        Assert.Equal(1, batch.PeopleCount);
        Assert.Equal(1, batch.ReviewCount);
        Assert.Equal(0, batch.AlertCount);
        Assert.NotNull(batch.DurationLabel);
        Assert.Equal("Movies", Assert.Single(batch.MediaTypes).MediaType);
        Assert.Equal(1, batch.FilesDiscoveredCount);
        Assert.Equal(1, batch.FilesProcessedCount);
        Assert.Equal(1, batch.ItemsIdentifiedCount);
        Assert.Equal(0, batch.WarningCount);
        Assert.Equal(0, batch.FailureCount);

        Assert.Equal(seed.BatchId, insights.BatchId);

        var group = Assert.Single(groups);
        Assert.Equal("Movies", group.MediaType);
        Assert.Equal(1, group.TitleCount);
        Assert.Equal(1, group.PeopleCount);
        Assert.Equal(1, group.ReviewCount);
        Assert.Single(movieItems.Items);
        Assert.Single(searchedItems.Items);
        Assert.Equal(1, events.TotalCount);
        Assert.All(events.Items, evt => Assert.Equal(seed.BatchId, evt.BatchId));

        var item = Assert.Single(items.Items);
        Assert.Equal(seed.AssetId, item.AssetId);
        Assert.Equal("Dune (2021)", item.Title);
        Assert.Equal("tmdb", item.Provider);
        Assert.Equal("Q1058080", item.WikidataQid);
        Assert.Equal($"/stream/artwork/{seed.ArtworkId:D}", item.CoverUrl);
        Assert.Equal("Needs review", item.Status);
        Assert.Equal("succeeded", item.ProcessingStatus);
        Assert.Equal("NeedsReview", item.AuditStatus);
        Assert.Equal("2h 35m", item.DurationLabel);
        Assert.Equal(seed.WorkId, item.LibraryEntityId);
        Assert.Equal(1, item.PeopleCount);
        Assert.Equal(1, item.ArtworkCount);
        Assert.Equal(1, item.ReviewCount);

        Assert.NotNull(detail);
        Assert.Equal(seed.AssetId, detail.AssetId);
        Assert.Equal("Dune (2021)", detail.Title);
        Assert.Equal(seed.WorkId, detail.LibraryEntityId);
        Assert.Equal("2h 35m", detail.DurationLabel);
        Assert.Contains(detail.Timeline, evt => evt.EventType == "MediaAdded");
        Assert.Contains(detail.Timeline, evt => evt.EventType == "FileHashed");
        var detailPerson = Assert.Single(detail.People);
        Assert.Equal("Frank Herbert", detailPerson.PersonName);
        Assert.Equal("Author", detailPerson.Role);
        var evidence = Assert.Single(detail.Evidence);
        Assert.Equal("provider_match", evidence.Kind);
        Assert.Equal("{not-json", evidence.Detail);

        var personAudit = Assert.Single(people.Items);
        Assert.Equal(seed.PersonId, personAudit.PersonId);
        Assert.Equal("Frank Herbert", personAudit.PersonName);
        Assert.Equal("Dune (2021)", personAudit.Title);
        Assert.Equal("Movies", personAudit.MediaType);
        Assert.Equal("Remote", personAudit.HeadshotStatus);
        Assert.Equal($"/persons/{seed.PersonId:D}/headshot", personAudit.HeadshotUrl);
    }

    [Fact]
    public async Task ActivityBatchReadService_KeepsBooksAndAudiobooksSeparate()
    {
        var seed = SeedBooksAndAudiobooksBatch();
        var service = new ActivityBatchReadService(_db);

        var groups = await service.GetGroupsAsync(seed.BatchId);
        var books = await service.GetItemsAsync(seed.BatchId, "Books", 0, 10, "title", "asc");
        var audiobooks = await service.GetItemsAsync(seed.BatchId, "Audiobooks", 0, 10, "title", "asc");

        Assert.Contains(groups, group => group.MediaType == "Books" && group.TitleCount == 1);
        Assert.Contains(groups, group => group.MediaType == "Audiobooks" && group.TitleCount == 1);
        Assert.Equal("Kindred", Assert.Single(books.Items).Title);
        Assert.Equal("Kindred", Assert.Single(audiobooks.Items).Title);
        Assert.Equal(1, books.TotalCount);
        Assert.Equal(1, audiobooks.TotalCount);
    }

    [Fact]
    public async Task ActivityBatchReadService_FormatsTvTitlesFromShowSeasonEpisodeMetadata()
    {
        var seed = SeedTvActivityBatch();
        var service = new ActivityBatchReadService(_db);

        var items = await service.GetItemsAsync(seed.BatchId, "TV", 0, 10, "title", "asc");
        var detail = await service.GetItemDetailAsync(seed.BatchId, seed.AssetId);

        var item = Assert.Single(items.Items);
        Assert.Equal("The Expanse - S01E01 - Dulcinea", item.Title);
        Assert.NotNull(detail);
        Assert.Equal("The Expanse - S01E01 - Dulcinea", detail.Title);
    }

    [Fact]
    public async Task ActivityBatchReadService_DoesNotPresentUnclassifiedFilesAsMediaItems()
    {
        var batchId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using (var conn = _db.CreateConnection())
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO ingestion_batches (
                        id, status, source_path, category, files_total, files_processed,
                        files_registered, files_review, files_no_match, files_failed,
                        started_at, completed_at, created_at, updated_at)
                    VALUES (
                        $batchId, 'completed', 'C:/watch/books', 'Books', 7, 7,
                        0, 0, 0, 0, $now, $now, $now, $now);
                    """;
                AddGuid(cmd, "$batchId", batchId);
                cmd.Parameters.AddWithValue("$now", now.ToString("O"));
                cmd.ExecuteNonQuery();
            }
        }

        var service = new ActivityBatchReadService(_db);
        var batch = await service.GetBatchAsync(batchId);
        var group = Assert.Single(await service.GetGroupsAsync(batchId));
        var mediaPage = await new IngestionPresentationReadService(_db)
            .GetBatchMediaAsync(batchId, 0, 50);

        Assert.NotNull(batch);
        Assert.Equal(7, batch.FilesDiscoveredCount);
        Assert.Equal(7, batch.FilesProcessedCount);
        Assert.Equal(0, batch.AddedGroupCount);
        Assert.Equal(0, batch.ItemCount);
        Assert.Equal(0, group.ItemCount);
        Assert.Equal(0, mediaPage.TotalCount);
        Assert.Empty(mediaPage.Items);
    }

    [Fact]
    public async Task ActivityBatchReadService_KeepsDiscoveredAndProcessedCountsDistinct()
    {
        var batchId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using (var conn = _db.CreateConnection())
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO ingestion_batches (
                        id, status, source_path, category, files_total, files_processed,
                        files_registered, files_review, files_no_match, files_failed,
                        started_at, completed_at, created_at, updated_at)
                    VALUES (
                        $batchId, 'interrupted', 'C:/watch/mixed', 'Mixed', 38, 12,
                        0, 0, 0, 0, $now, $now, $now, $now);
                    """;
                AddGuid(cmd, "$batchId", batchId);
                cmd.Parameters.AddWithValue("$now", now.ToString("O"));
                cmd.ExecuteNonQuery();
            }
        }

        var batch = await new ActivityBatchReadService(_db).GetBatchAsync(batchId);

        Assert.NotNull(batch);
        Assert.Equal(38, batch.FilesDiscoveredCount);
        Assert.Equal(12, batch.FilesProcessedCount);
    }

    private ActivitySeed SeedActivityBatch()
    {
        var batchId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var personId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var artworkId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ingestion_batches (
                id,
                status,
                source_path,
                category,
                files_total,
                files_processed,
                files_registered,
                files_review,
                files_no_match,
                files_failed,
                started_at,
                completed_at,
                created_at,
                updated_at
            )
            VALUES (
                $batchId,
                'completed',
                'C:/watch/movies',
                'Movies',
                1,
                1,
                1,
                1,
                0,
                0,
                $startedAt,
                $completedAt,
                $startedAt,
                $completedAt
            );

            INSERT INTO works (id, media_type, work_kind, wikidata_qid)
            VALUES ($workId, 'Movies', 'standalone', 'Q1058080');

            INSERT INTO editions (id, work_id, format_label)
            VALUES ($editionId, $workId, 'Theatrical');

            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
            VALUES ($assetId, $editionId, 'dune-hash', 'C:/library/movies/Dune.2021.mkv');

            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES
                ($assetId, 'title', 'Dune (2021)', $completedAt),
                ($assetId, 'wikidata_qid', 'Q1058080', $completedAt),
                ($assetId, 'duration_seconds', '9300', $completedAt);

            INSERT INTO identity_jobs (
                id,
                entity_id,
                entity_type,
                media_type,
                ingestion_run_id,
                state,
                pass,
                resolved_qid,
                created_at,
                updated_at
            )
            VALUES (
                $jobId,
                $assetId,
                'MediaAsset',
                'Movies',
                $batchId,
                'ReadyWithoutUniverse',
                'Quick',
                'Q1058080',
                $startedAt,
                $completedAt
            );

            INSERT INTO media_operations (
                id,
                operation_type,
                operation_kind,
                entity_id,
                entity_kind,
                batch_id,
                source_path,
                content_hash,
                status,
                stage,
                priority,
                queue_name,
                position_key,
                progress_percent,
                items_total,
                items_completed,
                items_failed,
                result_summary,
                created_at,
                started_at,
                updated_at,
                completed_at,
                idempotency_key
            )
            VALUES (
                $operationId,
                'ingestion.file',
                'ingestion',
                $assetId,
                'MediaAsset',
                $batchId,
                'C:/watch/movies/Dune.2021.mkv',
                'dune-hash',
                'succeeded',
                'completed',
                100,
                'ingestion',
                1,
                100,
                1,
                1,
                0,
                'Dune (2021)',
                $startedAt,
                $startedAt,
                $completedAt,
                $completedAt,
                'activity-batch-test'
            );

            INSERT INTO ingestion_log (
                id,
                file_path,
                media_asset_id,
                content_hash,
                status,
                media_type,
                detected_title,
                normalized_title,
                wikidata_qid,
                ingestion_run_id,
                created_at,
                updated_at
            )
            VALUES (
                $ingestionLogId,
                'C:/watch/movies/Dune.2021.mkv',
                $assetId,
                'dune-hash',
                'registered',
                'Movies',
                'Dune',
                'dune',
                'Q1058080',
                $batchId,
                $startedAt,
                $completedAt
            );

            INSERT INTO media_operation_events (
                id,
                operation_id,
                entity_id,
                batch_id,
                event_type,
                new_status,
                new_stage,
                message,
                detail_json,
                occurred_at
            )
            VALUES (
                $operationEventId,
                $operationId,
                $assetId,
                $batchId,
                'FileHashed',
                'succeeded',
                'hashed',
                'SHA-256 fingerprint computed',
                '{}',
                $startedAt
            );

            INSERT INTO persons (id, name, wikidata_qid, headshot_url, created_at, enriched_at)
            VALUES ($personId, 'Frank Herbert', 'Q255149', 'https://example.invalid/herbert.jpg', $startedAt, $completedAt);

            INSERT INTO person_media_links (media_asset_id, person_id, role)
            VALUES ($assetId, $personId, 'Author');

            INSERT INTO entity_assets (
                id,
                entity_id,
                entity_type,
                asset_type,
                image_url,
                local_image_path,
                aspect_class,
                created_at
            )
            VALUES (
                $artworkId,
                $workId,
                'Work',
                'CoverArt',
                'https://example.invalid/dune.jpg',
                'assets/dune.jpg',
                'Poster',
                $completedAt
            );

            INSERT INTO review_queue (
                id,
                entity_id,
                entity_type,
                trigger,
                status,
                detail,
                created_at,
                source_operation_id,
                source_capability_id,
                review_ready_at
            )
            VALUES (
                $reviewId,
                $assetId,
                'MediaAsset',
                'NeedsMetadataReview',
                'Pending',
                'Provider mismatch requires review',
                $completedAt,
                $operationId,
                'identity.retail',
                $completedAt
            );

            INSERT INTO ingestion_batch_artifacts (
                id,
                batch_id,
                artifact_type,
                artifact_id,
                parent_entity_id,
                parent_entity_type,
                action,
                display_name,
                provider_id,
                source,
                detail_json,
                occurred_at
            )
            VALUES (
                $artifactId,
                $batchId,
                'provider_match',
                $assetId,
                $workId,
                'Work',
                'matched',
                'Dune (2021) [TMDB]',
                'tmdb',
                'Retail Match',
                '{not-json',
                $completedAt
            );

            INSERT INTO system_activity (
                occurred_at,
                action_type,
                collection_name,
                entity_id,
                entity_type,
                detail,
                ingestion_run_id
            )
            VALUES
                ($completedAt, 'MediaAdded', 'Dune (2021)', $assetId, 'MediaAsset', 'Dune (2021) added to library', $batchId),
                ($completedAt, 'PersonHydrated', 'Frank Herbert', $personId, 'Person', 'Person "Frank Herbert" enriched from Wikidata', $batchId);
            """;

        AddGuid(cmd, "$batchId", batchId);
        AddGuid(cmd, "$workId", workId);
        AddGuid(cmd, "$editionId", editionId);
        AddGuid(cmd, "$assetId", assetId);
        AddGuid(cmd, "$personId", personId);
        AddGuid(cmd, "$operationId", operationId);
        AddGuid(cmd, "$jobId", Guid.NewGuid());
        AddGuid(cmd, "$ingestionLogId", Guid.NewGuid());
        AddGuid(cmd, "$operationEventId", Guid.NewGuid());
        AddGuid(cmd, "$artworkId", artworkId);
        AddGuid(cmd, "$reviewId", Guid.NewGuid());
        AddGuid(cmd, "$artifactId", Guid.NewGuid());
        cmd.Parameters.AddWithValue("$startedAt", now.AddMinutes(-5).ToString("O"));
        cmd.Parameters.AddWithValue("$completedAt", now.ToString("O"));
        cmd.ExecuteNonQuery();

        return new ActivitySeed(batchId, workId, assetId, personId, artworkId);
    }

    private ActivitySeed SeedBooksAndAudiobooksBatch()
    {
        var batchId = Guid.NewGuid();
        var bookWorkId = Guid.NewGuid();
        var audiobookWorkId = Guid.NewGuid();
        var bookEditionId = Guid.NewGuid();
        var audiobookEditionId = Guid.NewGuid();
        var bookAssetId = Guid.NewGuid();
        var audiobookAssetId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ingestion_batches (
                id,
                status,
                source_path,
                category,
                files_total,
                files_processed,
                files_registered,
                files_review,
                files_no_match,
                files_failed,
                started_at,
                completed_at,
                created_at,
                updated_at
            )
            VALUES (
                $batchId,
                'completed',
                'C:/watch/read',
                'Books',
                2,
                2,
                2,
                0,
                0,
                0,
                $startedAt,
                $completedAt,
                $startedAt,
                $completedAt
            );

            INSERT INTO works (id, media_type, work_kind)
            VALUES
                ($bookWorkId, 'Books', 'standalone'),
                ($audiobookWorkId, 'Books', 'standalone');

            INSERT INTO editions (id, work_id, format_label)
            VALUES
                ($bookEditionId, $bookWorkId, 'Ebook'),
                ($audiobookEditionId, $audiobookWorkId, 'Audiobook');

            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
            VALUES
                ($bookAssetId, $bookEditionId, 'kindred-book-hash', 'C:/library/books/Kindred.epub'),
                ($audiobookAssetId, $audiobookEditionId, 'kindred-audio-hash', 'C:/library/audiobooks/Kindred.m4b');

            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES
                ($bookAssetId, 'title', 'Kindred', $completedAt),
                ($audiobookAssetId, 'title', 'Kindred', $completedAt);

            INSERT INTO identity_jobs (
                id,
                entity_id,
                entity_type,
                media_type,
                ingestion_run_id,
                state,
                pass,
                created_at,
                updated_at
            )
            VALUES
                ($bookJobId, $bookAssetId, 'MediaAsset', 'Books', $batchId, 'ReadyWithoutUniverse', 'Quick', $startedAt, $completedAt),
                ($audiobookJobId, $audiobookAssetId, 'MediaAsset', 'Books', $batchId, 'ReadyWithoutUniverse', 'Quick', $startedAt, $completedAt);

            INSERT INTO media_operations (
                id,
                operation_type,
                operation_kind,
                entity_id,
                entity_kind,
                batch_id,
                source_path,
                content_hash,
                status,
                stage,
                priority,
                queue_name,
                position_key,
                progress_percent,
                items_total,
                items_completed,
                items_failed,
                result_summary,
                created_at,
                started_at,
                updated_at,
                completed_at,
                idempotency_key
            )
            VALUES
                ($bookOperationId1, 'ingestion.file', 'ingestion', $bookAssetId, 'MediaAsset', $batchId, 'C:/watch/books/Kindred.epub', 'kindred-book-hash', 'succeeded', 'completed', 100, 'ingestion', 1, 100, 1, 1, 0, 'Kindred', $startedAt, $startedAt, $startedAt, $startedAt, 'kindred-book-first'),
                ($bookOperationId2, 'ingestion.file', 'ingestion', $bookAssetId, 'MediaAsset', $batchId, 'C:/watch/books/Kindred.epub', 'kindred-book-hash', 'succeeded', 'completed', 100, 'ingestion', 2, 100, 1, 1, 0, 'Kindred', $startedAt, $startedAt, $completedAt, $completedAt, 'kindred-book-latest'),
                ($audiobookOperationId, 'ingestion.file', 'ingestion', $audiobookAssetId, 'MediaAsset', $batchId, 'C:/watch/books/Kindred.m4b', 'kindred-audio-hash', 'succeeded', 'completed', 100, 'ingestion', 3, 100, 1, 1, 0, 'Kindred', $startedAt, $startedAt, $completedAt, $completedAt, 'kindred-audio');
            """;

        AddGuid(cmd, "$batchId", batchId);
        AddGuid(cmd, "$bookWorkId", bookWorkId);
        AddGuid(cmd, "$audiobookWorkId", audiobookWorkId);
        AddGuid(cmd, "$bookEditionId", bookEditionId);
        AddGuid(cmd, "$audiobookEditionId", audiobookEditionId);
        AddGuid(cmd, "$bookAssetId", bookAssetId);
        AddGuid(cmd, "$audiobookAssetId", audiobookAssetId);
        AddGuid(cmd, "$bookJobId", Guid.NewGuid());
        AddGuid(cmd, "$audiobookJobId", Guid.NewGuid());
        AddGuid(cmd, "$bookOperationId1", Guid.NewGuid());
        AddGuid(cmd, "$bookOperationId2", Guid.NewGuid());
        AddGuid(cmd, "$audiobookOperationId", Guid.NewGuid());
        cmd.Parameters.AddWithValue("$startedAt", now.AddMinutes(-2).ToString("O"));
        cmd.Parameters.AddWithValue("$completedAt", now.ToString("O"));
        cmd.ExecuteNonQuery();

        return new ActivitySeed(batchId, bookWorkId, bookAssetId, Guid.Empty, Guid.Empty);
    }

    private ActivitySeed SeedTvActivityBatch()
    {
        var batchId = Guid.NewGuid();
        var showWorkId = Guid.NewGuid();
        var seasonWorkId = Guid.NewGuid();
        var episodeWorkId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ingestion_batches (
                id,
                status,
                source_path,
                category,
                files_total,
                files_processed,
                files_registered,
                files_review,
                files_no_match,
                files_failed,
                started_at,
                completed_at,
                created_at,
                updated_at
            )
            VALUES (
                $batchId,
                'completed',
                'C:/watch/tv',
                'TV',
                1,
                1,
                1,
                0,
                0,
                0,
                $startedAt,
                $completedAt,
                $startedAt,
                $completedAt
            );

            INSERT INTO works (id, media_type, work_kind, parent_work_id)
            VALUES
                ($showWorkId, 'TV', 'parent', NULL),
                ($seasonWorkId, 'TV', 'parent', $showWorkId),
                ($episodeWorkId, 'TV', 'child', $seasonWorkId);

            INSERT INTO editions (id, work_id, format_label)
            VALUES ($editionId, $episodeWorkId, 'Episode');

            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
            VALUES ($assetId, $editionId, 'expanse-s01e01-hash', 'C:/library/tv/The Expanse/Season 01/The Expanse - S01E01.mkv');

            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES
                ($showWorkId, 'show_name', 'The Expanse', $completedAt),
                ($seasonWorkId, 'season_number', '1', $completedAt),
                ($episodeWorkId, 'episode_number', '1', $completedAt),
                ($episodeWorkId, 'episode_title', 'Dulcinea', $completedAt);

            INSERT INTO identity_jobs (
                id,
                entity_id,
                entity_type,
                media_type,
                ingestion_run_id,
                state,
                pass,
                created_at,
                updated_at
            )
            VALUES (
                $jobId,
                $assetId,
                'MediaAsset',
                'TV',
                $batchId,
                'ReadyWithoutUniverse',
                'Quick',
                $startedAt,
                $completedAt
            );

            INSERT INTO media_operations (
                id,
                operation_type,
                operation_kind,
                entity_id,
                entity_kind,
                batch_id,
                source_path,
                content_hash,
                status,
                stage,
                priority,
                queue_name,
                position_key,
                progress_percent,
                items_total,
                items_completed,
                items_failed,
                result_summary,
                created_at,
                started_at,
                updated_at,
                completed_at,
                idempotency_key
            )
            VALUES (
                $operationId,
                'ingestion.file',
                'ingestion',
                $assetId,
                'MediaAsset',
                $batchId,
                'C:/watch/tv/The Expanse/Season 01/The Expanse - S01E01 - Dulcinea.mkv',
                'expanse-s01e01-hash',
                'succeeded',
                'completed',
                100,
                'ingestion',
                1,
                100,
                1,
                1,
                0,
                'Dulcinea',
                $startedAt,
                $startedAt,
                $completedAt,
                $completedAt,
                'expanse-s01e01'
            );
            """;

        AddGuid(cmd, "$batchId", batchId);
        AddGuid(cmd, "$showWorkId", showWorkId);
        AddGuid(cmd, "$seasonWorkId", seasonWorkId);
        AddGuid(cmd, "$episodeWorkId", episodeWorkId);
        AddGuid(cmd, "$editionId", editionId);
        AddGuid(cmd, "$assetId", assetId);
        AddGuid(cmd, "$operationId", operationId);
        AddGuid(cmd, "$jobId", Guid.NewGuid());
        cmd.Parameters.AddWithValue("$startedAt", now.AddMinutes(-1).ToString("O"));
        cmd.Parameters.AddWithValue("$completedAt", now.ToString("O"));
        cmd.ExecuteNonQuery();

        return new ActivitySeed(batchId, episodeWorkId, assetId, Guid.Empty, Guid.Empty);
    }

    private static void AddGuid(SqliteCommand cmd, string name, Guid value) =>
        cmd.Parameters.Add(name, SqliteType.Blob).Value = GuidSql.ToBlob(value);

    private sealed record ActivitySeed(Guid BatchId, Guid WorkId, Guid AssetId, Guid PersonId, Guid ArtworkId);
}
