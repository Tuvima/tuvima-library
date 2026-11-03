using Dapper;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion.Organise;
using MediaEngine.Storage;
using MediaEngine.Storage.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Putting a video in a series, putting a series in order and renaming its files, as changes of an edit session (universal editor v3.1):
/// real handlers over a temp SQLite data store. Series and order only change Library's own data; the rename is physical and refuses a
/// group that is not wholly in a Managed folder.
/// </summary>
public sealed class VideoSeriesAndOrderEditSessionTests : IDisposable
{
    private static readonly IReadOnlyDictionary<Guid, string> NoRefusals = new Dictionary<Guid, string>();

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima-video-edit-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly HierarchyResolver _resolver;
    private readonly VideoSeriesRepository _series;
    private readonly VideoOrderRepository _order;
    private readonly EditSessionService _service;

    public VideoSeriesAndOrderEditSessionTests()
    {
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _resolver = new HierarchyResolver(new WorkRepository(_db));
        _series = new VideoSeriesRepository(_db);
        _order = new VideoOrderRepository(_db);
        var activity = new SystemActivityRepository(_db);
        var writer = new VideoOrderWriter(_order, new MetadataClaimRepository(_db), new CanonicalValueRepository(_db), activity);
        var renamer = new VideoOrderRenamer(new MediaAssetRepository(_db), null!, null!, null!, NullLogger<VideoOrderRenamer>.Instance);
        _service = new EditSessionService(
            [
                new VideoSeriesEditSessionChangeHandler(_series, activity, new ItemCanonicalRepository(_db, NullLogger<ItemCanonicalRepository>.Instance)),
                new VideoOrderEditSessionChangeHandler(_order, writer),
                new VideoRenameEditSessionChangeHandler(_order, renamer, activity),
            ],
            NullLogger<EditSessionService>.Instance);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch (Exception) { /* test cleanup: the temp file goes next */ }
        try { File.Delete(_dbPath); } catch (IOException) { /* a leftover temp file is harmless */ }
    }

    [Fact]
    public async Task Preview_OfAJoin_ChecksTheSeriesAndSavesNothing()
    {
        var (course, loose) = await CourseWithOneLooseVideoAsync();

        var result = await _service.RunAsync(Request(true, Join("c1", loose.Work, course.Parent)), NoRefusals);

        Assert.True(result.Valid);
        Assert.False(result.DataSaved);
        Assert.Null((await _series.GetMembershipAsync(loose.Work))!.ParentWorkId);
    }

    [Fact]
    public async Task Apply_OfAJoin_PutsTheVideoLastInTheSeries_AndNeverTouchesAFile()
    {
        var (course, loose) = await CourseWithOneLooseVideoAsync();

        var result = await _service.RunAsync(Request(false, Join("c1", loose.Work, course.Parent)), NoRefusals);

        Assert.True(result.DataSaved);
        Assert.Equal(course.Parent, (await _series.GetMembershipAsync(loose.Work))!.ParentWorkId);
        var entries = (await _order.LoadAsync(course.Parent)).Single().Entries;
        Assert.Equal(loose.Work, entries[^1].WorkId);
    }

    [Fact]
    public async Task Apply_OfAJoin_WhenTheEditorHoldsTheVideoByItsFileId_StillPutsTheVideoInTheSeries()
    {
        var (course, loose) = await CourseWithOneLooseVideoAsync();
        Guid asset;
        using (var conn = _db.CreateConnection())
        {
            asset = conn.ExecuteScalar<Guid>(
                "SELECT ma.id FROM media_assets ma JOIN editions e ON e.id = ma.edition_id WHERE e.work_id = @work;", new { work = loose.Work });
        }

        var result = await _service.RunAsync(Request(false, Join("c1", asset, course.Parent)), NoRefusals);

        Assert.True(result.DataSaved);
        Assert.Equal(course.Parent, (await _series.GetMembershipAsync(loose.Work))!.ParentWorkId);
    }

    [Fact]
    public async Task Check_OfSeveralVideosJoining_FlagsOnlyTheOneThatCannot()
    {
        var (course, loose) = await CourseWithOneLooseVideoAsync();
        var gone = Guid.NewGuid();

        var result = await _service.RunAsync(Request(true, Join("c1", loose.Work, course.Parent), Join("c2", gone, course.Parent)), NoRefusals);

        Assert.False(result.Valid);
        Assert.Equal(EditSessionChangeStatuses.Ok, result.Changes.Single(change => change.Id == "c1").Status);
        Assert.Equal(EditSessionReasons.NotAVideo, result.Changes.Single(change => change.Id == "c2").Reason);
        Assert.Null((await _series.GetMembershipAsync(loose.Work))!.ParentWorkId);
    }

