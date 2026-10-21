using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage.Services;

namespace MediaEngine.Storage.Tests;

/// <summary>Moving a video into a series, or out of one, in Library's own data: no file moves, positions stay unique.</summary>
public sealed class VideoSeriesRepositoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_video_series_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly HierarchyResolver _resolver;
    private readonly VideoSeriesRepository _series;
    private readonly VideoOrderRepository _order;

    public VideoSeriesRepositoryTests()
    {
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _resolver = new HierarchyResolver(new WorkRepository(_db));
        _series = new VideoSeriesRepository(_db);
        _order = new VideoOrderRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        File.Delete(_dbPath);
    }

    private async Task<(Guid Work, Guid Asset, Guid? Parent)> AddVideoAsync(string title, string? seriesKey = null, int episode = 1, string? section = null, int sectionIndex = 1, string? library = null)
    {
        var meta = new Dictionary<string, string>();
        if (seriesKey is not null)
        {
            meta[MetadataFieldConstants.VideoSeriesKey] = seriesKey;
            meta[MetadataFieldConstants.VideoEpisodeIndex] = episode.ToString();
        }

        if (section is not null)
        {
            meta[MetadataFieldConstants.VideoSection] = section;
            meta[MetadataFieldConstants.VideoSectionIndex] = sectionIndex.ToString();
        }

        var placed = await _resolver.ResolveAsync(MediaType.Videos, meta);
        var edition = Guid.NewGuid();
        var asset = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute("INSERT INTO editions (id, work_id) VALUES (@edition, @work);", new { edition, work = placed.WorkId });
        conn.Execute(
            "INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status, library_id) VALUES (@asset, @edition, @hash, @path, 'Normal', @library);",
            new { asset, edition, hash = asset.ToString("N"), path = $"clips/{title}.mp4", library });
        conn.Execute(
            "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@asset, 'title', @title, '2026-01-01');",
            new { asset, title });
        return (placed.WorkId, asset, placed.ParentWorkId);
    }

    private void Title(Guid workId, string title)
    {
        using var conn = _db.CreateConnection();
        conn.Execute(
            "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@workId, 'title', @title, '2026-01-01');",
            new { workId, title });
    }

    [Fact]
    public async Task Promote_PutsAStandaloneVideoLastInTheSeries_AndTheOrderShowsIt()
    {
        var first = await AddVideoAsync("Intro", "course", 1);
        await AddVideoAsync("Basics", "course", 2);
        Title(first.Parent!.Value, "Course");
        var loose = await AddVideoAsync("Bonus");

        var change = await _series.PromoteAsync(loose.Work, first.Parent.Value);

        Assert.Equal(VideoSeriesOutcome.Done, change.Outcome);
        Assert.Equal(3, change.Position);
        Assert.Equal(loose.Asset, change.AssetId);
        Assert.Equal("Course", change.SeriesTitle);
        var group = Assert.Single(await _order.LoadAsync(loose.Work));
        Assert.Equal(["Intro", "Basics", "Bonus"], group.Entries.Select(entry => entry.Title));
    }

    [Fact]
    public async Task Promote_TwoVideosInARow_GetTheNextTwoFreePositions()
    {
        var first = await AddVideoAsync("Intro", "course", 1);
        var one = await AddVideoAsync("One");
        var two = await AddVideoAsync("Two");

        Assert.Equal(2, (await _series.PromoteAsync(one.Work, first.Parent!.Value)).Position);
        Assert.Equal(3, (await _series.PromoteAsync(two.Work, first.Parent.Value)).Position);
    }

    [Fact]
    public async Task Promote_RefusesAVideoThatIsAlreadyInASeries_AndAParentWithSections()
    {
        var inSeries = await AddVideoAsync("Intro", "course", 1);
        var sectioned = await AddVideoAsync("Welcome", "guide", 1, "Getting started");
        var loose = await AddVideoAsync("Bonus");
        var sectionedSeries = sectioned.Parent!;
        using (var conn = _db.CreateConnection())
        {
            sectionedSeries = conn.QuerySingle<Guid>("SELECT parent_work_id FROM works WHERE id = @id;", new { id = sectioned.Parent!.Value });
        }

        Assert.Equal(VideoSeriesOutcome.AlreadyInSeries, (await _series.PromoteAsync(inSeries.Work, sectioned.Parent!.Value)).Outcome);
        Assert.Equal(VideoSeriesOutcome.TargetUnavailable, (await _series.PromoteAsync(loose.Work, sectionedSeries.Value)).Outcome);
        Assert.Equal(VideoSeriesOutcome.TargetUnavailable, (await _series.PromoteAsync(loose.Work, Guid.NewGuid())).Outcome);
        Assert.Equal(VideoSeriesOutcome.NotAVideo, (await _series.PromoteAsync(Guid.NewGuid(), inSeries.Parent!.Value)).Outcome);
    }

    [Fact]
    public async Task PromoteToNewSeries_StartsOneSeries_AndALaterVideoWithTheSameNameJoinsIt()
    {
        var one = await AddVideoAsync("One");
        var two = await AddVideoAsync("Two");

        var first = await _series.PromoteToNewSeriesAsync(one.Work, "  Road   Trip ");
        var second = await _series.PromoteToNewSeriesAsync(two.Work, "road trip");

        Assert.Equal(VideoSeriesOutcome.Done, first.Outcome);
        Assert.True(first.CreatedSeries);
        Assert.Equal("Road Trip", first.SeriesTitle);
        Assert.False(second.CreatedSeries);
        Assert.Equal(first.ParentWorkId, second.ParentWorkId);
        Assert.Equal(2, second.Position);
    }

    [Fact]
    public async Task PromoteToNewSeries_NeedsAName()
    {
        var loose = await AddVideoAsync("Bonus");

        Assert.Equal(VideoSeriesOutcome.NameRequired, (await _series.PromoteToNewSeriesAsync(loose.Work, "   ")).Outcome);
    }

    [Fact]
    public async Task Remove_MakesTheVideoStandAlone_AndTheOthersKeepTheirOrder()
    {
        var first = await AddVideoAsync("Intro", "course", 1);
        var middle = await AddVideoAsync("Basics", "course", 2);
        await AddVideoAsync("Advanced", "course", 3);

        var change = await _series.RemoveAsync(middle.Work);

        Assert.Equal(VideoSeriesOutcome.Done, change.Outcome);
        Assert.Equal(middle.Asset, change.AssetId);
        var membership = await _series.GetMembershipAsync(middle.Work);
        Assert.Null(membership!.ParentWorkId);
        var group = Assert.Single(await _order.LoadAsync(first.Work));
        Assert.Equal(["Intro", "Advanced"], group.Entries.Select(entry => entry.Title));
        Assert.Equal(VideoSeriesOutcome.NotInSeries, (await _series.RemoveAsync(middle.Work)).Outcome);
    }

    [Fact]
    public async Task Remove_ThenPromoteAgain_DoesNotClashWithAnExistingPosition()
    {
        var first = await AddVideoAsync("Intro", "course", 1);
        var second = await AddVideoAsync("Basics", "course", 2);
        await _series.RemoveAsync(first.Work);

        var change = await _series.PromoteAsync(first.Work, second.Parent!.Value);

        Assert.Equal(VideoSeriesOutcome.Done, change.Outcome);
        Assert.Equal(3, change.Position);
    }

    [Fact]
    public async Task ListTargets_GivesSeriesAndSectionsThatTakeVideosDirectly_NotASeriesMadeOfSections()
    {
        var plain = await AddVideoAsync("Intro", "course", 1);
        Title(plain.Parent!.Value, "Course");
        var sectioned = await AddVideoAsync("Welcome", "guide", 1, "Getting started");
        var seriesOfSections = await SeriesOfAsync(sectioned.Parent!.Value);
        Title(seriesOfSections, "Guide");
        Title(sectioned.Parent!.Value, "Getting started");
        var loose = await AddVideoAsync("Bonus");

        var targets = await _series.ListTargetsAsync(loose.Work);

        Assert.Equal(["Course", "Guide: Getting started"], targets.Select(target => target.Title));
        Assert.DoesNotContain(targets, target => target.ParentWorkId == seriesOfSections);
        Assert.Equal([1, 1], targets.Select(target => target.VideoCount));
    }

    [Fact]
    public async Task GetMembership_OfAnythingThatIsNotAVideo_IsNull()
    {
        Assert.Null(await _series.GetMembershipAsync(Guid.NewGuid()));
    }

    private int CurrentClaims(Guid asset, bool lockedOnly = false)
    {
        using var conn = _db.CreateConnection();
        return conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM metadata_claims WHERE entity_id = @asset AND is_current = 1 AND claim_key IN @keys"
            + (lockedOnly ? " AND is_user_locked = 1;" : ";"),
            new { asset, keys = SeriesKeys });
    }

    private int CanonicalRows(Guid asset)
    {
        using var conn = _db.CreateConnection();
        return conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM canonical_values WHERE entity_id = @asset AND key IN @keys;", new { asset, keys = SeriesKeys });
    }

    private static readonly string[] SeriesKeys =
    [
        MetadataFieldConstants.VideoSeries, MetadataFieldConstants.ShowName, MetadataFieldConstants.SeasonNumber,
        MetadataFieldConstants.EpisodeNumber, MetadataFieldConstants.VideoEpisodeIndex,
    ];

    private void Execute(string sql)
    {
        using var conn = _db.CreateConnection();
        conn.Execute(sql);
    }

    [Fact]
    public async Task Promote_SavesTheLockedSeriesFieldsWithTheMove()
    {
        var first = await AddVideoAsync("Intro", "course", 1);
        Title(first.Parent!.Value, "Course");
        var loose = await AddVideoAsync("Bonus");

        await _series.PromoteAsync(loose.Work, first.Parent.Value);

        Assert.Equal(5, CurrentClaims(loose.Asset, lockedOnly: true));
        Assert.Equal(5, CanonicalRows(loose.Asset));
    }

    [Fact]
    public async Task Remove_ClearsTheLockedFieldsAndTheScanClaims_SoNoSeriesIsLeftToResolve()
    {
        var first = await AddVideoAsync("Intro", "course", 1);
        Title(first.Parent!.Value, "Course");
        var loose = await AddVideoAsync("Bonus");
        await _series.PromoteAsync(loose.Work, first.Parent.Value);
        // What a scan wrote for the same fields before the person moved the video.
        Execute($"""
            INSERT INTO metadata_providers (id, name, version, is_enabled) VALUES (x'{"11".PadRight(32, '1')}', 'scan_test', '1', 1);
            INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value, confidence, claimed_at, is_user_locked, is_current)
            VALUES (x'{"22".PadRight(32, '2')}', x'{loose.Asset.ToString("N")}', x'{"11".PadRight(32, '1')}', 'video_series', 'Old Series', 0.98, '2026-01-01', 0, 1);
            """);

        Assert.Equal(6, CurrentClaims(loose.Asset));
        var change = await _series.RemoveAsync(loose.Work);

        Assert.Equal(VideoSeriesOutcome.Done, change.Outcome);
        Assert.Equal(0, CurrentClaims(loose.Asset));
        Assert.Equal(0, CanonicalRows(loose.Asset));
        Assert.Null((await _series.GetMembershipAsync(loose.Work))!.ParentWorkId);
    }

    [Fact]
    public async Task Promote_WhenTheClaimsCannotBeSaved_LeavesTheVideoStandaloneAndAnotherTryWorks()
    {
        var first = await AddVideoAsync("Intro", "course", 1);
        var loose = await AddVideoAsync("Bonus");
        Execute("CREATE TRIGGER fail_claims BEFORE INSERT ON metadata_claims BEGIN SELECT RAISE(ABORT, 'no claims'); END;");

        var failed = await _series.PromoteAsync(loose.Work, first.Parent!.Value);

        Assert.Equal(VideoSeriesOutcome.Conflict, failed.Outcome);
        Assert.Null((await _series.GetMembershipAsync(loose.Work))!.ParentWorkId);
        Execute("DROP TRIGGER fail_claims;");
        var retry = await _series.PromoteAsync(loose.Work, first.Parent.Value);
        Assert.Equal(VideoSeriesOutcome.Done, retry.Outcome);
        Assert.Equal(2, retry.Position);
        Assert.Equal(5, CurrentClaims(loose.Asset, lockedOnly: true));
    }

    [Fact]
    public async Task PromoteToNewSeries_WhenTheClaimsCannotBeSaved_StartsNoSeries()
    {
        var loose = await AddVideoAsync("Bonus");
        Execute("CREATE TRIGGER fail_claims BEFORE INSERT ON metadata_claims BEGIN SELECT RAISE(ABORT, 'no claims'); END;");

        var failed = await _series.PromoteToNewSeriesAsync(loose.Work, "Road Trip");

        Assert.Equal(VideoSeriesOutcome.Conflict, failed.Outcome);
        using (var conn = _db.CreateConnection())
        {
            Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE work_kind = 'parent';"));
        }

        Execute("DROP TRIGGER fail_claims;");
        Assert.Equal(VideoSeriesOutcome.Done, (await _series.PromoteToNewSeriesAsync(loose.Work, "Road Trip")).Outcome);
    }

    [Fact]
    public async Task Remove_WhenTheClaimsCannotBeCleared_KeepsTheVideoInItsSeriesAndAnotherTryWorks()
    {
        var first = await AddVideoAsync("Intro", "course", 1);
        var loose = await AddVideoAsync("Bonus");
        await _series.PromoteAsync(loose.Work, first.Parent!.Value);
        Execute("CREATE TRIGGER fail_delete BEFORE DELETE ON metadata_claims BEGIN SELECT RAISE(ABORT, 'no delete'); END;");

        var failed = await _series.RemoveAsync(loose.Work);

        Assert.Equal(VideoSeriesOutcome.Conflict, failed.Outcome);
        Assert.Equal(first.Parent, (await _series.GetMembershipAsync(loose.Work))!.ParentWorkId);
        Assert.Equal(5, CurrentClaims(loose.Asset, lockedOnly: true));
        Execute("DROP TRIGGER fail_delete;");
        Assert.Equal(VideoSeriesOutcome.Done, (await _series.RemoveAsync(loose.Work)).Outcome);
        Assert.Equal(0, CurrentClaims(loose.Asset));
    }

    [Fact]
    public async Task ListTargets_OnlyOffersSeriesOfTheVideosOwnLibrary()
    {
        var mine = await AddVideoAsync("Intro", "course", 1, library: "lib-a");
        Title(mine.Parent!.Value, "Course");
        var other = await AddVideoAsync("Lecture", "talks", 1, library: "lib-b");
        Title(other.Parent!.Value, "Talks");
        var loose = await AddVideoAsync("Bonus", library: "lib-a");

        var targets = await _series.ListTargetsAsync(loose.Work);

        Assert.Equal([mine.Parent.Value], targets.Select(target => target.ParentWorkId));
    }

    [Fact]
    public async Task Promote_RefusesASeriesOfAnotherLibrary()
    {
        var other = await AddVideoAsync("Lecture", "talks", 1, library: "lib-b");
        var loose = await AddVideoAsync("Bonus", library: "lib-a");

        var change = await _series.PromoteAsync(loose.Work, other.Parent!.Value);

        Assert.Equal(VideoSeriesOutcome.TargetUnavailable, change.Outcome);
        Assert.Null((await _series.GetMembershipAsync(loose.Work))!.ParentWorkId);
    }

    [Fact]
    public async Task PromoteToNewSeries_OnlyJoinsAnExistingSeriesOfTheSameLibrary()
    {
        var other = await AddVideoAsync("Lecture", "talks", 1, library: "lib-b");
        var sameLibrary = await AddVideoAsync("Extra", library: "lib-b");
        var elsewhere = await AddVideoAsync("Bonus", library: "lib-a");

        Assert.Equal(VideoSeriesOutcome.TargetUnavailable, (await _series.PromoteToNewSeriesAsync(elsewhere.Work, "Talks")).Outcome);
        var joined = await _series.PromoteToNewSeriesAsync(sameLibrary.Work, "Talks");
        Assert.Equal(VideoSeriesOutcome.Done, joined.Outcome);
        Assert.Equal(other.Parent, joined.ParentWorkId);
    }

    private Task<Guid> SeriesOfAsync(Guid sectionId)
    {
        using var conn = _db.CreateConnection();
        return Task.FromResult(conn.QuerySingle<Guid>("SELECT parent_work_id FROM works WHERE id = @sectionId;", new { sectionId }));
    }
}
