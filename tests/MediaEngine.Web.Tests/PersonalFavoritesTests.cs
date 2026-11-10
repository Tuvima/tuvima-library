using MediaEngine.Contracts.ProfileState;
using MediaEngine.Domain.Enums;
using MediaEngine.Web.Services.MediaTiles;

namespace MediaEngine.Web.Tests;

/// <summary>Favorites are the songs a profile saved; Liked is only what it marked Like; My List never carries a song.</summary>
public sealed class PersonalFavoritesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-10T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Favorites_ListTheSavedSongsOnly()
    {
        var song = Guid.NewGuid();
        var movie = Guid.NewGuid();
        var saved = new List<ProfileSavedItemDto>
        {
            new(ProfileEntityKind.Song, song, Now, null),
            new(ProfileEntityKind.Movie, movie, Now, null),
        };

        Assert.Equal([song], PersonalTileIndex.FavoriteMediaIds(saved));
    }

    [Fact]
    public void MyList_LeavesOutSavedSongs_BecauseTheyBelongToFavorites()
    {
        var song = Guid.NewGuid();
        var movie = Guid.NewGuid();
        var saved = new List<ProfileSavedItemDto>
        {
            new(ProfileEntityKind.Song, song, Now, null),
            new(ProfileEntityKind.Movie, movie, Now, null),
        };

        Assert.Equal([movie], PersonalTileIndex.SavedMediaIds(saved));
    }

    [Fact]
    public void Liked_IncludesLikeOnly_AndASongThatIsOnlyFavoritedHasNoReactionToList()
    {
        var liked = Guid.NewGuid();
        var disliked = Guid.NewGuid();
        var favoritedOnly = Guid.NewGuid();
        var reactions = new List<ProfileReactionDto>
        {
            new(ProfileEntityKind.Song, liked, ProfileReactionKind.Like, Now),
            new(ProfileEntityKind.Song, disliked, ProfileReactionKind.Dislike, Now),
        };
        var saved = new List<ProfileSavedItemDto> { new(ProfileEntityKind.Song, favoritedOnly, Now, null) };

        var likedIds = PersonalTileIndex.ReactionMediaIds(reactions);

        Assert.Equal([liked], likedIds);
        Assert.DoesNotContain(favoritedOnly, likedIds);
        Assert.Equal([favoritedOnly], PersonalTileIndex.FavoriteMediaIds(saved));
    }
}