    [Fact]
    public async Task Apply_OfLeavingASeries_MakesTheVideoStandAlone()
    {
        var (course, _) = await CourseWithOneLooseVideoAsync();

        var result = await _service.RunAsync(Request(false, Leave("c1", course.Work)), NoRefusals);

        Assert.True(result.DataSaved);
        Assert.Null((await _series.GetMembershipAsync(course.Work))!.ParentWorkId);
    }

    [Fact]
    public async Task Check_OfLeavingASeries_WhenTheVideoStandsAlone_SaysSo()
    {
        var (_, loose) = await CourseWithOneLooseVideoAsync();

        var result = await _service.RunAsync(Request(true, Leave("c1", loose.Work)), NoRefusals);

        Assert.False(result.Valid);
        Assert.Equal(EditSessionReasons.AlreadyAlone, Assert.Single(result.Changes).Reason);
    }

    [Fact]
    public async Task Apply_OfANewSeries_StartsItWithTheGivenNameAndPutsTheVideoIn()
    {
        var (_, loose) = await CourseWithOneLooseVideoAsync();

        var result = await _service.RunAsync(Request(false, NewSeries("c1", loose.Work, "Garden talks")), NoRefusals);

        Assert.True(result.DataSaved);
        var membership = (await _series.GetMembershipAsync(loose.Work))!;
        Assert.NotNull(membership.ParentWorkId);
        Assert.Equal("Garden talks", membership.SeriesTitle);
    }

    [Fact]
    public async Task Check_OfANewSeriesWithoutAName_IsRefused()
    {
        var (_, loose) = await CourseWithOneLooseVideoAsync();

        var result = await _service.RunAsync(Request(true, new EditSessionChange
        {
            Id = "c1",
            Kind = EditSessionChangeKinds.VideoSeries,
            EntityId = loose.Work,
            Token = "new:   ",
        }), NoRefusals);

        Assert.False(result.Valid);
        Assert.Equal(EditSessionReasons.SeriesNeedsName, Assert.Single(result.Changes).Reason);
    }

    [Fact]
    public async Task Apply_OfAMoveBetweenSections_TakesTheVideoOutAndPutsItInTheOtherSection()
    {
        var one = await AddVideoAsync("A1", "course", 1, "Part one", 1);
        var two = await AddVideoAsync("B1", "course", 1, "Part two", 2);
        Assert.NotEqual(one.Parent, two.Parent);

        var result = await _service.RunAsync(Request(false, Join("c1", one.Work, two.Parent)), NoRefusals);

        Assert.True(result.DataSaved);
        Assert.Equal(two.Parent, (await _series.GetMembershipAsync(one.Work))!.ParentWorkId);
    }

    [Fact]
    public async Task Apply_OfANewOrder_SavesIt()
    {
        var a = await AddVideoAsync("Intro", "course", 1);
        var b = await AddVideoAsync("Basics", "course", 2);
        var c = await AddVideoAsync("Advanced", "course", 3);

        var result = await _service.RunAsync(Request(false, Order("o1", a.Parent, c.Work, a.Work, b.Work)), NoRefusals);

        Assert.True(result.DataSaved);
        var entries = (await _order.LoadAsync(a.Parent)).Single().Entries.Select(entry => entry.WorkId).ToList();
        Assert.Equal([c.Work, a.Work, b.Work], entries);
    }

    [Fact]
    public async Task Preview_OfANewOrder_SavesNothing()
    {
        var a = await AddVideoAsync("Intro", "course", 1);
        var b = await AddVideoAsync("Basics", "course", 2);

        var result = await _service.RunAsync(Request(true, Order("o1", a.Parent, b.Work, a.Work)), NoRefusals);

        Assert.True(result.Valid);
        Assert.Equal([a.Work, b.Work], (await _order.LoadAsync(a.Parent)).Single().Entries.Select(entry => entry.WorkId));
    }

    [Fact]
    public async Task Apply_OfAnOrderThatMissesAVideo_IsRefusedWholeAndLeavesTheOrderAlone()
    {
        var a = await AddVideoAsync("Intro", "course", 1);
        var b = await AddVideoAsync("Basics", "course", 2);
        var c = await AddVideoAsync("Advanced", "course", 3);

        var result = await _service.RunAsync(Request(false, Order("o1", a.Parent, c.Work, b.Work)), NoRefusals);

        Assert.False(result.DataSaved);
        Assert.Equal(EditSessionChangeStatuses.Failed, Assert.Single(result.Changes).Status);
        Assert.Equal([a.Work, b.Work, c.Work], (await _order.LoadAsync(a.Parent)).Single().Entries.Select(entry => entry.WorkId));
    }

