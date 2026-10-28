using MediaEngine.Domain.Services;

namespace MediaEngine.Domain.Tests;

public sealed class ReleaseTitleHintsTests
{
    [Theory]
    [InlineData("A Time to Kill (1996) - VC1 TrueHD", "A Time to Kill", "1996")]
    [InlineData("American History X (1998) - VC1 DTS-HD MA", "American History X", "1998")]
    [InlineData("Blade Runner 2049 (2017) 2160p HEVC", "Blade Runner 2049", "2017")]
    [InlineData("1917", "1917", null)]
    [InlineData("1984", "1984", null)]
    [InlineData("2001: A Space Odyssey", "2001: A Space Odyssey", null)]
    [InlineData("The Web", "The Web", null)]
    [InlineData("True Romance", "True Romance", null)]
    [InlineData("Amélie (2001) WEB-DL", "Amélie", "2001")]
    [InlineData("Burnt (2015) - AVC", "Burnt", "2015")]
    [InlineData("Movie.2019.1080p.HDR.AAC", "Movie", "2019")]
    [InlineData("Show (2020) - HDTV", "Show", "2020")]
    [InlineData("Heat (1995) - UHD", "Heat", "1995")]
    [InlineData("Magnum Opus", "Magnum Opus", null)]
    [InlineData("Blade Runner 2049 1080p BluRay", "Blade Runner 2049", null)]
    [InlineData("Wonder Woman 1984 2160p", "Wonder Woman 1984", null)]
    [InlineData("Blade Runner 2049.", "Blade Runner 2049", null)]
    public void SeparatesSearchEvidenceWithoutDestroyingTitles(string input, string title, string? year)
    {
        var hints = ReleaseTitleHints.Parse(input);
        Assert.Equal(title, hints.Title);
        Assert.Equal(year, hints.Year);
    }
}
