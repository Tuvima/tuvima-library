using MediaEngine.Domain.Services;

namespace MediaEngine.Domain.Tests;

public sealed class VideoFolderWordingTests
{
    [Theory]
    [InlineData(88, 14, "88 videos · 14 sections")]
    [InlineData(1, 0, "1 video")]
    [InlineData(5, 1, "5 videos")]
    public void TheCountsLine_NamesVideosAndOnlyRealSections(int videos, int sections, string expected)
        => Assert.Equal(expected, VideoFolderWording.CountsLine(videos, sections));

    [Theory]
    [InlineData(2280d, "38 minutes left")]
    [InlineData(30d, "1 minute left")]
    [InlineData(61d, "2 minutes left")]
    public void TimeLeft_IsWrittenInWholeMinutesRoundedUp(double seconds, string expected)
        => Assert.Equal(expected, VideoFolderWording.MinutesLeft(seconds));

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(-5d)]
    [InlineData(double.NaN)]
    public void TimeLeft_IsNothingWhenNoneIsLeftOrItIsNotKnown(double? seconds)
        => Assert.Null(VideoFolderWording.MinutesLeft(seconds));

    [Fact]
    public void TheHeroLine_AddsNextUpAndTimeLeftOnlyOnceAVideoWasStarted()
    {
        Assert.Equal("88 videos · 14 sections", VideoFolderWording.HeroLine(88, 14, null, null));
        Assert.Equal("88 videos · 14 sections · Next up: Variables · 25 minutes left", VideoFolderWording.HeroLine(88, 14, "Variables", 1500));
        Assert.Equal("3 videos · Next up: Variables", VideoFolderWording.HeroLine(3, 1, "Variables", null));
        Assert.False(VideoFolderWording.UsesTvWording(VideoFolderWording.HeroLine(88, 14, "Variables", 1500)));
    }

    [Theory]
    [InlineData("3", "Ball handling", "3 · Ball handling")]
    [InlineData("03", "Ball handling", "3 · Ball handling")]
    [InlineData("E3", "Ball handling", "3 · Ball handling")]
    [InlineData("Episode 12", "Ball handling", "12 · Ball handling")]
    [InlineData(null, "Ball handling", "Ball handling")]
    [InlineData("", "Ball handling", "Ball handling")]
    [InlineData("Special", "Ball handling", "Ball handling")]
    public void ANumberedTitle_IsTheVideosNumberAndTitle_AndNeverASeasonOrEpisodeCode(string? number, string title, string expected)
    {
        Assert.Equal(expected, VideoFolderWording.NumberedTitle(number, title));
        Assert.False(VideoFolderWording.UsesTvWording(VideoFolderWording.NumberedTitle(number, title)));
    }

    [Theory]
    [InlineData("Week 1", null, "Week 1")]
    [InlineData("  Week 1  ", "", "Week 1")]
    [InlineData("Child", "Parent", "Parent / Child")]
    [InlineData(null, null, "Videos")]
    [InlineData(" ", null, "Videos")]
    public void ASectionTitle_IsItsFolderName_AndASubFolderReadsParentSlashChild(string? name, string? parent, string expected)
    {
        Assert.Equal(expected, VideoFolderWording.SectionTitle(name, parent));
        Assert.False(VideoFolderWording.UsesTvWording(VideoFolderWording.SectionTitle(name, parent)));
    }

    [Theory]
    [InlineData("Week 1", 12, "Week 1 · 12 videos")]
    [InlineData("Week 2", 1, "Week 2 · 1 video")]
    [InlineData("Parent / Child", 3, "Parent / Child · 3 videos")]
    [InlineData("Empty", 0, "Empty")]
    public void ASectionOption_NamesTheFolderAndItsVideoCount(string title, int count, string expected)
        => Assert.Equal(expected, VideoFolderWording.SectionOptionLabel(title, count));

    [Theory]
    [InlineData("Videos", true)]
    [InlineData("Video", true)]
    [InlineData("TV", false)]
    [InlineData("TvEpisode", false)]
    [InlineData("Movies", false)]
    [InlineData(null, false)]
    public void OnlyAVideosLibraryItemUsesVideoWording(string? mediaType, bool expected)
        => Assert.Equal(expected, VideoFolderWording.IsVideoItemMediaType(mediaType));
}
