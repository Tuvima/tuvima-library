using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Items;
using MediaEngine.Web.Services.Editing;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The file move staged beside a video's series change (universal editor v6, D24) belongs to that change: it names it by id and carries the
/// choice it was shown with, it goes whenever the series change goes, a drag in the order list never makes one, and a match's own move and a
/// series move never sit in one save.
/// </summary>
public sealed class VideoSeriesMoveStagingTests
{
    private static readonly Guid Video = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Course = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid Garden = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");

    private static PendingEditList StagedWithMove(Guid target)
    {
        var pending = new PendingEditList();
        pending.SetTarget(EditSessionChangeKinds.VideoSeries, Video, target.ToString(), null, "Cooking with Sam", null, EditReviewRows.NotInASeries);
        Assert.True(pending.SetVideoSeriesMove(Video, "/videos/chef.mp4", "/videos/Cooking with Sam/chef.mp4"));
        return pending;
    }

    // ----- The pair -----

    [Fact]
    public void TheMove_NamesItsSeriesChangeById_AndCarriesTheChoiceItWasShownWith()
    {
        var pending = StagedWithMove(Course);

        var series = pending.FindVideoSeries(Video)!;
        var move = pending.FindVideoSeriesMove(Video)!;

        Assert.Equal(series.Id, move.TargetId);
        Assert.Equal(EditSessionVideo.MovesWithSeries(Course.ToString(), null), move.Token);
        Assert.Equal("/videos/chef.mp4", pending.Before(move.Id));
        Assert.Equal("/videos/Cooking with Sam/chef.mp4", pending.Label(move.Id));
    }

    [Fact]
    public void TakingTheSeriesChangeOut_TakesItsMoveOut_AndTheOtherWayAround()
    {
        var pending = StagedWithMove(Course);
        pending.Remove(pending.FindVideoSeries(Video)!.Id);
        Assert.True(pending.IsEmpty);

        pending = StagedWithMove(Course);
        pending.Remove(pending.FindVideoSeriesMove(Video)!.Id);
        Assert.True(pending.IsEmpty);
    }

    [Fact]
    public void ReplacingTheSeriesChoice_DropsTheOldMove_SoNoMoveOutlivesTheChoiceItWasShownFor()
    {
        var pending = StagedWithMove(Course);

        pending.SetTarget(EditSessionChangeKinds.VideoSeries, Video, Garden.ToString(), null, "Garden talks", null, EditReviewRows.NotInASeries);

        Assert.Null(pending.FindVideoSeriesMove(Video));
        Assert.Single(pending.Changes);
    }

    [Fact]
    public void AMoveCannotBeStagedWithoutItsSeriesChange()
    {
        var pending = new PendingEditList();

        Assert.False(pending.SetVideoSeriesMove(Video, "/videos/chef.mp4", "/videos/Cooking with Sam/chef.mp4"));
        Assert.True(pending.IsEmpty);
    }

    [Fact]
    public void AMatchsMoveAndASeriesMove_CannotBeStagedForTheSameVideo_EitherWayAround()
    {
        var pending = new PendingEditList();
        pending.SetTarget(EditSessionChangeKinds.VideoSeries, Video, Course.ToString());
        pending.SetTarget(EditSessionChangeKinds.FileMove, Video, null, null, "/videos/Chef.mp4", null, "/videos/chef.mp4");

        Assert.False(pending.SetVideoSeriesMove(Video, "/videos/chef.mp4", "/videos/Cooking with Sam/chef.mp4"));

        var withSeriesMove = StagedWithMove(Course);
        Assert.Throws<InvalidOperationException>(
            () => withSeriesMove.SetTarget(EditSessionChangeKinds.FileMove, Video, null, null, "/videos/Chef.mp4", null, "/videos/chef.mp4"));
        Assert.Throws<ArgumentException>(
            () => withSeriesMove.SetTarget(EditSessionChangeKinds.FileMove, Video, null, EditSessionVideo.MovesWithSeries(null, EditSessionVideo.NoSeriesToken)));
    }

