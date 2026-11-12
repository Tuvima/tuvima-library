using MediaEngine.Domain.Services;

namespace MediaEngine.Domain.Tests;

public sealed class LeadingNumberTitleCleanerTests
{
    [Theory]
    [InlineData("5.5 - The Vital Abyss", "The Vital Abyss", "5.5")]
    [InlineData("03.5 - The Vital Abyss", "The Vital Abyss", "03.5")]
    [InlineData("02. Dune", "Dune", "02")]
    [InlineData("  1) Leviathan Wakes", "Leviathan Wakes", "1")]
    public void Strip_RemovesLeadingNumberAndReturnsIt(string title, string expected, string number)
    {
        Assert.Equal(expected, LeadingNumberTitleCleaner.Strip(title, out var found));
        Assert.Equal(number, found);
    }

    [Theory]
    [InlineData("1984")]
    [InlineData("11/22/63")]
    [InlineData("2001: A Space Odyssey")]
    [InlineData("The Vital Abyss")]
    [InlineData("7 - X")]
    public void Strip_LeavesTitlesWithoutAPrefixAlone(string title)
    {
        Assert.Equal(title, LeadingNumberTitleCleaner.Strip(title, out var found));
        Assert.Null(found);
    }
}
