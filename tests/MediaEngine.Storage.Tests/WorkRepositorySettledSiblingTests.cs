using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// <see cref="WorkRepository.FindConfirmedSiblingQidAsync"/> with <c>requireSettledIdentity</c>: an owned book
/// only counts when it is not waiting in Needs Review for its identity. Without the option the earlier,
/// broader behaviour is unchanged. Real SQLite, no mocks.
/// </summary>
public sealed class WorkRepositorySettledSiblingTests : IDisposable
{
    private const string Qid = "Q190192";

    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly WorkRepository _works;
    private readonly IdentityJobRepository _jobs;
    private readonly ReviewQueueRepository _reviews;

    public WorkRepositorySettledSiblingTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_settled_sibling_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();

        _works = new WorkRepository(_db);
        _jobs = new IdentityJobRepository(_db);
        _reviews = new ReviewQueueRepository(_db);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch (Exception) { /* temp store: cleanup is best effort */ }
        try { File.Delete(_dbPath); } catch (Exception) { /* best effort */ }
    }

    [Fact]
    public async Task SettledBook_MatchesWhenSettledIdentityIsRequired()
    {
        var book = await SeedOwnedBookAsync();
        await CreateJobAsync(book.AssetId, IdentityJobState.Ready);

        var match = await FindAsync(requireSettledIdentity: true);

        Assert.NotNull(match);
        Assert.Equal(book.WorkId, match!.WorkId);
        Assert.Equal(Qid, match.WikidataQid);
    }

    [Fact]
    public async Task BookWithNoIdentityJobAndNoReviewItem_MatchesWhenSettledIdentityIsRequired()
    {
        await SeedOwnedBookAsync();

        Assert.NotNull(await FindAsync(requireSettledIdentity: true));
    }

    [Theory]
    [InlineData(IdentityJobState.RetailMatchedNeedsReview)]
    [InlineData(IdentityJobState.QidNeedsReview)]
    public async Task BookWaitingInNeedsReview_DoesNotMatchWhenSettledIdentityIsRequired_ButStillMatchesByDefault(
        IdentityJobState state)
    {
        var book = await SeedOwnedBookAsync();
        await CreateJobAsync(book.AssetId, state);

        Assert.Null(await FindAsync(requireSettledIdentity: true));
        Assert.NotNull(await FindAsync(requireSettledIdentity: false));
    }

    [Theory]
    [InlineData(ReviewTrigger.RetailMatchFailed)]
    [InlineData(ReviewTrigger.RetailMatchAmbiguous)]
    [InlineData(ReviewTrigger.LowConfidence)]
    [InlineData(ReviewTrigger.MultipleQidMatches)]
    [InlineData(ReviewTrigger.WikidataBridgeFailed)]
    [InlineData(ReviewTrigger.UserFixMatch)]
    public async Task BookWithAnOpenIdentityReviewItem_DoesNotMatchWhenSettledIdentityIsRequired_ButStillMatchesByDefault(
        string trigger)
    {
        var book = await SeedOwnedBookAsync();
        await _reviews.InsertAsync(new ReviewQueueEntry { EntityId = book.AssetId, EntityType = "MediaAsset", Trigger = trigger });

        Assert.Null(await FindAsync(requireSettledIdentity: true));
        Assert.NotNull(await FindAsync(requireSettledIdentity: false));
    }

    [Fact]
    public async Task BookWhoseIdentityReviewItemWasResolved_MatchesWhenSettledIdentityIsRequired()
    {
        var book = await SeedOwnedBookAsync();
        var reviewId = await _reviews.InsertAsync(new ReviewQueueEntry
        {
            EntityId = book.AssetId,
            EntityType = "MediaAsset",
            Trigger = ReviewTrigger.LowConfidence,
        });
        await _reviews.UpdateStatusAsync(reviewId, ReviewStatus.Resolved, "test");

        Assert.NotNull(await FindAsync(requireSettledIdentity: true));
    }

    [Theory]
    [InlineData(ReviewTrigger.WritebackFailed)]
    [InlineData(ReviewTrigger.MediaInspectionFailed)]
    public async Task BookWithOnlyAFileHandlingReviewItem_MatchesWhenSettledIdentityIsRequired(string trigger)
    {
        var book = await SeedOwnedBookAsync();
        await _reviews.InsertAsync(new ReviewQueueEntry { EntityId = book.AssetId, EntityType = "MediaAsset", Trigger = trigger });

        Assert.NotNull(await FindAsync(requireSettledIdentity: true));
    }

    [Theory]
    [InlineData(ReviewTrigger.AmbiguousMediaType)]
    [InlineData(ReviewTrigger.PlaceholderTitle)]
    [InlineData(ReviewTrigger.LanguageMismatch)]
    [InlineData(ReviewTrigger.MetadataConflict)]
    [InlineData(ReviewTrigger.NameCollision)]
    [InlineData(ReviewTrigger.StagedUnidentifiable)]
    [InlineData(ReviewTrigger.MissingQid)]
    public async Task BookWithAnyOtherOpenReviewItem_DoesNotMatchWhenSettledIdentityIsRequired(string trigger)
    {
        var book = await SeedOwnedBookAsync();
        await _reviews.InsertAsync(new ReviewQueueEntry { EntityId = book.AssetId, EntityType = "MediaAsset", Trigger = trigger });

        Assert.Null(await FindAsync(requireSettledIdentity: true));
        Assert.NotNull(await FindAsync(requireSettledIdentity: false));
    }

    [Theory]
    [InlineData(IdentityJobState.Queued)]
    [InlineData(IdentityJobState.RetailSearching)]
    [InlineData(IdentityJobState.RetailMatched)]
    [InlineData(IdentityJobState.BridgeSearching)]
    [InlineData(IdentityJobState.QidNoMatch)]
    [InlineData(IdentityJobState.Failed)]
    public async Task BookWhoseLatestIdentityJobIsNotSettled_DoesNotMatchWhenSettledIdentityIsRequired_ButStillMatchesByDefault(
        IdentityJobState state)
    {
        var book = await SeedOwnedBookAsync();
        await CreateJobAsync(book.AssetId, state);

        Assert.Null(await FindAsync(requireSettledIdentity: true));
        Assert.NotNull(await FindAsync(requireSettledIdentity: false));
    }

    [Theory]
    [InlineData(IdentityJobState.QidResolved)]
    [InlineData(IdentityJobState.Hydrating)]
    [InlineData(IdentityJobState.UniverseEnriching)]
    [InlineData(IdentityJobState.Ready)]
    [InlineData(IdentityJobState.ReadyWithoutUniverse)]
    public async Task BookWhoseLatestIdentityJobIsSettled_MatchesWhenSettledIdentityIsRequired(IdentityJobState state)
    {
        var book = await SeedOwnedBookAsync();
        await CreateJobAsync(book.AssetId, state);

        Assert.NotNull(await FindAsync(requireSettledIdentity: true));
    }

    [Fact]
    public async Task BookWhoseEarlierJobWaitedInNeedsReviewButWhoseLatestJobIsReady_MatchesWhenSettledIdentityIsRequired()
    {
        var book = await SeedOwnedBookAsync();
        await CreateJobAsync(book.AssetId, IdentityJobState.QidNeedsReview, DateTimeOffset.UtcNow.AddHours(-2));
        await CreateJobAsync(book.AssetId, IdentityJobState.Ready, DateTimeOffset.UtcNow.AddHours(-1));

        Assert.NotNull(await FindAsync(requireSettledIdentity: true));
    }

    [Fact]
    public async Task BookWhoseLatestJobIsInProgressAfterAnEarlierReadyJob_DoesNotMatchWhenSettledIdentityIsRequired()
    {
        var book = await SeedOwnedBookAsync();
        await CreateJobAsync(book.AssetId, IdentityJobState.Ready, DateTimeOffset.UtcNow.AddHours(-2));
        await CreateJobAsync(book.AssetId, IdentityJobState.BridgeSearching, DateTimeOffset.UtcNow.AddHours(-1));

        Assert.Null(await FindAsync(requireSettledIdentity: true));
    }

    private Task<ConfirmedSiblingWorkQid?> FindAsync(bool requireSettledIdentity) =>
        _works.FindConfirmedSiblingQidAsync(
            MediaType.Audiobooks,
            [MediaType.Books],
            "Dune",
            "Frank Herbert",
            requireSettledIdentity: requireSettledIdentity);

    private Task CreateJobAsync(Guid assetId, IdentityJobState state, DateTimeOffset? updatedAt = null) =>
        _jobs.CreateAsync(new IdentityJob
        {
            Id = Guid.NewGuid(),
            EntityId = assetId,
            EntityType = "MediaAsset",
            MediaType = "Books",
            State = state.ToString(),
            UpdatedAt = updatedAt ?? DateTimeOffset.UtcNow,
        });

    private async Task<(Guid WorkId, Guid AssetId)> SeedOwnedBookAsync()
    {
        var workId = await _works.InsertStandaloneAsync(MediaType.Books);
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using (var conn = _db.CreateConnection())
        {
            conn.Execute(
                "INSERT INTO editions (id, work_id, format_label) VALUES (@editionId, @workId, 'Test')",
                new { editionId, workId });
            conn.Execute(
                "INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status) VALUES (@assetId, @editionId, @hash, @path, 'Normal')",
                new
                {
                    assetId,
                    editionId,
                    hash = Guid.NewGuid().ToString("N"),
                    path = Path.Combine(Path.GetTempPath(), $"{assetId:N}.media"),
                });
        }

        await new CanonicalValueRepository(_db).UpsertBatchAsync(
        [
            new CanonicalValue { EntityId = assetId, Key = MetadataFieldConstants.Title, Value = "Dune", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = assetId, Key = BridgeIdKeys.WikidataQid, Value = Qid, LastScoredAt = DateTimeOffset.UtcNow },
        ]);
        await new CanonicalValueArrayRepository(_db).SetValuesAsync(
            assetId,
            MetadataFieldConstants.Author,
            [new CanonicalArrayEntry { Ordinal = 0, Value = "Frank Herbert" }]);

        return (workId, assetId);
    }
}
