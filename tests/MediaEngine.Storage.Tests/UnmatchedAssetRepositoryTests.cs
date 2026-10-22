using Dapper;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;

namespace MediaEngine.Storage.Tests;

public sealed class UnmatchedAssetRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_unmatched_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly UnmatchedAssetRepository _unmatched;
    private readonly IdentityJobRepository _jobs;

    public UnmatchedAssetRepositoryTests()
    {
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _unmatched = new UnmatchedAssetRepository(_db);
        _jobs = new IdentityJobRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        DatabaseConnection.ReleasePooledConnections(_dbPath);
        File.Delete(_dbPath);
    }

    [Fact]
    public async Task MarkUnmatched_NewAsset_IsStoredWithItsReasonAndNextRetry()
    {
        var asset = AddAsset("movies", "Dune.mkv");

        await _unmatched.MarkUnmatchedAsync(asset, "movies", UnmatchedReasons.NoMatch, Now, Now.AddDays(7));

        var row = await _unmatched.GetAsync(asset);
        Assert.NotNull(row);
        Assert.Equal("movies", row.LibraryId);
        Assert.Equal(UnmatchedReasons.NoMatch, row.Reason);
        Assert.Equal(Now, row.FirstUnmatchedAt);
        Assert.Equal(Now.AddDays(7), row.NextRetryAt);
        Assert.False(row.KeptAsIs);
    }

    [Fact]
    public async Task MarkUnmatched_AgainLater_KeepsWhenItFirstBecameUnmatched()
    {
        var asset = AddAsset("movies", "Dune.mkv");
        await _unmatched.MarkUnmatchedAsync(asset, "movies", UnmatchedReasons.NoMatch, Now, Now.AddDays(7));

        await _unmatched.MarkUnmatchedAsync(asset, "movies", UnmatchedReasons.NoMatch, Now.AddDays(7), Now.AddDays(14));

        var row = await _unmatched.GetAsync(asset);
        Assert.Equal(Now, row!.FirstUnmatchedAt);
        Assert.Equal(Now.AddDays(7), row.LastTriedAt);
        Assert.Equal(Now.AddDays(14), row.NextRetryAt);
    }

    [Fact]
    public async Task MarkUnmatched_AKeptItem_StaysKeptAndHasNoNextRetry()
    {
        var asset = AddAsset("movies", "Dune.mkv");
        await _unmatched.MarkUnmatchedAsync(asset, "movies", UnmatchedReasons.NoMatch, Now, Now.AddDays(7));
        await _unmatched.KeepAsIsAsync(asset, Now);

        await _unmatched.MarkUnmatchedAsync(asset, "movies", UnmatchedReasons.NoMatch, Now.AddDays(1), Now.AddDays(8));

        var row = await _unmatched.GetAsync(asset);
        Assert.True(row!.KeptAsIs);
        Assert.Null(row.NextRetryAt);
    }

    [Fact]
    public async Task KeepAsIs_TakesTheItemOutOfTheCountAndTheDueList()
    {
        var kept = AddAsset("movies", "Kept.mkv");
        var open = AddAsset("movies", "Open.mkv");
        await _unmatched.MarkUnmatchedAsync(kept, "movies", UnmatchedReasons.NoMatch, Now, Now);
        await _unmatched.MarkUnmatchedAsync(open, "movies", UnmatchedReasons.NoMatch, Now, Now);

        await _unmatched.KeepAsIsAsync(kept, Now);

        Assert.Equal(1, (await _unmatched.CountByLibraryAsync())["movies"]);
        var due = await _unmatched.ListDueAsync(Now.AddDays(30), 10);
        Assert.Equal(open, Assert.Single(due).AssetId);
    }

    [Fact]
    public async Task KeepAllAsIs_KeepsEveryItemOfThatLibraryOnly()
    {
        var movieOne = AddAsset("movies", "One.mkv");
        var movieTwo = AddAsset("movies", "Two.mkv");
        var show = AddAsset("tv", "Show.mkv");
        foreach (var (asset, library) in new[] { (movieOne, "movies"), (movieTwo, "movies"), (show, "tv") })
        {
            await _unmatched.MarkUnmatchedAsync(asset, library, UnmatchedReasons.NoMatch, Now, Now);
        }

        var kept = await _unmatched.KeepAllAsIsAsync("movies", Now);

        Assert.Equal(2, kept);
        var counts = await _unmatched.CountByLibraryAsync();
        Assert.False(counts.ContainsKey("movies"));
        Assert.Equal(1, counts["tv"]);
    }

    [Fact]
    public async Task LookUpAgain_ClearsTheKeptMarkAndMakesTheItemDue()
    {
        var asset = AddAsset("movies", "Dune.mkv");
        await _unmatched.MarkUnmatchedAsync(asset, "movies", UnmatchedReasons.NoMatch, Now, Now.AddDays(7));
        await _unmatched.KeepAsIsAsync(asset, Now);

        var found = await _unmatched.LookUpAgainAsync(asset, Now.AddDays(1));

        Assert.True(found);
        var row = await _unmatched.GetAsync(asset);
        Assert.False(row!.KeptAsIs);
        Assert.Null(row.KeptAt);
        Assert.Equal(asset, Assert.Single(await _unmatched.ListDueAsync(Now.AddDays(1), 10)).AssetId);
    }

    [Fact]
    public async Task MakeDue_AKeptItem_IsNotMadeDue()
    {
        var asset = AddAsset("movies", "Dune.mkv");
        await _unmatched.MarkUnmatchedAsync(asset, "movies", UnmatchedReasons.NoMatch, Now, Now.AddDays(7));
        await _unmatched.KeepAsIsAsync(asset, Now);

        Assert.False(await _unmatched.MakeDueAsync(asset, Now));
        Assert.Equal(0, await _unmatched.MakeAllDueAsync("movies", Now));
        Assert.Empty(await _unmatched.ListDueAsync(Now.AddDays(30), 10));
    }

    [Fact]
    public async Task MakeAllDue_OneLibrary_LeavesTheOthersWaitingForTheirSchedule()
    {
        var movie = AddAsset("movies", "One.mkv");
        var show = AddAsset("tv", "Show.mkv");
        await _unmatched.MarkUnmatchedAsync(movie, "movies", UnmatchedReasons.NoMatch, Now, Now.AddDays(7));
        await _unmatched.MarkUnmatchedAsync(show, "tv", UnmatchedReasons.NoMatch, Now, Now.AddDays(7));

        var made = await _unmatched.MakeAllDueAsync("movies", Now.AddHours(1));

        Assert.Equal(1, made);
        Assert.Equal(movie, Assert.Single(await _unmatched.ListDueAsync(Now.AddHours(1), 10)).AssetId);
    }

    [Fact]
    public async Task ListDue_NothingIsDueYet_ReturnsNothing()
    {
        var asset = AddAsset("movies", "Dune.mkv");
        await _unmatched.MarkUnmatchedAsync(asset, "movies", UnmatchedReasons.NoMatch, Now, Now.AddDays(7));

        Assert.Empty(await _unmatched.ListDueAsync(Now.AddDays(6), 10));
        Assert.Single(await _unmatched.ListDueAsync(Now.AddDays(7), 10));
    }

    [Fact]
    public async Task ListByLibrary_LeavesKeptItemsOutUnlessAsked_AndPagesNewestFirst()
    {
        var first = AddAsset("movies", "First.mkv");
        var second = AddAsset("movies", "Second.mkv");
        var kept = AddAsset("movies", "Kept.mkv");
        AddTitle(second, "Second Film");
        await _unmatched.MarkUnmatchedAsync(first, "movies", UnmatchedReasons.NoMatch, Now, Now);
        await _unmatched.MarkUnmatchedAsync(second, "movies", UnmatchedReasons.NoMatch, Now.AddHours(1), Now);
        await _unmatched.MarkUnmatchedAsync(kept, "movies", UnmatchedReasons.NoMatch, Now.AddHours(2), Now);
        await _unmatched.KeepAsIsAsync(kept, Now);

        var open = await _unmatched.ListByLibraryAsync("movies", includeKept: false, skip: 0, take: 1);
        var all = await _unmatched.ListByLibraryAsync("movies", includeKept: true, skip: 0, take: 10);

        Assert.Equal(2, open.Total);
        Assert.Equal(second, Assert.Single(open.Items).Asset.AssetId);
        Assert.Equal("Second Film", open.Items[0].Title);
        Assert.EndsWith("Second.mkv", open.Items[0].FilePath);
        Assert.Equal(3, all.Total);
        Assert.Equal(kept, all.Items[0].Asset.AssetId);
    }

    [Fact]
    public async Task DeletingTheAsset_RemovesItsUnmatchedRow()
    {
        var asset = AddAsset("movies", "Dune.mkv");
        await _unmatched.MarkUnmatchedAsync(asset, "movies", UnmatchedReasons.NoMatch, Now, Now);

        using (var conn = _db.CreateConnection())
        {
            conn.Execute("DELETE FROM media_assets WHERE id = @asset;", new { asset });
        }

        Assert.Null(await _unmatched.GetAsync(asset));
    }

    [Theory]
    [InlineData(IdentityJobState.RetailMatched)]
    [InlineData(IdentityJobState.RetailMatchedNeedsReview)]
    [InlineData(IdentityJobState.ReadyWithoutUniverse)]
    public async Task AJobThatMatches_TakesItsFileOutOfUnmatched(IdentityJobState outcome)
    {
        var (asset, job) = await UnmatchedAssetWithJobAsync();

        await _jobs.UpdateStateAsync(job.Id, outcome);

        Assert.Null(await _unmatched.GetAsync(asset));
    }

    [Theory]
    [InlineData(IdentityJobState.Queued)]
    [InlineData(IdentityJobState.RetailSearching)]
    [InlineData(IdentityJobState.RetailNoMatch)]
    [InlineData(IdentityJobState.Failed)]
    public async Task AJobThatHasNotMatched_LeavesItsFileUnmatched(IdentityJobState state)
    {
        var (asset, job) = await UnmatchedAssetWithJobAsync();

        await _jobs.UpdateStateAsync(job.Id, state);

        Assert.NotNull(await _unmatched.GetAsync(asset));
    }

    [Fact]
    public async Task APersonChoosingTheMatch_TakesTheFileOutOfUnmatched()
    {
        var asset = AddAsset("movies", "Dune.mkv");
        await _unmatched.MarkUnmatchedAsync(asset, "movies", UnmatchedReasons.NoMatch, Now, Now);

        await _jobs.CreateOrResumeUserResolutionAsync(new IdentityJob
        {
            EntityId = asset,
            EntityType = "MediaAsset",
            MediaType = "Movies",
            State = nameof(IdentityJobState.RetailMatched),
        });

        Assert.Null(await _unmatched.GetAsync(asset));
    }

    private async Task<(Guid Asset, IdentityJob Job)> UnmatchedAssetWithJobAsync()
    {
        var asset = AddAsset("movies", "Dune.mkv");
        var job = new IdentityJob { EntityId = asset, EntityType = "MediaAsset", MediaType = "Movies" };
        await _jobs.CreateAsync(job);
        await _jobs.UpdateStateAsync(job.Id, IdentityJobState.RetailNoMatch);
        await _unmatched.MarkUnmatchedAsync(asset, "movies", UnmatchedReasons.NoMatch, Now, Now);
        return (asset, job);
    }

    private Guid AddAsset(string libraryId, string fileName)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute(
            """
            INSERT INTO works (id, media_type) VALUES (@workId, 'Movies');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, library_id)
            VALUES (@assetId, @editionId, @contentHash, @path, @libraryId);
            """,
            new { workId, editionId, assetId, contentHash = Guid.NewGuid().ToString("N"), path = $"/media/{libraryId}/{fileName}", libraryId });
        return assetId;
    }

    private void AddTitle(Guid assetId, string title)
    {
        using var conn = _db.CreateConnection();
        conn.Execute(
            "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@assetId, 'title', @title, @at);",
            new { assetId, title, at = Now.ToString("O") });
    }
}
