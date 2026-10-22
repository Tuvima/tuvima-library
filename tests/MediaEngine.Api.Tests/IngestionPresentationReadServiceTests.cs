using Dapper;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Ingestion;
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
    public async Task AvailabilityReadServicesHonorCancellationBeforeOpeningDatabase()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => IngestionAvailability.IsUpdatingAsync(null!, Guid.NewGuid(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MediaEngine.Api.Services.Details.ContributorReadiness(null!).ApplyAsync(new MediaEngine.Contracts.Details.DetailPageViewModel(), cancellation.Token));
    }

    [Fact]
    public async Task ContributorReadinessUpdatesEveryCreditAndClearsAfterEnrichment()
    {
        var work = AddStandalone("Books", "Readiness Fixture");
        var person = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        var asset = conn.QuerySingle<Guid>("SELECT a.id FROM media_assets a JOIN editions e ON e.id=a.edition_id WHERE e.work_id=@work;", new { work });
        conn.Execute("INSERT INTO persons (id,name,created_at) VALUES (@person,'Fixture Author',@now);", new { person, now = DateTimeOffset.UtcNow.ToString("O") });
        conn.Execute("INSERT INTO person_media_links (media_asset_id,person_id,role) VALUES (@asset,@person,'Author');", new { asset, person });
        conn.Execute("""
            INSERT INTO media_operations (id,operation_type,operation_kind,entity_id,status,position_key,created_at,updated_at,idempotency_key)
            VALUES (@id,'enrichment.people','enrichment',@asset,'queued',1,@now,@now,@key);
            """, new { id = Guid.NewGuid(), asset, now = DateTimeOffset.UtcNow.ToString("O"), key = Guid.NewGuid().ToString() });
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
    public async Task RecentAdditions_GroupFilesByStructuralIdentityAndShowAddedCountsWithoutCatalogueTotals()
    {
        var completeAlbum = AddContainer("Music", "Album One", expectedKey: "track_count", expectedValue: "17");
        AddChildren(completeAlbum, "Music", "Song", 17, "track_number");

        var partialAlbum = AddContainer("Music", "Album Two", expectedKey: "track_count", expectedValue: "17");
        AddChildren(partialAlbum, "Music", "Part", 14, "track_number");

        var unknownAlbum = AddContainer("Music", "Unknown Total");
        AddChildren(unknownAlbum, "Music", "Track", 3, "track_number");

        var show = AddContainer("TV", "The Example Show");
        var season = AddContainer("TV", "Season 2", show, "episode_count", "10");
        AddChildren(season, "TV", "Episode", 10, "episode_number", seasonNumber: 2);

        var audiobook = AddContainer("Audiobooks", "A Long Listen", expectedKey: "audiobook_part_count", expectedValue: "4");
        AddChildren(audiobook, "Audiobooks", "Part", 4, partCount: 4);

        var service = new IngestionPresentationReadService(_db);
        var page = await service.GetRecentAdditionsAsync(null, null, null, null, 0, 50);
        var snapshot = await service.GetSnapshotAsync();

        Assert.Equal(5, page.TotalCount);
        Assert.Equal(0, snapshot.CurrentMediaTotal); // Settled groups leave the live tile rail.
        var one = Assert.Single(page.Items, item => item.GroupId == completeAlbum);
        Assert.Equal(17, one.ChildCompleted);
        Assert.Equal(17, one.FileCount);
        Assert.Null(one.ChildExpected);
        Assert.Equal("tracks", one.ChildUnit);
        // A Ready album with no relationship work left is settled with nothing found, not "queued".
        Assert.Equal("complete", one.Relationships.State);
        Assert.Equal("No relationships found", one.Relationships.Label);

        // Neither the album's catalogue total nor a partly added album is turned into a denominator.
        var partial = Assert.Single(page.Items, item => item.GroupId == partialAlbum);
        Assert.Equal(14, partial.ChildCompleted);
        Assert.Null(partial.ChildExpected);
        Assert.Equal("tracks", partial.ChildUnit);

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
    public async Task CurrentMedia_GroupsFilesStillBeingAddedByStructuralIdentity()
    {
        var album = AddContainer("Music", "Album In Flight", expectedKey: "track_count", expectedValue: "17");
        AddChildren(album, "Music", "Song", 3, "track_number", ready: false);

        var show = AddContainer("TV", "Show In Flight");
        var season = AddContainer("TV", "Season 1", show);
        AddChildren(season, "TV", "Episode", 4, "episode_number", seasonNumber: 1, ready: false);

        var page = await new IngestionPresentationReadService(_db).GetCurrentMediaAsync(0, 50);

        Assert.Equal(2, page.TotalCount);
        var music = Assert.Single(page.Items, item => item.GroupId == album);
        Assert.Equal(3, music.FileCount);
        Assert.Equal(0, music.ChildCompleted);
        Assert.Null(music.ChildExpected);
        var tv = Assert.Single(page.Items, item => item.GroupId == show);
        Assert.Equal(4, tv.FileCount);
        Assert.Equal("episodes", tv.ChildUnit);
    }

    [Fact]
    public async Task CurrentMedia_DoesNotMergeDistinctWorksWithTheSameTitle()
    {
        AddStandalone("Movies", "The Same Title", ready: false);
        AddStandalone("Movies", "The Same Title", ready: false);

        var service = new IngestionPresentationReadService(_db);
        var page = await service.GetCurrentMediaAsync(0, 50);

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(2, page.Items.Select(item => item.GroupId).Distinct().Count());
        Assert.All(page.Items, item => Assert.Equal("The Same Title", item.Title));
    }

    [Fact]
    public async Task CurrentMedia_TwoFilesOfTheSameShowIngestedInSeparateIntakesFormOneGroup()
    {
        var show = AddContainer("TV", "The Slow Show");
        var season = AddContainer("TV", "Season 1", show);
        AddChildren(season, "TV", "Episode", 1, "episode_number", seasonNumber: 1, ready: false);
        // The second episode arrives in a later, separate intake.
        AddChildren(season, "TV", "Later Episode", 1, "episode_number", seasonNumber: 1, ready: false, loggedAt: DateTimeOffset.UtcNow.AddDays(-2));

        var current = await new IngestionPresentationReadService(_db).GetCurrentMediaAsync(0, 50);

        var group = Assert.Single(current.Items);
        Assert.Equal(show, group.GroupId);
        Assert.Equal(2, group.FileCount);
        Assert.Equal(1, current.TotalCount);
    }

    [Fact]
    public async Task CurrentMedia_AFileWithAnOpenIntakeRowStaysInFlightEvenWhenItsWorkIsReady()
    {
        var work = AddStandalone("Movies", "Still Being Placed");
        AddIntakeOperation(work, "running");

        var page = await new IngestionPresentationReadService(_db).GetCurrentMediaAsync(0, 50);

        Assert.Equal(work, Assert.Single(page.Items).GroupId);
    }

    [Fact]
    public async Task CurrentMedia_AGroupWhoseFileFailedTerminallyIsNotInFlight()
    {
        var failed = AddStandalone("Movies", "Gave Up", ready: false);
        var waiting = AddStandalone("Movies", "Still Waiting", ready: false);
        AddIntakeOperation(failed, "dead_lettered");

        var page = await new IngestionPresentationReadService(_db).GetCurrentMediaAsync(0, 50);

        Assert.Equal(waiting, Assert.Single(page.Items).GroupId);
    }

    [Fact]
    public async Task CurrentMedia_CountsDistinctTracksInsteadOfDuplicateAssetsOrTrackNumbers()
    {
        var albumId = AddContainer("Music", "A Night at the Opera", expectedKey: "track_count", expectedValue: "12");
        AddChildren(albumId, "Music", "Song", 5, "track_number");

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
            InsertLog(conn, Guid.NewGuid(), duplicateAssetId, "Music", "Duplicate encoding", now);
        }

        var service = new IngestionPresentationReadService(_db);
        var item = Assert.Single((await service.GetRecentAdditionsAsync(null, null, null, null, 0, 50)).Items);

        Assert.Equal(5, item.ChildCompleted);
        Assert.Equal(6, item.FileCount);
        Assert.Null(item.ChildExpected);
        Assert.Equal("tracks", item.ChildUnit);
    }

    [Fact]
    public async Task Children_FormatDecimalMinuteTrackDurationsAndFitSixtyRowsInOneResponse()
    {
        var albumId = AddContainer("Music", "Long Album");
        AddChildren(albumId, "Music", "Song", 60, "track_number");
        using (var conn = _db.CreateConnection())
        {
            var firstTrackId = conn.QuerySingle<Guid>("SELECT id FROM works WHERE parent_work_id = @albumId ORDER BY ordinal LIMIT 1;", new { albumId });
            conn.Execute("INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@firstTrackId, 'duration', '5.905', @now);",
                new { firstTrackId, now = DateTimeOffset.UtcNow.ToString("O") });
        }

        var children = await new IngestionPresentationReadService(_db).GetChildrenAsync(albumId, 0, 250);

        Assert.Equal(60, children.Items.Count);
        Assert.False(children.HasMore);
        Assert.Equal("5:54", children.Items[0].DurationLabel);
    }

    [Fact]
    public async Task CurrentMedia_PagesGroupsBeforeProjectingDetails()
    {
        for (var index = 1; index <= 75; index++)
        {
            AddStandalone("Movies", $"Current Movie {index:00}", ready: false);
        }

        var service = new IngestionPresentationReadService(_db);
        var page = await service.GetCurrentMediaAsync(50, 50);

        Assert.Equal(75, page.TotalCount);
        Assert.Equal(25, page.Items.Count);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task RecentAdditions_IncludesEverythingEverAddedAndPagesBeyondFiftyGroups()
    {
        for (var index = 1; index <= 30; index++)
        {
            AddStandalone("Books", $"Earlier Book {index:00}", presented: true, occurredAt: DateTimeOffset.UtcNow.AddDays(-2));
        }

        for (var index = 1; index <= 55; index++)
        {
            AddStandalone("Movies", $"Recent Movie {index:00}", presented: true, occurredAt: DateTimeOffset.UtcNow.AddDays(-1));
        }

        var service = new IngestionPresentationReadService(_db);
        var firstPage = await service.GetRecentAdditionsAsync(null, null, null, null, 0, 50);
        var secondPage = await service.GetRecentAdditionsAsync(null, null, null, null, 50, 50);

        Assert.Equal(85, firstPage.TotalCount);
        Assert.Equal(50, firstPage.Items.Count);
        Assert.True(firstPage.HasMore);
        Assert.Equal(35, secondPage.Items.Count);
        Assert.False(secondPage.HasMore);
        Assert.Contains(secondPage.Items, item => item.Title.StartsWith("Earlier Book", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecentAdditions_HidesArtworkBearingGroupsUntilTheyHaveARealTitle()
    {
        var workId = AddStandalone("Books", "Temporary detection", presented: true);
        using (var conn = _db.CreateConnection())
        {
            conn.Execute("DELETE FROM canonical_values WHERE entity_id = @workId AND key = 'title';", new { workId });
            conn.Execute("UPDATE ingestion_log SET detected_title = NULL, normalized_title = NULL;");
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

        var page = await new IngestionPresentationReadService(_db).GetRecentAdditionsAsync(null, null, null, null, 0, 50);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task RecentAdditions_AScanOfTheSameFileLaterIsNotANewAddition()
    {
        var firstReady = DateTimeOffset.UtcNow.AddDays(-3);
        var workId = AddStandalone("Movies", "Added Once", presented: true, occurredAt: firstReady);
        Guid assetId;
        using (var conn = _db.CreateConnection())
        {
            assetId = conn.ExecuteScalar<Guid>("""
                SELECT ma.id
                FROM media_assets ma
                JOIN editions e ON e.id = ma.edition_id
                WHERE e.work_id = @workId;
                """, new { workId });
            InsertLog(conn, Guid.NewGuid(), assetId, "Movies", "Added Once", DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"));
        }

        var service = new IngestionPresentationReadService(_db);
        var page = await service.GetRecentAdditionsAsync(null, null, null, null, 0, 50);
        var days = (await service.GetSnapshotAsync()).RecentDays;

        var item = Assert.Single(page.Items);
        Assert.Equal(workId, item.GroupId);
        Assert.Equal(firstReady.ToUniversalTime().Date, item.AddedAt.ToUniversalTime().Date);
        var day = Assert.Single(days);
        Assert.Equal(DateOnly.FromDateTime(firstReady.ToLocalTime().DateTime), day.Date);
    }

    [Fact]
    public async Task RecentAdditions_IncludesSettledItemsWhileOthersAreStillInFlight()
    {
        // A long first-run import keeps working for hours; its finished items must show up while it runs.
        var addedWork = AddStandalone("Movies", "Already Added", presented: true);
        var inFlightWork = AddStandalone("Movies", "Still Hydrating", presented: false, ready: false);
        AddIdentityJob(inFlightWork, "Hydrating", "Movies");

        var service = new IngestionPresentationReadService(_db);
        var whileRunning = await service.GetRecentAdditionsAsync(null, null, null, null, 0, 50);

        var added = Assert.Single(whileRunning.Items);
        Assert.Equal(addedWork, added.GroupId);
        Assert.Equal("Already Added", added.Title);

        // Once the in-flight item settles it joins the list.
        using (var conn = _db.CreateConnection())
        {
            conn.Execute("UPDATE identity_jobs SET state = 'Ready';");
        }

        MarkReady(inFlightWork);

        var afterwards = await service.GetRecentAdditionsAsync(null, null, null, null, 0, 50);
        Assert.Contains(afterwards.Items, item => item.GroupId == addedWork);
        Assert.Contains(afterwards.Items, item => item.GroupId == inFlightWork);
    }

    [Fact]
    public async Task RecentAdditions_AnItemThatBecameReadyIsListedUnderItsFirstReadyDay()
    {
        var firstReady = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        var work = AddStandalone("Movies", "Added Without A Run Record", occurredAt: firstReady);
        var service = new IngestionPresentationReadService(_db);
        var snapshot = await service.GetSnapshotAsync(currentLimit: 4, recentDayLimit: 7, recentItemsPerDay: 4);
        var page = await service.GetRecentAdditionsAsync(null, null, firstReady.AddHours(-1), firstReady.AddHours(1), 0, 50);

        var day = Assert.Single(snapshot.RecentDays);
        Assert.Equal(DateOnly.FromDateTime(firstReady.ToLocalTime().DateTime), day.Date);
        Assert.Equal(work, Assert.Single(day.Items).GroupId);
        Assert.Equal(work, Assert.Single(page.Items).GroupId);
    }

    [Fact]
    public async Task RecentAdditions_AShowThatGotEpisodesOnTwoDaysIsCountedOnBothDays()
    {
        var show = AddContainer("TV", "Weekly Show");
        var season = AddContainer("TV", "Season 1", show);
        var yesterday = DateTimeOffset.UtcNow.AddDays(-1);
        AddChildren(season, "TV", "Monday Episode", 2, "episode_number", seasonNumber: 1, readyAt: yesterday);
        AddChildren(season, "TV", "Tuesday Episode", 1, "episode_number", seasonNumber: 1, readyAt: DateTimeOffset.UtcNow);

        var service = new IngestionPresentationReadService(_db);
        var days = (await service.GetSnapshotAsync(recentDayLimit: 7)).RecentDays;
        var page = await service.GetRecentAdditionsAsync(null, null, null, null, 0, 50);

        Assert.Equal(2, days.Count);
        Assert.Equal([1, 2], days.OrderByDescending(day => day.Date).Select(day => Assert.Single(day.TypeCounts).Count).ToArray());
        Assert.Equal(show, Assert.Single(page.Items).GroupId);
    }

    [Fact]
    public async Task RecentAdditions_AppliesSearchAndLaneBeforeBuildingThePage()
    {
        for (var index = 1; index <= 60; index++)
        {
            AddStandalone("Movies", $"Movie {index:00}", presented: true);
        }

        AddStandalone("Books", "The Needle Book", presented: true);

        var service = new IngestionPresentationReadService(_db);
        var page = await service.GetRecentAdditionsAsync("needle", "Read", null, null, 0, 50);

        var item = Assert.Single(page.Items);
        Assert.Equal("The Needle Book", item.Title);
        Assert.Equal("Books", item.MediaType);
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task RecentAdditions_StatusAddedExcludesNeedsReviewAndFailedGroups_AndKeepsTheTotalHonest()
    {
        var added = AddStandalone("Movies", "Calmly Added", presented: true);
        var alsoAdded = AddStandalone("Books", "Quietly Added", presented: true);
        var review = AddStandalone("Movies", "Needs A Look", presented: true);
        var failedOperation = AddStandalone("Movies", "Enrichment Failed", presented: true);
        var failedFile = AddStandalone("Music", "File Failed", presented: true);
        var now = DateTimeOffset.UtcNow.ToString("O");
        using (var conn = _db.CreateConnection())
        {
            conn.Execute("""
                INSERT INTO review_queue (id, entity_id, entity_type, trigger, status, review_ready_at, created_at)
                VALUES (@id, @work, 'Work', 'LowConfidence', 'Pending', @now, @now);
                """, new { id = Guid.NewGuid(), work = review, now });
            conn.Execute("""
                INSERT INTO media_operations (id,operation_type,operation_kind,entity_id,status,position_key,created_at,updated_at,idempotency_key)
                VALUES (@id,'enrichment.people','enrichment',@work,'failed_terminal',1,@now,@now,@key);
                """, new { id = Guid.NewGuid(), work = failedOperation, now, key = Guid.NewGuid().ToString() });
            var fileAsset = conn.QuerySingle<Guid>(
                "SELECT ma.id FROM media_assets ma JOIN editions e ON e.id = ma.edition_id WHERE e.work_id = @work;",
                new { work = failedFile });
            conn.Execute("""
                INSERT INTO media_operations (id,operation_type,operation_kind,entity_id,status,position_key,created_at,updated_at,idempotency_key)
                VALUES (@id,'ingestion.file','ingestion',@asset,'dead_lettered',1,@now,@now,@key);
                """, new { id = Guid.NewGuid(), asset = fileAsset, now, key = Guid.NewGuid().ToString() });
        }

        var service = new IngestionPresentationReadService(_db);
        var everything = await service.GetRecentAdditionsAsync(null, null, null, null, 0, 50);
        Assert.Equal(5, everything.TotalCount);
        Assert.Equal("Needs review", everything.Items.Single(item => item.GroupId == review).StatusLabel);
        Assert.Equal("Failed", everything.Items.Single(item => item.GroupId == failedOperation).StatusLabel);
        Assert.Equal("Failed", everything.Items.Single(item => item.GroupId == failedFile).StatusLabel);

        var onlyAdded = await service.GetRecentAdditionsAsync(null, null, null, null, 0, 50, status: "added");
        Assert.Equal(new[] { added, alsoAdded }.Order(), onlyAdded.Items.Select(item => item.GroupId).Order());
        Assert.All(onlyAdded.Items, item => Assert.NotEqual("Needs review", item.StatusLabel));
        Assert.All(onlyAdded.Items, item => Assert.NotEqual("Failed", item.StatusLabel));
        Assert.Equal(2, onlyAdded.TotalCount);
        Assert.False(onlyAdded.HasMore);

        var firstPage = await service.GetRecentAdditionsAsync(null, null, null, null, 0, 1, status: "added");
        var secondPage = await service.GetRecentAdditionsAsync(null, null, null, null, 1, 1, status: "added");
        Assert.Single(firstPage.Items);
        Assert.True(firstPage.HasMore);
        Assert.Equal(2, firstPage.TotalCount);
        Assert.Single(secondPage.Items);
        Assert.False(secondPage.HasMore);
        Assert.NotEqual(firstPage.Items[0].GroupId, secondPage.Items[0].GroupId);

        var withLane = await service.GetRecentAdditionsAsync(null, "Read", null, null, 0, 50, status: "added");
        Assert.Equal(alsoAdded, Assert.Single(withLane.Items).GroupId);
        Assert.Equal(1, withLane.TotalCount);

        var unknown = await service.GetRecentAdditionsAsync(null, null, null, null, 0, 50, status: "whatever");
        Assert.Equal(5, unknown.TotalCount);
    }

    [Fact]
    public async Task RecentAdditions_ShowsAFileHeldForAMissingMediaToolAsWaitingNotFailed()
    {
        var plain = AddStandalone("Movies", "Added Normally", presented: true);
        var waiting = AddStandalone("Movies", "Waiting For FFmpeg", presented: true);
        var waitingButReview = AddStandalone("Movies", "Waiting And Unreadable", presented: true);
        HoldInspection(waiting);
        HoldInspection(waitingButReview);
        using (var conn = _db.CreateConnection())
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            conn.Execute("""
                INSERT INTO review_queue (id, entity_id, entity_type, trigger, status, review_ready_at, created_at)
                VALUES (@id, @work, 'Work', 'MediaInspectionFailed', 'Pending', @now, @now);
                """, new { id = Guid.NewGuid(), work = waitingButReview, now });
        }

        var page = await new IngestionPresentationReadService(_db).GetRecentAdditionsAsync(null, null, null, null, 0, 50);

        var held = page.Items.Single(item => item.GroupId == waiting);
        Assert.Equal("waiting_for_tools", held.Availability);
        Assert.Equal("Waiting for media tools", held.StatusLabel);
        Assert.NotEqual("waiting_for_tools", page.Items.Single(item => item.GroupId == plain).Availability);
        Assert.Equal("review", page.Items.Single(item => item.GroupId == waitingButReview).Availability);
    }

    [Fact]
    public async Task LiveSnapshot_AnIntakeRowThatFailedAfterRegistration_IsNeitherReadyNorFinishing()
    {
        var good = AddStandalone("Books", "Registered And Fine");
        var bad = AddStandalone("Books", "Failed After Registering");
        foreach (var work in new[] { good, bad })
        {
            AddReadyIdentityJob(work);
        }

        AddIntakeOperation(good, "succeeded");
        AddIntakeOperation(bad, "dead_lettered");

        // The active period began before either work became Ready, so the live numbers cover both groups.
        var inPeriod = await new IngestionPresentationReadService(_db, new FixedQueueProgress(
            new IngestionQueueProgressDto { State = "working", StartedAt = DateTimeOffset.UtcNow.AddHours(-1) })).GetSnapshotAsync();
        var outsidePeriod = await new IngestionPresentationReadService(_db).GetSnapshotAsync();

        Assert.Equal(2, inPeriod.LibraryGroups);
        Assert.Equal(1, inPeriod.ReadyGroups);
        Assert.Equal(0, inPeriod.FinishingGroups);
        Assert.Equal(0, inPeriod.ReviewGroups);
        Assert.Equal(1, outsidePeriod.LibraryGroups);
        Assert.Equal(0, outsidePeriod.ReadyGroups);
        Assert.Equal(0, outsidePeriod.FinishingGroups);
        Assert.Empty(outsidePeriod.CurrentMedia);
    }

    [Fact]
    public async Task LiveSnapshot_TakesFilesAndTimesFromTheQueueProgress()
    {
        var started = DateTimeOffset.UtcNow.AddMinutes(-10);
        var lastActivity = DateTimeOffset.UtcNow.AddSeconds(-5);
        var snapshot = await new IngestionPresentationReadService(_db, new FixedQueueProgress(new IngestionQueueProgressDto
        {
            State = "working",
            FilesFound = 40,
            FilesDone = 12,
            StartedAt = started,
            LastActivityAt = lastActivity,
        })).GetSnapshotAsync();

        Assert.Equal(40, snapshot.FilesDiscovered);
        Assert.Equal(12, snapshot.FilesProcessed);
        Assert.Equal(started, snapshot.StartedAt);
        Assert.Equal(lastActivity, snapshot.LastActivityAt);
    }

    [Fact]
    public async Task RecentAdditions_AnIntakeRowThatFailedAfterRegistration_ShowsFailedAndLeavesTheAddedFilter()
    {
        var good = AddStandalone("Movies", "Registered And Fine", presented: true);
        var bad = AddStandalone("Movies", "Failed After Registering", presented: true);
        AddIntakeOperation(good, "succeeded");
        AddIntakeOperation(bad, "failed_terminal");
        var service = new IngestionPresentationReadService(_db);

        var everything = await service.GetRecentAdditionsAsync(null, null, null, null, 0, 50);
        var onlyAdded = await service.GetRecentAdditionsAsync(null, null, null, null, 0, 50, status: "added");

        Assert.Equal("Failed", everything.Items.Single(item => item.GroupId == bad).StatusLabel);
        Assert.Equal(good, Assert.Single(onlyAdded.Items).GroupId);
        Assert.Equal(1, onlyAdded.TotalCount);
    }

    [Fact]
    public async Task RecentAdditions_StaysOneRowPerFileWhenSeveralIntakeRowsPointAtTheSameAsset()
    {
        var work = AddStandalone("Movies", "Seen Twice", presented: true);
        AddIntakeOperation(work, "succeeded", updatedAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        AddIntakeOperation(work, "succeeded", resultSummary: "duplicate", updatedAt: DateTimeOffset.UtcNow);

        var page = await new IngestionPresentationReadService(_db).GetRecentAdditionsAsync(null, null, null, null, 0, 50);
        var recent = (await new IngestionPresentationReadService(_db).GetSnapshotAsync()).RecentDays;

        Assert.Equal(work, Assert.Single(page.Items).GroupId);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(1, recent.Sum(day => day.Items.Count));
    }

    [Fact]
    public async Task OperationsArtwork_UsesSmallPreviewAndMediumGridRenditions()
    {
        var workId = AddStandalone("Books", "Sized Artwork", presented: true);
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
        var snapshot = await service.GetSnapshotAsync();
        var page = await service.GetRecentAdditionsAsync(null, null, null, null, 0, 50);
        var drawer = await service.GetMediaGroupAsync(workId);

        Assert.EndsWith($"/stream/artwork/{artworkId:D}?size=s", Assert.Single(Assert.Single(snapshot.RecentDays).Items).CoverUrl);
        Assert.EndsWith($"/stream/artwork/{artworkId:D}?size=m", Assert.Single(page.Items).CoverUrl);
        Assert.EndsWith($"/stream/artwork/{artworkId:D}?size=m", drawer!.CoverUrl);
    }

    [Fact]
    public async Task ActivitySummary_CountsGroupedAdditionsWithoutBuildingMediaCards()
    {
        var album = AddContainer("Music", "One Album", expectedKey: "track_count", expectedValue: "17");
        AddChildren(album, "Music", "Track", 17, "track_number");
        using (var conn = _db.CreateConnection())
        {
            conn.Execute("UPDATE media_assets SET presented_at = @now;", new { now = DateTimeOffset.UtcNow.ToString("O") });
        }

        var service = new IngestionPresentationReadService(_db);
        var summary = await service.GetActivitySummaryAsync();

        Assert.Equal(1, summary.ItemsAddedToday);
        Assert.NotNull(summary.LastActivityAt);
    }

    [Fact]
    public async Task Snapshot_BoundsRecentDayCardsWhileKeepingDayTotals()
    {
        var firstDay = DateTimeOffset.UtcNow.AddDays(-2);
        var secondDay = DateTimeOffset.UtcNow.AddDays(-1);
        for (var index = 1; index <= 4; index++)
        {
            AddStandalone("Books", $"Earlier Book {index}", presented: true, occurredAt: firstDay);
        }

        for (var index = 1; index <= 5; index++)
        {
            AddStandalone("Movies", $"Recent Movie {index}", presented: true, occurredAt: secondDay);
        }

        var service = new IngestionPresentationReadService(_db);
        var snapshot = await service.GetSnapshotAsync(currentLimit: 8, recentDayLimit: 2, recentItemsPerDay: 2);

        Assert.Equal(2, snapshot.RecentDays.Count);
        Assert.All(snapshot.RecentDays, day => Assert.Equal(2, day.Items.Count));
        Assert.Equal([5, 4], snapshot.RecentDays.Select(day => day.TotalCount).ToArray());
    }

    [Fact]
    public async Task Snapshot_CountsEachDayByTypeOverEveryItemNotTheCappedPreview()
    {
        for (var index = 1; index <= 5; index++)
        {
            AddStandalone("Movies", $"Counted Movie {index}", presented: true);
        }

        for (var index = 1; index <= 3; index++)
        {
            AddStandalone("Books", $"Counted Book {index}", presented: true);
        }

        var show = AddContainer("TV", "Counted Show");
        var season = AddContainer("TV", "Season 1", show);
        AddChildren(season, "TV", "Episode", 3, "episode_number", seasonNumber: 1);
        using (var conn = _db.CreateConnection())
        {
            conn.Execute("UPDATE media_assets SET presented_at = @now WHERE presented_at IS NULL;", new { now = DateTimeOffset.UtcNow.ToString("O") });
        }

        var snapshot = await new IngestionPresentationReadService(_db).GetSnapshotAsync(currentLimit: 8, recentDayLimit: 1, recentItemsPerDay: 2);

        var today = Assert.Single(snapshot.RecentDays);
        Assert.Equal(2, today.Items.Count);
        Assert.Equal(
            [("Movies", 5), ("Books", 3), ("TV", 3)],
            today.TypeCounts.Select(type => (type.MediaType, type.Count)).ToArray());
    }

    [Fact]
    public async Task Snapshot_ShowsAtLeastTenInFlightRowsWhenThatManyAreWorkedOn()
    {
        for (var index = 1; index <= 15; index++)
        {
            AddStandalone("Movies", $"In Flight {index}", ready: false);
        }

        using (var conn = _db.CreateConnection())
        {
            conn.Execute("""
                INSERT INTO identity_jobs (id,entity_id,entity_type,media_type,state,pass,created_at,updated_at)
                SELECT randomblob(16), id, 'MediaAsset', 'Movies', 'Hydrating', 'Quick', @now, @now FROM media_assets;
                UPDATE ingestion_log SET status = 'queued_identity';
                """, new { now = DateTimeOffset.UtcNow.ToString("O") });
        }

        var snapshot = await new IngestionPresentationReadService(_db).GetSnapshotAsync();

        Assert.Equal(15, snapshot.CurrentMediaTotal);
        Assert.True(snapshot.CurrentMedia.Count >= 10);
        Assert.True(snapshot.IsRunning);
    }

    [Fact]
    public async Task UnmatchedWorkShowsAsUnmatchedAndNotACompletedAddition_UntilItBecomesReady()
    {
        var work = AddStandalone("Audiobooks", "A forty-file book", presented: true, ready: false);
        AddIdentityJob(work, "RetailNoMatch", "Audiobooks");
        AddUnmatched(work);
        var presentation = new IngestionPresentationReadService(_db);

        // A no-match file is Unmatched: a finished state of its own, labelled so, not a review and not stuck "adding".
        var item = Assert.Single((await presentation.GetCurrentMediaAsync(0, 50)).Items);
        Assert.Equal("unmatched", item.Availability);
        Assert.Equal("Unmatched", item.StatusLabel);
        Assert.Empty(item.WaitingFor);
        Assert.Empty((await presentation.GetSnapshotAsync()).RecentDays);

        using (var conn = _db.CreateConnection())
        {
            conn.Execute("UPDATE identity_jobs SET state='ReadyWithoutUniverse';");
            conn.Execute("DELETE FROM unmatched_assets;");
        }

        MarkReady(work);
        Assert.Single((await presentation.GetSnapshotAsync()).RecentDays);
        Assert.Empty((await presentation.GetCurrentMediaAsync(0, 50)).Items);
    }

    [Fact]
    public async Task NotReadyRow_QueuedForMatching_WaitsForMatchingOnly()
    {
        var work = AddStandalone("Movies", "Queued Movie", ready: false);
        AddIdentityJob(work, "Queued", "Movies");
        SettleArtwork(work);

        var item = await CurrentItemAsync(work);

        Assert.Equal("adding", item.Availability);
        Assert.Equal(["matching"], item.WaitingFor);
    }

    [Fact]
    public async Task NotReadyRow_MatchedEpisodeWithNoShow_WaitsForPlacing()
    {
        var work = AddStandalone("TV", "Orphan Episode", ready: false);
        AddIdentityJob(work, "Ready", "TV");
        SettleArtwork(work);

        var item = await CurrentItemAsync(work);

        Assert.Equal("adding", item.Availability);
        Assert.Equal(["placing"], item.WaitingFor);
    }

    [Fact]
    public async Task NotReadyRow_MatchedAndPlacedMovieWithoutArtwork_WaitsForArtwork()
    {
        var work = AddStandalone("Movies", "Plain Movie", ready: false);
        AddIdentityJob(work, "Ready", "Movies");

        var item = await CurrentItemAsync(work);

        Assert.Equal("adding", item.Availability);
        Assert.Equal(["artwork"], item.WaitingFor);
    }

    [Fact]
    public async Task NotReadyRow_MissingEverything_ListsReasonsInTheFixedOrder()
    {
        var work = AddStandalone("TV", "Brand New Episode", ready: false);
        AddIdentityJob(work, "Queued", "TV");

        var item = await CurrentItemAsync(work);

        Assert.Equal(["matching", "placing", "artwork"], item.WaitingFor);
    }

    [Fact]
    public async Task NotReadyRow_InALibraryThatSkipsIdentity_DoesNotWaitForMatching()
    {
        var work = AddStandalone("Movies", "Local Movie", ready: false);
        string library;
        using (var conn = _db.CreateConnection())
        {
            library = Guid.NewGuid().ToString("D");
            conn.Execute(
                "UPDATE media_assets SET library_id = @library WHERE edition_id IN (SELECT id FROM editions WHERE work_id = @work);",
                new { library, work });
        }

        var item = await CurrentItemAsync(work, new FixedBypassLibraries(library));

        Assert.Equal(["artwork"], item.WaitingFor);
    }

    [Fact]
    public async Task ReadyRow_WithTheStoredMark_IsReadyAndWaitsForNothing()
    {
        var work = AddStandalone("Movies", "Marked Movie");

        var item = await AddedItemAsync(work);

        Assert.Equal("ready", item.Availability);
        Assert.Empty(item.WaitingFor);
    }

    [Fact]
    public async Task Row_WhoseIdentityJobSaysReadyButHasNoStoredMark_IsNotReady()
    {
        var work = AddStandalone("Movies", "Job Says Ready", ready: false);
        AddIdentityJob(work, "Ready", "Movies");

        var item = await CurrentItemAsync(work);

        Assert.NotEqual("ready", item.Availability);
        Assert.Equal(0, item.ChildCompleted);
    }

    [Fact]
    public async Task ReviewRow_SendsNoWaitingReasons()
    {
        var work = AddStandalone("Movies", "Needs A Look", ready: false);
        AddIdentityJob(work, "RetailMatchedNeedsReview", "Movies");

        var item = await CurrentItemAsync(work);

        Assert.Equal("review", item.Availability);
        Assert.Empty(item.WaitingFor);
    }

    [Fact]
    public async Task AddedDay_FollowsTheWorksFirstReadyTime_NotTheIdentityJob()
    {
        var firstReady = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        var work = AddStandalone("Movies", "Added Long Ago", occurredAt: firstReady);
        AddIdentityJob(work, "Ready", "Movies");

        var snapshot = await new IngestionPresentationReadService(_db).GetSnapshotAsync(currentLimit: 4, recentDayLimit: 7, recentItemsPerDay: 4);

        var day = Assert.Single(snapshot.RecentDays);
        Assert.Equal(DateOnly.FromDateTime(firstReady.ToLocalTime().DateTime), day.Date);
        Assert.Equal(work, Assert.Single(day.Items).GroupId);
    }

    [Fact]
    public async Task Facets_ThatDoNotApplyToTheType_AreNotApplicable_AndThatApplyButHaveNotStarted_ArePending()
    {
        var book = AddStandalone("Books", "A Book");
        var movie = AddStandalone("Movies", "A Movie");
        MarkNotReady(movie);

        var bookItem = await AddedItemAsync(book);
        var movieItem = await CurrentItemAsync(movie);

        Assert.Equal("notApplicable", bookItem.TextTracks.State);
        Assert.Equal("pending", movieItem.TextTracks.State);
        Assert.Equal("Subtitles queued", movieItem.TextTracks.Label);
    }

    [Fact]
    public async Task Facets_ThatApplyButHaveNothingToDo_OnAReadyRow_AreSettledWithNothingFound_NotQueued()
    {
        var movie = AddStandalone("Movies", "A Ready Movie");
        MarkReady(movie);

        var item = await AddedItemAsync(movie);

        Assert.Equal("ready", item.Availability);
        Assert.Equal("complete", item.TextTracks.State);
        Assert.Equal("No subtitles found", item.TextTracks.Label);
        Assert.Equal("No relationships found", item.Relationships.Label);
    }

    [Fact]
    public async Task ArtworkSearch_ThatFinishedWithoutACover_IsASettledOutcomeNotNotApplicable()
    {
        var work = AddStandalone("Movies", "No Cover Movie");
        using (var conn = _db.CreateConnection())
        {
            var asset = conn.QuerySingle<Guid>("SELECT ma.id FROM media_assets ma JOIN editions e ON e.id = ma.edition_id WHERE e.work_id = @work;", new { work });
            conn.Execute("DELETE FROM entity_assets;");
            conn.Execute("""
                INSERT INTO media_operations (id,operation_type,operation_kind,entity_id,entity_kind,status,stage,position_key,created_at,updated_at,idempotency_key)
                VALUES (@id,'enrichment.artwork','enrichment',@asset,'MediaAsset','completed','artwork',1,@now,@now,@key);
                """, new { id = Guid.NewGuid(), asset, now = DateTimeOffset.UtcNow.ToString("O"), key = Guid.NewGuid().ToString() });
        }

        var item = await AddedItemAsync(work);

        Assert.Equal("complete", item.Artwork.State);
        Assert.Equal("No artwork found", item.Artwork.Label);
    }

    [Fact]
    public async Task CurrentPreview_FollowsIdentityActivity_AndASettledGroupMovesToRecentAdditions()
    {
        var older = AddStandalone("Books", "Older intake", occurredAt: DateTimeOffset.UtcNow.AddHours(-1), ready: false);
        var newer = AddStandalone("Books", "Newer intake");
        AddIdentityJob(older, "Hydrating", "Books");
        using var conn = _db.CreateConnection();
        conn.Execute("UPDATE ingestion_log SET status = 'queued_identity' WHERE detected_title = 'Older intake';");
        var service = new IngestionPresentationReadService(_db);

        Assert.Equal(older, Assert.Single((await service.GetCurrentMediaAsync(0, 1)).Items).GroupId);

        conn.Execute("UPDATE identity_jobs SET state = 'Ready';");
        MarkReady(older);
        Assert.Empty((await service.GetCurrentMediaAsync(0, 50)).Items);
        var recent = await service.GetRecentAdditionsAsync(null, null, null, null, 0, 50);
        Assert.Equal(new[] { older, newer }.Order(), recent.Items.Select(item => item.GroupId).Order());
        var ready = recent.Items.Single(item => item.GroupId == older);
        Assert.Equal(1, ready.ChildCompleted);
        Assert.Equal("ready", ready.Availability);
    }

    [Fact]
    public async Task LiveSnapshot_WaitsForRootCoverAndNeverUsesEpisodeStill()
    {
        var show = AddContainer("TV", "Root Show");
        var season = AddContainer("TV", "Season 1", show);
        AddChildren(season, "TV", "Episode", 1, "episode_number", seasonNumber: 1, ready: false);
        using var conn = _db.CreateConnection();
        var child = conn.QuerySingle<Guid>("SELECT id FROM works WHERE parent_work_id=@season", new { season });
        var asset = conn.QuerySingle<Guid>("SELECT ma.id FROM media_assets ma JOIN editions e ON e.id=ma.edition_id WHERE e.work_id=@child", new { child });
        var now = DateTimeOffset.UtcNow.ToString("O");
        conn.Execute("DELETE FROM entity_assets;");
        conn.Execute("""
            INSERT INTO identity_jobs (id,entity_id,entity_type,media_type,state,pass,created_at,updated_at)
            VALUES (@job,@asset,'MediaAsset','TV','Hydrating','Quick',@now,@now);
            INSERT INTO entity_assets (id,entity_id,entity_type,asset_type,local_image_path,aspect_class,asset_class,storage_location,owner_scope,is_preferred,created_at)
            VALUES (@still,@child,'Work','EpisodeStill','C:/test/still.jpg','Landscape','Artwork','Central','Work',1,@now);
            """, new { job = Guid.NewGuid(), asset, child, still = Guid.NewGuid(), now });

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
        var work = AddStandalone("Books", "Locally identified book");
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO media_operations (
                id, operation_type, operation_kind, entity_id, entity_kind,
                status, stage, position_key, created_at, updated_at, idempotency_key)
            VALUES (
                @id, 'ingestion.enrichment', 'enrichment', @work, 'Work',
                'running', 'artwork', 1, @now, @now, @key);
            """, new { id = Guid.NewGuid(), work, now, key = $"three-gates-{work:N}" });

        var tile = Assert.Single((await new IngestionPresentationReadService(_db).GetSnapshotAsync()).CurrentMedia);

        Assert.Equal(33, tile.ProgressPercent);
        Assert.Equal("enriched", tile.CurrentGateKey);
        Assert.Equal(["Identified", "Enriching details", "Ready in library"], tile.ProgressGates.Select(gate => gate.Label));
    }

    [Theory]
    [InlineData("UniverseEnriching", false)]
    [InlineData("Hydrating", true)]
    [InlineData("BridgeSearching", true)]
    [InlineData("RetailSearching", false)]
    [InlineData("Queued", false)]
    [InlineData("RetailMatched", false)]
    [InlineData("QidResolved", false)]
    public async Task AnInterruptedJobKeepsItsGroupInFlightUntilDurableWorkFinishes(string state, bool leased)
    {
        var work = AddStandalone("Books", "Resumed book", ready: false);
        using var conn = _db.CreateConnection();
        var asset = conn.QuerySingle<Guid>("SELECT ma.id FROM media_assets ma JOIN editions e ON e.id=ma.edition_id WHERE e.work_id=@work", new { work });
        conn.Execute("""
            INSERT INTO identity_jobs (id,entity_id,entity_type,media_type,state,pass,lease_owner,created_at,updated_at)
            VALUES (@id,@asset,'MediaAsset','Books',@state,'Quick',@owner,@now,@now);
            UPDATE ingestion_log SET status='queued_identity' WHERE media_asset_id=@asset;
            """, new { id = Guid.NewGuid(), asset, state, owner = leased ? "previous-engine" : null, now = DateTimeOffset.UtcNow.AddHours(-2).ToString("O") });
        var presentation = new IngestionPresentationReadService(_db);

        // The same group stays in flight before and after startup lease recovery.
        for (var restart = 0; restart < 2; restart++)
        {
            if (restart > 0)
            {
                await new IdentityJobRepository(_db).RecoverInterruptedJobsAsync();
            }

            var snapshot = await presentation.GetSnapshotAsync();
            Assert.True(snapshot.IsRunning);
            Assert.Equal(1, snapshot.CurrentMediaTotal);
            Assert.Equal(work, Assert.Single(snapshot.CurrentMedia).GroupId);
            Assert.Equal(work, Assert.Single((await presentation.GetCurrentMediaAsync(0, 50)).Items).GroupId);
            Assert.NotNull(await presentation.GetMediaGroupAsync(work));
        }

        conn.Execute("UPDATE identity_jobs SET state='Ready',lease_owner=NULL;");
        MarkReady(work);
        Assert.Empty((await presentation.GetCurrentMediaAsync(0, 50)).Items);
        Assert.Equal(work, Assert.Single((await presentation.GetRecentAdditionsAsync(null, null, null, null, 0, 50)).Items).GroupId);
    }

    [Fact]
    public async Task ComicPreviewPrefersOwnedIssueCoverOverNewerParentCover()
    {
        var series = AddContainer("Comics", "Example comic");
        AddChildren(series, "Comics", "Issue", 1, "issue_number", ready: false);
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

    private async Task<IngestionMediaGroupDto> CurrentItemAsync(
        Guid groupId,
        MediaEngine.Api.Services.IIdentityBypassLibraries? bypass = null)
    {
        var page = await new IngestionPresentationReadService(_db, bypassLibraries: bypass).GetCurrentMediaAsync(0, 50);
        return Assert.Single(page.Items, item => item.GroupId == groupId);
    }

    private async Task<IngestionMediaGroupDto> AddedItemAsync(Guid groupId)
    {
        var page = await new IngestionPresentationReadService(_db).GetRecentAdditionsAsync(null, null, null, null, 0, 50);
        return Assert.Single(page.Items, item => item.GroupId == groupId);
    }

    private void AddIdentityJob(Guid workId, string state, string mediaType)
    {
        using var conn = _db.CreateConnection();
        var asset = conn.QuerySingle<Guid>(
            "SELECT ma.id FROM media_assets ma JOIN editions e ON e.id = ma.edition_id WHERE e.work_id = @workId;",
            new { workId });
        conn.Execute("""
            INSERT INTO identity_jobs (id,entity_id,entity_type,media_type,state,pass,created_at,updated_at)
            VALUES (@id,@asset,'MediaAsset',@mediaType,@state,'Quick',@now,@now);
            """, new { id = Guid.NewGuid(), asset, state, mediaType, now = DateTimeOffset.UtcNow.ToString("O") });
    }

    private void AddUnmatched(Guid workId, string libraryId = "movies-library")
    {
        using var conn = _db.CreateConnection();
        var asset = conn.QuerySingle<Guid>(
            "SELECT ma.id FROM media_assets ma JOIN editions e ON e.id = ma.edition_id WHERE e.work_id = @workId;",
            new { workId });
        var now = DateTimeOffset.UtcNow.ToString("o");
        conn.Execute("""
            INSERT INTO unmatched_assets (asset_id, library_id, reason, first_unmatched_at, last_tried_at, next_retry_at, kept_as_is)
            VALUES (@asset, @libraryId, 'no_match', @now, @now, @now, 0);
            """, new { asset, libraryId, now });
    }

    // Artwork counts as present when the Work carries a stored cover state, the same value the Ready rule reads.
    private void SettleArtwork(Guid workId)
    {
        using var conn = _db.CreateConnection();
        conn.Execute(
            "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@workId, 'cover_state', 'present', @now);",
            new { workId, now = DateTimeOffset.UtcNow.ToString("O") });
    }

    private sealed class FixedBypassLibraries(params string[] ids) : MediaEngine.Api.Services.IIdentityBypassLibraries
    {
        public IReadOnlyCollection<string> GetLibraryIds() => ids;
    }

    private sealed class FixedQueueProgress(IngestionQueueProgressDto current) : MediaEngine.Api.Services.IIngestionQueueProgressService
    {
        public IngestionQueueProgressDto Current { get; } = current;
    }

    [Fact]
    public async Task AnOldFailureOnAnEpisodeAlreadyInTheLibrary_DoesNotMarkTheWholeShowFailed()
    {
        var show = AddContainer("TV", "The Expanse");
        var season = AddContainer("TV", "Season 1", show, "episode_count", "3");
        AddChildren(season, "TV", "Episode", 2, "episode_number", seasonNumber: 1);
        AddChildren(season, "TV", "New Episode", 1, "episode_number", seasonNumber: 1, ready: false);
        using (var conn = _db.CreateConnection())
        {
            var readyEpisode = conn.QueryFirst<Guid>(
                "SELECT w.id FROM works w WHERE w.parent_work_id = @season AND w.ready_at IS NOT NULL ORDER BY w.ordinal LIMIT 1;", new { season });
            var newEpisode = conn.QueryFirst<Guid>(
                "SELECT w.id FROM works w WHERE w.parent_work_id = @season AND w.ready_at IS NULL LIMIT 1;", new { season });
            var now = DateTimeOffset.UtcNow.AddDays(-1).ToString("O");
            // A lyrics/subtitle fetch that gave up yesterday on an episode that is already in the library.
            conn.Execute("""
                INSERT INTO media_operations (id,operation_type,operation_kind,entity_id,entity_kind,status,position_key,created_at,updated_at,idempotency_key)
                VALUES (@id,'enrichment.text','enrichment',@readyEpisode,'Work','dead_lettered',1,@now,@now,@key);
                """, new { id = Guid.NewGuid(), readyEpisode, now, key = Guid.NewGuid().ToString() });
            AddIntakeOperation(newEpisode, "running");
        }

        var page = await new IngestionPresentationReadService(_db).GetCurrentMediaAsync(0, 50);

        var row = Assert.Single(page.Items);
        Assert.Equal("adding", row.Availability);
        Assert.NotEqual("Failed", row.StatusLabel);
    }

    [Fact]
    public async Task AFailureNewerThanTheGroupsNewestFile_StillMarksTheGroupFailed()
    {
        var movie = AddStandalone("Movies", "Failed Today", ready: false, occurredAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        using (var conn = _db.CreateConnection())
        {
            conn.Execute("""
                INSERT INTO media_operations (id,operation_type,operation_kind,entity_id,entity_kind,status,position_key,created_at,updated_at,idempotency_key)
                VALUES (@id,'enrichment.people','enrichment',@movie,'Work','failed_terminal',1,@now,@now,@key);
                """, new { id = Guid.NewGuid(), movie, now = DateTimeOffset.UtcNow.ToString("O"), key = Guid.NewGuid().ToString() });
        }

        var group = await new IngestionPresentationReadService(_db).GetMediaGroupAsync(movie);

        Assert.Equal("failed", group!.Availability);
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
        Guid parentId,
        string mediaType,
        string titlePrefix,
        int count,
        string? ordinalKey = null,
        int? seasonNumber = null,
        int? partCount = null,
        bool ready = true,
        DateTimeOffset? loggedAt = null,
        DateTimeOffset? readyAt = null)
    {
        for (var index = 1; index <= count; index++)
        {
            var workId = Guid.NewGuid();
            var editionId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var logId = Guid.NewGuid();
            var now = (loggedAt ?? DateTimeOffset.UtcNow).ToString("O");
            var readyStamp = ready ? (readyAt ?? loggedAt ?? DateTimeOffset.UtcNow).ToString("O") : null;
            using var conn = _db.CreateConnection();
            conn.Execute(
                "INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ready_at, first_ready_at) VALUES (@workId, @mediaType, 'child', @parentId, @index, @readyStamp, @readyStamp);",
                new { workId, mediaType, parentId, index, readyStamp });
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

            InsertLog(conn, logId, assetId, mediaType, $"{titlePrefix} {index:00}", now);
        }
    }

    private Guid AddStandalone(
        string mediaType,
        string title,
        bool presented = false,
        DateTimeOffset? occurredAt = null,
        bool ready = true)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var logId = Guid.NewGuid();
        var now = (occurredAt ?? DateTimeOffset.UtcNow).ToString("O");
        using var conn = _db.CreateConnection();
        conn.Execute(
            "INSERT INTO works (id, media_type, work_kind, ready_at, first_ready_at) VALUES (@workId, @mediaType, 'standalone', @readyAt, @readyAt);",
            new { workId, mediaType, readyAt = ready ? now : null });
        conn.Execute("INSERT INTO editions (id, work_id, format_label) VALUES (@editionId, @workId, 'Test');", new { editionId, workId });
        conn.Execute("INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at) VALUES (@assetId, @editionId, @hash, @path, @presentedAt);", new { assetId, editionId, hash = $"hash-{assetId:N}", path = $"C:/watch/{assetId:N}.media", presentedAt = presented ? now : null });
        conn.Execute("INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@workId, 'title', @title, @now);", new { workId, title, now });
        conn.Execute("""
            INSERT INTO entity_assets (id,entity_id,entity_type,asset_type,local_image_path,aspect_class,asset_class,storage_location,owner_scope,is_preferred,created_at)
            VALUES (@artId,@workId,'Work','CoverArt','C:/test/cover.jpg','Portrait','Artwork','Central','Work',1,@now);
            """, new { artId = Guid.NewGuid(), workId, now });
        InsertLog(conn, logId, assetId, mediaType, title, now);
        return workId;
    }

    // The stored Ready mark is the one Ready rule; these set or clear it directly, as the readiness service would.
    private void MarkReady(Guid workId, DateTimeOffset? at = null)
    {
        using var conn = _db.CreateConnection();
        var stamp = (at ?? DateTimeOffset.UtcNow).ToString("O");
        conn.Execute("UPDATE works SET ready_at = @stamp, first_ready_at = COALESCE(first_ready_at, @stamp) WHERE id = @workId;", new { workId, stamp });
    }

    private void MarkNotReady(Guid workId)
    {
        using var conn = _db.CreateConnection();
        conn.Execute("UPDATE works SET ready_at = NULL WHERE id = @workId;", new { workId });
    }

    private void AddReadyIdentityJob(Guid workId) => AddIdentityJob(workId, "Ready", "Books");

    private void AddIntakeOperation(
        Guid workId,
        string status,
        string? resultSummary = null,
        DateTimeOffset? updatedAt = null)
    {
        using var conn = _db.CreateConnection();
        var asset = conn.QuerySingle<Guid>(
            "SELECT ma.id FROM media_assets ma JOIN editions e ON e.id = ma.edition_id WHERE e.work_id = @workId;",
            new { workId });
        var now = (updatedAt ?? DateTimeOffset.UtcNow).ToString("O");
        conn.Execute("""
            INSERT INTO media_operations (id,operation_type,operation_kind,entity_id,entity_kind,status,result_summary,position_key,created_at,updated_at,idempotency_key)
            VALUES (@id,'ingestion.file','ingestion',@asset,'MediaAsset',@status,@resultSummary,1,@now,@now,@key);
            """, new { id = Guid.NewGuid(), asset, status, resultSummary, now, key = Guid.NewGuid().ToString() });
    }

    private void HoldInspection(Guid workId)
    {
        using var conn = _db.CreateConnection();
        var assetId = conn.QuerySingle<Guid>(
            "SELECT ma.id FROM media_assets ma JOIN editions e ON e.id = ma.edition_id WHERE e.work_id = @workId;",
            new { workId });
        conn.Execute(
            "INSERT INTO media_inspection_holds (asset_id, tool_id, held_at) VALUES (@assetId, 'ffmpeg', @now);",
            new { assetId, now = DateTimeOffset.UtcNow.ToString("O") });
    }

    private static void InsertLog(System.Data.IDbConnection conn, Guid logId, Guid assetId, string mediaType, string title, string now)
    {
        conn.Execute("""
            INSERT INTO ingestion_log (
                id, file_path, media_asset_id, content_hash, status, media_type,
                detected_title, normalized_title, created_at, updated_at)
            VALUES (
                @logId, @path, @assetId, @hash, 'registered', @mediaType,
                @title, @title, @now, @now);
            """, new { logId, path = $"C:/watch/{assetId:N}.media", assetId, hash = $"hash-{assetId:N}", mediaType, title, now });
    }

    private sealed class ChildIdentity
    {
        public Guid WorkId { get; init; }
        public Guid EditionId { get; init; }
    }
}
