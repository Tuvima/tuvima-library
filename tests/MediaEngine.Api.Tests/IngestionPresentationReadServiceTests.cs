using Dapper;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

public sealed class IngestionPresentationReadServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;

    public IngestionPresentationReadServiceTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_ingestion_presentation_{Guid.NewGuid():N}.db");
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
    public async Task NotificationReadReturnsOnlyNewestThreeWithDurableOutstandingWork()
    {
        var now = DateTimeOffset.UtcNow;
        var ids = Enumerable.Range(0, 4).Select(index => AddBatch("completed", 1, 1, now.AddMinutes(index))).ToArray();
        using var conn = _db.CreateConnection();
        conn.Execute("UPDATE ingestion_batches SET files_review=2 WHERE id=@id;", new { id = ids[3] });
        conn.Execute("""
            INSERT INTO identity_jobs (id,entity_id,entity_type,media_type,ingestion_run_id,state,pass,created_at,updated_at)
            VALUES (@id,@asset,'MediaAsset','Books',@batch,'Queued','Quick',@now,@now);
            """, new { id = Guid.NewGuid(), asset = Guid.NewGuid(), batch = ids[3], now = now.ToString("O") });
        var rows = await new MediaEngine.Api.Services.IngestionNotificationReadService(_db).GetRecentAsync();
        Assert.Equal(ids.Reverse().Take(3), rows.Select(row => row.BatchId));
        Assert.Equal("completed", rows[0].Status);
        Assert.Equal(2, rows[0].ReviewCount);
        Assert.Equal(1, rows[0].OutstandingOperations);
        Assert.Equal(0, rows[1].OutstandingOperations);
    }

    [Fact]
    public async Task AvailabilityReadServicesHonorCancellationBeforeOpeningDatabase()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => IngestionAvailability.IsUpdatingAsync(null!, Guid.NewGuid(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MediaEngine.Api.Services.IngestionNotificationReadService(null!).GetRecentAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MediaEngine.Api.Services.Details.ContributorReadiness(null!).ApplyAsync(new MediaEngine.Contracts.Details.DetailPageViewModel(), cancellation.Token));
    }

    [Fact]
    public async Task ContributorReadinessUpdatesEveryCreditAndClearsAfterEnrichment()
    {
        var batch = AddBatch("running", 1, 1);
        var work = AddStandalone(batch, "Books", "Readiness Fixture");
        var person = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        var asset = conn.QuerySingle<Guid>("SELECT a.id FROM media_assets a JOIN editions e ON e.id=a.edition_id WHERE e.work_id=@work;", new { work });
        conn.Execute("INSERT INTO persons (id,name,created_at) VALUES (@person,'Fixture Author',@now);", new { person, now = DateTimeOffset.UtcNow.ToString("O") });
        conn.Execute("INSERT INTO person_media_links (media_asset_id,person_id,role) VALUES (@asset,@person,'Author');", new { asset, person });
        conn.Execute("""
            INSERT INTO media_operations (id,operation_type,operation_kind,entity_id,batch_id,status,position_key,created_at,updated_at,idempotency_key)
            VALUES (@id,'enrichment.people','enrichment',@asset,@batch,'queued',1,@now,@now,@key);
            """, new { id = Guid.NewGuid(), asset, batch, now = DateTimeOffset.UtcNow.ToString("O"), key = Guid.NewGuid().ToString() });
        var first = new MediaEngine.Contracts.Details.EntityCreditViewModel { EntityId = person.ToString("D") };
        var duplicate = new MediaEngine.Contracts.Details.EntityCreditViewModel { EntityId = person.ToString("D") };
        var unrelated = new MediaEngine.Contracts.Details.EntityCreditViewModel { EntityId = Guid.NewGuid().ToString("D"), IsUpdatingDetails = true };
        var detail = new MediaEngine.Contracts.Details.DetailPageViewModel
        {
            ContributorGroups = [new() { Credits = [first] }],
            FullContributorGroups = [new() { Credits = [duplicate] }],
            PreviewContributors = [unrelated],
        };
        var readiness = new MediaEngine.Api.Services.Details.ContributorReadiness(_db);
        await readiness.ApplyAsync(detail, CancellationToken.None);
        Assert.True(first.IsUpdatingDetails);
        Assert.True(duplicate.IsUpdatingDetails);
        Assert.False(unrelated.IsUpdatingDetails);
        conn.Execute("UPDATE persons SET enriched_at=@now WHERE id=@person;", new { person, now = DateTimeOffset.UtcNow.ToString("O") });
        await readiness.ApplyAsync(detail, CancellationToken.None);
        Assert.False(first.IsUpdatingDetails);
        Assert.False(duplicate.IsUpdatingDetails);
    }

    [Fact]
    public async Task CurrentMedia_GroupsFilesByStructuralIdentityAndShowsAddedCountsWithoutCatalogueTotals()
    {
        var batchId = AddBatch("running", 45, 28);
        var completeAlbum = AddContainer("Music", "Album One", expectedKey: "track_count", expectedValue: "17");
        AddChildren(batchId, completeAlbum, "Music", "Song", 17, "track_number");

        var partialAlbum = AddContainer("Music", "Album Two", expectedKey: "track_count", expectedValue: "17");
        AddChildren(batchId, partialAlbum, "Music", "Part", 14, "track_number");

        var unknownAlbum = AddContainer("Music", "Unknown Total");
        AddChildren(batchId, unknownAlbum, "Music", "Track", 3, "track_number");

        var show = AddContainer("TV", "The Example Show");
        var season = AddContainer("TV", "Season 2", show, "episode_count", "10");
        AddChildren(batchId, season, "TV", "Episode", 10, "episode_number", seasonNumber: 2);

        var audiobook = AddContainer("Audiobooks", "A Long Listen", expectedKey: "audiobook_part_count", expectedValue: "4");
        AddChildren(batchId, audiobook, "Audiobooks", "Part", 4, partCount: 4);

        var service = new IngestionPresentationReadService(_db);
        var page = await service.GetCurrentMediaAsync(0, 50);
        var snapshot = await service.GetSnapshotAsync();

        Assert.Equal(5, page.TotalCount);
        Assert.Equal(0, snapshot.CurrentMediaTotal); // Settled groups leave the live tile rail.
        Assert.Equal(5, snapshot.ReadyGroups);
        var one = Assert.Single(page.Items, item => item.GroupId == completeAlbum);
        Assert.Equal(17, one.ChildCompleted);
        Assert.Equal(17, one.FileCount);
        Assert.Null(one.ChildExpected);
        Assert.Equal("tracks", one.ChildUnit);
        Assert.Equal("notApplicable", one.Relationships.State);

        var partial = Assert.Single(page.Items, item => item.GroupId == partialAlbum);
        Assert.Equal(14, partial.ChildCompleted);
        Assert.Null(partial.ChildExpected);

        var unknown = Assert.Single(page.Items, item => item.GroupId == unknownAlbum);
        Assert.Equal(3, unknown.ChildCompleted);
        Assert.Null(unknown.ChildExpected);

        var tv = Assert.Single(page.Items, item => item.GroupId == show);
        Assert.Equal("Season 2", tv.Subtitle);
        Assert.Equal(10, tv.ChildCompleted);
        Assert.Null(tv.ChildExpected);

        var audio = Assert.Single(page.Items, item => item.GroupId == audiobook);
        Assert.Equal("Audiobooks", audio.MediaType);
        Assert.Equal(4, audio.ChildCompleted);
        Assert.Null(audio.ChildExpected);
    }

    [Fact]
    public async Task CurrentMedia_DoesNotMergeDistinctWorksWithTheSameTitle()
    {
        var batchId = AddBatch("running", 2, 2);
        AddStandalone(batchId, "Movies", "The Same Title");
        AddStandalone(batchId, "Movies", "The Same Title");

        var service = new IngestionPresentationReadService(_db);
        var page = await service.GetCurrentMediaAsync(0, 50);

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(2, page.Items.Select(item => item.GroupId).Distinct().Count());
        Assert.All(page.Items, item => Assert.Equal("The Same Title", item.Title));
    }

    [Fact]
    public async Task CurrentMedia_CountsDistinctTracksInsteadOfDuplicateAssetsOrTrackNumbers()
    {
        var batchId = AddBatch("running", 6, 6);
        var albumId = AddContainer("Music", "A Night at the Opera", expectedKey: "track_count", expectedValue: "12");
        AddChildren(batchId, albumId, "Music", "Song", 5, "track_number");

        using (var conn = _db.CreateConnection())
        {
            var child = conn.QuerySingle<ChildIdentity>("""
                SELECT w.id AS WorkId, e.id AS EditionId
                FROM works w
                JOIN editions e ON e.work_id = w.id
                WHERE w.parent_work_id = @albumId
                ORDER BY w.id
                LIMIT 1;
                """, new { albumId });
            var duplicateAssetId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow.ToString("O");
            conn.Execute("""
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at)
                VALUES (@duplicateAssetId, @editionId, @hash, @path, @now);
                """, new
            {
                duplicateAssetId,
                editionId = child.EditionId,
                hash = $"hash-{duplicateAssetId:N}",
                path = $"C:/watch/{duplicateAssetId:N}.media",
                now,
            });
            InsertLog(conn, Guid.NewGuid(), batchId, duplicateAssetId, "Music", "Duplicate encoding", now);
        }

        var service = new IngestionPresentationReadService(_db);
        var item = Assert.Single((await service.GetCurrentMediaAsync(0, 50)).Items);

        Assert.Equal(5, item.ChildCompleted);
        Assert.Equal(6, item.FileCount);
        Assert.Null(item.ChildExpected);
        Assert.Equal("tracks", item.ChildUnit);
    }

    [Fact]
    public async Task Children_FormatDecimalMinuteTrackDurationsAndFitSixtyRowsInOneResponse()
    {
        var batchId = AddBatch("running", 60, 60);
        var albumId = AddContainer("Music", "Long Album");
        AddChildren(batchId, albumId, "Music", "Song", 60, "track_number");
        using (var conn = _db.CreateConnection())
        {
            var firstTrackId = conn.QuerySingle<Guid>("SELECT id FROM works WHERE parent_work_id = @albumId ORDER BY ordinal LIMIT 1;", new { albumId });
            conn.Execute("INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@firstTrackId, 'duration', '5.905', @now);",
                new { firstTrackId, now = DateTimeOffset.UtcNow.ToString("O") });
        }

        var children = await new IngestionPresentationReadService(_db).GetChildrenAsync(batchId, albumId, 0, 250);

        Assert.Equal(60, children.Items.Count);
        Assert.False(children.HasMore);
        Assert.Equal("5:54", children.Items[0].DurationLabel);
    }

    [Fact]
    public async Task CurrentMedia_PagesGroupsBeforeProjectingDetails()
    {
        var batchId = AddBatch("running", 75, 75);
        for (var index = 1; index <= 75; index++)
        {
            AddStandalone(batchId, "Movies", $"Current Movie {index:00}");
        }

        var service = new IngestionPresentationReadService(_db);
        var page = await service.GetCurrentMediaAsync(50, 50);

        Assert.Equal(75, page.TotalCount);
        Assert.Equal(25, page.Items.Count);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task RecentAdditions_IncludesEarlierRunsAndPagesBeyondFiftyGroups()
    {
        var firstBatch = AddBatch("completed", 30, 30, DateTimeOffset.UtcNow.AddDays(-2));
        var secondBatch = AddBatch("completed", 55, 55, DateTimeOffset.UtcNow.AddDays(-1));
        for (var index = 1; index <= 30; index++)
        {
            AddStandalone(firstBatch, "Books", $"Earlier Book {index:00}", presented: true);
        }

        for (var index = 1; index <= 55; index++)
        {
            AddStandalone(secondBatch, "Movies", $"Recent Movie {index:00}", presented: true);
        }

        var service = new IngestionPresentationReadService(_db);
        var firstPage = await service.GetRecentAdditionsAsync(null, null, null, null, 0, 50);
        var secondPage = await service.GetRecentAdditionsAsync(null, null, null, null, 50, 50);

        Assert.Equal(85, firstPage.TotalCount);
        Assert.Equal(50, firstPage.Items.Count);
        Assert.True(firstPage.HasMore);
        Assert.Equal(35, secondPage.Items.Count);
        Assert.False(secondPage.HasMore);
        Assert.Contains(secondPage.Items, item => item.BatchId == firstBatch);
    }

    [Fact]
    public async Task BatchMedia_PagesPastTheFirstTwentyFiveItems()
    {
        var batchId = AddBatch("completed", 30, 30);
        for (var index = 1; index <= 30; index++)
        {
            AddStandalone(batchId, "Movies", $"Movie {index:00}", presented: true);
        }

        var service = new IngestionPresentationReadService(_db);
        var page = await service.GetBatchMediaAsync(batchId, 25, 25);
        var detail = await service.GetBatchPresentationAsync(batchId);

        Assert.Equal(30, page.TotalCount);
        Assert.Equal(5, page.Items.Count);
        Assert.NotNull(detail);
        Assert.Equal(30, detail.AddedTotal);
        Assert.Null(detail.PeopleUpdated);
        Assert.Null(detail.ArtworkAdded);
        Assert.Null(detail.TextTracksAdded);
    }

    [Fact]
    public async Task HistoricalBatch_DoesNotUseCatalogueTotalsAsRunDenominators()
    {
        var batchId = AddBatch("completed", 3, 3);
        var album = AddContainer("Music", "Partial Album", expectedKey: "track_count", expectedValue: "17");
        AddChildren(batchId, album, "Music", "Track", 3, "track_number");
        using (var conn = _db.CreateConnection())
        {
            conn.Execute("UPDATE media_assets SET presented_at = @now;", new { now = DateTimeOffset.UtcNow.ToString("O") });
        }

        var service = new IngestionPresentationReadService(_db);
        var page = await service.GetBatchMediaAsync(batchId, 0, 50);

        var item = Assert.Single(page.Items);
        Assert.Equal(3, item.ChildCompleted);
        Assert.Null(item.ChildExpected);
        Assert.Equal("tracks", item.ChildUnit);
    }

    [Fact]
    public async Task BatchMedia_AppliesSearchAndLaneBeforePaging()
    {
        var batchId = AddBatch("completed", 61, 61);
        for (var index = 1; index <= 60; index++)
        {
            AddStandalone(batchId, "Movies", $"Movie {index:00}", presented: true);
        }

        AddStandalone(batchId, "Books", "The Needle Book", presented: true);

        var service = new IngestionPresentationReadService(_db);
        var page = await service.GetBatchMediaAsync(batchId, 0, 50, "needle", "Read", "newest");

        var item = Assert.Single(page.Items);
        Assert.Equal("The Needle Book", item.Title);
        Assert.Equal("Books", item.MediaType);
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task BatchMedia_HidesArtworkBearingGroupsUntilTheyHaveARealTitle()
    {
        var batchId = AddBatch("completed", 1, 1);
        var workId = AddStandalone(batchId, "Books", "Temporary detection", presented: true);
        using (var conn = _db.CreateConnection())
        {
            conn.Execute("DELETE FROM canonical_values WHERE entity_id = @workId AND key = 'title';", new { workId });
            conn.Execute("UPDATE ingestion_log SET detected_title = NULL, normalized_title = NULL WHERE ingestion_run_id = @batchId;", new { batchId });
            conn.Execute("""
                INSERT INTO entity_assets (
                    id, entity_id, entity_type, asset_type, local_image_path,
                    aspect_class, asset_class, storage_location, owner_scope,
                    is_preferred, created_at)
                VALUES (
                    @id, @workId, 'Work', 'CoverArt', 'C:/test/premature-cover.jpg',
                    'Portrait', 'Artwork', 'Central', 'Work', 1, @now);
                """, new { id = Guid.NewGuid(), workId, now = DateTimeOffset.UtcNow.ToString("O") });
        }

        var service = new IngestionPresentationReadService(_db);
        var page = await service.GetBatchMediaAsync(batchId, 0, 50);
        var presentation = await service.GetBatchPresentationAsync(batchId);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
        Assert.NotNull(presentation);
        Assert.Empty(presentation.AddedPreview);
        Assert.Equal(0, presentation.AddedTotal);
    }

    [Fact]
    public async Task RecentAdditions_DoesNotTreatLaterReprocessingAsANewAddition()
    {
        var additionBatch = AddBatch("completed", 1, 1);
        var workId = AddStandalone(additionBatch, "Movies", "Added Once", presented: true);
        Guid assetId;
        using (var conn = _db.CreateConnection())
        {
            assetId = conn.ExecuteScalar<Guid>("""
                SELECT ma.id
                FROM media_assets ma
                JOIN editions e ON e.id = ma.edition_id
                WHERE e.work_id = @workId;
                """, new { workId });
        }

        var refreshTime = DateTimeOffset.UtcNow.AddMinutes(5);
        var refreshBatch = AddBatch("completed", 1, 1, refreshTime);
        using (var conn = _db.CreateConnection())
        {
            InsertLog(
                conn,
                Guid.NewGuid(),
                refreshBatch,
                assetId,
                "Movies",
                "Added Once",
                refreshTime.ToString("O"));
        }

        var service = new IngestionPresentationReadService(_db);
        var page = await service.GetRecentAdditionsAsync(null, null, null, null, 0, 50);

        var item = Assert.Single(page.Items);
        Assert.Equal(additionBatch, item.BatchId);
    }

    [Fact]
    public async Task RecentAdditions_AppliesSearchAndLaneBeforeBuildingThePage()
    {
        var batchId = AddBatch("completed", 61, 61);
        for (var index = 1; index <= 60; index++)
        {
            AddStandalone(batchId, "Movies", $"Movie {index:00}", presented: true);
        }

        AddStandalone(batchId, "Books", "The Needle Book", presented: true);

        var service = new IngestionPresentationReadService(_db);
        var page = await service.GetRecentAdditionsAsync("needle", "Read", null, null, 0, 50);

        var item = Assert.Single(page.Items);
        Assert.Equal("The Needle Book", item.Title);
        Assert.Equal("Books", item.MediaType);
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task OperationsArtwork_UsesSmallPreviewAndMediumGridRenditions()
    {
        var batchId = AddBatch("completed", 1, 1);
        var workId = AddStandalone(batchId, "Books", "Sized Artwork", presented: true);
        var artworkId = Guid.NewGuid();
        using (var conn = _db.CreateConnection())
        {
            conn.Execute("""
                INSERT INTO entity_assets (
                    id, entity_id, entity_type, asset_type, local_image_path,
                    aspect_class, asset_class, storage_location, owner_scope,
                    is_preferred, created_at)
                VALUES (
                    @artworkId, @workId, 'Work', 'CoverArt', 'C:/test/cover.jpg',
                    'Portrait', 'Artwork', 'Central', 'Work', 1, @now);
                """, new { artworkId, workId, now = DateTimeOffset.UtcNow.ToString("O") });
        }

        var service = new IngestionPresentationReadService(_db);
        var presentation = await service.GetBatchPresentationAsync(batchId);
        var page = await service.GetBatchMediaAsync(batchId, 0, 50);

        Assert.NotNull(presentation);
        Assert.EndsWith($"/stream/artwork/{artworkId:D}?size=s", Assert.Single(presentation.AddedPreview).CoverUrl);
        Assert.EndsWith($"/stream/artwork/{artworkId:D}?size=m", Assert.Single(page.Items).CoverUrl);
    }

    [Fact]
    public async Task ActivitySummary_CountsGroupedAdditionsWithoutBuildingMediaCards()
    {
        var batchId = AddBatch("completed", 17, 17);
        var album = AddContainer("Music", "One Album", expectedKey: "track_count", expectedValue: "17");
        AddChildren(batchId, album, "Music", "Track", 17, "track_number");
        using (var conn = _db.CreateConnection())
        {
            conn.Execute("UPDATE media_assets SET presented_at = @now;", new { now = DateTimeOffset.UtcNow.ToString("O") });
        }

        var service = new IngestionPresentationReadService(_db);
        var summary = await service.GetActivitySummaryAsync();

        Assert.Equal(1, summary.ItemsAddedToday);
        Assert.Equal(1, summary.LastGroupsAdded);
    }

    [Fact]
    public async Task Snapshot_BoundsRecentDayCardsWhileKeepingDayTotals()
    {
        var firstDay = DateTimeOffset.UtcNow.AddDays(-2);
        var secondDay = DateTimeOffset.UtcNow.AddDays(-1);
        var firstBatch = AddBatch("completed", 4, 4, firstDay);
        var secondBatch = AddBatch("completed", 5, 5, secondDay);
        for (var index = 1; index <= 4; index++)
        {
            AddStandalone(firstBatch, "Books", $"Earlier Book {index}", presented: true, occurredAt: firstDay);
        }

        for (var index = 1; index <= 5; index++)
        {
            AddStandalone(secondBatch, "Movies", $"Recent Movie {index}", presented: true, occurredAt: secondDay);
        }

        var service = new IngestionPresentationReadService(_db);
        var snapshot = await service.GetSnapshotAsync(currentLimit: 8, recentDayLimit: 2, recentItemsPerDay: 2);

        Assert.Equal(2, snapshot.RecentDays.Count);
        Assert.All(snapshot.RecentDays, day => Assert.Equal(2, day.Items.Count));
        Assert.Equal([5, 4], snapshot.RecentDays.Select(day => day.TotalCount).ToArray());
    }

    [Fact]
    public async Task MultiFileBookUsesIdentityUnitsAndUnmatchedWorkIsNotACompletedAddition()
    {
        var batch = AddBatch("running", 40, 40);
        var work = AddStandalone(batch, "Audiobooks", "A forty-file book", presented: true);
        using var conn = _db.CreateConnection();
        var asset = conn.QuerySingle<Guid>("SELECT ma.id FROM media_assets ma JOIN editions e ON e.id=ma.edition_id WHERE e.work_id=@work", new { work });
        conn.Execute("""
            INSERT INTO identity_jobs (id,entity_id,entity_type,media_type,ingestion_run_id,state,pass,created_at,updated_at)
            VALUES (@id,@asset,'MediaAsset','Audiobooks',@batch,'RetailNoMatch','Quick',@now,@now);
            """, new { id = Guid.NewGuid(), asset, batch, now = DateTimeOffset.UtcNow.ToString("O") });
        var service = new MediaEngine.Providers.Services.BatchProgressService(new IngestionBatchRepository(_db), null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaEngine.Providers.Services.BatchProgressService>.Instance);
        var progress = (await service.GetProgressAsync(batch))!;
        Assert.Equal(40, progress.FilesProcessed);
        Assert.Equal(1, progress.WorkUnitsTotal);
        Assert.Equal(1, progress.WorkUnitsCompleted);
        Assert.True(progress.IsComplete);
        var presentation = new IngestionPresentationReadService(_db);
        Assert.Equal("review", Assert.Single((await presentation.GetCurrentMediaAsync(0, 50)).Items).Availability);
        Assert.Empty((await presentation.GetSnapshotAsync()).RecentDays);
        conn.Execute("UPDATE identity_jobs SET state='ReadyWithoutUniverse';");
        Assert.Single((await presentation.GetSnapshotAsync()).RecentDays);
        await new MediaOperationRepository(_db).EnsureAsync(new MediaEngine.Domain.Entities.MediaOperation
        {
            OperationType = "enrichment.people", OperationKind = "enrichment", BatchId = batch,
            EntityId = asset, Status = "queued", QueueName = "people", IdempotencyKey = "test:people",
        });
        var enriching = (await service.GetProgressAsync(batch))!;
        Assert.False(enriching.IsComplete);
        Assert.True(enriching.ProgressIsIndeterminate);
        Assert.Equal("Finishing enrichment", enriching.CurrentStage);
        Assert.NotEqual(99, enriching.ProgressPercent);
    }

    private Guid AddBatch(string status, int total, int processed, DateTimeOffset? occurredAt = null)
    {
        var id = Guid.NewGuid();
        var time = occurredAt ?? DateTimeOffset.UtcNow;
        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO ingestion_batches (
                id, status, source_path, category, files_total, files_processed,
                files_registered, files_review, files_no_match, files_failed,
                started_at, completed_at, created_at, updated_at)
            VALUES (
                @id, @status, 'C:/watch', 'Mixed', @total, @processed,
                @processed, 0, 0, 0, @time, @completedAt, @time, @time);
            """, new { id, status, total, processed, time = time.ToString("O"), completedAt = status == "completed" ? time.ToString("O") : null });
        return id;
    }

    [Fact]
    public async Task SharedBatchProgressStaysIncompleteUntilIdentityFinishes()
    {
        var batch = AddBatch("running", 1, 1);
        var work = AddStandalone(batch, "Books", "Still identifying");
        using var conn = _db.CreateConnection();
        var asset = conn.QuerySingle<Guid>("SELECT ma.id FROM media_assets ma JOIN editions e ON e.id=ma.edition_id WHERE e.work_id=@work", new { work });
        conn.Execute("""
            INSERT INTO identity_jobs (id,entity_id,entity_type,media_type,ingestion_run_id,state,pass,created_at,updated_at)
            VALUES (@id,@asset,'MediaAsset','Books',@batch,'Hydrating','Quick',@now,@now);
            """, new { id = Guid.NewGuid(), asset, batch, now = DateTimeOffset.UtcNow.ToString("O") });
        var service = new MediaEngine.Providers.Services.BatchProgressService(new IngestionBatchRepository(_db), null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaEngine.Providers.Services.BatchProgressService>.Instance);
        var active = await service.GetProgressAsync(batch);
        Assert.NotNull(active);
        Assert.False(active.IsComplete);
        Assert.Equal(0, active.ProgressPercent);
        conn.Execute("UPDATE identity_jobs SET state='Ready';");
        var complete = await service.GetProgressAsync(batch);
        Assert.True(complete!.IsComplete);
        Assert.Equal(100, complete.ProgressPercent);
        conn.Execute("UPDATE media_assets SET presented_at=NULL; UPDATE ingestion_log SET status='queued_identity'; UPDATE ingestion_batches SET status='completed';");
        var history = await new IngestionPresentationReadService(_db).GetRecentAdditionsAsync(null, null, null, null, 0, 50);
        Assert.Equal(work, Assert.Single(history.Items).GroupId);
        conn.Execute("""
            INSERT INTO ingestion_log (id,file_path,status,ingestion_run_id) VALUES (@id,'C:/watch/duplicate.epub','duplicate',@batch);
            UPDATE ingestion_batches SET files_total=2 WHERE id=@batch;
            """, new { id = Guid.NewGuid(), batch });
        var withDuplicate = await service.GetProgressAsync(batch);
        Assert.True(withDuplicate!.IsComplete);
        Assert.Equal(2, withDuplicate.FilesProcessed);
        Assert.Equal(1, withDuplicate.FilesIdentified);

        // A different input file can resolve to an asset already handled in this
        // batch. Its duplicate outcome still settles that input in the batch total.
        conn.Execute("UPDATE ingestion_log SET media_asset_id=@asset WHERE status='duplicate';", new { asset });
        Assert.True((await service.GetProgressAsync(batch))!.IsComplete);
        var originalPath = conn.QuerySingle<string>("SELECT file_path FROM ingestion_log WHERE status='queued_identity';");
        conn.Execute("INSERT INTO ingestion_log (id,file_path,status,media_asset_id,ingestion_run_id) VALUES (@id,@originalPath,'duplicate',@asset,@batch);", new { id = Guid.NewGuid(), originalPath, asset, batch });
        Assert.Equal(1, (await new IngestionBatchRepository(_db).GetProgressSnapshotAsync(batch)).FilesSkipped);
    }

    [Fact]
    public async Task HistoricalPagingExcludesActiveAndStillEnrichingBatchesBeforeLimiting()
    {
        AddBatch("running", 1, 1);
        var pending = AddBatch("completed", 1, 1);
        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO identity_jobs (id,entity_id,entity_type,media_type,ingestion_run_id,state,pass,created_at,updated_at)
            VALUES (@id,@asset,'MediaAsset','Books',@pending,'Hydrating','Quick',@now,@now);
            """, new { id = Guid.NewGuid(), asset = Guid.NewGuid(), pending, now = DateTimeOffset.UtcNow.ToString("O") });
        var historical = AddBatch("completed", 1, 1, DateTimeOffset.UtcNow.AddDays(-1));
        var page = await new ActivityBatchReadService(_db).GetBatchesAsync(new MediaEngine.Application.ReadModels.ActivityBatchQuery(null, null, null, null, null, null, null, 0, 1, HistoricalOnly: true));
        Assert.Equal(historical, Assert.Single(page.Items).BatchId);
        Assert.Equal(1, page.TotalCount);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task CurrentPreviewFollowsIdentityActivityAndCountsReadyFiles()
    {
        var batch = AddBatch("running", 2, 2);
        var older = AddStandalone(batch, "Books", "Older intake", occurredAt: DateTimeOffset.UtcNow.AddHours(-1));
        var newer = AddStandalone(batch, "Books", "Newer intake");
        using var conn = _db.CreateConnection();
        var asset = conn.QuerySingle<Guid>("SELECT ma.id FROM media_assets ma JOIN editions e ON e.id = ma.edition_id WHERE e.work_id = @older", new { older });
        conn.Execute("""
            INSERT INTO identity_jobs (id,entity_id,entity_type,media_type,ingestion_run_id,state,pass,created_at,updated_at)
            VALUES (@id,@asset,'MediaAsset','Books',@batch,'Hydrating','Quick',@now,@now);
            UPDATE ingestion_log SET status = 'queued_identity' WHERE media_asset_id = @asset;
            """, new { id = Guid.NewGuid(), asset, batch, now = DateTimeOffset.UtcNow.ToString("O") });
        var service = new IngestionPresentationReadService(_db);
        Assert.Equal(older, Assert.Single((await service.GetCurrentMediaAsync(0, 1)).Items).GroupId);
        conn.Execute("UPDATE identity_jobs SET state = 'Ready';");
        var ready = (await service.GetCurrentMediaAsync(0, 50)).Items.Single(item => item.GroupId == older);
        Assert.Equal(1, ready.ChildCompleted);
        Assert.Equal("ready", ready.Availability);
        Assert.Equal(2, (await service.GetSnapshotAsync()).ReadyGroups);
    }

    [Fact]
    public async Task LiveSnapshot_WaitsForRootCoverAndNeverUsesEpisodeStill()
    {
        var batch = AddBatch("running", 1, 1);
        var show = AddContainer("TV", "Root Show");
        var season = AddContainer("TV", "Season 1", show);
        AddChildren(batch, season, "TV", "Episode", 1, "episode_number", seasonNumber: 1);
        using var conn = _db.CreateConnection();
        var child = conn.QuerySingle<Guid>("SELECT id FROM works WHERE parent_work_id=@season", new { season });
        var asset = conn.QuerySingle<Guid>("SELECT ma.id FROM media_assets ma JOIN editions e ON e.id=ma.edition_id WHERE e.work_id=@child", new { child });
        var now = DateTimeOffset.UtcNow.ToString("O");
        conn.Execute("DELETE FROM entity_assets;");
        conn.Execute("""
            INSERT INTO identity_jobs (id,entity_id,entity_type,media_type,ingestion_run_id,state,pass,created_at,updated_at)
            VALUES (@job,@asset,'MediaAsset','TV',@batch,'Hydrating','Quick',@now,@now);
            INSERT INTO entity_assets (id,entity_id,entity_type,asset_type,local_image_path,aspect_class,asset_class,storage_location,owner_scope,is_preferred,created_at)
            VALUES (@still,@child,'Work','EpisodeStill','C:/test/still.jpg','Landscape','Artwork','Central','Work',1,@now);
            """, new { job = Guid.NewGuid(), asset, batch, child, still = Guid.NewGuid(), now });

        var service = new IngestionPresentationReadService(_db);
        Assert.Empty((await service.GetSnapshotAsync()).CurrentMedia);

        var cover = Guid.NewGuid();
        conn.Execute("""
            INSERT INTO entity_assets (id,entity_id,entity_type,asset_type,local_image_path,aspect_class,asset_class,storage_location,owner_scope,is_preferred,created_at)
            VALUES (@cover,@show,'Work','CoverArt','C:/test/show-cover.jpg','Portrait','Artwork','Central','Work',1,@now);
            """, new { cover, show, now });

        var tile = Assert.Single((await service.GetSnapshotAsync()).CurrentMedia);
        Assert.Contains(cover.ToString("D"), tile.CoverUrl);
        Assert.Equal(50, tile.ProgressPercent);
        Assert.Equal("enriched", tile.CurrentGateKey);
        Assert.Equal(["Identified", "Matched metadata", "Enriching details", "Ready in library"], tile.ProgressGates.Select(gate => gate.Label));
    }

    [Fact]
    public async Task LiveSnapshot_UsesThreeEqualGatesWhenMatchingDoesNotApply()
    {
        var batch = AddBatch("running", 1, 1);
        var work = AddStandalone(batch, "Books", "Locally identified book");
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO media_operations (
                id, operation_type, operation_kind, entity_id, entity_kind, batch_id,
                status, stage, position_key, created_at, updated_at, idempotency_key)
            VALUES (
                @id, 'ingestion.enrichment', 'enrichment', @work, 'Work', @batch,
                'running', 'artwork', 1, @now, @now, @key);
            """, new { id = Guid.NewGuid(), work, batch, now, key = $"three-gates-{work:N}" });

        var tile = Assert.Single((await new IngestionPresentationReadService(_db).GetSnapshotAsync()).CurrentMedia);

        Assert.Equal(33, tile.ProgressPercent);
        Assert.Equal("enriched", tile.CurrentGateKey);
        Assert.Equal(["Identified", "Enriching details", "Ready in library"], tile.ProgressGates.Select(gate => gate.Label));
    }

    [Theory]
    [InlineData("abandoned", "UniverseEnriching", false)]
    [InlineData("interrupted", "Hydrating", true)]
    [InlineData("completed", "BridgeSearching", true)]
    [InlineData("failed", "RetailSearching", false)]
    [InlineData("abandoned", "Queued", false)]
    [InlineData("completed", "RetailMatched", false)]
    [InlineData("completed", "QidResolved", false)]
    public async Task ResumedBatchRemainsCurrentUntilDurableWorkFinishes(string status, string state, bool leased)
    {
        var batch = AddBatch(status, 1, 1, DateTimeOffset.UtcNow.AddDays(-2));
        var work = AddStandalone(batch, "Books", "Resumed book");
        var newer = AddBatch("completed", 1, 1);
        using var conn = _db.CreateConnection();
        var asset = conn.QuerySingle<Guid>("SELECT ma.id FROM media_assets ma JOIN editions e ON e.id=ma.edition_id WHERE e.work_id=@work", new { work });
        conn.Execute("""
            INSERT INTO identity_jobs (id,entity_id,entity_type,media_type,ingestion_run_id,state,pass,lease_owner,created_at,updated_at)
            VALUES (@id,@asset,'MediaAsset','Books',@batch,@state,'Quick',@owner,@now,@now);
            UPDATE ingestion_log SET status='queued_identity' WHERE media_asset_id=@asset;
            """, new { id = Guid.NewGuid(), asset, batch, state, owner = leased ? "previous-engine" : null, now = DateTimeOffset.UtcNow.AddHours(-2).ToString("O") });
        var batches = new IngestionBatchRepository(_db);
        var progress = new MediaEngine.Providers.Services.BatchProgressService(batches, new RecordingEvents(), Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaEngine.Providers.Services.BatchProgressService>.Instance);
        var presentation = new IngestionPresentationReadService(_db, progress);
        var history = new ActivityBatchReadService(_db);
        var historyQuery = new MediaEngine.Application.ReadModels.ActivityBatchQuery(null, null, null, null, null, null, null, 0, 50, HistoricalOnly: true);

        // The same durable batch is visible before and after startup lease recovery,
        // even when a newer completed run exists.
        for (var restart = 0; restart < 2; restart++)
        {
            if (restart > 0)
            {
                await new IdentityJobRepository(_db).RecoverInterruptedJobsAsync();
            }

            Assert.Equal(batch, Assert.Single(await batches.GetActiveAsync()).Id);
            var snapshot = await presentation.GetSnapshotAsync();
            Assert.True(snapshot.IsRunning);
            Assert.Equal(batch, snapshot.BatchProgress!.BatchId);
            Assert.False(snapshot.BatchProgress.IsComplete);
            Assert.Equal(1, snapshot.CurrentMediaTotal);
            Assert.Equal(work, Assert.Single(snapshot.CurrentMedia).GroupId);
            Assert.Equal(work, Assert.Single((await presentation.GetCurrentMediaAsync(0, 50)).Items).GroupId);
            Assert.NotNull(await presentation.GetMediaGroupAsync(batch, work));
            Assert.Contains("still adding", (await presentation.GetBatchPresentationAsync(batch))!.Summary);
            Assert.Equal(newer, Assert.Single((await history.GetBatchesAsync(historyQuery)).Items).BatchId);
        }

        conn.Execute("UPDATE identity_jobs SET state='Ready',lease_owner=NULL;");
        await progress.EmitProgressAsync(batch, isFinal: true, CancellationToken.None);
        Assert.Empty(await batches.GetActiveAsync());
        Assert.Empty((await presentation.GetCurrentMediaAsync(0, 50)).Items);
        Assert.Equal(2, (await history.GetBatchesAsync(historyQuery)).TotalCount);
        Assert.Equal("completed", (await batches.GetByIdAsync(batch))!.Status);
    }

    [Theory]
    [InlineData("Queued")]
    [InlineData("RetailMatched")]
    [InlineData("QidResolved")]
    [InlineData("UniverseEnriching")]
    public async Task ReconciliationDoesNotFailRecoverableJobsBecauseTheyAreOld(string state)
    {
        var batch = AddBatch("running", 1, 1, DateTimeOffset.UtcNow.AddDays(-2));
        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO identity_jobs (id,entity_id,entity_type,media_type,ingestion_run_id,state,pass,created_at,updated_at)
            VALUES (@id,@asset,'MediaAsset','Books',@batch,@state,'Quick',@now,@now);
            """, new { id = Guid.NewGuid(), asset = Guid.NewGuid(), batch, state, now = DateTimeOffset.UtcNow.AddDays(-2).ToString("O") });
        var batches = new IngestionBatchRepository(_db);
        var service = new MediaEngine.Api.Services.IngestionOperationsStatusService(_db, null!, null!, batches, null!, null!, null!);
        var reconcile = typeof(MediaEngine.Api.Services.IngestionOperationsStatusService).GetMethod("ReconcileCompletedBatchesAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)reconcile.Invoke(service, [await batches.GetRecentAsync(), CancellationToken.None])!;
        var saved = await batches.GetByIdAsync(batch);
        Assert.Equal("running", saved!.Status);
        Assert.Equal(0, saved.FilesFailed);
        Assert.Null(saved.CompletedAt);
    }

    [Theory]
    [InlineData(3, 3)]
    [InlineData(5, 3)]
    public async Task ReconciliationPreservesProcessedFilesAndBoundsThemToTotal(int processed, int expected)
    {
        var batch = AddBatch("running", 3, processed);
        var batches = new IngestionBatchRepository(_db);
        var service = new MediaEngine.Api.Services.IngestionOperationsStatusService(_db, null!, null!, batches, null!, null!, null!);
        var reconcile = typeof(MediaEngine.Api.Services.IngestionOperationsStatusService).GetMethod("ReconcileCompletedBatchesAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)reconcile.Invoke(service, [await batches.GetRecentAsync(), CancellationToken.None])!;
        var saved = await batches.GetByIdAsync(batch);
        Assert.Equal("completed", saved!.Status);
        Assert.Equal(expected, saved.FilesProcessed);
    }

    private sealed class RecordingEvents : MediaEngine.Domain.Contracts.IEventPublisher
    {
        public Task PublishAsync<TPayload>(string eventName, TPayload payload, CancellationToken ct = default) where TPayload : notnull => Task.CompletedTask;
    }

    [Fact]
    public async Task ComicPreviewPrefersOwnedIssueCoverOverNewerParentCover()
    {
        var batch = AddBatch("running", 1, 1);
        var series = AddContainer("Comics", "Example comic");
        AddChildren(batch, series, "Comics", "Issue", 1, "issue_number");
        using var conn = _db.CreateConnection();
        var issue = conn.QuerySingle<Guid>("SELECT id FROM works WHERE parent_work_id = @series", new { series });
        var issueArt = Guid.NewGuid();
        foreach (var (owner, id, timestamp) in new[] { (issue, issueArt, DateTimeOffset.UtcNow.AddDays(-1)), (series, Guid.NewGuid(), DateTimeOffset.UtcNow) })
        {
            conn.Execute("""
                INSERT INTO entity_assets (id,entity_id,entity_type,asset_type,local_image_path,aspect_class,asset_class,storage_location,owner_scope,is_preferred,created_at)
                VALUES (@id,@owner,'Work','CoverArt','C:/test/cover.jpg','Portrait','Artwork','Central','Work',1,@now);
                """, new { id, owner, now = timestamp.ToString("O") });
        }

        var item = Assert.Single((await new IngestionPresentationReadService(_db).GetCurrentMediaAsync(0, 50)).Items);
        Assert.Contains(issueArt.ToString("D"), item.CoverUrl);
        Assert.Null(item.ChildExpected);
    }

    private Guid AddContainer(
        string mediaType,
        string title,
        Guid? parentId = null,
        string? expectedKey = null,
        string? expectedValue = null)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var conn = _db.CreateConnection();
        conn.Execute("INSERT INTO works (id, media_type, work_kind, parent_work_id) VALUES (@id, @mediaType, 'parent', @parentId);", new { id, mediaType, parentId });
        conn.Execute("INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@id, 'title', @title, @now);", new { id, title, now });
        conn.Execute("""
            INSERT INTO entity_assets (id,entity_id,entity_type,asset_type,local_image_path,aspect_class,asset_class,storage_location,owner_scope,is_preferred,created_at)
            VALUES (@artId,@id,'Work','CoverArt','C:/test/cover.jpg','Portrait','Artwork','Central','Work',1,@now);
            """, new { artId = Guid.NewGuid(), id, now });
        if (expectedKey is not null && expectedValue is not null)
        {
            conn.Execute("INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@id, @expectedKey, @expectedValue, @now);", new { id, expectedKey, expectedValue, now });
        }

        return id;
    }

    private void AddChildren(
        Guid batchId,
        Guid parentId,
        string mediaType,
        string titlePrefix,
        int count,
        string? ordinalKey = null,
        int? seasonNumber = null,
        int? partCount = null)
    {
        for (var index = 1; index <= count; index++)
        {
            var workId = Guid.NewGuid();
            var editionId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var logId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow.ToString("O");
            using var conn = _db.CreateConnection();
            conn.Execute("INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal) VALUES (@workId, @mediaType, 'child', @parentId, @index);", new { workId, mediaType, parentId, index });
            conn.Execute("INSERT INTO editions (id, work_id, format_label) VALUES (@editionId, @workId, 'Test');", new { editionId, workId });
            conn.Execute("INSERT INTO media_assets (id, edition_id, content_hash, file_path_root) VALUES (@assetId, @editionId, @hash, @path);", new { assetId, editionId, hash = $"hash-{assetId:N}", path = $"C:/watch/{assetId:N}.media" });
            conn.Execute("INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@workId, 'title', @title, @now);", new { workId, title = $"{titlePrefix} {index:00}", now });
            if (ordinalKey is not null)
            {
                conn.Execute("INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@workId, @ordinalKey, @ordinal, @now);", new { workId, ordinalKey, ordinal = index.ToString(), now });
            }

            if (seasonNumber.HasValue)
            {
                conn.Execute("INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@workId, 'season_number', @season, @now);", new { workId, season = seasonNumber.Value.ToString(), now });
            }

            if (partCount.HasValue)
            {
                conn.Execute("INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@workId, 'audiobook_part_count', @parts, @now);", new { workId, parts = partCount.Value.ToString(), now });
            }

            InsertLog(conn, logId, batchId, assetId, mediaType, $"{titlePrefix} {index:00}", now);
        }
    }

    private Guid AddStandalone(
        Guid batchId,
        string mediaType,
        string title,
        bool presented = false,
        DateTimeOffset? occurredAt = null)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var logId = Guid.NewGuid();
        var now = (occurredAt ?? DateTimeOffset.UtcNow).ToString("O");
        using var conn = _db.CreateConnection();
        conn.Execute("INSERT INTO works (id, media_type, work_kind) VALUES (@workId, @mediaType, 'standalone');", new { workId, mediaType });
        conn.Execute("INSERT INTO editions (id, work_id, format_label) VALUES (@editionId, @workId, 'Test');", new { editionId, workId });
        conn.Execute("INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at) VALUES (@assetId, @editionId, @hash, @path, @presentedAt);", new { assetId, editionId, hash = $"hash-{assetId:N}", path = $"C:/watch/{assetId:N}.media", presentedAt = presented ? now : null });
        conn.Execute("INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@workId, 'title', @title, @now);", new { workId, title, now });
        conn.Execute("""
            INSERT INTO entity_assets (id,entity_id,entity_type,asset_type,local_image_path,aspect_class,asset_class,storage_location,owner_scope,is_preferred,created_at)
            VALUES (@artId,@workId,'Work','CoverArt','C:/test/cover.jpg','Portrait','Artwork','Central','Work',1,@now);
            """, new { artId = Guid.NewGuid(), workId, now });
        InsertLog(conn, logId, batchId, assetId, mediaType, title, now);
        return workId;
    }

    private static void InsertLog(System.Data.IDbConnection conn, Guid logId, Guid batchId, Guid assetId, string mediaType, string title, string now)
    {
        conn.Execute("""
            INSERT INTO ingestion_log (
                id, file_path, media_asset_id, content_hash, status, media_type,
                detected_title, normalized_title, ingestion_run_id, created_at, updated_at)
            VALUES (
                @logId, @path, @assetId, @hash, 'registered', @mediaType,
                @title, @title, @batchId, @now, @now);
            """, new { logId, path = $"C:/watch/{assetId:N}.media", assetId, hash = $"hash-{assetId:N}", mediaType, title, batchId, now });
    }

    private sealed class ChildIdentity
    {
        public Guid WorkId { get; init; }
        public Guid EditionId { get; init; }
    }
}
