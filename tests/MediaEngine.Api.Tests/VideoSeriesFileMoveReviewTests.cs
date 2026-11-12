using Dapper;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Contracts;
using MediaEngine.Ingestion;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The review of the series file move (universal editor v6, D24): a move belongs to the series change it was shown with, a match's move and
/// a series move never share a save, a failed undo says why and leaves Library and disk agreeing, the name the person was shown is the name the
/// file gets, and a name Windows would not make a folder of is refused on any machine.
/// </summary>
public sealed partial class VideoSeriesFileMoveTests
{
    // ----- A move belongs to the series change it was shown with -----

    [Fact]
    public void Pairs_AMoveThatNamesItsSeriesChangeAndChoice_LetsThatChangeMoveItsFile()
    {
        var series = Join("c1", Guid.NewGuid(), Guid.NewGuid());

        var pairs = VideoSeriesMovePairs.Read([series, MoveShownWith(series)]);

        Assert.Contains("c1", pairs.FollowingSeriesChanges);
        Assert.Empty(pairs.Refusals);
    }

    [Fact]
    public void Pairs_AMoveShownForAnotherChoice_IsRefused_EvenWhenTheSeriesChangeKeptItsId()
    {
        var video = Guid.NewGuid();
        var shownFor = Join("c1", video, Guid.NewGuid());
        var replacedBy = Join("c1", video, Guid.NewGuid());

        var pairs = VideoSeriesMovePairs.Read([replacedBy, MoveShownWith(shownFor)]);

        Assert.Empty(pairs.FollowingSeriesChanges);
        Assert.Equal(EditSessionReasons.SeriesMoveStale, pairs.Refusals["c1-move"]);
    }

    [Fact]
    public void Pairs_AMoveWhoseSeriesChangeIsGone_IsRefused()
    {
        var shownFor = Join("c1", Guid.NewGuid(), Guid.NewGuid());

        var pairs = VideoSeriesMovePairs.Read([MoveShownWith(shownFor)]);

        Assert.Equal(EditSessionReasons.SeriesMoveStale, pairs.Refusals["c1-move"]);
    }

    [Fact]
    public void Pairs_AMoveNamingAnotherVideosSeriesChange_IsRefused()
    {
        var mine = Join("c1", Guid.NewGuid(), Guid.NewGuid());
        var theirs = Join("c2", Guid.NewGuid(), Guid.NewGuid());
        var crossed = new EditSessionChange
        {
            Id = "m1", Kind = EditSessionChangeKinds.FileMove, EntityId = mine.EntityId, TargetId = theirs.Id,
            Token = EditSessionVideo.MovesWithSeries(theirs.TargetId, theirs.Token),
        };

        var pairs = VideoSeriesMovePairs.Read([mine, theirs, crossed]);

        Assert.Equal(EditSessionReasons.SeriesMoveStale, pairs.Refusals["m1"]);
        Assert.Empty(pairs.FollowingSeriesChanges);
    }

    [Fact]
    public void Pairs_AMatchMoveBesideASeriesMove_IsRefusedWithAPlainLine()
    {
        var series = Join("c1", Guid.NewGuid(), Guid.NewGuid());
        var match = new EditSessionChange { Id = "match-move", Kind = EditSessionChangeKinds.FileMove, EntityId = series.EntityId };

        var pairs = VideoSeriesMovePairs.Read([series, MoveShownWith(series), match]);

        Assert.Equal(EditSessionReasons.SeriesMoveWithMatchMove, pairs.Refusals["c1-move"]);
        Assert.Empty(pairs.FollowingSeriesChanges);
    }

    [Fact]
    public async Task Save_WhenTheSeriesChoiceWasReplacedAfterTheMoveWasShown_RefusesTheSaveAndMovesNothing()
    {
        var course = await AddVideoAsync(_world.Managed, "Course/01 Intro.mp4", "course", 1, "Course");
        await AddVideoAsync(_world.Managed, "Course/02 Basics.mp4", "course", 2, "Course");
        var garden = await AddVideoAsync(_world.Managed, "Garden/01 Soil.mp4", "garden", 1, "Garden");
        await AddVideoAsync(_world.Managed, "Garden/02 Seeds.mp4", "garden", 2, "Garden");
        var bonus = await AddVideoAsync(_world.Managed, "Bonus.mp4", null, 1);
        // The footer showed the move into Course; the series choice was then replaced (a drag in the order list) by Garden, keeping its id.
        var request = new EditSessionRequest
        {
            Changes = [Join("c1", bonus.Work, garden.Parent), MoveShownWith(Join("c1", bonus.Work, course.Parent))],
        };

        var result = await _service.RunAsync(request, NoRefusals);

        Assert.False(result.Valid);
        Assert.Equal(EditSessionReasons.SeriesMoveStale, result.Changes.Single(change => change.Id == "c1-move").Reason);
        Assert.True(File.Exists(Path.Combine(_world.Managed, "Bonus.mp4")));
        Assert.False(File.Exists(Path.Combine(_world.Managed, "Garden", "Bonus.mp4")));
        Assert.Null((await _series.GetMembershipAsync(bonus.Work))!.ParentWorkId);
    }

