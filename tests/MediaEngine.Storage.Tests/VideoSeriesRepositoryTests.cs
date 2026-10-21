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

    private async Task<(Guid Work, Guid Asset, Guid? Parent)> AddVideoAsync(string title, string? seriesKey = null, int episode = 1, string? section = null, int sectionIndex = 1)
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
            "INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status) VALUES (@asset, @edition, @hash, @path, 'Normal');",
            new { asset, edition, hash = asset.ToString("N"), path = $"clips/{title}.mp4" });
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

        var targets = await _series.ListTargetsAsync();

        Assert.Equal(["Course", "Guide: Getting started"], targets.Select(target => target.Title));
        Assert.DoesNotContain(targets, target => target.ParentWorkId == seriesOfSections);
        Assert.Equal([1, 1], targets.Select(target => target.VideoCount));
    }

    [Fact]
    public async Task GetMembership_OfAnythingThatIsNotAVideo_IsNull()
    {
        Assert.Null(await _series.GetMembershipAsync(Guid.NewGuid()));
    }

    private Task<Guid> SeriesOfAsync(Guid sectionId)
    {
        using var conn = _db.CreateConnection();
        return Task.FromResult(conn.QuerySingle<Guid>("SELECT parent_work_id FROM works WHERE id = @sectionId;", new { sectionId }));
    }
}
