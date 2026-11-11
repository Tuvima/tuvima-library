using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.Extras;

namespace MediaEngine.Ingestion.Tests;

public sealed class ExtraNameGuesserTests
{
    [Fact]
    public void ASeasonTrailerName_IsATrailerOfThatSeason()
    {
        var guess = ExtraNameGuesser.Guess("Season 1 trailer.mkv", ItemExtraKinds.Clip);

        Assert.Equal(new ExtraNameGuess(ExtraKinds.Trailer, 1, null, null), guess);
    }

    [Fact]
    public void AWorkbookPdf_IsADocument_WithNoPlace()
    {
        var guess = ExtraNameGuesser.Guess("Workbook.pdf", ItemExtraKinds.Document);

        Assert.Equal(new ExtraNameGuess(ExtraKinds.Document, null, null, null), guess);
    }

    [Fact]
    public void ASeasonAndEpisodeCode_PointsAtThatEpisode()
    {
        var guess = ExtraNameGuesser.Guess("S02E05 - Making of.mkv", ItemExtraKinds.Clip);

        Assert.Equal(ExtraKinds.BehindTheScenes, guess.ExtraKind);
        Assert.Equal((2, 5), (guess.Season, guess.Episode));
    }

    [Fact]
    public void AFileInASeasonFolder_TakesTheSeasonFromTheFolder()
    {
        var guess = ExtraNameGuesser.Guess("Deleted scenes.mkv", ItemExtraKinds.Clip, "Season 2");

        Assert.Equal((ExtraKinds.DeletedScene, 2), (guess.ExtraKind, guess.Season));
    }

    [Fact]
    public void ATrackName_PointsAtTheTrack_AndNotASeason()
    {
        var guess = ExtraNameGuesser.Guess("Track 4 interview.mp4", ItemExtraKinds.Clip, "Season 1");

        Assert.Equal((ExtraKinds.Interview, (int?)null, (int?)4), (guess.ExtraKind, guess.Season, guess.Track));
    }

    [Fact]
    public void ASpecialsName_PointsAtSeasonZero()
    {
        var guess = ExtraNameGuesser.Guess("Specials featurette.mkv", ItemExtraKinds.Clip);

        Assert.Equal((ExtraKinds.Featurette, 0), (guess.ExtraKind, guess.Season));
    }

    [Fact]
    public void AClipWithNoClues_IsAnOtherClip()
    {
        var guess = ExtraNameGuesser.Guess("sample.mkv", ItemExtraKinds.Clip);

        Assert.Equal(ExtraKinds.OtherClip, guess.ExtraKind);
        Assert.Null(guess.Season);
    }
}