    [Fact]
    public async Task Apply_OfAJoinAndAnOrderTogether_PutsTheNewcomerWhereTheOrderSaysEvenWhenItWasStagedFirst()
    {
        var (course, loose) = await CourseWithOneLooseVideoAsync();
        var existing = (await _order.LoadAsync(course.Parent)).Single().Entries.Select(entry => entry.WorkId).ToList();

        // The order is staged before the join, as the editor may do; the join still runs first.
        var result = await _service.RunAsync(
            Request(false, Order("o1", course.Parent, [loose.Work, .. existing]), Join("c1", loose.Work, course.Parent)),
            NoRefusals);

        Assert.True(result.DataSaved);
        Assert.Equal(loose.Work, (await _order.LoadAsync(course.Parent)).Single().Entries[0].WorkId);
    }

    [Fact]
    public async Task Check_OfAnOrderOfOneVideo_SaysThereIsNothingToOrder()
    {
        var a = await AddVideoAsync("Intro", "course", 1);

        var result = await _service.RunAsync(Request(true, Order("o1", a.Parent, a.Work)), NoRefusals);

        Assert.False(result.Valid);
        Assert.Equal(EditSessionReasons.NothingToOrder, Assert.Single(result.Changes).Reason);
    }

    [Fact]
    public async Task Check_OfARename_WhenTheFilesAreNotInAManagedFolder_IsRefused()
    {
        var a = await AddVideoAsync("Intro", "course", 1);
        await AddVideoAsync("Basics", "course", 2);

        var result = await _service.RunAsync(Request(true, new EditSessionChange
        {
            Id = "r1",
            Kind = EditSessionChangeKinds.VideoRename,
            EntityId = a.Parent,
        }), NoRefusals);

        Assert.False(result.Valid);
        Assert.Equal(EditSessionReasons.RenameNotAllowed, Assert.Single(result.Changes).Reason);
    }

    [Fact]
    public async Task Apply_WhenARenameIsRefused_NothingInTheListIsSaved()
    {
        var a = await AddVideoAsync("Intro", "course", 1);
        var b = await AddVideoAsync("Basics", "course", 2);

        var result = await _service.RunAsync(Request(
            false,
            Order("o1", a.Parent, b.Work, a.Work),
            new EditSessionChange { Id = "r1", Kind = EditSessionChangeKinds.VideoRename, EntityId = a.Parent }), NoRefusals);

        Assert.False(result.DataSaved);
        Assert.Equal([a.Work, b.Work], (await _order.LoadAsync(a.Parent)).Single().Entries.Select(entry => entry.WorkId));
    }

    [Fact]
    public void ARename_IsAFileChange_SoItRunsLastAndIsNotSavedWithTheData()
    {
        Assert.True(EditSessionChangeKinds.IsFileChange(EditSessionChangeKinds.VideoRename));
        Assert.False(EditSessionChangeKinds.IsDataChange(EditSessionChangeKinds.VideoRename));
        Assert.True(EditSessionChangeKinds.IsDataChange(EditSessionChangeKinds.VideoSeries));
        Assert.True(EditSessionChangeKinds.IsDataChange(EditSessionChangeKinds.VideoOrder));
    }

    private async Task<((Guid Work, Guid Parent) Course, (Guid Work, Guid Parent) Loose)> CourseWithOneLooseVideoAsync()
    {
        var first = await AddVideoAsync("Intro", "course", 1);
        await AddVideoAsync("Basics", "course", 2);
        var loose = await AddVideoAsync("Bonus", null, 1);
        return ((first.Work, first.Parent), (loose.Work, loose.Parent));
    }

    private async Task<(Guid Work, Guid Parent)> AddVideoAsync(string title, string? seriesKey, int episode, string? section = null, int sectionIndex = 1)
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
        return (placed.WorkId, placed.ParentWorkId ?? Guid.Empty);
    }

    private static EditSessionRequest Request(bool previewOnly, params EditSessionChange[] changes) =>
        new() { PreviewOnly = previewOnly, Changes = [.. changes] };

    private static EditSessionChange Join(string id, Guid video, Guid parent) => new()
    {
        Id = id,
        Kind = EditSessionChangeKinds.VideoSeries,
        EntityId = video,
        TargetId = parent.ToString(),
    };

    private static EditSessionChange Leave(string id, Guid video) => new()
    {
        Id = id,
        Kind = EditSessionChangeKinds.VideoSeries,
        EntityId = video,
        Token = EditSessionVideo.NoSeriesToken,
    };

    private static EditSessionChange NewSeries(string id, Guid video, string name) => new()
    {
        Id = id,
        Kind = EditSessionChangeKinds.VideoSeries,
        EntityId = video,
        Token = EditSessionVideo.NewSeriesToken(name),
    };

    private static EditSessionChange Order(string id, Guid parent, params Guid[] ordered) => new()
    {
        Id = id,
        Kind = EditSessionChangeKinds.VideoOrder,
        EntityId = parent,
        OrderedIds = [.. ordered],
    };
}
