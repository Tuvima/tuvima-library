using MediaEngine.Domain.Services;

namespace MediaEngine.Domain.Tests;

public sealed class ExtraFileNamingTests
{
    private static readonly string[] Endings = ["trailer", "behindthescenes", "guide"];

    [Theory]
    [InlineData("Pilot.mkv", "trailer", "Pilot-trailer.mkv")]
    [InlineData("Pilot-trailer.mkv", "behindthescenes", "Pilot-behindthescenes.mkv")]
    [InlineData("Pilot-Trailer.mkv", null, "Pilot.mkv")]
    [InlineData("Lesson 2 - Notes.pdf", "guide", "Lesson 2 - Notes-guide.pdf")]
    public void TheEnding_IsSwappedNotStacked(string name, string? ending, string expected) =>
        Assert.Equal(expected, ExtraFileNaming.Rename(name, ending, Endings));

    [Theory]
    [InlineData("Pilot-trailer.mkv", "trailer")]
    [InlineData("Pilot.mkv", null)]
    public void AnUnchangedName_ReturnsNull(string name, string? ending) =>
        Assert.Null(ExtraFileNaming.Rename(name, ending, Endings));

    [Fact]
    public void ANameThatIsOnlyTheEnding_IsNotStripped() =>
        Assert.Equal("-trailer-guide.mkv", ExtraFileNaming.Rename("-trailer.mkv", "guide", Endings));
}