    [Fact]
    public async Task Save_OfAMoveWhoseSeriesChangeIsNotInTheSave_RefusesIt_AndTheFileStays()
    {
        var course = await AddVideoAsync(_world.Managed, "Course/01 Intro.mp4", "course", 1, "Course");
        await AddVideoAsync(_world.Managed, "Course/02 Basics.mp4", "course", 2, "Course");
        var bonus = await AddVideoAsync(_world.Managed, "Bonus.mp4", null, 1);

        var result = await _service.RunAsync(new EditSessionRequest { Changes = [MoveShownWith(Join("c1", bonus.Work, course.Parent))] }, NoRefusals);

        Assert.False(result.Valid);
        Assert.True(File.Exists(Path.Combine(_world.Managed, "Bonus.mp4")));
    }

    [Fact]
    public async Task Save_OfAMatchMoveAndASeriesMoveForOneVideo_IsRefusedWithAPlainLine_AndNothingMoves()
    {
        var course = await AddVideoAsync(_world.Managed, "Course/01 Intro.mp4", "course", 1, "Course");
        await AddVideoAsync(_world.Managed, "Course/02 Basics.mp4", "course", 2, "Course");
        var bonus = await AddVideoAsync(_world.Managed, "Bonus.mp4", null, 1);
        var match = new EditSessionChange { Id = "match-move", Kind = EditSessionChangeKinds.FileMove, EntityId = bonus.Work };

        var result = await _service.RunAsync(Request(false, Join("c1", bonus.Work, course.Parent), match), NoRefusals);

        Assert.False(result.Valid);
        Assert.Equal(EditSessionReasons.SeriesMoveWithMatchMove, result.Changes.Single(change => change.Id == "c1-move").Reason);
        Assert.True(File.Exists(Path.Combine(_world.Managed, "Bonus.mp4")));
        Assert.Null((await _series.GetMembershipAsync(bonus.Work))!.ParentWorkId);
    }

    [Fact]
    public async Task Save_OfATitleChangeAndASeriesChange_PutsTheFileInTheSeriesFolder_NotAtTheTopOfTheLibrary()
    {
        // The Videos naming template is {Title}{Ext}: it has no folder in it, so a refile by title would put the file at the library's top.
        // Saving a new title does not refile anything by itself (only a staged match move does, and that cannot share a save with a series
        // move), so the file ends in the series folder under the name it had, and the title is Library's own override.
        var course = await AddVideoAsync(_world.Managed, "Course/01 Intro.mp4", "course", 1, "Course");
        await AddVideoAsync(_world.Managed, "Course/02 Basics.mp4", "course", 2, "Course");
        var bonus = await AddVideoAsync(_world.Managed, "Bonus.mp4", null, 1);
        var writes = new RecordingFileWrites();
        var service = new EditSessionService(
            [_handler, new DetailsEditSessionChangeHandler(_items, _activity), new FileMoveEditSessionChangeHandler(null!, null!)],
            NullLogger<EditSessionService>.Instance, writes);
        var title = new EditSessionChange
        {
            Id = "t1", Kind = EditSessionChangeKinds.Details, EntityId = bonus.Work,
            Fields = [new EditSessionFieldChange { Key = "title", Value = "Bonus: the long cut" }],
        };

        var result = await service.RunAsync(Request(false, Join("c1", bonus.Work, course.Parent), title), NoRefusals);

        Assert.True(result.DataSaved);
        Assert.True(File.Exists(Path.Combine(_world.Managed, "Course", "Bonus.mp4")));
        Assert.False(File.Exists(Path.Combine(_world.Managed, "Bonus.mp4")));
        Assert.Equal(Path.Combine(_world.Managed, "Course", "Bonus.mp4"), _world.StoredPath(bonus.Asset));
        using var conn = _db.CreateConnection();
        Assert.Contains("Bonus: the long cut", conn.QuerySingle<string>("SELECT display_overrides_json FROM works WHERE id = @id;", new { id = bonus.Work }));
        Assert.Contains(bonus.Work, writes.Queued);
    }

