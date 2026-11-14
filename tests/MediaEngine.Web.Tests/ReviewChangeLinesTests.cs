using MediaEngine.Contracts.Editing;
using MediaEngine.Web.Services.Editing;

namespace MediaEngine.Web.Tests;

/// <summary>Review changes is a compact list (editor v7 D11): the same fact once, one place for files that move, a note only where it matters.</summary>
public sealed class ReviewChangeLinesTests
{
    private static readonly Guid Movie = Guid.Parse("11111111-aaaa-bbbb-cccc-000000000001");

    private static EditReviewRow Row(string kind, EditReviewGroup group, string what, string? before, string after, string? warning = null, Guid? entity = null, string item = "Dune", string changeId = "c1") =>
        new(changeId, null, entity ?? Movie, kind, group, item, what, before, after, warning);

    [Fact]
    public void ARowThatRepeatsAnEarlierOne_IsShownOnce()
    {
        var first = Row(EditSessionChangeKinds.Details, EditReviewGroup.Metadata, "Title", "Dune", "Dune 2", changeId: "a");
        var again = Row(EditSessionChangeKinds.Details, EditReviewGroup.Metadata, "Title", "Dune", "Dune 2", changeId: "b");

        var layout = ReviewChangeLines.Build([first, again]);

        Assert.Equal([first], layout.Lines);
        Assert.Equal(1, layout.Count);
    }

    [Fact]
    public void RowsAboutFiles_JoinOneAmberBlock_AndAreNotLines()
    {
        var title = Row(EditSessionChangeKinds.Details, EditReviewGroup.Metadata, "Title", "Dune", "Dune 2");
        var move = Row(EditSessionChangeKinds.FileMove, EditReviewGroup.PhysicalFiles, "Files", "Where they are now", "Moved and renamed to match the new details", EditReviewRows.FileMoveWarning);

        var layout = ReviewChangeLines.Build([title, move]);

        Assert.Equal([title], layout.Lines);
        var entry = Assert.Single(layout.Files);
        Assert.Equal("Moved and renamed to match the new details", entry.Text);
        Assert.Equal("This file will move when you save.", layout.FileHeading);
    }

    [Fact]
    public void WithNoFileRows_ThereIsNoAmberBlock()
    {
        var layout = ReviewChangeLines.Build([Row(EditSessionChangeKinds.Details, EditReviewGroup.Metadata, "Title", "Dune", "Dune 2")]);

        Assert.Null(layout.FileHeading);
        Assert.Empty(layout.Files);
    }

    [Fact]
    public void SeveralFiles_ShareOneBlock_WhoseHeadingSaysFiles()
    {
        var other = Guid.NewGuid();
        var layout = ReviewChangeLines.Build(
        [
            Row(EditSessionChangeKinds.SupportingFiles, EditReviewGroup.PhysicalFiles, "File moves", "A/one.srt", "B/one.srt", entity: Movie, changeId: "a"),
            Row(EditSessionChangeKinds.SupportingFiles, EditReviewGroup.PhysicalFiles, "File moves", "A/two.srt", "B/two.srt", entity: other, changeId: "b"),
        ]);

        Assert.Equal(2, layout.Files.Count);
        Assert.Equal("These files will move when you save.", layout.FileHeading);
        Assert.Equal("A/one.srt → B/one.srt", layout.Files[0].Text);
    }

    [Fact]
    public void ARenameOnly_SaysFilesWillBeRenamed()
    {
        var layout = ReviewChangeLines.Build([Row(EditSessionChangeKinds.VideoRename, EditReviewGroup.PhysicalFiles, "File names", "As they are now", "Numbered to match the order")]);

        Assert.Equal("Files will be renamed when you save.", layout.FileHeading);
    }

    [Fact]
    public void AVideosFileThatFollowsItsSeries_DoesNotSayTheSeriesFolderPhraseTwice()
    {
        var series = Row(EditSessionChangeKinds.VideoSeries, EditReviewGroup.Relationships, "Series", "Not in a series", "The Expanse", EditReviewRows.VideoSeriesNote, changeId: "s");
        var withPaths = Row(EditSessionChangeKinds.FileMove, EditReviewGroup.PhysicalFiles, "File moves", "Movies/One.mkv", $"{EditReviewRows.MovedToSeriesFolder}: Movies/The Expanse/One.mkv", EditReviewRows.FileMoveWarning, changeId: "m");
        var withoutPaths = Row(EditSessionChangeKinds.FileMove, EditReviewGroup.PhysicalFiles, "File moves", "Where it is now", $"{EditReviewRows.MovedToSeriesFolder}: The Expanse", EditReviewRows.FileMoveWarning, changeId: "n");

        Assert.Equal("Movies/One.mkv → Movies/The Expanse/One.mkv", ReviewChangeLines.Build([series, withPaths]).Files.Single().Text);
        Assert.Equal(EditReviewRows.MovedToSeriesFolder, ReviewChangeLines.Build([series, withoutPaths]).Files.Single().Text);
    }

    [Fact]
    public void ABeforeThatWasNeverRecorded_IsNotStruckThrough()
    {
        Assert.False(ReviewChangeLines.HasBefore(Row(EditSessionChangeKinds.Details, EditReviewGroup.Metadata, "Title", EditReviewRows.AsItIsNow, "Dune 2")));
        Assert.False(ReviewChangeLines.HasBefore(Row(EditSessionChangeKinds.Details, EditReviewGroup.Metadata, "Title", null, "Dune 2")));
        Assert.True(ReviewChangeLines.HasBefore(Row(EditSessionChangeKinds.Details, EditReviewGroup.Metadata, "Title", "Dune", "Dune 2")));
    }

    [Fact]
    public void TheLibraryOnlyReassurance_IsLeftOut_ButARealConsequenceStaysAsANote()
    {
        var libraryOnly = Row(EditSessionChangeKinds.VideoSeries, EditReviewGroup.Relationships, "Series", "Not in a series", "The Expanse", EditReviewRows.VideoSeriesNote);
        var removal = Row(EditSessionChangeKinds.MatchRemoval, EditReviewGroup.Identity, "Match removed", "Dune (tmdb)", "No match", EditReviewRows.MatchRemovalNote);

        Assert.Null(ReviewChangeLines.NoteFor(libraryOnly));
        Assert.Equal(EditReviewRows.MatchRemovalNote, ReviewChangeLines.NoteFor(removal));
    }

    [Fact]
    public void TheItemIsNamedOnEachLineOnlyWhenSeveralItemsAreChanged()
    {
        var one = Row(EditSessionChangeKinds.Details, EditReviewGroup.Metadata, "Title", "A", "B", item: "Dune", changeId: "a");
        var two = Row(EditSessionChangeKinds.Details, EditReviewGroup.Metadata, "Title", "C", "D", item: "Chani", entity: Guid.NewGuid(), changeId: "b");

        Assert.False(ReviewChangeLines.Build([one]).MultipleItems);
        Assert.True(ReviewChangeLines.Build([one, two]).MultipleItems);
    }
}
