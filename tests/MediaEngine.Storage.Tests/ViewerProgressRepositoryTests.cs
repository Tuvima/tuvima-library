using Dapper;
using MediaEngine.Storage;

namespace MediaEngine.Storage.Tests;

public sealed class ViewerProgressRepositoryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly ViewerProgressRepository _repository;

    public ViewerProgressRepositoryTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_viewer_progress_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _repository = new ViewerProgressRepository(_database);
    }

    [Fact]
    public async Task StartedWorks_ListsEachWorkTheProfileHasStarted_WithItsFurthestProgress()
    {
        var profile = SeedProfile();
        var other = SeedProfile();
        var (episodeOne, assetOne) = SeedWork("child", null);
        var (episodeTwo, assetTwo) = SeedWork("child", null);
        var (notStarted, assetNotStarted) = SeedWork("child", null);
        SetProgress(profile, assetOne, 100);
        SetProgress(profile, assetTwo, 12.5);
        SetProgress(profile, assetNotStarted, 0);
        SetProgress(other, assetNotStarted, 80);

        var started = await _repository.GetStartedWorksAsync(profile);

        Assert.Equal(
            new[] { episodeOne, episodeTwo }.Order(),
            started.Select(work => work.WorkId).Order());
        Assert.Equal(100, started.Single(work => work.WorkId == episodeOne).ProgressPct);
        Assert.Equal(12.5, started.Single(work => work.WorkId == episodeTwo).ProgressPct);
        Assert.DoesNotContain(started, work => work.WorkId == notStarted);
    }

    [Fact]
    public async Task StartedWorks_AreEmpty_ForAProfileThatHasStartedNothing()
    {
        SeedWork("standalone", null);

        Assert.Empty(await _repository.GetStartedWorksAsync(SeedProfile()));
    }

    [Fact]
    public async Task ChildWorkQids_ReturnOnlyTheIdentitiesOfEpisodeLevelWorks()
    {
        SeedWork("child", "Q100");
        SeedWork("parent", "Q200");
        SeedWork("standalone", "Q300");
        SeedWork("child", null);

        var found = await _repository.GetChildWorkQidsAsync(["Q100", "Q200", "Q300", "Q400", " "]);

        Assert.Equal(["Q100"], found.Order());
    }

    [Fact]
    public async Task WorkQids_ReturnTheOwnIdentityOrTheStoredValue_AndLeaveOutWorksWithNone()
    {
        var (own, _) = SeedWork("parent", "Q500");
        var (stored, _) = SeedWork("parent", null);
        var (none, _) = SeedWork("parent", null);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@stored, 'wikidata_qid', 'Q600', CURRENT_TIMESTAMP);",
                new { stored });
        }

        var found = await _repository.GetWorkQidsAsync([own, stored, none, Guid.NewGuid(), Guid.Empty]);

        Assert.Equal(2, found.Count);
        Assert.Equal("Q500", found[own]);
        Assert.Equal("Q600", found[stored]);
    }

    [Fact]
    public async Task ChildWorkQids_AreEmpty_WhenNoneIsAsked()
    {
        Assert.Empty(await _repository.GetChildWorkQidsAsync([]));
    }

    private Guid SeedProfile()
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO profiles (id, display_name, role, created_at) VALUES (@id, 'Viewer', 'Administrator', @createdAt);",
            new { id, createdAt = DateTimeOffset.UtcNow.ToString("O") });
        return id;
    }

    private (Guid Work, Guid Asset) SeedWork(string kind, string? qid)
    {
        var work = Guid.NewGuid();
        var edition = Guid.NewGuid();
        var asset = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO works (id, media_type, work_kind, wikidata_qid) VALUES (@work, 'TV', @kind, @qid);
            INSERT INTO editions (id, work_id) VALUES (@edition, @work);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
            VALUES (@asset, @edition, @hash, @path);
            """,
            new { work, edition, asset, kind, qid, hash = asset.ToString("N"), path = $"C:/library/{asset:N}.mkv" });
        return (work, asset);
    }

    private void SetProgress(Guid profile, Guid asset, double percent)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO user_states (user_id, asset_id, content_hash, progress_pct, last_accessed, extended_properties, revision)
            VALUES (@profile, @asset, @hash, @percent, CURRENT_TIMESTAMP, '{}', 0);
            """,
            new { profile, asset, hash = asset.ToString("N"), percent });
    }

    public void Dispose()
    {
        _database.Dispose();
        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a test-owned temporary database.
        }
    }
}
