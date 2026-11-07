using MediaEngine.Api.Services.Matching;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Tests;

/// <summary>What the Match tool starts from when an item has no match yet (universal editor v3, WP4): the file name, read as text.</summary>
public sealed class EditorFileNameParserTests
{
    [Theory]
    [InlineData("/media/tv/The Continental/Season 1/The.Continental.S01E02.Night.Two.1080p.WEB-DL.x264.mkv", "The Continental", 1, 2, "Night Two")]
    [InlineData("Other Show - 2x05 - The Fifth.mp4", "Other Show", 2, 5, "The Fifth")]
    [InlineData("Show Name Season 3 Episode 7.mkv", "Show Name", 3, 7, null)]
    [InlineData("show.s00e03.special.mkv", "show", 0, 3, "special")]
    public void ATvFileName_GivesTheShowSeasonAndEpisode(string path, string show, int season, int episode, string? title)
    {
        var parsed = EditorFileNameParser.Parse(path, isTv: true);

        Assert.Equal(show, parsed.Show);
        Assert.Equal(season, parsed.Season);
        Assert.Equal(episode, parsed.Episode);
        Assert.Equal(title, parsed.Title);
    }

    [Fact]
    public void ATvFileWithNoShowInItsName_TakesTheShowFromTheFolderAboveTheSeasonFolder()
    {
        var parsed = EditorFileNameParser.Parse("/tv/Mystery Show (2019)/Season 2/Mystery Show.mkv", isTv: true);

        Assert.Equal("Mystery Show", parsed.Show);
        Assert.Equal(2, parsed.Season);
        Assert.Null(parsed.Episode);
    }

    [Fact]
    public void ATvFileWithNoNumbers_KeepsItsTitle_AndSaysNoEpisode()
    {
        var parsed = EditorFileNameParser.Parse("/tv/Loose Files/Behind The Scenes.mkv", isTv: true);

        Assert.Null(parsed.Season);
        Assert.Null(parsed.Episode);
        Assert.Equal("Loose Files", parsed.Show);
        Assert.Equal("Behind The Scenes", parsed.Title);
    }

    [Fact]
    public void AnythingElse_GivesTheTitleWithTheYearAndReleaseTagsTakenOut()
    {
        var parsed = EditorFileNameParser.Parse("C:\\Movies\\Blade.Runner.(1982).2160p.BluRay.x265.mkv", isTv: false);

        Assert.Equal("Blade Runner", parsed.Title);
        Assert.Equal("1982", parsed.Year);
        Assert.Null(parsed.Show);
        Assert.Null(parsed.Episode);
    }

    [Fact]
    public void AnEditionMarkerAfterTheYear_IsReadAsTheEdition_AndLeavesTheTitle()
    {
        var parsed = EditorFileNameParser.Parse("/movies/Movie.Name.2019.Remastered.1080p.BluRay.mkv", isTv: false);

        Assert.Equal("Movie Name", parsed.Title);
        Assert.Equal("2019", parsed.Year);
        Assert.Equal("Remastered", parsed.Edition);
    }

    [Fact]
    public void AMusicFileName_GivesTheTrackNumberAndTitle()
    {
        var parsed = EditorFileNameParser.ParseMusic("/music/Radiohead/OK Computer/1-05 Karma Police.flac");

        Assert.Equal("Karma Police", parsed.Title);
        Assert.Equal(5, parsed.Track);
        Assert.Equal(1, parsed.Disc);
    }

    [Fact]
    public void AWordTheOwnerAddedToTheCleaningFile_IsTakenOutOfTheTitle()
    {
        var rules = FilenameCleaningConfigurationProvider.TryRead(
            "{ \"version\": \"1\", \"custom_noise_words\": [\"FanSub\"] }", FilenameCleaningConfigurationProvider.BuiltIn, out _);

        var parsed = EditorFileNameParser.Parse("/movies/Some.Film.FanSub.2012.mkv", isTv: false, rules!);

        Assert.Equal("Some Film", parsed.Title);
        Assert.Equal("2012", parsed.Year);
    }

    [Fact]
    public void ABlankPath_GivesNothing()
    {
        var parsed = EditorFileNameParser.Parse("  ", isTv: true);

        Assert.Equal(new ParsedFileName(null, null, null, null, null), parsed);
    }
}
