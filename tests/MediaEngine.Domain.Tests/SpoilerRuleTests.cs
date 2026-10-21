using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;

namespace MediaEngine.Domain.Tests;

public sealed class SpoilerRuleTests
{
    private static readonly Guid Show = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid OtherShow = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid Film = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid Book = Guid.Parse("00000000-0000-0000-0000-0000000000c1");

    private static EpisodePosition E(int season, int episode) => new(season, episode);

    private static WorkPosition InShow(int season, int episode) => new(Show, E(season, episode));

    private static ViewerProgress Viewer(params (Guid Title, TitleProgress Progress)[] titles) =>
        new(titles.ToDictionary(title => title.Title, title => title.Progress));

    private static TitleProgress WatchedUpTo(int season, int episode) =>
        new(Started: true, Finished: false, FurthestStartedEpisode: E(season, episode), FurthestFinishedEpisode: E(season, episode));

    private static CharacterVisibility Judge(
        SpoilerLevel level,
        IReadOnlyList<WorkPosition> firstSeen,
        ViewerProgress progress,
        IReadOnlyList<WorkPosition>? spoilerFor = null) =>
        SpoilerRule.Evaluate(level, firstSeen, spoilerFor ?? [], progress);

    // Level "Everything" -----------------------------------------------------------------------------------------

    [Fact]
    public void Everything_ShowsEveryCharacter_EvenOneFirstSeenLaterAndSpoilerMarked()
    {
        var progress = Viewer((Show, WatchedUpTo(1, 1)));

        Assert.Equal(CharacterVisibility.Shown, Judge(SpoilerLevel.Everything, [InShow(1, 9)], progress, [new WorkPosition(Film)]));
        Assert.Equal(CharacterVisibility.Shown, Judge(SpoilerLevel.Everything, [], ViewerProgress.Empty));
    }

    // Level "Up to where I am": episodes ------------------------------------------------------------------------

    [Theory]
    [InlineData(1, 1, 1, 1, CharacterVisibility.Shown)]
    [InlineData(1, 1, 1, 2, CharacterVisibility.Hidden)]
    [InlineData(1, 3, 1, 2, CharacterVisibility.Shown)]
    [InlineData(1, 2, 1, 2, CharacterVisibility.Shown)]
    [InlineData(1, 2, 1, 3, CharacterVisibility.Hidden)]
    [InlineData(2, 1, 1, 9, CharacterVisibility.Shown)]
    [InlineData(1, 9, 2, 1, CharacterVisibility.Hidden)]
    [InlineData(2, 4, 2, 4, CharacterVisibility.Shown)]
    public void CharacterFirstSeenAtAnEpisode_IsShownOnlyOnceTheViewerHasReachedIt(
        int viewerSeason, int viewerEpisode, int seenSeason, int seenEpisode, CharacterVisibility expected)
    {
        var progress = Viewer((Show, WatchedUpTo(viewerSeason, viewerEpisode)));

        Assert.Equal(expected, Judge(SpoilerLevel.UpToProgress, [InShow(seenSeason, seenEpisode)], progress));
    }

    [Fact]
    public void ACharacterSeenOnlyWithinTheShowAsAWhole_IsNoPlotDetails_ForAViewerPartWayThrough()
    {
        var progress = Viewer((Show, WatchedUpTo(1, 2)));

        Assert.Equal(CharacterVisibility.NoPlotDetails, Judge(SpoilerLevel.UpToProgress, [new WorkPosition(Show)], progress));
    }

    [Fact]
    public void ACharacterFirstSeenInAShowTheViewerHasNotStarted_IsNoPlotDetails_NotHidden()
    {
        Assert.Equal(CharacterVisibility.NoPlotDetails, Judge(SpoilerLevel.UpToProgress, [InShow(1, 5)], ViewerProgress.Empty));
        Assert.Equal(CharacterVisibility.NoPlotDetails, Judge(SpoilerLevel.UpToProgress, [new WorkPosition(Show)], ViewerProgress.Empty));
    }

    [Fact]
    public void AFinishedShow_ShowsEveryCharacterInIt()
    {
        var progress = Viewer((Show, new TitleProgress(true, true, E(3, 10), E(3, 10))));

        Assert.Equal(CharacterVisibility.Shown, Judge(SpoilerLevel.UpToProgress, [InShow(3, 10)], progress));
        Assert.Equal(CharacterVisibility.Shown, Judge(SpoilerLevel.UpToProgress, [new WorkPosition(Show)], progress));
    }

    // Level "Up to where I am": films and books -----------------------------------------------------------------

    [Fact]
    public void ACharacterInAFilmOrBook_IsNoPlotDetails_UntilTheWorkIsFinished()
    {
        var started = new TitleProgress(Started: true, Finished: false);
        var finished = new TitleProgress(Started: true, Finished: true);

        Assert.Equal(CharacterVisibility.NoPlotDetails, Judge(SpoilerLevel.UpToProgress, [new WorkPosition(Book)], ViewerProgress.Empty));
        Assert.Equal(CharacterVisibility.NoPlotDetails, Judge(SpoilerLevel.UpToProgress, [new WorkPosition(Book)], Viewer((Book, started))));
        Assert.Equal(CharacterVisibility.Shown, Judge(SpoilerLevel.UpToProgress, [new WorkPosition(Book)], Viewer((Book, finished))));
    }

    [Fact]
    public void ACharacterNothingIsKnownAbout_IsNoPlotDetails()
    {
        Assert.Equal(CharacterVisibility.NoPlotDetails, Judge(SpoilerLevel.UpToProgress, [], Viewer((Show, WatchedUpTo(1, 1)))));
    }