    [Fact]
    public void AMatchsMoveIsNotTheSeriesMove_AndStagingAMatchMoveWhileASeriesMoveIsStagedStagesNothing()
    {
        var pending = StagedWithMove(Course);
        var placement = new EditorMatchPlacementDto { Managed = true, FromPath = "/videos/chef.mp4", ToPath = "/videos/Chef/chef.mp4" };

        Assert.True(EditorMatchStaging.SeriesMoveInTheWay(pending, Video, placement));
        Assert.False(EditorMatchStaging.StageMove(pending, Video, placement));

        Assert.Equal(2, pending.Count);
        Assert.Null(pending.FindFileMove(Video));
        EditorMatchStaging.Unstage(pending, EditSessionChangeKinds.FileMove, Video);
        Assert.NotNull(pending.FindVideoSeriesMove(Video));
    }

    // ----- The order list -----

    [Fact]
    public void ADragInTheOrderList_StagesTheSeriesChangeAlone_AndNeverAFileMove()
    {
        var editor = new VideoOrderEditor();
        editor.Load(new VideoOrderResponse
        {
            Groups =
            [
                new VideoOrderGroupDto { ParentId = Course, Title = "Part one", Entries = [new VideoOrderEntryDto { Id = Video, Title = "Intro" }, new VideoOrderEntryDto { Id = Guid.NewGuid(), Title = "Basics" }] },
                new VideoOrderGroupDto { ParentId = Garden, Title = "Part two", Entries = [new VideoOrderEntryDto { Id = Guid.NewGuid(), Title = "Soil" }] },
            ],
        }, "Course");
        // The panel had staged this video joining part one, with the move it was shown. The drag now puts it in part two.
        var pending = StagedWithMove(Course);

        Assert.True(editor.Drop(0, 0, 1, 1));
        editor.Stage(pending);

        var series = Assert.Single(pending.Changes);
        Assert.Equal(EditSessionChangeKinds.VideoSeries, series.Kind);
        Assert.Equal(Garden.ToString(), series.TargetId);
        Assert.Null(pending.FindVideoSeriesMove(Video));
        Assert.DoesNotContain(pending.Changes, change => change.Kind == EditSessionChangeKinds.FileMove);
    }

    [Fact]
    public void PuttingTheOrderListBack_DropsTheSeriesChangeItHadReplaced_AndItsMoveWithIt()
    {
        var editor = new VideoOrderEditor();
        editor.Load(new VideoOrderResponse
        {
            Groups = [new VideoOrderGroupDto { ParentId = Course, Title = "Cooking with Sam", Entries = [new VideoOrderEntryDto { Id = Video, Title = "Intro" }] }],
        });
        var pending = StagedWithMove(Course);

        editor.Stage(pending);

        Assert.True(pending.IsEmpty);
    }

    // ----- Review -----

    [Fact]
    public void Review_SaysTheFileMovesToTheSeriesFolder_OrOutOfIt()
    {
        var into = StagedWithMove(Course);
        var outOf = new PendingEditList();
        outOf.SetTarget(EditSessionChangeKinds.VideoSeries, Video, null, EditSessionVideo.NoSeriesToken, null, null, "Cooking with Sam");
        Assert.True(outOf.SetVideoSeriesMove(Video, "/videos/Cooking with Sam/chef.mp4", "/videos/chef.mp4"));

        var intoRow = EditReviewRows.Build(into, _ => "Interview", null).Single(row => row.Kind == EditSessionChangeKinds.FileMove);
        var outRow = EditReviewRows.Build(outOf, _ => "Interview", null).Single(row => row.Kind == EditSessionChangeKinds.FileMove);

        Assert.Equal(EditReviewGroup.PhysicalFiles, intoRow.Group);
        Assert.StartsWith(EditReviewRows.MovedToSeriesFolder, intoRow.After);
        Assert.Equal("/videos/chef.mp4", intoRow.Before);
        Assert.StartsWith(EditReviewRows.MovedOutOfSeriesFolder, outRow.After);
        Assert.Equal("Moved to the series folder", EditReviewRows.MovedToSeriesFolder);
        Assert.Equal("Moved out of the series folder", EditReviewRows.MovedOutOfSeriesFolder);
    }
}
