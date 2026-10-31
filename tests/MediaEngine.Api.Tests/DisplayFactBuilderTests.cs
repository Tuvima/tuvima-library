using MediaEngine.Api.Services.Display;

namespace MediaEngine.Api.Tests;

public sealed class DisplayFactBuilderTests
{
    [Fact]
    public void MovieFacts_UseClassificationYearRuntimeAndStarScore()
    {
        var facts = DisplayFactBuilder.Build(
            "Movie",
            "Arrival",
            year: "2016",
            contentRating: "PG-13",
            runtime: "1h 56m",
            starRating: "7.9");

        Assert.Equal(["PG-13", "2016", "1h 56m", "★ 7.9"], facts);
    }

    [Fact]
    public void MovieFacts_LabelNumericRuntimeAndRoundStarScore()
    {
        var facts = DisplayFactBuilder.Build(
            "Movie",
            "The Shawshank Redemption",
            year: "1994",
            contentRating: "R",
            runtime: "142",
            starRating: "8.724");

        Assert.Equal(["R", "1994", "142 min", "★ 8.7"], facts);
    }

    [Fact]
    public void BookFacts_UseAuthorClassificationYearPagesAndStarScore()
    {
        var facts = DisplayFactBuilder.Build(
            "Book",
            "Leviathan Wakes",
            year: "2011",
            author: "James S. A. Corey",
            contentRating: "M",
            pageCount: "592",
            starRating: "4.3");

        Assert.Equal(["James S. A. Corey", "M", "2011", "592 pages", "★ 4.3"], facts);
    }

    [Fact]
    public void ComicFacts_IncludePublisherWhenAvailable()
    {
        var facts = DisplayFactBuilder.Build(
            "Comic",
            "Saga #1",
            year: "2012",
            author: "Brian K. Vaughan",
            publisher: "Image Comics",
            pageCount: "44");

        Assert.Equal(["Brian K. Vaughan", "Image Comics", "2012", "44 pages"], facts);
    }

    [Fact]
    public void AudiobookFacts_UseAuthorNarratorYearDurationAndScore()
    {
        var facts = DisplayFactBuilder.Build(
            "Audiobook",
            "Project Hail Mary",
            year: "2021",
            author: "Andy Weir",
            narrator: "Ray Porter",
            genre: "Science Fiction",
            duration: "16h 10m",
            starRating: "4.8");

        Assert.Equal(["Andy Weir", "Narrated by Ray Porter", "2021", "16h 10m", "★ 4.8"], facts);
    }

    [Fact]
    public void MusicFacts_UseArtistYearDurationAndScore()
    {
        var facts = DisplayFactBuilder.Build(
            "Music",
            "Not Strong Enough",
            year: "2023",
            artist: "boygenius",
            duration: "3:54",
            starRating: "4.6");

        Assert.Equal(["boygenius", "2023", "3:54", "★ 4.6"], facts);
    }

    [Fact]
    public void TvFacts_ReadFileProbeSecondsAsMinutesNotAsMinutesOfSeconds()
    {
        var facts = DisplayFactBuilder.Build("TV", "The Show", year: "2020", durationSeconds: "1361.4");

        Assert.Equal(["2020", "23 min"], facts);
    }

    [Fact]
    public void MovieFacts_PreferTheProviderRuntimeOverTheFileProbeSeconds()
    {
        var facts = DisplayFactBuilder.Build("Movie", "Arrival", runtime: "116", durationSeconds: "6980");

        Assert.Equal(["116 min"], facts);
    }

    [Fact]
    public void MusicFacts_ReadFileProbeSecondsAsTrackLength()
    {
        var facts = DisplayFactBuilder.Build("Music", "Soul Love", artist: "David Bowie", durationSeconds: "214.2");

        Assert.Equal(["David Bowie", "3:34"], facts);
    }

    [Fact]
    public void MusicFacts_PreferTheFilesLengthOverTheProvidersRoundedMinutes()
    {
        var facts = DisplayFactBuilder.Build("Music", "Soul Love", artist: "David Bowie", duration: "3.5", durationSeconds: "214.2");

        Assert.Equal(["David Bowie", "3:34"], facts);
    }

    [Fact]
    public void MusicFacts_ReadASmallProviderLengthAsMinutes()
    {
        var facts = DisplayFactBuilder.Build("Music", "Soul Love", artist: "David Bowie", duration: "3.5");

        Assert.Equal(["David Bowie", "3:30"], facts);
    }

    [Fact]
    public void MusicFacts_FormatProviderMillisecondsAsTrackLength()
    {
        var facts = DisplayFactBuilder.Build(
            "Music",
            "Soul Love",
            artist: "David Bowie",
            duration: "214200");

        Assert.Equal(["David Bowie", "3:34"], facts);
    }

    [Fact]
    public void GenresAndEpisodeLabels_AreNotHoverFacts()
    {
        var facts = DisplayFactBuilder.Build(
            "TV",
            "Pilot",
            year: "2008",
            genre: "Crime",
            showName: "Breaking Bad",
            season: "1",
            episode: "1");

        Assert.Equal(["2008"], facts);
    }
}
