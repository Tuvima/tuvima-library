using MediaEngine.Contracts.ProfileState;
using MediaEngine.Domain.Enums;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;

namespace MediaEngine.Web.Tests;

public sealed class SongRatingSemanticsTests
{
    [Theory]
    [InlineData(MediaReaction.Love, MediaReaction.Like, MediaReaction.Love)]
    [InlineData(MediaReaction.Love, MediaReaction.Dislike, MediaReaction.Dislike)]
    [InlineData(MediaReaction.Like, MediaReaction.Like, MediaReaction.Neutral)]
    [InlineData(MediaReaction.Dislike, MediaReaction.Dislike, MediaReaction.Neutral)]
    [InlineData(MediaReaction.Neutral, MediaReaction.Like, MediaReaction.Like)]
    public void RatingAndFavoritesRemainSeparate(MediaReaction current, MediaReaction choice, MediaReaction expected)
        => Assert.Equal(expected, MediaReactionService.ToggleRating(current, choice));

    [Fact]
    public async Task FavoritesIncludesOnlyLovedSongs()
    {
        var loved = Guid.NewGuid(); var liked = Guid.NewGuid(); var album = Guid.NewGuid();
        IReadOnlyList<ProfileReactionDto> states = [new(ProfileEntityKind.Song, loved, ProfileReactionKind.Love, DateTimeOffset.UtcNow), new(ProfileEntityKind.Song, liked, ProfileReactionKind.Like, DateTimeOffset.UtcNow), new(ProfileEntityKind.Album, album, ProfileReactionKind.Love, DateTimeOffset.UtcNow)];
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.GetProfileReactionsAsync), _ => Task.FromResult(states)));
        var service = new MediaReactionService(api);
        Assert.Equal(loved, Assert.Single(await service.GetFavoriteWorkIdsAsync(Guid.NewGuid())));
    }
}
