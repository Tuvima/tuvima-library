using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Storage.Tests;

public sealed class WorkReadinessRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly WorkReadinessRepository _repo;
    private static readonly string[] NoBypass = [];

    public WorkReadinessRepositoryTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima-work-readiness-{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _repo = new WorkReadinessRepository(_db);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch (Exception) { /* temp database cleanup is best effort */ }
        try { File.Delete(_dbPath); } catch (IOException) { /* temp database cleanup is best effort */ }
    }

    [Theory]
    [InlineData(IdentityJobState.Queued)]
    [InlineData(IdentityJobState.RetailSearching)]
    [InlineData(IdentityJobState.RetailMatchedNeedsReview)]
    [InlineData(IdentityJobState.RetailNoMatch)]
    [InlineData(IdentityJobState.QidNeedsReview)]
    [InlineData(IdentityJobState.Failed)]
    public async Task Work_IsNotReady_WhenItsLatestJobHasNotMatchedItToATitle(IdentityJobState state)
    {
        var (workId, assetId) = NewWork("Books");
        SetJob(assetId, state);
        SetCanonical(assetId, "cover_state", "present");

        await _repo.RefreshAsync([workId], NoBypass);

        Assert.Null(ReadyAt(workId));
    }

    [Fact]
    public async Task Work_IsNotReady_WhenItHasNoIdentityJob()
    {
        var (workId, assetId) = NewWork("Books");
        SetCanonical(assetId, "cover_state", "present");

        await _repo.RefreshAsync([workId], NoBypass);

        Assert.Null(ReadyAt(workId));
    }

    [Fact]
    public async Task Work_IsNotReady_WhileAReviewRowIsPending()
    {
        var (workId, assetId) = NewWork("Books");
        SetJob(assetId, IdentityJobState.RetailMatched);
        SetCanonical(assetId, "cover_state", "present");
        AddReview(assetId, "LowConfidence", "Pending");

        await _repo.RefreshAsync([workId], NoBypass);

        Assert.Null(ReadyAt(workId));
    }

    [Fact]
    public async Task Work_IsReady_WhenOnlyAWritebackFailureOrAResolvedReviewRemains()
    {
        var (workId, assetId) = NewWork("Books");
        SetJob(assetId, IdentityJobState.RetailMatched);
        SetCanonical(assetId, "cover_state", "present");
        AddReview(assetId, "WritebackFailed", "Pending");
        AddReview(assetId, "LowConfidence", "Resolved");

        await _repo.RefreshAsync([workId], NoBypass);

        Assert.NotNull(ReadyAt(workId));
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("provisional")]
    public async Task Work_IsNotReady_WhenTheCuratorRejectedOrHeldItBack(string curatorState)
    {
        var (workId, assetId) = NewWork("Books");
        SetJob(assetId, IdentityJobState.RetailMatched);
        SetCanonical(assetId, "cover_state", "present");
        Execute("UPDATE works SET curator_state = @curatorState WHERE id = @workId", new { curatorState, workId });

        await _repo.RefreshAsync([workId], NoBypass);

        Assert.Null(ReadyAt(workId));
    }

    [Fact]
    public async Task Work_IsNotReady_WhenItIsCatalogOnly()
    {
        var (workId, assetId) = NewWork("Books");
        SetJob(assetId, IdentityJobState.RetailMatched);
        SetCanonical(assetId, "cover_state", "present");
        Execute("UPDATE works SET is_catalog_only = 1 WHERE id = @workId", new { workId });

        await _repo.RefreshAsync([workId], NoBypass);

        Assert.Null(ReadyAt(workId));
    }

    [Fact]
    public async Task Work_IsReady_WhenMatchedAndItsCoverIsPresent()
    {
        var (workId, assetId) = NewWork("Books");
        SetJob(assetId, IdentityJobState.RetailMatched);
        SetCanonical(assetId, "cover_state", "present");

        var changes = await _repo.RefreshAsync([workId], NoBypass);

        Assert.NotNull(ReadyAt(workId));
        var change = Assert.Single(changes);
        Assert.Equal(workId, change.WorkId);
        Assert.True(change.IsReady);
        Assert.Equal("Books", change.MediaType);
    }

    [Fact]
    public async Task Work_IsReady_WhenWikidataFoundNothingButArtworkIsSettled()
    {
        var (workId, assetId) = NewWork("Movies");
        SetJob(assetId, IdentityJobState.QidNoMatch);
        SetCanonical(workId, "cover_state", "missing");
        SetCanonical(workId, "artwork_settled_at", DateTimeOffset.UtcNow.ToString("O"));

        await _repo.RefreshAsync([workId], NoBypass);

        Assert.NotNull(ReadyAt(workId));
    }

    [Fact]
    public async Task Work_IsReady_WhenAStoredCoverAddressExists()
    {
        var (workId, assetId) = NewWork("Movies");
        SetJob(assetId, IdentityJobState.Hydrating);
        SetCanonical(assetId, "cover_url", "https://images.example/poster.jpg");

        await _repo.RefreshAsync([workId], NoBypass);

        Assert.NotNull(ReadyAt(workId));
    }

    [Fact]
    public async Task Work_IsNotReady_WhenMatchedButNoArtworkStateIsStored()
    {
        var (workId, assetId) = NewWork("Movies");
        SetJob(assetId, IdentityJobState.Ready);

        await _repo.RefreshAsync([workId], NoBypass);

        Assert.Null(ReadyAt(workId));
    }

    [Fact]
    public async Task Work_IsNotReady_WhenItsCoverIsStillPendingAndNothingIsSettled()
    {
        var (workId, assetId) = NewWork("Movies");
        SetJob(assetId, IdentityJobState.RetailMatched);
        SetCanonical(workId, "cover_state", "pending");
        SetCanonical(assetId, "cover_url", "   ");

        await _repo.RefreshAsync([workId], NoBypass);

        Assert.Null(ReadyAt(workId));
    }

    [Fact]
    public async Task Track_IsReady_WhenOnlyItsAlbumHasTheCover()
    {
        var albumId = NewParent("Music");
        var (trackId, assetId) = NewWork("Music", albumId, "child");
        SetJob(assetId, IdentityJobState.RetailMatched);
        SetCanonical(albumId, "cover_state", "present");

        await _repo.RefreshAsync([trackId], NoBypass);

        Assert.NotNull(ReadyAt(trackId));
    }

    [Fact]
    public async Task Track_IsNotReady_WhenItHasNoAlbum()
    {
        var (trackId, assetId) = NewWork("Music");
        SetJob(assetId, IdentityJobState.RetailMatched);
        SetCanonical(assetId, "cover_state", "present");

        await _repo.RefreshAsync([trackId], NoBypass);

        Assert.Null(ReadyAt(trackId));
    }

    [Fact]
    public async Task Episode_IsNotReady_WhileItStandsAloneWithoutAShow()
    {
        var (episodeId, assetId) = NewWork("TV");
        SetJob(assetId, IdentityJobState.RetailMatched);
        SetCanonical(assetId, "cover_state", "present");

        await _repo.RefreshAsync([episodeId], NoBypass);

        Assert.Null(ReadyAt(episodeId));
    }

    [Fact]
    public async Task Episode_IsReady_WhenItsParentIsTheShow()
    {
        var showId = NewParent("TV");
        var (episodeId, assetId) = NewWork("TV", showId, "child");
        SetJob(assetId, IdentityJobState.RetailMatched);
        SetCanonical(showId, "cover_state", "present");

        await _repo.RefreshAsync([episodeId], NoBypass);

        Assert.NotNull(ReadyAt(episodeId));
    }

    [Fact]
    public async Task Episode_IsReady_WhenItsParentIsASeasonUnderAShow()
    {
        var showId = NewParent("TV");
        var seasonId = NewParent("TV", showId);
        var (episodeId, assetId) = NewWork("TV", seasonId, "child");
        SetJob(assetId, IdentityJobState.QidNoMatch);
        SetCanonical(showId, "artwork_settled_at", DateTimeOffset.UtcNow.ToString("O"));

        await _repo.RefreshAsync([episodeId], NoBypass);

        Assert.NotNull(ReadyAt(episodeId));
    }

    [Fact]
    public async Task CoveredEpisode_IsReady_WhenItsCombinedHostFileIsMatchedAndTheShowHasArtwork()
    {
        var showId = NewParent("TV");
        SetCanonical(showId, "cover_state", "present");
        var (hostId, hostAsset) = NewWork("TV", showId, "child");
        var coveredId = NewCoveredWork("TV", showId, hostAsset);
        SetJob(hostAsset, IdentityJobState.RetailMatched);

        await _repo.RefreshAsync([hostId, coveredId], NoBypass);

        Assert.NotNull(ReadyAt(hostId));
        Assert.NotNull(ReadyAt(coveredId));
        Assert.Equal(1, CountGated(coveredId));
    }

    [Fact]
    public async Task CoveredEpisode_IsNotReady_WhileItsHostFileIsInReviewOrNotMatched()
    {
        var showId = NewParent("TV");
        SetCanonical(showId, "cover_state", "present");
        var (hostId, hostAsset) = NewWork("TV", showId, "child");
        var coveredId = NewCoveredWork("TV", showId, hostAsset);
        SetJob(hostAsset, IdentityJobState.RetailMatched);
        AddReview(hostAsset, "LowConfidence", "Pending");

        await _repo.RefreshAsync([hostId, coveredId], NoBypass);

        Assert.Null(ReadyAt(hostId));
        Assert.Null(ReadyAt(coveredId));

        Execute("UPDATE review_queue SET status = 'Resolved' WHERE entity_id = @hostAsset", new { hostAsset });
        SetJob(hostAsset, IdentityJobState.RetailSearching, DateTimeOffset.UtcNow.AddSeconds(1));
        await _repo.RefreshAsync([hostId, coveredId], NoBypass);

        Assert.Null(ReadyAt(coveredId));
    }

    [Fact]
    public async Task FindTouched_ReturnsTheCoveredEpisode_WhenItsHostFilesJobChanges()
    {
        var showId = NewParent("TV");
        var (_, hostAsset) = NewWork("TV", showId, "child");
        var coveredId = NewCoveredWork("TV", showId, hostAsset);
        SetJob(hostAsset, IdentityJobState.Queued, DateTimeOffset.UtcNow.AddMinutes(-1));
        var (_, watermark) = await _repo.FindTouchedWorkIdsAsync(WorkReadinessWatermark.Empty);

        SetJob(hostAsset, IdentityJobState.RetailMatched, DateTimeOffset.UtcNow);
        var (touched, _) = await _repo.FindTouchedWorkIdsAsync(watermark);

        Assert.Contains(coveredId, touched);
    }

    private const string LocalLibrary = "11111111-aaaa-4aaa-8aaa-111111111111";
    private const string NormalLibrary = "22222222-bbbb-4bbb-8bbb-222222222222";

    [Fact]
    public async Task LocalOnlyLibraryWork_IsReady_WithArtworkSettledAndNoIdentityJob()
    {
        var (workId, assetId) = NewWork("Books");
        SetLibrary(assetId, LocalLibrary);
        SetCanonical(workId, "artwork_settled_at", DateTimeOffset.UtcNow.ToString("O"));

        var changes = await _repo.RefreshAsync([workId], [LocalLibrary]);

        Assert.NotNull(ReadyAt(workId));
        Assert.True(Assert.Single(changes).IsReady);
    }

    [Fact]
    public async Task LocalOnlyLibraryWork_IsNotReady_WhileAReviewIsPending()
    {
        var (workId, assetId) = NewWork("Books");
        SetLibrary(assetId, LocalLibrary);
        SetCanonical(workId, "artwork_settled_at", DateTimeOffset.UtcNow.ToString("O"));
        AddReview(assetId, "LowConfidence", "Pending");

        await _repo.RefreshAsync([workId], [LocalLibrary]);

        Assert.Null(ReadyAt(workId));
    }

    [Fact]
    public async Task LocalOnlyLibraryWork_IsNotReady_WithoutArtworkOrWhenRejected()
    {
        var (noArtId, noArtAsset) = NewWork("Books");
        SetLibrary(noArtAsset, LocalLibrary);
        var (rejectedId, rejectedAsset) = NewWork("Books");
        SetLibrary(rejectedAsset, LocalLibrary);
        SetCanonical(rejectedId, "cover_state", "present");
        Execute("UPDATE works SET curator_state = 'rejected' WHERE id = @rejectedId", new { rejectedId });

        await _repo.RefreshAllAsync([LocalLibrary]);

        Assert.Null(ReadyAt(noArtId));
        Assert.Null(ReadyAt(rejectedId));
    }

    [Fact]
    public async Task NormalLibraryWork_IsNotReady_WithoutAnIdentityJob_EvenWhenAnotherLibraryIsLocalOnly()
    {
        var (workId, assetId) = NewWork("Books");
        SetLibrary(assetId, NormalLibrary);
        SetCanonical(workId, "artwork_settled_at", DateTimeOffset.UtcNow.ToString("O"));

        await _repo.RefreshAsync([workId], [LocalLibrary]);

        Assert.Null(ReadyAt(workId));
    }

    [Fact]
    public async Task EmptyBypassList_LeavesEveryWorkNeedingAMatchedIdentityJob()
    {
        var (noJobId, noJobAsset) = NewWork("Books");
        SetLibrary(noJobAsset, LocalLibrary);
        SetCanonical(noJobId, "cover_state", "present");
        var (matchedId, matchedAsset) = NewWork("Books");
        SetLibrary(matchedAsset, NormalLibrary);
        SetCanonical(matchedId, "cover_state", "present");
        SetJob(matchedAsset, IdentityJobState.RetailMatched);

        await _repo.RefreshAllAsync([]);

        Assert.Null(ReadyAt(noJobId));
        Assert.NotNull(ReadyAt(matchedId));
    }

    [Fact]
    public async Task BypassLibraryIds_MatchRegardlessOfCase_AndALibraryDroppedFromTheListClearsItsWorks()
    {
        var (workId, assetId) = NewWork("Books");
        SetLibrary(assetId, LocalLibrary.ToUpperInvariant());
        SetCanonical(workId, "cover_state", "present");

        await _repo.RefreshAllAsync([LocalLibrary]);
        Assert.NotNull(ReadyAt(workId));

        var changes = await _repo.RefreshAllAsync([]);

        Assert.Null(ReadyAt(workId));
        Assert.False(Assert.Single(changes).IsReady);
    }

    [Fact]
    public async Task Refresh_IgnoresWorksThatAreNotLeaves()
    {
        var showId = NewParent("TV");
        SetCanonical(showId, "cover_state", "present");

        var changes = await _repo.RefreshAsync([showId], NoBypass);

        Assert.Empty(changes);
        Assert.Null(ReadyAt(showId));
    }

    [Fact]
    public async Task Refresh_ClearsTheMarkAndReportsIt_WhenAReadyWorkMovesBackIntoReview()
    {
        var (workId, assetId) = NewWork("Books");
        SetJob(assetId, IdentityJobState.RetailMatched);
        SetCanonical(assetId, "cover_state", "present");
        await _repo.RefreshAsync([workId], NoBypass);
        Assert.NotNull(ReadyAt(workId));

        SetJob(assetId, IdentityJobState.RetailMatchedNeedsReview, DateTimeOffset.UtcNow.AddSeconds(5));
        var changes = await _repo.RefreshAsync([workId], NoBypass);

        Assert.Null(ReadyAt(workId));
        var change = Assert.Single(changes);
        Assert.Equal(workId, change.WorkId);
        Assert.False(change.IsReady);
    }

    [Fact]
    public async Task Refresh_KeepsTheOriginalReadyTime_WhenAReadyWorkIsCheckedAgain()
    {
        var (workId, assetId) = NewWork("Books");
        SetJob(assetId, IdentityJobState.RetailMatched);
        SetCanonical(assetId, "cover_state", "present");
        await _repo.RefreshAsync([workId], NoBypass);
        var first = ReadyAt(workId);
        Assert.NotNull(first);

        await Task.Delay(20);
        var changes = await _repo.RefreshAsync([workId], NoBypass);
        var all = await _repo.RefreshAllAsync(NoBypass);

        Assert.Empty(changes);
        Assert.Empty(all);
        Assert.Equal(first, ReadyAt(workId));
    }

    [Fact]
    public async Task RefreshAll_MarksEveryLeafThatMeetsTheRuleAndSkipsTheRest()
    {
        var (readyId, readyAsset) = NewWork("Books");
        SetJob(readyAsset, IdentityJobState.RetailMatched);
        SetCanonical(readyAsset, "cover_state", "present");
        var (queuedId, queuedAsset) = NewWork("Books");
        SetJob(queuedAsset, IdentityJobState.Queued);
        SetCanonical(queuedAsset, "cover_state", "present");

        var changes = await _repo.RefreshAllAsync(NoBypass);

        var change = Assert.Single(changes);
        Assert.Equal(readyId, change.WorkId);
        Assert.Null(ReadyAt(queuedId));
    }

    [Fact]
    public async Task ReadyGate_ShowsAWorkOnlyWhileItIsMarkedAndItsFileIsVisible()
    {
        var (workId, assetId) = NewWork("Books");
        SetJob(assetId, IdentityJobState.RetailMatched);
        SetCanonical(assetId, "cover_state", "present");

        Assert.Equal(0, CountGated(workId));

        await _repo.RefreshAsync([workId], NoBypass);
        Assert.Equal(1, CountGated(workId));

        Execute("UPDATE media_assets SET file_path_root = '/library/.data/staging/x.epub' WHERE id = @assetId", new { assetId });
        Assert.Equal(0, CountGated(workId));
    }

    [Fact]
    public async Task ReadyGroupGate_ShowsAShowOnlyWhenOneOfItsEpisodesIsReady()
    {
        var showId = NewParent("TV");
        var seasonId = NewParent("TV", showId);
        var (episodeId, assetId) = NewWork("TV", seasonId, "child");
        SetJob(assetId, IdentityJobState.RetailMatched);
        SetCanonical(showId, "cover_state", "present");

        Assert.False(GroupIsReady(showId));

        await _repo.RefreshAsync([episodeId], NoBypass);

        Assert.True(GroupIsReady(showId));
        Assert.True(GroupIsReady(seasonId));
    }

    [Fact]
    public async Task FindTouched_ReturnsTheWork_WhenItsIdentityJobChangedAfterTheWatermark()
    {
        var (workId, assetId) = NewWork("Books");
        SetJob(assetId, IdentityJobState.Queued, DateTimeOffset.UtcNow.AddMinutes(-1));
        var (_, watermark) = await _repo.FindTouchedWorkIdsAsync(WorkReadinessWatermark.Empty);

        SetJob(assetId, IdentityJobState.RetailMatched, DateTimeOffset.UtcNow);
        var (touched, next) = await _repo.FindTouchedWorkIdsAsync(watermark);

        Assert.Equal(new[] { workId }, touched);
        var (again, _) = await _repo.FindTouchedWorkIdsAsync(next);
        Assert.Empty(again);
    }

    [Fact]
    public async Task FindTouched_ReturnsTheEpisodes_WhenACoverIsWrittenOnTheirShow()
    {
        var showId = NewParent("TV");
        var (episodeA, _) = NewWork("TV", showId, "child");
        var (episodeB, _) = NewWork("TV", showId, "child");
        var (_, watermark) = await _repo.FindTouchedWorkIdsAsync(WorkReadinessWatermark.Empty);

        SetCanonical(showId, "cover_url", "/stream/show/cover", DateTimeOffset.UtcNow.AddSeconds(1));
        var (touched, _) = await _repo.FindTouchedWorkIdsAsync(watermark);

        Assert.Equal(new HashSet<Guid> { episodeA, episodeB }, touched.ToHashSet());
    }

    [Fact]
    public async Task FindTouched_ReturnsTheWork_WhenACoverIsWrittenOnItsFileOrAReviewIsResolved()
    {
        var (workId, assetId) = NewWork("Books");
        var (otherId, otherAsset) = NewWork("Books");
        AddReview(otherAsset, "LowConfidence", "Pending");
        var (_, watermark) = await _repo.FindTouchedWorkIdsAsync(WorkReadinessWatermark.Empty);

        SetCanonical(assetId, "cover_state", "present", DateTimeOffset.UtcNow.AddSeconds(1));
        Execute(
            "UPDATE review_queue SET status = 'Resolved', resolved_at = @at WHERE entity_id = @otherAsset",
            new { at = DateTimeOffset.UtcNow.AddSeconds(2).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), otherAsset });
        var (touched, _) = await _repo.FindTouchedWorkIdsAsync(watermark);

        Assert.Equal(new HashSet<Guid> { workId, otherId }, touched.ToHashSet());
    }

    [Fact]
    public async Task FindTouched_ReturnsNothing_ForAnUnchangedLibrary()
    {
        var (_, assetId) = NewWork("Books");
        SetJob(assetId, IdentityJobState.RetailMatched);
        SetCanonical(assetId, "cover_state", "present");
        var (first, watermark) = await _repo.FindTouchedWorkIdsAsync(WorkReadinessWatermark.Empty);

        var (second, _) = await _repo.FindTouchedWorkIdsAsync(watermark);

        Assert.Empty(first);
        Assert.Empty(second);
    }

    [Fact]
    public async Task FindTouched_PicksUpTheFirstRows_WhenTheWatermarkWasTakenFromAnEmptyLibrary()
    {
        var (_, watermark) = await _repo.FindTouchedWorkIdsAsync(WorkReadinessWatermark.Empty);

        var (workId, assetId) = NewWork("Books");
        SetJob(assetId, IdentityJobState.Queued, DateTimeOffset.UtcNow);
        SetCanonical(assetId, "cover_state", "present", DateTimeOffset.UtcNow);
        var (touched, _) = await _repo.FindTouchedWorkIdsAsync(watermark);

        Assert.Equal(new[] { workId }, touched);
    }

    [Fact]
    public async Task FindMatchedAwaitingArtwork_ReturnsOnlyWorksThatAreMatchedAndPlacedButHaveNoArtworkYet()
    {
        var (waitingId, waitingAsset) = NewWork("Books");
        SetJob(waitingAsset, IdentityJobState.RetailMatched);
        var (queuedId, queuedAsset) = NewWork("Books");
        SetJob(queuedAsset, IdentityJobState.Queued);
        var (readyId, readyAsset) = NewWork("Books");
        SetJob(readyAsset, IdentityJobState.RetailMatched);
        SetCanonical(readyAsset, "cover_state", "present");

        var awaiting = await _repo.FindMatchedAwaitingArtworkAsync([waitingId, queuedId, readyId], NoBypass);

        Assert.Equal(new[] { waitingId }, awaiting);
    }

    [Fact]
    public async Task GetDetails_ReturnsTheTitleCollectionAndPrimaryFileOfEachWork()
    {
        var showId = NewParent("TV");
        SetCanonical(showId, "title", "The Show");
        var (episodeId, assetId) = NewWork("TV", showId, "child");
        var (bookId, bookAsset) = NewWork("Books");
        SetCanonical(bookAsset, "title", "Dune");

        var details = await _repo.GetDetailsAsync([episodeId, bookId]);

        Assert.Equal("The Show", details[episodeId].Title);
        Assert.Equal(assetId, details[episodeId].AssetId);
        Assert.Equal("TV", details[episodeId].MediaType);
        Assert.Equal("Dune", details[bookId].Title);
        Assert.Equal(bookAsset, details[bookId].AssetId);
        Assert.NotNull(details[bookId].CollectionId);
    }

    private (Guid WorkId, Guid AssetId) NewWork(string mediaType, Guid? parentWorkId = null, string workKind = "standalone")
    {
        var collectionId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        Execute("""
            INSERT INTO collections (id, created_at) VALUES (@collectionId, datetime('now'));
            INSERT INTO works (id, collection_id, media_type, work_kind, parent_work_id)
                VALUES (@workId, @collectionId, @mediaType, @workKind, @parentWorkId);
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
                workKind,
                parentWorkId,
                hash = $"hash_{assetId:N}",
                filePath = $"/library/{assetId:N}.bin",
            });
        return (workId, assetId);
    }

    // A work with no file of its own that a combined file (the host asset) covers.
    private Guid NewCoveredWork(string mediaType, Guid parentWorkId, Guid hostAssetId)
    {
        var workId = Guid.NewGuid();
        Execute("""
            INSERT INTO works (id, media_type, work_kind, parent_work_id) VALUES (@workId, @mediaType, 'child', @parentWorkId);
            INSERT INTO media_asset_coverage (asset_id, work_id, position, source) VALUES (@hostAssetId, @workId, 2, 'filename');
            """,
            new { workId, mediaType, parentWorkId, hostAssetId });
        return workId;
    }

    private void SetLibrary(Guid assetId, string libraryId) =>
        Execute("UPDATE media_assets SET library_id = @libraryId WHERE id = @assetId", new { libraryId, assetId });

    private Guid NewParent(string mediaType, Guid? parentWorkId = null)
    {
        var workId = Guid.NewGuid();
        Execute(
            "INSERT INTO works (id, media_type, work_kind, parent_work_id, parent_key) VALUES (@workId, @mediaType, 'parent', @parentWorkId, @key)",
            new { workId, mediaType, parentWorkId, key = $"parent:{workId:N}" });
        return workId;
    }

    private void SetJob(Guid assetId, IdentityJobState state, DateTimeOffset? updatedAt = null)
    {
        var at = (updatedAt ?? DateTimeOffset.UtcNow).ToString("O");
        using var conn = _db.CreateConnection();
        var updated = conn.Execute(
            "UPDATE identity_jobs SET state = @state, updated_at = @at WHERE entity_id = @assetId",
            new { state = state.ToString(), at, assetId });
        if (updated == 0)
        {
            conn.Execute("""
                INSERT INTO identity_jobs (id, entity_id, entity_type, media_type, state, created_at, updated_at)
                VALUES (@id, @assetId, 'MediaAsset', 'Books', @state, @at, @at)
                """,
                new { id = Guid.NewGuid(), assetId, state = state.ToString(), at });
        }
    }

    private void SetCanonical(Guid entityId, string key, string value, DateTimeOffset? at = null) =>
        Execute("""
            INSERT OR REPLACE INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@entityId, @key, @value, @at)
            """,
            new { entityId, key, value, at = (at ?? DateTimeOffset.UtcNow).ToString("O") });

    private void AddReview(Guid entityId, string trigger, string status) =>
        Execute("""
            INSERT INTO review_queue (id, entity_id, entity_type, trigger, status)
            VALUES (@id, @entityId, 'MediaAsset', @trigger, @status)
            """,
            new { id = Guid.NewGuid(), entityId, trigger, status });

    private string? ReadyAt(Guid workId)
    {
        using var conn = _db.CreateConnection();
        return conn.ExecuteScalar<string?>("SELECT ready_at FROM works WHERE id = @workId", new { workId });
    }

    private int CountGated(Guid workId)
    {
        using var conn = _db.CreateConnection();
        return conn.ExecuteScalar<int>(
            $"SELECT COUNT(*) FROM works w WHERE w.id = @workId AND {WorkReadinessSql.ReadyWorkPredicate("w")}",
            new { workId });
    }

    private bool GroupIsReady(Guid groupId)
    {
        using var conn = _db.CreateConnection();
        return conn.ExecuteScalar<int>(
            $"SELECT COUNT(*) FROM works g WHERE g.id = @groupId AND {WorkReadinessSql.ReadyGroupPredicate("g")}",
            new { groupId }) == 1;
    }

    private void Execute(string sql, object parameters)
    {
        using var conn = _db.CreateConnection();
        conn.Execute(sql, parameters);
    }
}