    // ----- Undo -----

    [Fact]
    public async Task Undo_WhenTheFileCannotBeMovedBack_KeepsTheSeriesChangeSaved_SoLibraryAndDiskAgree_AndSaysWhy()
    {
        var course = await AddVideoAsync(_world.Managed, "Course/01 Intro.mp4", "course", 1, "Course");
        await AddVideoAsync(_world.Managed, "Course/02 Basics.mp4", "course", 2, "Course");
        var bonus = await AddVideoAsync(_world.Managed, "Bonus.mp4", null, 1);
        var scripted = new ScriptedOrganizer(new FileOrganizer(NullLogger<FileOrganizer>.Instance, _world.HashCache)) { FailFromMove = 2 };
        var organizer = _world.CreateOrganizer(scripted);
        var handler = new VideoSeriesEditSessionChangeHandler(_series, _activity, _items, PlacementOver(organizer), organizer);

        var undo = await handler.ApplyAsync(Join("c1", bonus.Work, course.Parent), moveFile: true);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => undo!(CancellationToken.None));

        Assert.Contains("stays saved", failure.Message);
        Assert.Contains("Course", failure.Message);
        Assert.Equal(course.Parent, (await _series.GetMembershipAsync(bonus.Work))!.ParentWorkId);
        Assert.True(File.Exists(Path.Combine(_world.Managed, "Course", "Bonus.mp4")));
        Assert.Equal(Path.Combine(_world.Managed, "Course", "Bonus.mp4"), _world.StoredPath(bonus.Asset));
    }

    [Fact]
    public async Task Save_WhenALaterChangeFailsAndTheFileCannotBeMovedBack_TheReasonTheSessionGivesIsTheSpecificOne()
    {
        var course = await AddVideoAsync(_world.Managed, "Course/01 Intro.mp4", "course", 1, "Course");
        await AddVideoAsync(_world.Managed, "Course/02 Basics.mp4", "course", 2, "Course");
        var bonus = await AddVideoAsync(_world.Managed, "Bonus.mp4", null, 1);
        var scripted = new ScriptedOrganizer(new FileOrganizer(NullLogger<FileOrganizer>.Instance, _world.HashCache)) { FailFromMove = 2 };
        var organizer = _world.CreateOrganizer(scripted);
        var service = new EditSessionService(
            [new VideoSeriesEditSessionChangeHandler(_series, _activity, _items, PlacementOver(organizer), organizer), new FailingDetailsHandler(), new FileMoveEditSessionChangeHandler(null!, null!)],
            NullLogger<EditSessionService>.Instance);
        var title = new EditSessionChange
        {
            Id = "t1", Kind = EditSessionChangeKinds.Details, EntityId = bonus.Work,
            Fields = [new EditSessionFieldChange { Key = "title", Value = "x" }],
        };

        var result = await service.RunAsync(Request(false, Join("c1", bonus.Work, course.Parent), title), NoRefusals);

        var row = result.Changes.Single(change => change.Id == "c1");
        Assert.Equal(EditSessionChangeStatuses.Failed, row.Status);
        Assert.Contains("stays saved", row.Reason);
        Assert.NotEqual(EditSessionReasons.SaveFailed, row.Reason);
    }

    // ----- Files that travel with the video -----

    [Fact]
    public async Task Apply_OfAVideoAloneInItsFolder_TakesItsCoverAndNotesAlong_AndUndoPutsEveryFileBack()
    {
        var course = await AddVideoAsync(_world.Managed, "Course/01 Intro.mp4", "course", 1, "Course");
        await AddVideoAsync(_world.Managed, "Course/02 Basics.mp4", "course", 2, "Course");
        var solo = await AddVideoAsync(_world.Managed, "Solo/Bonus.mp4", null, 1);
        MoveFileWorld.WriteFile(_world.Managed, "Solo/Bonus.en.srt", "subtitle");
        MoveFileWorld.WriteFile(_world.Managed, "Solo/cover.jpg", "cover bytes");
        MoveFileWorld.WriteFile(_world.Managed, "Solo/metadata.json", "{\"title\":\"Bonus\"}");

        var undo = await _handler.ApplyAsync(Join("c1", solo.Work, course.Parent), moveFile: true);

        // Whatever the existing re-file does with the folder-wide files, this does the same: they follow once the video was the folder's last.
        Assert.True(File.Exists(Path.Combine(_world.Managed, "Course", "Bonus.mp4")));
        Assert.True(File.Exists(Path.Combine(_world.Managed, "Course", "Bonus.en.srt")));
        Assert.False(File.Exists(Path.Combine(_world.Managed, "Solo", "cover.jpg")));
        Assert.False(File.Exists(Path.Combine(_world.Managed, "Solo", "metadata.json")));

        await undo!(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_world.Managed, "Solo", "Bonus.mp4")));
        Assert.True(File.Exists(Path.Combine(_world.Managed, "Solo", "Bonus.en.srt")));
        Assert.True(File.Exists(Path.Combine(_world.Managed, "Solo", "cover.jpg")), "the cover is back beside the video");
        Assert.True(File.Exists(Path.Combine(_world.Managed, "Solo", "metadata.json")), "the notes file is back beside the video");
        Assert.Equal(Path.Combine(_world.Managed, "Solo", "Bonus.mp4"), _world.StoredPath(solo.Asset));
    }

    // ----- The name the person was shown is the name the file gets -----

    [Fact]
    public async Task Apply_WhenTheNameIsTakenBetweenTheCheckAndTheMove_RefusesTheFile_NeverSavesItAsNameTwo()
    {
        var course = await AddVideoAsync(_world.Managed, "Course/01 Intro.mp4", "course", 1, "Course");
        await AddVideoAsync(_world.Managed, "Course/02 Basics.mp4", "course", 2, "Course");
        var bonus = await AddVideoAsync(_world.Managed, "Bonus.mp4", null, 1);
        var taken = Path.Combine(_world.Managed, "Course", "Bonus.mp4");
        var scripted = new ScriptedOrganizer(new FileOrganizer(NullLogger<FileOrganizer>.Instance, _world.HashCache))
        {
            BeforeFirstMove = _ => File.WriteAllText(taken, "someone else's file"),
        };
        var organizer = _world.CreateOrganizer(scripted);
        var handler = new VideoSeriesEditSessionChangeHandler(_series, _activity, _items, PlacementOver(organizer), organizer);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.ApplyAsync(Join("c1", bonus.Work, course.Parent), moveFile: true));

        Assert.Contains("already in that folder", failure.Message);
        Assert.True(File.Exists(Path.Combine(_world.Managed, "Bonus.mp4")));
        Assert.Equal("someone else's file", File.ReadAllText(taken));
        Assert.False(File.Exists(Path.Combine(_world.Managed, "Course", "Bonus (2).mp4")));
        Assert.Null((await _series.GetMembershipAsync(bonus.Work))!.ParentWorkId);
    }

    [Fact]
    public async Task TheFileOrganizer_WhenAskedNotToRenameAround_RefusesATakenName_AndLeavesBothFilesAlone()
    {
        var organizer = new FileOrganizer(NullLogger<FileOrganizer>.Instance, _world.HashCache);
        var source = MoveFileWorld.WriteFile(_world.Managed, "a/Clip.mp4", "mine");
        var taken = MoveFileWorld.WriteFile(_world.Managed, "b/Clip.mp4", "theirs");

        var refused = await organizer.ExecuteMoveWithoutRenameAsync(source, taken);
        var renamedAround = await organizer.ExecuteMoveAsync(source, taken);

        Assert.Null(refused);
        Assert.Equal(Path.Combine(_world.Managed, "b", "Clip (2).mp4"), renamedAround);
        Assert.Equal("theirs", File.ReadAllText(taken));
    }

    // ----- Names Windows would not make a folder of -----

    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("Com1")]
    [InlineData("LPT3.txt")]
    [InlineData("aux")]
    [InlineData("Garden.")]
    public async Task Preview_OfANewSeriesNamedLikeAWindowsDeviceOrEndingInADot_IsRefusedOnAnyMachine(string name)
    {
        var bonus = await AddVideoAsync(_world.Managed, "Bonus.mp4", null, 1);

        var plan = await _placement.PlanAsync(bonus.Work, null, EditSessionVideo.NewSeriesToken(name));
        var result = await _service.RunAsync(Request(false, NewSeries("c1", bonus.Work, name)), NoRefusals);

        Assert.True(plan!.Blocks);
        Assert.Null(plan.ToPath);
        Assert.Equal(VideoSeriesPlacementService.NameNotUsable, plan.Reason);
        Assert.False(result.Valid);
        Assert.Equal(VideoSeriesPlacementService.NameNotUsable, result.Changes.Single(change => change.Id == "c1").Reason);
        Assert.True(File.Exists(Path.Combine(_world.Managed, "Bonus.mp4")));
        Assert.Empty(Directory.GetDirectories(_world.Managed));
    }

    [Theory]
    [InlineData("Concert")]
    [InlineData("Garden talks")]
    [InlineData("Com 1 notes")]
    public async Task Preview_OfANewSeriesWithAnOrdinaryName_StillGetsItsFolder(string name)
    {
        var bonus = await AddVideoAsync(_world.Managed, "Bonus.mp4", null, 1);

        var plan = await _placement.PlanAsync(bonus.Work, null, EditSessionVideo.NewSeriesToken(name));

        Assert.False(plan!.Blocks);
        Assert.Equal(Path.Combine(_world.Managed, name, "Bonus.mp4"), plan.ToPath);
    }

    [Fact]
    public async Task Apply_WhenTheMoveFailsInANewSeriesFolder_LeavesNoEmptyFolderBehind()
    {
        var bonus = await AddVideoAsync(_world.Managed, "Bonus.mp4", null, 1);
        var scripted = new ScriptedOrganizer(new FileOrganizer(NullLogger<FileOrganizer>.Instance, _world.HashCache)) { FailFromMove = 1 };
        var organizer = _world.CreateOrganizer(scripted);
        var service = ServiceOver(organizer, _series);

        var result = await service.RunAsync(Request(false, NewSeries("c1", bonus.Work, "Garden talks")), NoRefusals);

        Assert.False(result.DataSaved);
        Assert.False(Directory.Exists(Path.Combine(_world.Managed, "Garden talks")));
        Assert.True(File.Exists(Path.Combine(_world.Managed, "Bonus.mp4")));
    }

    // ----- Helpers for these tests -----

    /// <summary>The real mover, except that it can fail from its n-th move on (after making the folder, as a real one does) or let someone take the name first.</summary>
    private sealed class ScriptedOrganizer(IFileOrganizer inner) : IFileOrganizer
    {
        private int _moves;

        /// <summary>Moves numbered from this one on fail (1-based).</summary>
        public int FailFromMove { get; init; } = int.MaxValue;

        /// <summary>Runs just before the first move reaches the real mover, with the destination path.</summary>
        public Action<string>? BeforeFirstMove { get; init; }

        public string CalculatePath(IngestionCandidate candidate, string template) => inner.CalculatePath(candidate, template);

        public string? ValidateTemplate(string template, out string? error) => inner.ValidateTemplate(template, out error);

        public Task<string?> ExecuteMoveAsync(string sourcePath, string destinationPath, CancellationToken ct = default) =>
            inner.ExecuteMoveAsync(sourcePath, destinationPath, ct);

        public Task<string?> ExecuteMoveWithoutRenameAsync(string sourcePath, string destinationPath, CancellationToken ct = default)
        {
            _moves++;
            if (_moves >= FailFromMove)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                return Task.FromResult<string?>(null);
            }

            if (_moves == 1)
            {
                BeforeFirstMove?.Invoke(destinationPath);
            }

            return inner.ExecuteMoveWithoutRenameAsync(sourcePath, destinationPath, ct);
        }
    }

    private sealed class FailingDetailsHandler : IEditSessionChangeHandler
    {
        public string Kind => EditSessionChangeKinds.Details;

        public Task<string?> CheckAsync(EditSessionChange change, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<Func<CancellationToken, Task>?> ApplyAsync(EditSessionChange change, CancellationToken ct = default) =>
            throw new InvalidOperationException("A later change could not be saved.");
    }

    private sealed class RecordingFileWrites : IEditSessionFileWrites
    {
        public List<Guid> Queued { get; } = [];

        public Task QueueAsync(IReadOnlyCollection<Guid> entityIds, CancellationToken ct = default)
        {
            Queued.AddRange(entityIds);
            return Task.CompletedTask;
        }
    }
}
