using Dapper;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Organise;
using MediaEngine.Ingestion.Services;
using MediaEngine.Ingestion.Videos;
using MediaEngine.Intelligence;
using MediaEngine.Processors.Models;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A video changing series moves its file in a Managed folder and only changes Library in a Read-only one (universal editor v6, D24):
/// the real handlers, the real organiser and real temp folders over a temp SQLite data store. Where the file goes is the series' folder;
/// the series rows change only after the file moved, and the file goes back if they fail. After the change a rescan of the folder says the
/// same thing as the person's choice (Managed), or Library's own record still wins (Read-only).
/// </summary>
public sealed class VideoSeriesFileMoveTests : IDisposable
{
    private static readonly IReadOnlyDictionary<Guid, string> NoRefusals = new Dictionary<Guid, string>();

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-video-move-{Guid.NewGuid():N}");
    private readonly DatabaseConnection _db;
    private readonly HierarchyResolver _resolver;
    private readonly VideoSeriesRepository _series;
    private readonly ItemCanonicalRepository _items;
    private readonly SystemActivityRepository _activity;
    private readonly MoveFileWorld _world;
    private readonly VideoSeriesEditSessionChangeHandler _handler;
    private readonly VideoSeriesPlacementService _placement;
    private readonly EditSessionService _service;

    public VideoSeriesFileMoveTests()
    {
        Directory.CreateDirectory(_root);
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(Path.Combine(_root, "test.db"));
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _resolver = new HierarchyResolver(new WorkRepository(_db));
        _series = new VideoSeriesRepository(_db);
        _items = new ItemCanonicalRepository(_db, NullLogger<ItemCanonicalRepository>.Instance);
        _activity = new SystemActivityRepository(_db);
        _world = new MoveFileWorld(_db, _root);
        _placement = PlacementOver(_world.Organizer);
        _handler = new VideoSeriesEditSessionChangeHandler(_series, _activity, _items, _placement, _world.Organizer);
        _service = new EditSessionService([_handler, new FileMoveEditSessionChangeHandler(null!, null!)], NullLogger<EditSessionService>.Instance);
    }

