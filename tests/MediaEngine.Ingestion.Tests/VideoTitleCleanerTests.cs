using MediaEngine.Domain.Configuration;
using MediaEngine.Ingestion.Videos;

namespace MediaEngine.Ingestion.Tests;

public sealed class VideoTitleCleanerTests
{
    private static readonly VideoTitleCleaningConfiguration Rules = new();

    [Theory]
    [InlineData("01 - Introduction", "Introduction", 1)]
    [InlineData("Lesson 12 - Loops and lists", "Loops and lists", 12)]
    [InlineData("3. Variables", "Variables", 3)]
    [InlineData("How_to_bake_bread_1080p_x264", "How to bake bread", null)]
    [InlineData("Dr. Strangelove", "Dr. Strangelove", null)]
    [InlineData("2024-01-05 Team meeting", "2024-01-05 Team meeting", null)]
    [InlineData("42", "42", 42)]
    public void NamesBecomeReadableTitles(string stem, string title, int? number)
    {
        var cleaned = VideoTitleCleaner.Clean(stem, Rules);

        Assert.Equal(title, cleaned.Title);
        Assert.Equal(number, cleaned.Number);
    }

    [Fact]
    public void AYoutubeIdSuffixIsReadAndRemovedFromTheTitle()
    {
        var cleaned = VideoTitleCleaner.Clean("Learn Rust in 30 minutes [dQw4w9WgXcQ]", Rules);

        Assert.Equal("Learn Rust in 30 minutes", cleaned.Title);
        Assert.Equal("dQw4w9WgXcQ", cleaned.YoutubeId);
    }

    [Theory]
    [InlineData("A film [Documentary]")]
    [InlineData("Holiday [Anniversary]")]
    [InlineData("Clip [short]")]
    public void ElevenPlainLettersInBracketsAreNotAnId(string stem)
    {
        Assert.Null(VideoTitleCleaner.FindYoutubeId(stem));
    }

    [Fact]
    public void AFolderNameKeepsItsNumber()
    {
        var cleaned = VideoTitleCleaner.Clean("01 - Basics", Rules, stripLeadingNumber: false);

        Assert.Equal("01 - Basics", cleaned.Title.Replace("  ", " "));
        Assert.Equal(1, cleaned.Number);
    }

    [Fact]
    public void LeadingNumberStaysOffWhenTheRuleIsOff()
    {
        var rules = new VideoTitleCleaningConfiguration { StripLeadingNumber = false };

        Assert.Equal("01 - Intro", VideoTitleCleaner.Clean("01 - Intro", rules).Title);
    }
}
