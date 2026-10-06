using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;

namespace MediaEngine.Storage.Tests;

public sealed class LibraryItemProjectionTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;

    public LibraryItemProjectionTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_libraryItem_projection_{Guid.NewGuid():N}.db");
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
    public async Task LibraryItemProjection_AppliesVisibilityGateAndNormalizedOverviewCounts()
    {
        var now = DateTimeOffset.UtcNow;

        var (visibleWorkId, visibleAssetId) = await BuildStandaloneWorkAsync("Books");
        await InsertCanonicalAsync(visibleAssetId, "title", "Dune");
        await InsertCanonicalAsync(visibleWorkId, "wikidata_qid", "Q123");
        await InsertCanonicalAsync(visibleWorkId, "cover_state", "present");
        await InsertCanonicalAsync(visibleWorkId, "cover_source", "provider");
        await InsertCanonicalAsync(visibleWorkId, "artwork_settled_at", now.ToString("O"));

        var (hiddenWorkId, hiddenAssetId) = await BuildStandaloneWorkAsync("Movies");
        await InsertCanonicalAsync(hiddenAssetId, "title", "Hidden Pending Art");
        await InsertCanonicalAsync(hiddenWorkId, "cover_state", "pending");

        var (reviewWorkId, reviewAssetId) = await BuildStandaloneWorkAsync("Audiobooks");
        await InsertCanonicalAsync(reviewAssetId, "title", "Needs Review");
        await InsertCanonicalAsync(reviewWorkId, "cover_state", "present");
        await InsertCanonicalAsync(reviewWorkId, "artwork_settled_at", now.ToString("O"));
        await CreateIdentityJobAsync(reviewAssetId, IdentityJobState.RetailMatchedNeedsReview);

        var (qidNoMatchWorkId, qidNoMatchAssetId) = await BuildStandaloneWorkAsync("Music");
        await InsertCanonicalAsync(qidNoMatchAssetId, "title", "No Qid Yet");
        await InsertCanonicalAsync(qidNoMatchWorkId, "cover_state", "missing");
        await InsertCanonicalAsync(qidNoMatchWorkId, "cover_source", "none");
        await InsertCanonicalAsync(qidNoMatchWorkId, "artwork_settled_at", now.ToString("O"));
        await CreateIdentityJobAsync(qidNoMatchAssetId, IdentityJobState.QidNoMatch);

        var repo = new LibraryItemRepository(_db);

        var page = await repo.GetPageAsync(new LibraryItemQuery());
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(2, page.Items.Count);
        Assert.Contains(page.Items, i => i.EntityId == visibleWorkId);
        Assert.Contains(page.Items, i => i.EntityId == qidNoMatchWorkId);
        Assert.DoesNotContain(page.Items, i => i.EntityId == hiddenWorkId);
        Assert.DoesNotContain(page.Items, i => i.EntityId == reviewWorkId);

        var visibleListItem = page.Items.Single(i => i.EntityId == visibleWorkId);
        var visibleDetail = await repo.GetDetailAsync(visibleWorkId);
        Assert.NotNull(visibleDetail);
        Assert.Equal("visible", visibleListItem.LibraryVisibility);
        Assert.True(visibleListItem.IsReadyForLibrary);
        Assert.Equal("present", visibleListItem.ArtworkState);
        Assert.Equal(visibleListItem.PipelineStep, visibleDetail!.PipelineStep);
        Assert.Equal(visibleListItem.LibraryVisibility, visibleDetail.LibraryVisibility);
        Assert.Equal(visibleListItem.ArtworkState, visibleDetail.ArtworkState);
        Assert.Equal(visibleListItem.CoverUrl, visibleDetail.CoverUrl);

        var qidNoMatchDetail = await repo.GetDetailAsync(qidNoMatchWorkId);
        Assert.NotNull(qidNoMatchDetail);
        Assert.Equal("visible", qidNoMatchDetail!.LibraryVisibility);
        Assert.True(qidNoMatchDetail.IsReadyForLibrary);
        Assert.Equal("missing", qidNoMatchDetail.ArtworkState);
        Assert.Equal("Wikidata", qidNoMatchDetail.PipelineStep);
        Assert.Equal("QidNoMatch", qidNoMatchDetail.Status);
        Assert.Null(qidNoMatchDetail.CoverUrl);

        var hiddenDetail = await repo.GetDetailAsync(hiddenWorkId);
        Assert.NotNull(hiddenDetail);
        Assert.Equal("hidden", hiddenDetail!.LibraryVisibility);
        Assert.False(hiddenDetail.IsReadyForLibrary);
        Assert.Equal("pending", hiddenDetail.ArtworkState);

        var reviewDetail = await repo.GetDetailAsync(reviewWorkId);
        Assert.NotNull(reviewDetail);
        Assert.Equal("review_only", reviewDetail!.LibraryVisibility);
        Assert.Equal("Retail", reviewDetail.PipelineStep);

        var summary = await repo.GetProjectionSummaryAsync(CancellationToken.None);
        Assert.Equal(4, summary.TotalItems);
        Assert.Equal(1, summary.HiddenByQualityGate);
        Assert.Equal(1, summary.ArtPending);
        Assert.Equal(1, summary.RetailNeedsReview);
        Assert.Equal(1, summary.QidNoMatch);
        Assert.Equal(1, summary.CompletedWithArt);
    }

    [Fact]
    public async Task LibraryItemProjection_RequiresArtworkSettlementBeforeMissingCoverIsVisible()
    {
        var (workId, assetId) = await BuildStandaloneWorkAsync("Books");
        await InsertCanonicalAsync(assetId, "title", "Waiting For Artwork Verdict");
        await InsertCanonicalAsync(workId, "cover_state", "missing");
        await InsertCanonicalAsync(workId, "cover_source", "none");

        var repo = new LibraryItemRepository(_db);

        var beforePage = await repo.GetPageAsync(new LibraryItemQuery());
        Assert.Empty(beforePage.Items);

        var beforeDetail = await repo.GetDetailAsync(workId);
        Assert.NotNull(beforeDetail);
        Assert.Equal("hidden", beforeDetail!.LibraryVisibility);
        Assert.False(beforeDetail.IsReadyForLibrary);
        Assert.Equal("missing", beforeDetail.ArtworkState);
        Assert.Null(beforeDetail.CoverUrl);

        await InsertCanonicalAsync(workId, "artwork_settled_at", DateTimeOffset.UtcNow.ToString("O"));

        var afterPage = await repo.GetPageAsync(new LibraryItemQuery());
        var item = Assert.Single(afterPage.Items);
        Assert.Equal(workId, item.EntityId);
        Assert.Equal("visible", item.LibraryVisibility);
        Assert.True(item.IsReadyForLibrary);
        Assert.Equal("missing", item.ArtworkState);
        Assert.Null(item.CoverUrl);
    }

    [Fact]
    public async Task LibraryItemDetail_UsesOriginalWorkDateAcrossMediaTypes()
    {
        var cases = new[]
        {
            new DetailDateCase(
                "Books",
                "1937",
                "September 21, 1937",
                new Dictionary<string, string>
                {
                    ["date"] = "1937-09-21",
                    ["year"] = "2012",
                }),
            new DetailDateCase(
                "Audiobooks",
                "1937",
                "September 21, 1937",
                new Dictionary<string, string>
                {
                    ["original_release_date"] = "1937-09-21",
                    ["edition_release_date"] = "2007-09-18",
                    ["year"] = "2007",
                }),
            new DetailDateCase(
                "Movies",
                "2014",
                "2014",
                new Dictionary<string, string>
                {
                    ["year"] = "2014",
                    ["release_date"] = "2025-02-14",
                }),
            new DetailDateCase(
                "TV",
                "2008",
                "January 20, 2008",
                new Dictionary<string, string>
                {
                    ["first_air_date"] = "2008-01-20",
                    ["year"] = "2013",
                }),
            new DetailDateCase(
                "Comics",
                "1984",
                "1984",
                new Dictionary<string, string>
                {
                    ["year"] = "1984",
                    ["release_year"] = "1988",
                }),
            new DetailDateCase(
                "Music",
                "1971",
                "December 17, 1971",
                new Dictionary<string, string>
                {
                    ["original_release_date"] = "1971-12-17",
                    ["year"] = "2019",
                }),
        };
        var repository = new LibraryItemRepository(_db);

        foreach (var item in cases)
        {
            var (workId, assetId) = await BuildStandaloneWorkAsync(item.MediaType);
            await InsertCanonicalAsync(assetId, "title", $"{item.MediaType} date test");
            foreach (var (key, value) in item.Dates)
            {
                await InsertCanonicalAsync(assetId, key, value);
            }

            var detail = await repository.GetDetailAsync(workId);

            Assert.NotNull(detail);
            Assert.Equal(item.ExpectedYear, detail.Year);
            Assert.Equal(item.ExpectedDate, detail.ReleaseDate);
        }
    }

    [Fact]
    public async Task LibraryItemDetail_AudiobookUsesOwnedBookDateWhenLegacyEditionHasNoOriginalDate()
    {
        var (bookWorkId, bookAssetId) = await BuildStandaloneWorkAsync("Books");
        var (audioWorkId, audioAssetId) = await BuildStandaloneWorkAsync("Audiobooks");
        await InsertCanonicalAsync(bookAssetId, "title", "The Hobbit");
        await InsertCanonicalAsync(bookAssetId, "date", "1937-09-21");
        await InsertCanonicalAsync(bookAssetId, "year", "2012");
        await InsertCanonicalAsync(audioAssetId, "title", "The Hobbit");
        await InsertCanonicalAsync(audioAssetId, "release_year", "2000");
        await InsertCanonicalAsync(audioAssetId, "year", "2007");
        await InsertCanonicalArrayAsync(bookAssetId, "author", "J. R. R. Tolkien");
        await InsertCanonicalArrayAsync(audioAssetId, "author", "J. R. R. Tolkien");

        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO persons (id, name, created_at)
                VALUES (@personId, 'J. R. R. Tolkien', CURRENT_TIMESTAMP);
                """,
                new { personId = Guid.NewGuid() });
        }

        var detail = await new LibraryItemRepository(_db).GetDetailAsync(audioWorkId);

        Assert.NotNull(detail);
        Assert.Equal("1937", detail.Year);
        Assert.Equal("1937", detail.ReleaseDate);
        Assert.NotEqual(bookWorkId, audioWorkId);
    }

    [Fact]
    public async Task LibraryItemProjection_RetailNoMatchStaysOutOfVisibleLibraryAfterReviewDismissed()
    {
        var now = DateTimeOffset.UtcNow;
        var (workId, assetId) = await BuildStandaloneWorkAsync("Comics");
        await InsertCanonicalAsync(assetId, "title", "Unmatched Issue");
        await InsertCanonicalAsync(workId, "cover_state", "present");
        await InsertCanonicalAsync(workId, "artwork_settled_at", now.ToString("O"));
        await CreateIdentityJobAsync(assetId, IdentityJobState.RetailNoMatch);

        var reviewRepo = new ReviewQueueRepository(_db);
        var reviewId = await reviewRepo.InsertAsync(new ReviewQueueEntry
        {
            Id = Guid.NewGuid(),
            EntityId = assetId,
            EntityType = "MediaAsset",
            Trigger = ReviewTrigger.RetailMatchFailed,
            ConfidenceScore = 0,
            Detail = "Retail identification failed.",
            ReviewReadyAt = now,
            AutomationCompletedAt = now,
        });
        await reviewRepo.UpdateStatusAsync(reviewId, ReviewStatus.Dismissed, "test");

        var repo = new LibraryItemRepository(_db);

        var visiblePage = await repo.GetPageAsync(new LibraryItemQuery());
        Assert.DoesNotContain(visiblePage.Items, item => item.EntityId == workId);

        var allPage = await repo.GetPageAsync(new LibraryItemQuery(IncludeAll: true));
        var item = Assert.Single(allPage.Items, item => item.EntityId == workId);
        Assert.Equal("review_only", item.LibraryVisibility);
        Assert.False(item.IsReadyForLibrary);
        Assert.Equal("NoMatch", item.Status);
    }

    [Fact]
    public async Task LibraryItemProjection_WorkScopedPendingReviewStaysOutOfVisibleLibrary()
    {
        var now = DateTimeOffset.UtcNow;
        var (workId, assetId) = await BuildStandaloneWorkAsync("Books");
        await InsertCanonicalAsync(assetId, "title", "Work Level Review");
        await InsertCanonicalAsync(workId, "cover_state", "present");
        await InsertCanonicalAsync(workId, "artwork_settled_at", now.ToString("O"));

        var reviewRepo = new ReviewQueueRepository(_db);
        await reviewRepo.InsertAsync(new ReviewQueueEntry
        {
            Id = Guid.NewGuid(),
            EntityId = workId,
            EntityType = "Work",
            Trigger = ReviewTrigger.LowConfidence,
            ConfidenceScore = 0.2,
            Detail = "Work-level review.",
            ReviewReadyAt = now,
            AutomationCompletedAt = now,
        });

        var repo = new LibraryItemRepository(_db);

        var visiblePage = await repo.GetPageAsync(new LibraryItemQuery());
        Assert.DoesNotContain(visiblePage.Items, item => item.EntityId == workId);

        var allPage = await repo.GetPageAsync(new LibraryItemQuery(IncludeAll: true));
        var item = Assert.Single(allPage.Items, item => item.EntityId == workId);
        Assert.Equal("review_only", item.LibraryVisibility);
        Assert.Equal("InReview", item.Status);
    }

    [Fact]
    public async Task LibraryItemProjection_TreatsUniverseEnrichingAsRetailMatched()
    {
        var settledAt = DateTimeOffset.UtcNow.ToString("O");
        var (workId, assetId) = await BuildStandaloneWorkAsync("Books");
        await InsertCanonicalAsync(assetId, "title", "Identified During Stage 3");
        await InsertCanonicalAsync(assetId, "wikidata_qid", "Q123");
        await InsertCanonicalAsync(workId, "wikidata_qid", "Q123");
        await InsertCanonicalAsync(workId, "cover_state", "present");
        await InsertCanonicalAsync(workId, "cover_url", "/stream/test/cover");
        await InsertCanonicalAsync(workId, "artwork_settled_at", settledAt);
        await CreateIdentityJobAsync(assetId, IdentityJobState.UniverseEnriching);

        var repo = new LibraryItemRepository(_db);

        var page = await repo.GetPageAsync(new LibraryItemQuery(IncludeAll: true));
        var item = Assert.Single(page.Items, item => item.EntityId == workId);

        Assert.Equal("Identified", item.Status);
        Assert.Equal("matched", item.RetailMatch);
        Assert.Equal("matched", item.WikidataMatch);
    }

    [Fact]
    public async Task LibraryItemProjection_ComicRowsPreferIssueTitleAndIssueNumberFallback()
    {
        var (issueTitleWorkId, issueTitleAssetId) = await BuildStandaloneWorkAsync("Comics");
        await InsertCanonicalAsync(issueTitleAssetId, MetadataFieldConstants.Title, "Saga");
        await InsertCanonicalAsync(issueTitleAssetId, MetadataFieldConstants.IssueTitle, "Chapter One");
        await InsertCanonicalAsync(issueTitleAssetId, MetadataFieldConstants.SeriesPosition, "1");
        await InsertCanonicalAsync(issueTitleWorkId, MetadataFieldConstants.Series, "Saga");

        var (fallbackWorkId, fallbackAssetId) = await BuildStandaloneWorkAsync("Comics");
        await InsertCanonicalAsync(fallbackAssetId, MetadataFieldConstants.Title, "Batman");
        await InsertCanonicalAsync(fallbackAssetId, MetadataFieldConstants.IssueTitle, "Batman");
        await InsertCanonicalAsync(fallbackAssetId, MetadataFieldConstants.IssueNumber, "405");
        await InsertCanonicalAsync(fallbackAssetId, MetadataFieldConstants.SeriesPosition, "405");
        await InsertCanonicalAsync(fallbackWorkId, MetadataFieldConstants.Series, "Batman");

        var repo = new LibraryItemRepository(_db);

        var page = await repo.GetPageAsync(new LibraryItemQuery(IncludeAll: true));

        Assert.Contains(page.Items, item => item.EntityId == issueTitleWorkId && item.Title == "Chapter One");
        Assert.Contains(page.Items, item => item.EntityId == fallbackWorkId && item.Title == "Issue #405");

        var detail = await repo.GetDetailAsync(issueTitleWorkId);
        Assert.NotNull(detail);
        Assert.Equal("Chapter One", detail!.Title);
    }

    [Fact]
    public async Task LibraryItemProjection_ComicIssueShowsSelfScopedCreatorWhenParentCreatorMissing()
    {
        var (parentWorkId, issueWorkId, issueAssetId) = await BuildComicIssueWorkWithParentAsync();
        await InsertCanonicalAsync(parentWorkId, MetadataFieldConstants.Series, "Saga");
        await InsertCanonicalAsync(issueAssetId, MetadataFieldConstants.Title, "Saga");
        await InsertCanonicalAsync(issueAssetId, MetadataFieldConstants.IssueTitle, "Chapter One");
        await InsertCanonicalArrayAsync(issueAssetId, MetadataFieldConstants.Author, "Brian K. Vaughan");
        await InsertCanonicalAsync(issueAssetId, MetadataFieldConstants.SeriesPosition, "1");

        var repo = new LibraryItemRepository(_db);

        var page = await repo.GetPageAsync(new LibraryItemQuery(IncludeAll: true));
        var item = Assert.Single(page.Items, item => item.EntityId == issueWorkId);
        Assert.Equal("Chapter One", item.Title);
        Assert.Equal("Brian K. Vaughan", item.Author);

        var detail = await repo.GetDetailAsync(issueWorkId);
        Assert.NotNull(detail);
        Assert.Equal("Brian K. Vaughan", detail!.Author);
    }

    [Fact]
    public async Task LibraryItemProjection_ComicIssueShowsLocalCreatorClaimWhenCanonicalCreatorMissing()
    {
        var (parentWorkId, issueWorkId, issueAssetId) = await BuildComicIssueWorkWithParentAsync();
        await InsertCanonicalAsync(parentWorkId, MetadataFieldConstants.Series, "Akira");
        await InsertCanonicalAsync(issueAssetId, MetadataFieldConstants.Title, "Akira Vol 1");
        await InsertCanonicalAsync(issueAssetId, MetadataFieldConstants.IssueTitle, "Akira Vol 1");
        await InsertCanonicalAsync(issueAssetId, MetadataFieldConstants.SeriesPosition, "1");
        await InsertClaimAsync(issueAssetId, MetadataFieldConstants.Author, "Katsuhiro Otomo", WellKnownProviders.LocalProcessor, 0.8);

        var repo = new LibraryItemRepository(_db);

        var page = await repo.GetPageAsync(new LibraryItemQuery(IncludeAll: true));
        var item = Assert.Single(page.Items, item => item.EntityId == issueWorkId);
        Assert.Equal("Akira Vol 1", item.Title);
        Assert.Equal("Katsuhiro Otomo", item.Author);

        var detail = await repo.GetDetailAsync(issueWorkId);
        Assert.NotNull(detail);
        Assert.Equal("Katsuhiro Otomo", detail!.Author);
    }

    [Fact]
    public async Task LibraryItemProjection_ExposesCuratorLifecycleStatesOnlyWhenRequested()
    {
        var settledAt = DateTimeOffset.UtcNow.ToString("O");

        var (identifiedWorkId, identifiedAssetId) = await BuildStandaloneWorkAsync("Books");
        await InsertCanonicalAsync(identifiedAssetId, "title", "Identified Item");
        await InsertCanonicalAsync(identifiedWorkId, "wikidata_qid", "Q777");
        await InsertCanonicalAsync(identifiedWorkId, "cover_state", "present");
        await InsertCanonicalAsync(identifiedWorkId, "artwork_settled_at", settledAt);

        var (provisionalWorkId, provisionalAssetId) = await BuildStandaloneWorkAsync("Movies");
        await InsertCanonicalAsync(provisionalAssetId, "title", "Provisional Item");
        await InsertCanonicalAsync(provisionalWorkId, "cover_state", "present");
        await InsertCanonicalAsync(provisionalWorkId, "artwork_settled_at", settledAt);
        await UpdateWorkCuratorStateAsync(provisionalWorkId, "provisional");

        var (rejectedWorkId, rejectedAssetId) = await BuildStandaloneWorkAsync("Music");
        await InsertCanonicalAsync(rejectedAssetId, "title", "Rejected Item");
        await InsertCanonicalAsync(rejectedWorkId, "cover_state", "present");
        await InsertCanonicalAsync(rejectedWorkId, "artwork_settled_at", settledAt);
        await UpdateWorkCuratorStateAsync(rejectedWorkId, "rejected");

        var repo = new LibraryItemRepository(_db);

        var visiblePage = await repo.GetPageAsync(new LibraryItemQuery());
        Assert.Contains(visiblePage.Items, item => item.EntityId == identifiedWorkId);
        Assert.DoesNotContain(visiblePage.Items, item => item.EntityId == provisionalWorkId);
        Assert.DoesNotContain(visiblePage.Items, item => item.EntityId == rejectedWorkId);

        var allPage = await repo.GetPageAsync(new LibraryItemQuery(IncludeAll: true));
        Assert.Contains(allPage.Items, item => item.EntityId == identifiedWorkId && item.Status == "Confirmed");
        Assert.Contains(allPage.Items, item => item.EntityId == provisionalWorkId && item.Status == "Provisional");
        Assert.Contains(allPage.Items, item => item.EntityId == rejectedWorkId && item.Status == "Rejected");
    }

    [Fact]
    public async Task LibraryItemDetail_UsesWikidataAuthorWhenRetailCanonicalIsStale()
    {
        var (workId, _) = await BuildStandaloneWorkAsync("Books");
        await InsertCanonicalAsync(workId, "title", "Nineteen Eighty-Four");
        await InsertCanonicalAsync(workId, "author", "Michael Dean", WellKnownProviders.OpenLibrary);
        await InsertClaimAsync(workId, "author", "Michael Dean", WellKnownProviders.OpenLibrary, 0.80);
        await InsertClaimAsync(workId, "author", "George Orwell", WellKnownProviders.Wikidata, 0.75);

        var repo = new LibraryItemRepository(_db);

        var detail = await repo.GetDetailAsync(workId);

        Assert.NotNull(detail);
        Assert.Equal("George Orwell", detail!.Author);
    }

    [Fact]
    public async Task LibraryItemDetail_UsesExplicitActiveRetailMatchInsteadOfOlderClaims()
    {
        var (workId, assetId) = await BuildStandaloneWorkAsync("Books");
        await InsertCanonicalAsync(assetId, MetadataFieldConstants.Title, "The Martian", WellKnownProviders.OpenLibrary);
        await InsertClaimAsync(assetId, MetadataFieldConstants.Title, "The Martian", WellKnownProviders.OpenLibrary, 0.99);
        await InsertCanonicalAsync(assetId, MetadataFieldConstants.IdentityProvider, "apple_api", WellKnownProviders.AppleApi);
        await InsertCanonicalAsync(assetId, MetadataFieldConstants.IdentityProviderItemId, "721429965", WellKnownProviders.AppleApi);

        var repo = new LibraryItemRepository(_db);
        var detail = await repo.GetDetailAsync(workId);

        Assert.NotNull(detail);
        Assert.Equal("apple_api", detail!.RetailProviderName);
        Assert.Equal("721429965", detail.RetailProviderItemId);
    }

    [Fact]
    public async Task LibraryItemDetail_UsesOnlyValidatedPreferredAssetAsRepresentative()
    {
        var (workId, firstAssetId) = await BuildStandaloneWorkAsync("Books");
        var secondEditionId = Guid.NewGuid();
        var secondAssetId = Guid.NewGuid();
        using (var connection = _db.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO editions (id, work_id) VALUES (@secondEditionId, @workId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
                VALUES (@secondAssetId, @secondEditionId, @hash, @filePath, 'Normal');
                """,
                new
                {
                    secondEditionId,
                    workId,
                    secondAssetId,
                    hash = $"hash_{secondAssetId:N}",
                    filePath = $"/allowed/{secondAssetId:N}.epub",
                });
        }
        await InsertCanonicalAsync(firstAssetId, "title", "Denied edition title");
        await InsertCanonicalAsync(secondAssetId, "title", "Allowed edition title");

        var repository = new LibraryItemRepository(_db);
        var detail = await repository.GetDetailAsync(workId, secondAssetId);

        Assert.NotNull(detail);
        Assert.Equal("Allowed edition title", detail!.Title);
        Assert.Equal($"/allowed/{secondAssetId:N}.epub", detail.FilePath);

        var (otherWorkId, otherAssetId) = await BuildStandaloneWorkAsync("Books");
        Assert.Null(await repository.GetDetailAsync(workId, otherAssetId));
        Assert.Null(await repository.GetDetailAsync(otherWorkId, firstAssetId));
    }

    [Fact]
    public async Task OwnedCountsCollapseAudiobookChaptersAndEditionsButKeepMusicFiles()
    {
        var (book, _) = await BuildStandaloneWorkAsync("Audiobooks");
        await BuildStandaloneWorkAsync("Audiobooks"); // One-file M4B.
        var (music, _) = await BuildStandaloneWorkAsync("Music");
        await BuildStandaloneWorkAsync("Movies");
        using var connection = _db.CreateConnection();
        foreach (var work in new[] { book, music })
        {
            var edition = Guid.NewGuid();
            await connection.ExecuteAsync("INSERT INTO editions (id, work_id) VALUES (@edition, @work)", new { edition, work });
            for (var i = 0; i < 9; i++)
            {
                var asset = Guid.NewGuid();
                await connection.ExecuteAsync("""
                    INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
                    VALUES (@asset, @edition, @hash, @path, 'Normal')
                    """, new { asset, edition, hash = $"hash_{asset:N}", path = $"/library/{asset:N}.mp3" });
            }
        }
        var catalogue = Guid.NewGuid();
        var collection = Guid.NewGuid();
        await connection.ExecuteAsync("""
            INSERT INTO collections (id, created_at) VALUES (@collection, datetime('now'));
            INSERT INTO works (id, collection_id, media_type) VALUES (@catalogue, @collection, 'Audiobooks');
            """, new { collection, catalogue });
        var counts = await new LibraryItemRepository(_db).GetOwnedMediaTypeCountsAsync();
        Assert.Equal(2, counts["Audiobooks"]);
        Assert.Equal(10, counts["Music"]);
        Assert.Equal(1, counts["Movies"]);
    }

    private async Task<(Guid WorkId, Guid AssetId)> BuildStandaloneWorkAsync(string mediaType)
    {
        using var conn = _db.CreateConnection();
        var collectionId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        await conn.ExecuteAsync("""
            INSERT INTO collections (id, created_at) VALUES (@collectionId, datetime('now'));
            INSERT INTO works (id, collection_id, media_type)
                VALUES (@workId, @collectionId, @mediaType);
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
                VALUES (@assetId, @editionId, @hash, @filePath, 'Normal');
            """,
            new
            {
                collectionId,
                workId,
                editionId,
                assetId,
                mediaType,
                hash = $"hash_{assetId:N}",
                filePath = $"/library/{assetId:N}.bin",
            });

        return (workId, assetId);
    }

    private async Task<(Guid ParentWorkId, Guid IssueWorkId, Guid AssetId)> BuildComicIssueWorkWithParentAsync()
    {
        using var conn = _db.CreateConnection();
        var collectionId = Guid.NewGuid();
        var parentWorkId = Guid.NewGuid();
        var issueWorkId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        await conn.ExecuteAsync("""
            INSERT INTO collections (id, created_at) VALUES (@collectionId, datetime('now'));
            INSERT INTO works (id, collection_id, media_type, work_kind, parent_key)
                VALUES (@parentWorkId, @collectionId, 'Comics', 'parent', 'comic:saga');
            INSERT INTO works (id, collection_id, media_type, work_kind, parent_work_id, ordinal)
                VALUES (@issueWorkId, @collectionId, 'Comics', 'child', @parentWorkId, 1);
            INSERT INTO editions (id, work_id) VALUES (@editionId, @issueWorkId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
                VALUES (@assetId, @editionId, @hash, @filePath, 'Normal');
            """,
            new
            {
                collectionId,
                parentWorkId,
                issueWorkId,
                editionId,
                assetId,
                hash = $"hash_{assetId:N}",
                filePath = $"/library/{assetId:N}.cbz",
            });

        return (parentWorkId, issueWorkId, assetId);
    }

    private async Task InsertCanonicalAsync(Guid entityId, string key, string value, Guid? winningProviderId = null)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO canonical_values (entity_id, key, value, last_scored_at, winning_provider_id)
            VALUES (@entityId, @key, @value, datetime('now'), @winningProviderId);
            """;
        cmd.Parameters.Add("@entityId", Microsoft.Data.Sqlite.SqliteType.Blob).Value = GuidSql.ToBlob(entityId);
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        cmd.Parameters.Add("@winningProviderId", Microsoft.Data.Sqlite.SqliteType.Blob).Value =
            winningProviderId.HasValue ? GuidSql.ToBlob(winningProviderId.Value) : DBNull.Value;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertClaimAsync(Guid entityId, string key, string value, Guid providerId, double confidence)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value, confidence, claimed_at)
            VALUES (@id, @entityId, @providerId, @key, @value, @confidence, datetime('now'));
            """;
        cmd.Parameters.Add("@id", Microsoft.Data.Sqlite.SqliteType.Blob).Value = GuidSql.ToBlob(Guid.NewGuid());
        cmd.Parameters.Add("@entityId", Microsoft.Data.Sqlite.SqliteType.Blob).Value = GuidSql.ToBlob(entityId);
        cmd.Parameters.Add("@providerId", Microsoft.Data.Sqlite.SqliteType.Blob).Value = GuidSql.ToBlob(providerId);
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        cmd.Parameters.AddWithValue("@confidence", confidence);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertCanonicalArrayAsync(Guid entityId, string key, string value, int ordinal = 0)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
            VALUES (@entityId, @key, @ordinal, @value);
            """;
        cmd.Parameters.Add("@entityId", Microsoft.Data.Sqlite.SqliteType.Blob).Value = GuidSql.ToBlob(entityId);
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@ordinal", ordinal);
        cmd.Parameters.AddWithValue("@value", value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task UpdateWorkCuratorStateAsync(Guid workId, string state)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE works
            SET curator_state = @state
            WHERE id = @workId;
            """;
        cmd.Parameters.Add("@workId", Microsoft.Data.Sqlite.SqliteType.Blob).Value = GuidSql.ToBlob(workId);
        cmd.Parameters.AddWithValue("@state", state);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task CreateIdentityJobAsync(Guid assetId, IdentityJobState state)
    {
        var repo = new IdentityJobRepository(_db);
        await repo.CreateAsync(new IdentityJob
        {
            EntityId = assetId,
            EntityType = "MediaAsset",
            MediaType = "Books",
            State = state.ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
    }

    private sealed record DetailDateCase(
        string MediaType,
        string ExpectedYear,
        string ExpectedDate,
        IReadOnlyDictionary<string, string> Dates);
}
