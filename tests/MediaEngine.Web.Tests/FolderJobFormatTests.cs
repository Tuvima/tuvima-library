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

    [Fact]
    public void AMergeJobSaysMergingNotOrganising()
    {
        Assert.Equal(
            "Merging 4 of 12 · about 25 min left",
            FolderJobFormat.Line(new FolderJobStatusDto { JobType = "merge_folder", State = "running", ItemsTotal = 12, ItemsCompleted = 4, EstimatedSecondsRemaining = 1500 }));
        Assert.Equal("Merging 4 of 12", FolderJobFormat.Line(new FolderJobStatusDto { JobType = "merge_folder", State = "running", ItemsTotal = 12, ItemsCompleted = 4 }));
        Assert.Equal("Waiting to merge 12 files", FolderJobFormat.Line(new FolderJobStatusDto { JobType = "merge_folder", State = "queued", ItemsTotal = 12 }));
        Assert.Equal("Waiting to merge", FolderJobFormat.Line(new FolderJobStatusDto { JobType = "merge_folder", State = "queued" }));
        Assert.Equal("merging", FolderJobFormat.Verb(new FolderJobStatusDto { JobType = "merge_folder" }));
        Assert.Equal("organising", FolderJobFormat.Verb(new FolderJobStatusDto { JobType = "organise_folder" }));
        Assert.True(FolderJobFormat.IsMerge(new FolderJobStatusDto { JobType = "merge_folder" }));
        Assert.False(FolderJobFormat.IsMerge(new FolderJobStatusDto()));
    }

    [Fact]
    public void AMergedFolderRowNamesTheTargetAndTheDay()
    {
        var when = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(
            $"Merged into Movies on {when.LocalDateTime.ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture)}",
            FolderJobFormat.MergedLine("Movies", when));
        Assert.Equal("Merged into Movies", FolderJobFormat.MergedLine("Movies", null));
    }

    [Theory]
    [InlineData(@"E:\Plex\Movies", "drive E:")]
    [InlineData(@"d:\Films", "drive D:")]
    [InlineData("/mnt/user/Movies", "the drive that holds this folder")]
    public void TheDriveIsNamedTheWayPeopleSeeIt(string path, string expected)
    {
        Assert.Equal(expected, FolderJobFormat.DriveLabel(path));
    }

    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(500, "500 bytes")]
    [InlineData(2048, "2 KB")]
    [InlineData(850L * 1024 * 1024, "850 MB")]
    [InlineData(13L * 1024 * 1024 * 1024 + 400L * 1024 * 1024, "13.4 GB")]
    [InlineData(48L * 1024 * 1024 * 1024, "48 GB")]
    public void SizesAreRoughAndReadable(long bytes, string expected)
    {
        Assert.Equal(expected, FolderJobFormat.Bytes(bytes).Replace(".0 ", " ", StringComparison.Ordinal));
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
