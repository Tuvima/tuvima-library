using MediaEngine.Domain.Text;
namespace MediaEngine.Domain.Tests;

public sealed class GenreLabelsTests
{
    [Fact]
    public void CommaJoinedProviderString_BecomesSeparateShortLabels()
    {
        var labels = GenreLabels.Split("Adventure sci-fi, books, sci-fi & fantasy, Science Fiction, high tech sci-fi, mysteries & thrillers");
        Assert.Equal(["adventure sci-fi", "books", "sci-fi & fantasy", "science fiction", "high tech sci-fi", "mysteries & thrillers"], labels);
    }

    [Fact]
    public void RepeatedOrBlankParts_AreDroppedAndCaseIsIgnored()
    {
        Assert.Equal(["drama"], GenreLabels.Split("Drama, , drama; DRAMA"));
        Assert.Empty(GenreLabels.Split("  "));
        Assert.Empty(GenreLabels.Split(null));
    }

    [Fact]
    public void SingleGenre_IsKeptWhole()
    {
        Assert.Equal(["rock & roll"], GenreLabels.Split(" Rock & Roll "));
    }
}
