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
    public void SeparatesSearchEvidenceWithoutDestroyingTitles(string input, string title, string? year)
    {
        var hints = ReleaseTitleHints.Parse(input);
        Assert.Equal(title, hints.Title);
        Assert.Equal(year, hints.Year);
    }
}
