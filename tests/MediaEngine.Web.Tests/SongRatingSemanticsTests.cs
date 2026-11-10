using MediaEngine.Web.Services.Playback;

namespace MediaEngine.Web.Tests;

public sealed class SongRatingSemanticsTests
{
    [Theory]
    [InlineData(MediaReaction.Dislike, MediaReaction.Like, MediaReaction.Like)]
    [InlineData(MediaReaction.Like, MediaReaction.Dislike, MediaReaction.Dislike)]
    [InlineData(MediaReaction.Like, MediaReaction.Like, MediaReaction.Neutral)]
    [InlineData(MediaReaction.Dislike, MediaReaction.Dislike, MediaReaction.Neutral)]
    [InlineData(MediaReaction.Neutral, MediaReaction.Like, MediaReaction.Like)]
    public void PressingTheActiveRatingClearsIt_AndTheOtherRatingReplacesIt(MediaReaction current, MediaReaction choice, MediaReaction expected)
        => Assert.Equal(expected, MediaReactionService.ToggleRating(current, choice));
}
