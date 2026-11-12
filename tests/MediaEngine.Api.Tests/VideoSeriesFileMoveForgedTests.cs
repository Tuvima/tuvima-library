using MediaEngine.Api.Services.EditSession;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Contracts;
using MediaEngine.Ingestion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The second review of the series file move (universal editor v6, D24): one reader of a series choice for every part of the save, an undo
/// whose forward move is refused tells the person where the file is, a folder that cannot be listed is said and logged, and a video held by
/// its file's id and by its own id is one video.
/// </summary>
public sealed partial class VideoSeriesFileMoveTests
{
    // ----- One reader of a series choice -----

    private static EditSessionChange Forged(Guid video, Guid series, string name) => new()
    {
        Id = "c1",
        Kind = EditSessionChangeKinds.VideoSeries,
        EntityId = video,
        TargetId = series.ToString(),
        Token = EditSessionVideo.NewSeriesToken(name),
    };

    [Fact]
    public void AChoice_NamingASeriesAndATokenAtOnce_IsNotOneChoice()
    {
        var series = Guid.NewGuid().ToString();

        Assert.False(EditSessionVideo.IsOneChoice(series, EditSessionVideo.NewSeriesToken("x")));
        Assert.False(EditSessionVideo.IsOneChoice(series, EditSessionVideo.NoSeriesToken));
        Assert.False(EditSessionVideo.IsOneChoice(series, "anything"));
        Assert.True(EditSessionVideo.IsOneChoice(series, null));
        Assert.True(EditSessionVideo.IsOneChoice(null, EditSessionVideo.NewSeriesToken("x")));
        Assert.True(EditSessionVideo.IsOneChoice(null, EditSessionVideo.NoSeriesToken));
    }

    [Fact]
    public async Task Save_OfAChangeNamingASeriesAndANewSeriesName_IsRefusedAtTheCheck_AndNeitherTheFileNorTheRowsMove()
    {
        var course = await AddVideoAsync(_world.Managed, "Course/01 Intro.mp4", "course", 1, "Course");
        await AddVideoAsync(_world.Managed, "Course/02 Basics.mp4", "course", 2, "Course");
        var bonus = await AddVideoAsync(_world.Managed, "Bonus.mp4", null, 1);
        var forged = Forged(bonus.Work, course.Parent, "x");

        // The move names the very choice the forged change carries, so the pair itself agrees.
        var result = await _service.RunAsync(Request(false, forged), NoRefusals);

        Assert.False(result.Valid);
        Assert.Equal(EditSessionReasons.SeriesChoiceUnclear, result.Changes.Single(change => change.Id == "c1").Reason);
        Assert.True(File.Exists(Path.Combine(_world.Managed, "Bonus.mp4")));
        Assert.False(Directory.Exists(Path.Combine(_world.Managed, "x")));
        Assert.Null((await _series.GetMembershipAsync(bonus.Work))!.ParentWorkId);
    }

    [Fact]
    public async Task Apply_OfAChangeNamingASeriesAndANewSeriesName_RefusesBeforeAnythingIsTouched()
    {
        var course = await AddVideoAsync(_world.Managed, "Course/01 Intro.mp4", "course", 1, "Course");
        await AddVideoAsync(_world.Managed, "Course/02 Basics.mp4", "course", 2, "Course");
        var bonus = await AddVideoAsync(_world.Managed, "Bonus.mp4", null, 1);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _handler.ApplyAsync(Forged(bonus.Work, course.Parent, "x"), moveFile: true));