    public void Dispose()
    {
        _world.Dispose();
        try { _db.Dispose(); } catch (Exception) { /* test cleanup: the folder goes next */ }
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* a leftover temp folder is harmless */ }
    }

    private VideoSeriesPlacementService PlacementOver(IAutoOrganizeService organizer) => new(
        _series, _items, new MediaAssetRepository(_db), new LibraryFolderResolver(new StaticMonitor(_world.Options)), organizer);

    // ----- The preview -----

    [Fact]
    public async Task Preview_OfJoiningASeries_InAManagedFolder_NamesTheSeriesFolderAndKeepsTheFileName()
    {
        var course = await AddVideoAsync(_world.Managed, "Course/01 Intro.mp4", "course", 1, "Course");
        await AddVideoAsync(_world.Managed, "Course/02 Basics.mp4", "course", 2, "Course");
        var bonus = await AddVideoAsync(_world.Managed, "Bonus.mp4", null, 1);

        var plan = await _placement.PlanAsync(bonus.Work, course.Parent.ToString(), null);

        Assert.NotNull(plan);
        Assert.True(plan.Managed);
        Assert.False(plan.Blocks);
        Assert.Null(plan.Reason);
        Assert.Equal(Path.Combine(_world.Managed, "Bonus.mp4"), plan.FromPath);
        Assert.Equal(Path.Combine(_world.Managed, "Course", "Bonus.mp4"), plan.ToPath);
    }

    [Fact]
    public async Task Preview_InAReadOnlyFolder_SaysTheFileStays()
    {
        var course = await AddVideoAsync(_world.ReadOnly, "Course/01 Intro.mp4", "course", 1, "Course");
        await AddVideoAsync(_world.ReadOnly, "Course/02 Basics.mp4", "course", 2, "Course");
        var bonus = await AddVideoAsync(_world.ReadOnly, "Bonus.mp4", null, 1);

        var plan = await _placement.PlanAsync(bonus.Work, course.Parent.ToString(), null);

        Assert.NotNull(plan);
        Assert.False(plan.Managed);
        Assert.Null(plan.ToPath);
        Assert.False(plan.Blocks);
    }

    [Fact]
    public async Task Preview_InAnAsIsFolder_SaysTheFileStays()
    {
        var course = await AddVideoAsync(_world.AsIs, "Course/01 Intro.mp4", "course", 1, "Course");
        await AddVideoAsync(_world.AsIs, "Course/02 Basics.mp4", "course", 2, "Course");
        var bonus = await AddVideoAsync(_world.AsIs, "Bonus.mp4", null, 1);

        var plan = await _placement.PlanAsync(bonus.Work, course.Parent.ToString(), null);

        Assert.False(plan!.Managed);
        Assert.Null(plan.ToPath);
    }

    [Fact]
    public async Task Preview_OfANewSeries_PointsAtAFolderNamedForItInsideTheLibraryFolder()
    {
        var bonus = await AddVideoAsync(_world.Managed, "Bonus.mp4", null, 1);

        var plan = await _placement.PlanAsync(bonus.Work, null, EditSessionVideo.NewSeriesToken("Garden talks"));

        Assert.True(plan!.Managed);
        Assert.Equal(Path.Combine(_world.Managed, "Garden talks", "Bonus.mp4"), plan.ToPath);
    }

    [Fact]
    public async Task Preview_OfANewSeries_CleansTheNameTheWayTheOrganiserCleansEverySegment()
    {
        var bonus = await AddVideoAsync(_world.Managed, "Bonus.mp4", null, 1);

        var plan = await _placement.PlanAsync(bonus.Work, null, EditSessionVideo.NewSeriesToken("Garden/talks"));

        Assert.Equal(Path.Combine(_world.Managed, "Garden_talks", "Bonus.mp4"), plan!.ToPath);
    }

    [Fact]
    public async Task Preview_OfANewSeriesNamedWithOnlyDots_NeverLeavesTheLibraryFolder()
    {
        var bonus = await AddVideoAsync(_world.Managed, "Bonus.mp4", null, 1);

        var plan = await _placement.PlanAsync(bonus.Work, null, EditSessionVideo.NewSeriesToken(".."));

        Assert.Null(plan!.ToPath);
        Assert.Equal(VideoSeriesPlacementService.NameNotUsable, plan.Reason);
    }

    [Fact]
    public async Task Preview_OfStandingAlone_PointsAtTheTopOfTheLibraryFolder()
    {
        var course = await AddVideoAsync(_world.Managed, "Course/01 Intro.mp4", "course", 1, "Course");
        await AddVideoAsync(_world.Managed, "Course/02 Basics.mp4", "course", 2, "Course");

        var plan = await _placement.PlanAsync(course.Work, null, EditSessionVideo.NoSeriesToken);

        Assert.True(plan!.Managed);
        Assert.Equal(Path.Combine(_world.Managed, "Course", "01 Intro.mp4"), plan.FromPath);
        Assert.Equal(Path.Combine(_world.Managed, "01 Intro.mp4"), plan.ToPath);
    }

    [Fact]
    public async Task Preview_OfASectionOfASeries_PointsAtTheFolderItsVideosSitIn()
    {
        var hello = await AddVideoAsync(_world.Managed, "Python/01 Basics/01 Hello.mp4", "python", 1, "Python", section: "Basics", sectionIndex: 1);
        await AddVideoAsync(_world.Managed, "Python/01 Basics/02 Vars.mp4", "python", 2, "Python", section: "Basics", sectionIndex: 1);
        await AddVideoAsync(_world.Managed, "Python/02 More/01 Classes.mp4", "python", 1, "Python", section: "More", sectionIndex: 2);
        var bonus = await AddVideoAsync(_world.Managed, "Bonus.mp4", null, 1);

        var plan = await _placement.PlanAsync(bonus.Work, hello.Parent.ToString(), null);

        Assert.Equal(Path.Combine(_world.Managed, "Python", "01 Basics", "Bonus.mp4"), plan!.ToPath);
    }

    [Fact]
    public async Task Preview_OfASeriesWhoseVideosAreInSeveralFolders_SaysTheFileStaysAndWhy()
    {
        var spread = await AddVideoAsync(_world.Managed, "One/01 A.mp4", "spread", 1, "Spread");
        await AddVideoAsync(_world.Managed, "Two/02 B.mp4", "spread", 2, "Spread");
        var bonus = await AddVideoAsync(_world.Managed, "Bonus.mp4", null, 1);

        var plan = await _placement.PlanAsync(bonus.Work, spread.Parent.ToString(), null);

        Assert.True(plan!.Managed);
        Assert.Null(plan.ToPath);
        Assert.Equal(VideoSeriesPlacementService.SeriesInManyFolders, plan.Reason);
    }

    [Fact]
    public async Task Preview_OfASeriesWithNoFolderOfItsOwn_SaysTheFileStaysAndWhy()
    {
        var loose = await AddVideoAsync(_world.Managed, "A.mp4", "loose", 1, "Loose");
        await AddVideoAsync(_world.Managed, "B.mp4", "loose", 2, "Loose");
        var bonus = await AddVideoAsync(_world.Managed, "Bonus.mp4", null, 1);

        var plan = await _placement.PlanAsync(bonus.Work, loose.Parent.ToString(), null);

        Assert.Null(plan!.ToPath);
        Assert.Equal(VideoSeriesPlacementService.SeriesHasNoFolder, plan.Reason);
    }

    [Fact]
    public async Task Preview_OfSomethingThatIsNotAVideo_IsNull()
    {
        Assert.Null(await _placement.PlanAsync(Guid.NewGuid(), null, EditSessionVideo.NoSeriesToken));
    }

    // ----- The check -----

    [Fact]
    public async Task Check_WhenTheNameIsTakenAtTheDestination_BlocksTheSaveAndMovesNothing()
    {
        var course = await AddVideoAsync(_world.Managed, "Course/01 Intro.mp4", "course", 1, "Course");
        await AddVideoAsync(_world.Managed, "Course/Bonus.mp4", "course", 2, "Course");
        var bonus = await AddVideoAsync(_world.Managed, "Bonus.mp4", null, 1);

        var result = await _service.RunAsync(Request(false, Join("c1", bonus.Work, course.Parent)), NoRefusals);

        Assert.False(result.Valid);
        var reason = result.Changes.Single(change => change.Id == "c1").Reason;
        Assert.Contains("Bonus.mp4", reason);
        Assert.Contains("already in that folder", reason);
        Assert.True(File.Exists(Path.Combine(_world.Managed, "Bonus.mp4")));
        Assert.Null((await _series.GetMembershipAsync(bonus.Work))!.ParentWorkId);
    }

    [Fact]
    public async Task Preview_OnlySession_CheckingAMove_NeverMovesTheFile()
    {
        var course = await AddVideoAsync(_world.Managed, "Course/01 Intro.mp4", "course", 1, "Course");
        await AddVideoAsync(_world.Managed, "Course/02 Basics.mp4", "course", 2, "Course");
        var bonus = await AddVideoAsync(_world.Managed, "Bonus.mp4", null, 1);

        var result = await _service.RunAsync(Request(true, Join("c1", bonus.Work, course.Parent)), NoRefusals);

        Assert.True(result.Valid);
        Assert.True(File.Exists(Path.Combine(_world.Managed, "Bonus.mp4")));
    }

    [Fact]
    public async Task TheFileMoveThatTravelsWithASeriesChange_HasNothingToCheckOrApply_SoTheNamingPolicyNeverMovesTheVideoElsewhere()
    {
        var marked = new EditSessionChange
        {
            Id = "m1",
            Kind = EditSessionChangeKinds.FileMove,
            EntityId = Guid.NewGuid(),
            Token = EditSessionVideo.MovesWithSeriesToken,
        };
        var handler = new FileMoveEditSessionChangeHandler(null!, null!);

        Assert.Null(await handler.CheckAsync(marked));
        Assert.Null(await handler.ApplyAsync(marked));
    }

    // ----- Apply -----

    [Fact]
    public async Task Apply_InAManagedFolder_MovesTheFileAndItsSubtitles_AndUpdatesLibrary()
    {
        var course = await AddVideoAsync(_world.Managed, "Course/01 Intro.mp4", "course", 1, "Course");
        await AddVideoAsync(_world.Managed, "Course/02 Basics.mp4", "course", 2, "Course");
        var bonus = await AddVideoAsync(_world.Managed, "Bonus.mp4", null, 1);
        MoveFileWorld.WriteFile(_world.Managed, "Bonus.en.srt", "1\n00:00:01,000 --> 00:00:02,000\nHi\n");

        var result = await _service.RunAsync(Request(false, Join("c1", bonus.Work, course.Parent)), NoRefusals);

        Assert.True(result.DataSaved);
        var moved = Path.Combine(_world.Managed, "Course", "Bonus.mp4");
        Assert.True(File.Exists(moved));
        Assert.False(File.Exists(Path.Combine(_world.Managed, "Bonus.mp4")));
        Assert.True(File.Exists(Path.Combine(_world.Managed, "Course", "Bonus.en.srt")));
        Assert.False(File.Exists(Path.Combine(_world.Managed, "Bonus.en.srt")));
        Assert.Equal(moved, _world.StoredPath(bonus.Asset));
        Assert.Equal(course.Parent, (await _series.GetMembershipAsync(bonus.Work))!.ParentWorkId);
    }

    [Fact]
    public async Task Apply_OfANewSeries_InAManagedFolder_CreatesItsFolderAndMovesTheFileIn()
    {
        var bonus = await AddVideoAsync(_world.Managed, "Bonus.mp4", null, 1);

        var result = await _service.RunAsync(Request(false, NewSeries("c1", bonus.Work, "Garden talks")), NoRefusals);

        Assert.True(result.DataSaved);
        Assert.True(File.Exists(Path.Combine(_world.Managed, "Garden talks", "Bonus.mp4")));
        Assert.Equal(Path.Combine(_world.Managed, "Garden talks", "Bonus.mp4"), _world.StoredPath(bonus.Asset));
        Assert.NotNull((await _series.GetMembershipAsync(bonus.Work))!.ParentWorkId);
    }

    [Fact]
    public async Task Apply_OfASeriesChangeWithNoFileMoveBesideIt_NeverMovesTheFile()
    {
        // A drag between sections in the order list stages only the series change: Order and section changes move nothing on disk.
        var course = await AddVideoAsync(_world.Managed, "Course/01 Intro.mp4", "course", 1, "Course");
        await AddVideoAsync(_world.Managed, "Course/02 Basics.mp4", "course", 2, "Course");
        var bonus = await AddVideoAsync(_world.Managed, "Bonus.mp4", null, 1);

        var result = await _service.RunAsync(WithoutTheMove(Join("c1", bonus.Work, course.Parent)), NoRefusals);

        Assert.True(result.DataSaved);
        Assert.True(File.Exists(Path.Combine(_world.Managed, "Bonus.mp4")));
        Assert.Equal(Path.Combine(_world.Managed, "Bonus.mp4"), _world.StoredPath(bonus.Asset));
        Assert.Equal(course.Parent, (await _series.GetMembershipAsync(bonus.Work))!.ParentWorkId);
    }

    [Fact]
    public async Task Apply_OfStandingAlone_InAManagedFolder_MovesTheFileToTheTopOfTheLibraryFolder()
    {
        var course = await AddVideoAsync(_world.Managed, "Course/01 Intro.mp4", "course", 1, "Course");
        await AddVideoAsync(_world.Managed, "Course/02 Basics.mp4", "course", 2, "Course");

        var result = await _service.RunAsync(Request(false, Leave("c1", course.Work)), NoRefusals);

        Assert.True(result.DataSaved);
        Assert.True(File.Exists(Path.Combine(_world.Managed, "01 Intro.mp4")));
        Assert.Equal(Path.Combine(_world.Managed, "01 Intro.mp4"), _world.StoredPath(course.Asset));
        Assert.Null((await _series.GetMembershipAsync(course.Work))!.ParentWorkId);
    }

    [Fact]
    public async Task Apply_InAReadOnlyFolder_ChangesTheSeriesAndLeavesTheFileWhereItIs()
    {
        var course = await AddVideoAsync(_world.ReadOnly, "Course/01 Intro.mp4", "course", 1, "Course");
        await AddVideoAsync(_world.ReadOnly, "Course/02 Basics.mp4", "course", 2, "Course");
        var bonus = await AddVideoAsync(_world.ReadOnly, "Bonus.mp4", null, 1);

        var result = await _service.RunAsync(Request(false, Join("c1", bonus.Work, course.Parent)), NoRefusals);

        Assert.True(result.DataSaved);
        Assert.True(File.Exists(Path.Combine(_world.ReadOnly, "Bonus.mp4")));
        Assert.Equal(Path.Combine(_world.ReadOnly, "Bonus.mp4"), _world.StoredPath(bonus.Asset));
        Assert.Equal(course.Parent, (await _series.GetMembershipAsync(bonus.Work))!.ParentWorkId);
    }

    [Fact]
    public async Task Apply_WhenTheFileCannotBeMoved_ChangesNoSeriesRowAndSaysSo()
    {
        var course = await AddVideoAsync(_world.Managed, "Course/01 Intro.mp4", "course", 1, "Course");
        await AddVideoAsync(_world.Managed, "Course/02 Basics.mp4", "course", 2, "Course");
        var bonus = await AddVideoAsync(_world.Managed, "Bonus.mp4", null, 1);
        var failing = _world.CreateOrganizer(new MoveFileWorld.FlakyFileOrganizer(
            new FileOrganizer(NullLogger<FileOrganizer>.Instance, _world.HashCache), failures: 5));
        var service = ServiceOver(failing, _series);

        var result = await service.RunAsync(Request(false, Join("c1", bonus.Work, course.Parent)), NoRefusals);

        Assert.False(result.DataSaved);
        Assert.Equal(EditSessionChangeStatuses.Failed, result.Changes.Single(change => change.Id == "c1").Status);
        Assert.Contains("could not be moved", result.Changes[0].Reason);
        Assert.True(File.Exists(Path.Combine(_world.Managed, "Bonus.mp4")));
        Assert.Equal(Path.Combine(_world.Managed, "Bonus.mp4"), _world.StoredPath(bonus.Asset));
        Assert.Null((await _series.GetMembershipAsync(bonus.Work))!.ParentWorkId);
    }

    [Fact]
    public async Task Apply_WhenTheSeriesRowsFailAfterTheMove_PutsTheFileAndItsSubtitlesBack()
    {
        var course = await AddVideoAsync(_world.Managed, "Course/01 Intro.mp4", "course", 1, "Course");
        await AddVideoAsync(_world.Managed, "Course/02 Basics.mp4", "course", 2, "Course");
        var bonus = await AddVideoAsync(_world.Managed, "Bonus.mp4", null, 1);
        MoveFileWorld.WriteFile(_world.Managed, "Bonus.en.srt", "subtitle");
        var service = ServiceOver(_world.Organizer, new RefusingSeries(_series));

        var result = await service.RunAsync(Request(false, Join("c1", bonus.Work, course.Parent)), NoRefusals);

        Assert.False(result.DataSaved);
        Assert.Equal(EditSessionChangeStatuses.Failed, result.Changes.Single(change => change.Id == "c1").Status);
        Assert.True(File.Exists(Path.Combine(_world.Managed, "Bonus.mp4")));
        Assert.True(File.Exists(Path.Combine(_world.Managed, "Bonus.en.srt")));
        Assert.False(File.Exists(Path.Combine(_world.Managed, "Course", "Bonus.mp4")));
        Assert.Equal(Path.Combine(_world.Managed, "Bonus.mp4"), _world.StoredPath(bonus.Asset));
        Assert.Null((await _series.GetMembershipAsync(bonus.Work))!.ParentWorkId);
    }

    [Fact]
    public async Task Undo_OfASavedSeriesChange_PutsTheSeriesAndTheFileBack()
    {
        var course = await AddVideoAsync(_world.Managed, "Course/01 Intro.mp4", "course", 1, "Course");
        await AddVideoAsync(_world.Managed, "Course/02 Basics.mp4", "course", 2, "Course");
        var bonus = await AddVideoAsync(_world.Managed, "Bonus.mp4", null, 1);

        var undo = await _handler.ApplyAsync(Join("c1", bonus.Work, course.Parent), moveFile: true);
        Assert.NotNull(undo);
        Assert.True(File.Exists(Path.Combine(_world.Managed, "Course", "Bonus.mp4")));
        await undo(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_world.Managed, "Bonus.mp4")));
        Assert.Equal(Path.Combine(_world.Managed, "Bonus.mp4"), _world.StoredPath(bonus.Asset));
        Assert.Null((await _series.GetMembershipAsync(bonus.Work))!.ParentWorkId);
    }

    [Fact]
    public async Task Apply_WithAStalePreview_FollowsTheFolderAsItIsNow()
    {
        // The Dashboard's preview said Managed; by the time of the save the video sits in a Read-only folder. The save re-plans and moves nothing.
        var course = await AddVideoAsync(_world.ReadOnly, "Course/01 Intro.mp4", "course", 1, "Course");
        await AddVideoAsync(_world.ReadOnly, "Course/02 Basics.mp4", "course", 2, "Course");
        var bonus = await AddVideoAsync(_world.ReadOnly, "Bonus.mp4", null, 1);
        var staleMove = new EditSessionChange
        {
            Id = "m1",
            Kind = EditSessionChangeKinds.FileMove,
            EntityId = bonus.Work,
            Token = EditSessionVideo.MovesWithSeriesToken,
        };

        var result = await _service.RunAsync(Request(false, Join("c1", bonus.Work, course.Parent)), NoRefusals);

        Assert.True(result.DataSaved);
        Assert.True(File.Exists(Path.Combine(_world.ReadOnly, "Bonus.mp4")));
        Assert.Null(await new FileMoveEditSessionChangeHandler(null!, null!).ApplyAsync(staleMove));
    }

    // ----- A rescan says the same thing -----

    private static readonly VideoNamingConfigurationProvider Naming = new(() => null);

    private ProcessorResult Scan(string libraryFolder, string path) =>
        new VideoLibraryReader(Naming).Apply(new ProcessorResult { FilePath = path, DetectedType = MediaType.Unknown, Claims = [] }, libraryFolder);

    private static string? Claim(ProcessorResult result, string key) => result.Claims.FirstOrDefault(claim => claim.Key == key)?.Value;

    [Fact]
    public async Task Rescan_AfterAMoveInAManagedFolder_ReadsTheVideoAsAnEpisodeOfTheChosenSeries()
    {
        var course = await AddVideoAsync(_world.Managed, "Course/01 Intro.mp4", "course", 1, "Course");
        await AddVideoAsync(_world.Managed, "Course/02 Basics.mp4", "course", 2, "Course");
        var bonus = await AddVideoAsync(_world.Managed, "03 Bonus.mp4", null, 1);

        await _service.RunAsync(Request(false, Join("c1", bonus.Work, course.Parent)), NoRefusals);

        var scanned = Scan(_world.Managed, _world.StoredPath(bonus.Asset));
        Assert.Equal("Course", Claim(scanned, MetadataFieldConstants.VideoSeries));
        Assert.Equal("3", Claim(scanned, MetadataFieldConstants.VideoEpisodeIndex));
        Assert.Equal("Bonus", Claim(scanned, MetadataFieldConstants.Title));
    }

    [Fact]
    public async Task Rescan_AfterAMoveToAFolderOfAChosenSeriesWithSections_ReadsTheSectionToo()
    {
        var hello = await AddVideoAsync(_world.Managed, "Python/01 Basics/01 Hello.mp4", "python", 1, "Python", section: "Basics", sectionIndex: 1);
        await AddVideoAsync(_world.Managed, "Python/01 Basics/02 Vars.mp4", "python", 2, "Python", section: "Basics", sectionIndex: 1);
        await AddVideoAsync(_world.Managed, "Python/02 More/01 Classes.mp4", "python", 1, "Python", section: "More", sectionIndex: 2);
        var bonus = await AddVideoAsync(_world.Managed, "03 Bonus.mp4", null, 1);

        await _service.RunAsync(Request(false, Join("c1", bonus.Work, hello.Parent)), NoRefusals);

        var scanned = Scan(_world.Managed, _world.StoredPath(bonus.Asset));
        Assert.Equal("Python", Claim(scanned, MetadataFieldConstants.VideoSeries));
        Assert.Equal("Basics", Claim(scanned, MetadataFieldConstants.VideoSection));
    }

    [Fact]
    public async Task Rescan_AfterStandingAloneInAManagedFolder_StaysStandalone()
    {
        var course = await AddVideoAsync(_world.Managed, "Course/01 Intro.mp4", "course", 1, "Course");
        await AddVideoAsync(_world.Managed, "Course/02 Basics.mp4", "course", 2, "Course");
        await AddVideoAsync(_world.Managed, "Course/03 More.mp4", "course", 3, "Course");

        await _service.RunAsync(Request(false, Leave("c1", course.Work)), NoRefusals);

        var scanned = Scan(_world.Managed, _world.StoredPath(course.Asset));
        Assert.Null(Claim(scanned, MetadataFieldConstants.VideoSeries));
        Assert.Equal("Intro", Claim(scanned, MetadataFieldConstants.Title));
        // The two videos left in the folder are still its series.
        Assert.Equal("Course", Claim(Scan(_world.Managed, Path.Combine(_world.Managed, "Course", "02 Basics.mp4")), MetadataFieldConstants.VideoSeries));
    }

    [Fact]
    public async Task Rescan_AfterTheLastButOneVideoLeavesAFolder_DocumentsWhatTheFolderSaysAboutTheOneLeft_AndLeavesItsLibraryStateAlone()
    {
        var first = await AddVideoAsync(_world.Managed, "Course/01 Intro.mp4", "course", 1, "Course");
        var second = await AddVideoAsync(_world.Managed, "Course/02 Basics.mp4", "course", 2, "Course");

        await _service.RunAsync(Request(false, Leave("c1", first.Work)), NoRefusals);

        // The folder now holds one video, which VideoFolderLayout reads as standalone (a folder of one video is not a series).
        var scanned = Scan(_world.Managed, Path.Combine(_world.Managed, "Course", "02 Basics.mp4"));
        Assert.Null(Claim(scanned, MetadataFieldConstants.VideoSeries));
        // Library's record of the video left behind is not touched by the change: the file is where it was and still in its series.
        Assert.True(File.Exists(Path.Combine(_world.Managed, "Course", "02 Basics.mp4")));
        Assert.Equal(Path.Combine(_world.Managed, "Course", "02 Basics.mp4"), _world.StoredPath(second.Asset));
        Assert.Equal(second.Parent, (await _series.GetMembershipAsync(second.Work))!.ParentWorkId);
    }

    [Fact]
    public async Task Rescan_AfterAChoiceInAReadOnlyFolder_StillGivesTheSeriesThePersonChose()
    {
        // The folder says "Course", the person chose "Other": the file stays where it is, so the scan keeps reporting the folder's series,
        // and the person's locked choice must outrank it every time the claims are scored again.
        var course = await AddVideoAsync(_world.ReadOnly, "Course/01 Intro.mp4", "course", 1, "Course");
        await AddVideoAsync(_world.ReadOnly, "Course/02 Basics.mp4", "course", 2, "Course");
        var other = await AddVideoAsync(_world.ReadOnly, "Other/01 A.mp4", "other", 1, "Other");
        await AddVideoAsync(_world.ReadOnly, "Other/02 B.mp4", "other", 2, "Other");
        Assert.NotEqual(course.Parent, other.Parent);

        var result = await _service.RunAsync(Request(false, Join("c1", course.Work, other.Parent)), NoRefusals);
        Assert.True(result.DataSaved);
        Assert.Equal(Path.Combine(_world.ReadOnly, "Course", "01 Intro.mp4"), _world.StoredPath(course.Asset));

        var scanned = Scan(_world.ReadOnly, _world.StoredPath(course.Asset));
        Assert.Equal("Course", Claim(scanned, MetadataFieldConstants.VideoSeries));
        var scoring = new PriorityCascadeEngine(new NoConfigurationLoader(), NullLogger<PriorityCascadeEngine>.Instance);
        using (var connection = _db.CreateConnection())
        {
            connection.Execute(
                "INSERT OR IGNORE INTO metadata_providers (id, name, version, is_enabled) VALUES (@id, 'local-processor', '1', 1);",
                new { id = GuidSql.ToBlob(WellKnownProviders.LocalProcessor) });
        }

        await ScoringHelper.PersistClaimsAndScoreAsync(
            course.Asset,
            scanned.Claims.Select(claim => new ProviderClaim(claim.Key, claim.Value, claim.Confidence)).ToList(),
            WellKnownProviders.LocalProcessor, new MetadataClaimRepository(_db), new CanonicalValueRepository(_db), scoring,
            new NoConfigurationLoader(), [], CancellationToken.None, snapshotFieldKeys: scanned.Claims.Select(claim => claim.Key).ToList());

        var values = (await new CanonicalValueRepository(_db).GetByEntityAsync(course.Asset)).ToDictionary(v => v.Key, v => v.Value);
        Assert.Equal("Other", values[MetadataFieldConstants.VideoSeries]);
        Assert.Equal(other.Parent, (await _series.GetMembershipAsync(course.Work))!.ParentWorkId);
    }

    // ----- Helpers -----

    private EditSessionService ServiceOver(IAutoOrganizeService organizer, IVideoSeriesRepository series) => new(
        [new VideoSeriesEditSessionChangeHandler(series, _activity, _items, PlacementOver(organizer), organizer), new FileMoveEditSessionChangeHandler(null!, null!)],
        NullLogger<EditSessionService>.Instance);

    private sealed record Video(Guid Work, Guid Parent, Guid Asset);

    private async Task<Video> AddVideoAsync(
        string folder, string relativePath, string? seriesKey, int episode, string? seriesTitle = null, string? section = null, int sectionIndex = 1)
    {
        var path = MoveFileWorld.WriteFile(folder, relativePath, "video bytes of " + relativePath);
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
            new { asset, edition, hash = asset.ToString("N"), path });
        conn.Execute(
            "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@asset, 'title', @title, '2026-01-01');",
            new { asset, title = Path.GetFileNameWithoutExtension(path) });
        if (placed.ParentWorkId is { } parent && seriesTitle is not null)
        {
            // A series is titled by its own name; a section by its name, under the series.
            var grandparent = conn.QuerySingleOrDefault<Guid?>("SELECT parent_work_id FROM works WHERE id = @parent;", new { parent });
            var titled = grandparent is { } top
                ? new[] { (top, seriesTitle), (parent, section ?? "Section") }
                : new[] { (parent, seriesTitle) };
            foreach (var (id, title) in titled)
            {
                conn.Execute(
                    "INSERT OR REPLACE INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@id, 'title', @title, '2026-01-01');",
                    new { id, title });
            }
        }

        return new Video(placed.WorkId, placed.ParentWorkId ?? Guid.Empty, asset);
    }

    // What the Dashboard stages for a series choice in a Managed folder: the series change and, beside it, the file move the person sees in the
    // footer and in Review. WithoutTheMove is the same series change as the order list stages it, with no file move beside it.
    private static EditSessionRequest Request(bool previewOnly, params EditSessionChange[] changes) => new()
    {
        PreviewOnly = previewOnly,
        Changes =
        [
            .. changes,
            .. changes.Where(change => change.Kind == EditSessionChangeKinds.VideoSeries).Select(change => new EditSessionChange
            {
                Id = change.Id + "-move",
                Kind = EditSessionChangeKinds.FileMove,
                EntityId = change.EntityId,
                Token = EditSessionVideo.MovesWithSeriesToken,
            }),
        ],
    };

    private static EditSessionRequest WithoutTheMove(params EditSessionChange[] changes) => new() { Changes = [.. changes] };

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

    /// <summary>The real series store, except that putting a video in a series always fails (the second half of a move).</summary>
    private sealed class RefusingSeries(IVideoSeriesRepository inner) : IVideoSeriesRepository
    {
        public Task<VideoSeriesMembership?> GetMembershipAsync(Guid workId, CancellationToken ct = default) => inner.GetMembershipAsync(workId, ct);
        public Task<IReadOnlyList<VideoSeriesTarget>> ListTargetsAsync(Guid workId, CancellationToken ct = default) => inner.ListTargetsAsync(workId, ct);
        public Task<IReadOnlyList<string>> ListMemberFilePathsAsync(Guid parentWorkId, CancellationToken ct = default) => inner.ListMemberFilePathsAsync(parentWorkId, ct);
        public Task<bool> IsSeriesChosenByUserAsync(Guid workId, CancellationToken ct = default) => inner.IsSeriesChosenByUserAsync(workId, ct);
        public Task<VideoSeriesChange> RemoveAsync(Guid workId, CancellationToken ct = default) => inner.RemoveAsync(workId, ct);

        public Task<VideoSeriesChange> PromoteAsync(Guid workId, Guid parentWorkId, CancellationToken ct = default) =>
            Task.FromResult(new VideoSeriesChange(VideoSeriesOutcome.Conflict));

        public Task<VideoSeriesChange> PromoteToNewSeriesAsync(Guid workId, string seriesName, CancellationToken ct = default) =>
            Task.FromResult(new VideoSeriesChange(VideoSeriesOutcome.Conflict));
    }

    private sealed class StaticMonitor(IngestionOptions value) : IOptionsMonitor<IngestionOptions>
    {
        public IngestionOptions CurrentValue => value;

        public IngestionOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<IngestionOptions, string?> listener) => null;
    }

    /// <summary>A configuration loader with the built-in defaults, so the Priority Cascade scores with no provider files.</summary>
    private sealed class NoConfigurationLoader : IConfigurationLoader
    {
        public event EventHandler<ConfigurationFileChangedEventArgs>? ConfigurationChanged { add { } remove { } }

        public string ConfigDirectoryPath => string.Empty;
        public CoreConfiguration LoadCore() => new();
        public void SaveCore(CoreConfiguration config) { }
        public ScoringSettings LoadScoring() => new();
        public void SaveScoring(ScoringSettings settings) { }
        public MaintenanceSettings LoadMaintenance() => new();
        public void SaveMaintenance(MaintenanceSettings settings) { }
        public HydrationSettings LoadHydration() => new();
        public void SaveHydration(HydrationSettings settings) { }
        public PipelineConfiguration LoadPipelines() => new();
        public void SavePipelines(PipelineConfiguration config) { }
        public DisambiguationSettings LoadDisambiguation() => new();
        public void SaveDisambiguation(DisambiguationSettings settings) { }
        public MediaTypeConfiguration LoadMediaTypes() => new();
        public void SaveMediaTypes(MediaTypeConfiguration config) { }
        public TranscodingSettings LoadTranscoding() => new();
        public void SaveTranscoding(TranscodingSettings settings) { }
        public FieldPriorityConfiguration LoadFieldPriorities() => new();
        public void SaveFieldPriorities(FieldPriorityConfiguration config) { }
        public LibrariesConfiguration LoadLibraries() => new();
        public ProviderConfiguration? LoadProvider(string name) => null;
        public void DeleteProvider(string name) { }
        public void SaveProvider(ProviderConfiguration config) { }
        public IReadOnlyList<ProviderConfiguration> LoadAllProviders() => [];
        public T? LoadConfig<T>(string subdirectory, string name) where T : class => null;
        public void SaveConfig<T>(string subdirectory, string name, T config) where T : class { }
        public T? LoadAi<T>() where T : class => default;
        public void SaveAi<T>(T settings) where T : class { }
        public PaletteConfiguration LoadPalette() => new();
        public void SavePalette(PaletteConfiguration palette) { }
    }
}
