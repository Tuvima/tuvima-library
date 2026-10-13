using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Services.Formatting;

namespace MediaEngine.Web.Tests;

public sealed class FolderJobFormatTests
{
    [Theory]
    [InlineData(20, "less than a minute")]
    [InlineData(59, "less than a minute")]
    [InlineData(60, "about 1 min")]
    [InlineData(1500, "about 25 min")]
    [InlineData(3600, "about 1 h")]
    [InlineData(10_700, "about 3 h")]
    [InlineData(9000, "about 2.5 h")]
    [InlineData(72_000, "about 20 h")]
    public void TimeIsRoundedToTheWayPeopleSayIt(int seconds, string expected)
    {
        Assert.Equal(expected, FolderJobFormat.Duration(seconds));
    }

    [Fact]
    public void EachStateHasOnePlainSentence()
    {
        Assert.Equal(
            "Organising 1,240 of 8,310 · about 2.5 h left",
            FolderJobFormat.Line(new FolderJobStatusDto { State = "running", ItemsTotal = 8310, ItemsCompleted = 1240, EstimatedSecondsRemaining = 9000 }));
        Assert.Equal(
            "Organising 5 of 100",
            FolderJobFormat.Line(new FolderJobStatusDto { State = "running", ItemsTotal = 100, ItemsCompleted = 5 }));
        Assert.Equal(
            "Queued behind Library Movies",
            FolderJobFormat.Line(new FolderJobStatusDto { State = "queued", QueuedBehind = "Library Movies" }));
        Assert.Equal(
            "Waiting to organise 50 files",
            FolderJobFormat.Line(new FolderJobStatusDto { State = "queued", ItemsTotal = 50 }));
        Assert.Equal(
            "Paused at 7 of 9",
            FolderJobFormat.Line(new FolderJobStatusDto { State = "paused", ItemsTotal = 9, ItemsCompleted = 7 }));
        Assert.Equal(
            "Stopped after 1,240 of 8,310; the rest will be tidied gradually.",
            FolderJobFormat.Line(new FolderJobStatusDto { State = "cancelled", Message = "Stopped after 1,240 of 8,310; the rest will be tidied gradually." }));
    }

    [Theory]
    [InlineData("queued", true)]
    [InlineData("running", true)]
    [InlineData("paused", true)]
    [InlineData("completed", false)]
    [InlineData("cancelled", false)]
    [InlineData("failed", false)]
    public void OnlyAJobWithWorkLeftIsOpen(string state, bool open)
    {
        Assert.Equal(open, FolderJobFormat.IsOpen(new FolderJobStatusDto { State = state }));
    }
}