        Assert.Equal(EditSessionReasons.SeriesChoiceUnclear, failure.Message);
        Assert.True(File.Exists(Path.Combine(_world.Managed, "Bonus.mp4")));
        Assert.False(Directory.Exists(Path.Combine(_world.Managed, "x")));
        Assert.Null((await _series.GetMembershipAsync(bonus.Work))!.ParentWorkId);
    }

    [Fact]
    public async Task Preview_OfAChoiceNamingASeriesAndANewSeriesName_IsBlocked()
    {
        var course = await AddVideoAsync(_world.Managed, "Course/01 Intro.mp4", "course", 1, "Course");
        await AddVideoAsync(_world.Managed, "Course/02 Basics.mp4", "course", 2, "Course");
        var bonus = await AddVideoAsync(_world.Managed, "Bonus.mp4", null, 1);

        var plan = await _placement.PlanAsync(bonus.Work, course.Parent.ToString(), EditSessionVideo.NewSeriesToken("x"));

        Assert.True(plan!.Blocks);
        Assert.Null(plan.ToPath);
        Assert.Equal(EditSessionReasons.SeriesChoiceUnclear, plan.Reason);
    }

    // ----- A video held by its file's id and by its own -----

    [Fact]
    public async Task Save_OfASeriesChangeOnTheVideosIdAndAMatchMoveOnItsFilesId_IsRefusedAsOneVideo()
    {
        var course = await AddVideoAsync(_world.Managed, "Course/01 Intro.mp4", "course", 1, "Course");
        await AddVideoAsync(_world.Managed, "Course/02 Basics.mp4", "course", 2, "Course");
        var bonus = await AddVideoAsync(_world.Managed, "Bonus.mp4", null, 1);
        var service = ServiceKnowingVideos();
        var matchOnTheFile = new EditSessionChange { Id = "match-move", Kind = EditSessionChangeKinds.FileMove, EntityId = bonus.Asset };

        var result = await service.RunAsync(Request(false, Join("c1", bonus.Work, course.Parent), matchOnTheFile), NoRefusals);

        Assert.False(result.Valid);
        Assert.Equal(EditSessionReasons.SeriesMoveWithMatchMove, result.Changes.Single(change => change.Id == "c1-move").Reason);
        Assert.True(File.Exists(Path.Combine(_world.Managed, "Bonus.mp4")));
        Assert.Null((await _series.GetMembershipAsync(bonus.Work))!.ParentWorkId);
    }

    [Fact]
    public async Task Save_OfASeriesChangeOnTheFilesIdAndAMatchMoveOnTheVideosId_IsRefusedAsOneVideo()
    {
        var course = await AddVideoAsync(_world.Managed, "Course/01 Intro.mp4", "course", 1, "Course");
        await AddVideoAsync(_world.Managed, "Course/02 Basics.mp4", "course", 2, "Course");
        var bonus = await AddVideoAsync(_world.Managed, "Bonus.mp4", null, 1);
        var service = ServiceKnowingVideos();
        var matchOnTheVideo = new EditSessionChange { Id = "match-move", Kind = EditSessionChangeKinds.FileMove, EntityId = bonus.Work };

        var result = await service.RunAsync(Request(false, Join("c1", bonus.Asset, course.Parent), matchOnTheVideo), NoRefusals);

        Assert.False(result.Valid);
        Assert.Equal(EditSessionReasons.SeriesMoveWithMatchMove, result.Changes.Single(change => change.Id == "c1-move").Reason);
        Assert.True(File.Exists(Path.Combine(_world.Managed, "Bonus.mp4")));
    }

    [Fact]
    public void Pairs_ReadWithAResolver_TreatTheFilesIdAndTheVideosIdAsOneVideo()
    {
        var video = Guid.NewGuid();
        var file = Guid.NewGuid();
        var series = Join("c1", video, Guid.NewGuid());
        var shown = MoveShownWith(series);
        var move = new EditSessionChange { Id = shown.Id, Kind = shown.Kind, EntityId = file, TargetId = shown.TargetId, Token = shown.Token };

        var unresolved = VideoSeriesMovePairs.Read([series, move]);
        var resolved = VideoSeriesMovePairs.Read([series, move], id => id == file ? video : id);

        Assert.Equal(EditSessionReasons.SeriesMoveStale, unresolved.Refusals["c1-move"]);
        Assert.Contains("c1", resolved.FollowingSeriesChanges);
        Assert.Empty(resolved.Refusals);
    }

    private EditSessionService ServiceKnowingVideos() => new(
        [_handler, new FileMoveEditSessionChangeHandler(null!, null!)], NullLogger<EditSessionService>.Instance, null, _items);

    // ----- An undo whose forward move is refused -----

    [Fact]
    public async Task Undo_WhenTheRowsCannotBePutBackAndTheFileCannotGoForwardAgain_SaysWhereTheFileIsAndThatTheyDisagree()
    {
        var course = await AddVideoAsync(_world.Managed, "Course/01 Intro.mp4", "course", 1, "Course");
        await AddVideoAsync(_world.Managed, "Course/02 Basics.mp4", "course", 2, "Course");
        var garden = await AddVideoAsync(_world.Managed, "Garden/01 Soil.mp4", "garden", 1, "Garden");
        await AddVideoAsync(_world.Managed, "Garden/02 Seeds.mp4", "garden", 2, "Garden");
        // The move into Garden works, the move back works, and the move forward again is refused.
        var scripted = new ScriptedOrganizer(new FileOrganizer(NullLogger<FileOrganizer>.Instance, _world.HashCache)) { FailOnlyMove = 3 };
        var organizer = _world.CreateOrganizer(scripted);
        var log = new ListLogger<VideoSeriesEditSessionChangeHandler>();
        var handler = new VideoSeriesEditSessionChangeHandler(
            new RowsThatCannotBePutBack(_series), _activity, _items, PlacementOver(organizer), organizer, log);

        var undo = await handler.ApplyAsync(Join("c1", course.Work, garden.Parent), moveFile: true);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => undo!(CancellationToken.None));

        Assert.Contains("could not be moved forward again", failure.Message);
        Assert.Contains(Path.Combine(_world.Managed, "Course"), failure.Message);
        Assert.Contains("disagree", failure.Message);
        Assert.True(File.Exists(Path.Combine(_world.Managed, "Course", "01 Intro.mp4")));
        Assert.Contains(log.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains(course.Asset.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Undo_WhenTheRowsCannotBePutBackButTheFileGoesForwardAgain_TheOriginalFailureIsTheOneTold()
    {
        var course = await AddVideoAsync(_world.Managed, "Course/01 Intro.mp4", "course", 1, "Course");
        await AddVideoAsync(_world.Managed, "Course/02 Basics.mp4", "course", 2, "Course");
        var garden = await AddVideoAsync(_world.Managed, "Garden/01 Soil.mp4", "garden", 1, "Garden");
        await AddVideoAsync(_world.Managed, "Garden/02 Seeds.mp4", "garden", 2, "Garden");
        var handler = new VideoSeriesEditSessionChangeHandler(
            new RowsThatCannotBePutBack(_series), _activity, _items, PlacementOver(_world.Organizer), _world.Organizer);

        var undo = await handler.ApplyAsync(Join("c1", course.Work, garden.Parent), moveFile: true);
        var failure = await Assert.ThrowsAsync<IOException>(() => undo!(CancellationToken.None));

        Assert.Equal("The data store is busy.", failure.Message);
        Assert.True(File.Exists(Path.Combine(_world.Managed, "Garden", "01 Intro.mp4")));
    }

    // ----- Folders that cannot be listed -----

    [Fact]
    public void WhenAFolderCannotBeListed_ItIsLoggedAsAWarning_AndNotTakenForEmpty()
    {
        var log = new ListLogger<VideoSeriesEditSessionChangeHandler>();
        var handler = new VideoSeriesEditSessionChangeHandler(_series, _activity, _items, null, null, log);
        var asset = Guid.NewGuid();

        var names = handler.NamesIn(_root, asset, _ => throw new UnauthorizedAccessException("no access"));

        Assert.Null(names);
        Assert.Contains(log.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains(asset.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void AnUndoThatCouldNotLookInAFolder_SaysSo_AndOneThatFoundNothingSaysNothing()
    {
        VideoSeriesEditSessionChangeHandler.ThrowIfCompanionsLeft([], unknown: false, "/v/Course", "/v/Garden");

        var unknown = Assert.Throws<InvalidOperationException>(
            () => VideoSeriesEditSessionChangeHandler.ThrowIfCompanionsLeft([], unknown: true, "/v/Course", "/v/Garden"));
        var left = Assert.Throws<InvalidOperationException>(
            () => VideoSeriesEditSessionChangeHandler.ThrowIfCompanionsLeft(["cover.jpg"], unknown: false, "/v/Course", "/v/Garden"));

        Assert.Contains("could not look in the folders", unknown.Message);
        Assert.Contains("/v/Garden", unknown.Message);
        Assert.Contains("cover.jpg could not be moved back", left.Message);
    }

    // ----- Helpers -----

    /// <summary>The real series store, except that putting a video back in the series it left fails (the undo's second half).</summary>
    private sealed class RowsThatCannotBePutBack(IVideoSeriesRepository inner) : IVideoSeriesRepository
    {
        private int _promotes;

        public Task<VideoSeriesMembership?> GetMembershipAsync(Guid workId, CancellationToken ct = default) => inner.GetMembershipAsync(workId, ct);
        public Task<IReadOnlyList<VideoSeriesTarget>> ListTargetsAsync(Guid workId, CancellationToken ct = default) => inner.ListTargetsAsync(workId, ct);
        public Task<IReadOnlyList<string>> ListMemberFilePathsAsync(Guid parentWorkId, CancellationToken ct = default) => inner.ListMemberFilePathsAsync(parentWorkId, ct);
        public Task<bool> IsSeriesChosenByUserAsync(Guid workId, CancellationToken ct = default) => inner.IsSeriesChosenByUserAsync(workId, ct);
        public Task<VideoSeriesChange> RemoveAsync(Guid workId, CancellationToken ct = default) => inner.RemoveAsync(workId, ct);

        public Task<VideoSeriesChange> PromoteAsync(Guid workId, Guid parentWorkId, CancellationToken ct = default) =>
            ++_promotes == 1 ? inner.PromoteAsync(workId, parentWorkId, ct) : throw new IOException("The data store is busy.");

        public Task<VideoSeriesChange> PromoteToNewSeriesAsync(Guid workId, string seriesName, CancellationToken ct = default) =>
            inner.PromoteToNewSeriesAsync(workId, seriesName, ct);
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