    // Several titles ---------------------------------------------------------------------------------------------

    [Fact]
    public void ACharacterMetInAFinishedFilm_IsShown_EvenIfAShowFirstSeesThemLater()
    {
        var progress = Viewer((Film, new TitleProgress(true, true)), (Show, WatchedUpTo(1, 1)));

        Assert.Equal(
            CharacterVisibility.Shown,
            Judge(SpoilerLevel.UpToProgress, [new WorkPosition(Film), InShow(1, 8)], progress));
    }

    [Fact]
    public void ACharacterLaterInEveryTitleItAppearsIn_IsHidden_ButNoPlotDetailsWinsOverHidden()
    {
        var progress = Viewer((Show, WatchedUpTo(1, 1)), (OtherShow, WatchedUpTo(1, 2)));

        Assert.Equal(
            CharacterVisibility.Hidden,
            Judge(SpoilerLevel.UpToProgress, [InShow(1, 4), new WorkPosition(OtherShow, E(1, 9))], progress));
        Assert.Equal(
            CharacterVisibility.NoPlotDetails,
            Judge(SpoilerLevel.UpToProgress, [InShow(1, 4), new WorkPosition(Film)], progress));
    }

    // Spoiler markers --------------------------------------------------------------------------------------------

    [Fact]
    public void ASpoilerMarkedCharacter_IsHidden_UntilTheMarkedWorkIsFinished()
    {
        var spoilerFor = new[] { new WorkPosition(Book) };
        var reachedShow = Viewer((Show, WatchedUpTo(1, 5)));

        Assert.Equal(CharacterVisibility.Hidden, Judge(SpoilerLevel.UpToProgress, [InShow(1, 1)], reachedShow, spoilerFor));
        Assert.Equal(
            CharacterVisibility.Hidden,
            Judge(SpoilerLevel.UpToProgress, [InShow(1, 1)], Viewer((Show, WatchedUpTo(1, 5)), (Book, new TitleProgress(true, false))), spoilerFor));
        Assert.Equal(
            CharacterVisibility.Shown,
            Judge(SpoilerLevel.UpToProgress, [InShow(1, 1)], Viewer((Show, WatchedUpTo(1, 5)), (Book, new TitleProgress(true, true))), spoilerFor));
    }

    [Fact]
    public void ASpoilerMarkedForAWorkTheViewerDoesNotOwn_IsHidden()
    {
        var progress = Viewer((Show, WatchedUpTo(1, 5)));

        Assert.Equal(
            CharacterVisibility.Hidden,
            Judge(SpoilerLevel.UpToProgress, [InShow(1, 1)], progress, [WorkPosition.Unowned]));
    }

    [Fact]
    public void ASpoilerMarkedForAnEpisode_IsHidden_UntilThatEpisodeIsFinished()
    {
        var spoilerFor = new[] { InShow(1, 4) };
        var started = new TitleProgress(true, false, E(1, 4), E(1, 3));
        var finished = new TitleProgress(true, false, E(1, 5), E(1, 4));

        Assert.Equal(CharacterVisibility.Hidden, Judge(SpoilerLevel.UpToProgress, [InShow(1, 1)], Viewer((Show, started)), spoilerFor));
        Assert.Equal(CharacterVisibility.Shown, Judge(SpoilerLevel.UpToProgress, [InShow(1, 1)], Viewer((Show, finished)), spoilerFor));
    }

    [Fact]
    public void EveryMarkedWorkMustBeFinished_ForTheCharacterToShow()
    {
        var progress = Viewer((Show, WatchedUpTo(1, 1)), (Book, new TitleProgress(true, true)));

        Assert.Equal(
            CharacterVisibility.Hidden,
            Judge(SpoilerLevel.UpToProgress, [InShow(1, 1)], progress, [new WorkPosition(Book), new WorkPosition(Film)]));
    }

    // Value types ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("up_to_progress", SpoilerLevel.UpToProgress)]
    [InlineData("everything", SpoilerLevel.Everything)]
    [InlineData("EVERYTHING", SpoilerLevel.Everything)]
    [InlineData(null, SpoilerLevel.UpToProgress)]
    [InlineData("", SpoilerLevel.UpToProgress)]
    [InlineData("nonsense", SpoilerLevel.UpToProgress)]
    public void ASpoilerLevelSpelling_ReadsAsTheLevelItNames_AndAnythingElseIsTheSafeDefault(string? value, SpoilerLevel expected)
    {
        Assert.Equal(expected, SpoilerLevels.Parse(value));
    }

    [Fact]
    public void OnlyTheTwoSpellingsAreValid_AndTheyRoundTrip()
    {
        Assert.True(SpoilerLevels.IsValid("up_to_progress"));
        Assert.True(SpoilerLevels.IsValid("Everything"));
        Assert.False(SpoilerLevels.IsValid("whole_work"));
        Assert.False(SpoilerLevels.IsValid(null));
        Assert.Equal("up_to_progress", SpoilerLevels.ToWire(SpoilerLevel.UpToProgress));
        Assert.Equal("everything", SpoilerLevels.ToWire(SpoilerLevel.Everything));
        Assert.Equal(SpoilerLevels.UpToProgress, SpoilerLevels.Default);
    }

    [Fact]
    public void EpisodesOrderBySeasonThenNumber()
    {
        Assert.True(E(1, 9) < E(2, 1));
        Assert.True(E(2, 1) > E(1, 9));
        Assert.True(E(2, 3) <= E(2, 3));
        Assert.True(E(2, 3) >= E(2, 3));
        Assert.Equal([E(1, 2), E(1, 10), E(2, 1)], new[] { E(2, 1), E(1, 10), E(1, 2) }.Order());
    }
}
