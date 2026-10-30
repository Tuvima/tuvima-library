using MediaEngine.Api.Security;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Tests;

/// <summary>
/// What a Kid's profile may see (library access D4, D9, D10): the household's level for the item's area (Watch, Read or Listen), one
/// "Show unrated items" switch for items with no rating, and a rated item always following its level. Everyone else is unaffected.
/// </summary>
public sealed class KidsContentLimitFilterTests
{
    private const string VaultLibrary = "88888888-8888-4888-8888-888888888888";

    private static ContentLimitFilter Kid(string watch = "PG", string read = "PG", string listen = "PG", bool showUnrated = true) =>
        new(new ProfileContentLimit(true, new HouseholdKidSettings(watch, read, listen, showUnrated)));

    [Theory]
    [InlineData("Movies")]
    [InlineData("TV")]
    [InlineData("Videos")]
    [InlineData("Books")]
    [InlineData("Comics")]
    [InlineData("Music")]
    [InlineData("Audiobooks")]
    public void AKidsProfileAtPg_SeesEveryUnratedItem_WhenShowUnratedItemsIsOn_TheDefault(string mediaType)
    {
        var kid = Kid();

        Assert.True(kid.AllowsItem(null, mediaType));
        Assert.True(kid.AllowsItem("", mediaType));
        Assert.True(kid.AllowsItem("Not Rated", mediaType));
        Assert.True(kid.AllowsItem("NR", mediaType));
    }

    [Theory]
    [InlineData("Movies")]
    [InlineData("TV")]
    [InlineData("Videos")]
    [InlineData("Books")]
    [InlineData("Comics")]
    [InlineData("Music")]
    [InlineData("Audiobooks")]
    public void AKidsProfileAtPg_SeesNoUnratedItem_WhenShowUnratedItemsIsOff(string mediaType)
    {
        var kid = Kid(showUnrated: false);

        Assert.False(kid.AllowsItem(null, mediaType));
        Assert.False(kid.AllowsItem("", mediaType));
        Assert.False(kid.AllowsItem("Not Rated", mediaType));
        Assert.False(kid.AllowsItem("Unrated", mediaType));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARatedRFilm_StaysHiddenFromAKidsProfileAtPg_WhetherOrNotUnratedItemsAreShown(bool showUnrated)
    {
        var kid = Kid(showUnrated: showUnrated);

        Assert.False(kid.AllowsItem("R", "Movies"));
        Assert.False(kid.AllowsItem("TV-MA", "TV"));
        Assert.False(kid.AllowsItem("PG-13", "Movies"));
        Assert.True(kid.AllowsItem("PG", "Movies"));
        Assert.True(kid.AllowsItem("G", "Movies"));
    }

    [Fact]
    public void WatchReadAndListen_FollowTheirOwnLevels()
    {
        // Watch PG-13, Read G, Listen R: music narrows less than books, and books narrow more than films.
        var kid = Kid(watch: "PG-13", read: "G", listen: "R");

        Assert.True(kid.AllowsItem("PG-13", "Movies"));
        Assert.True(kid.AllowsItem("TV-14", "TV"));
        Assert.False(kid.AllowsItem("R", "Movies"));

        Assert.True(kid.AllowsItem("G", "Books"));
        Assert.False(kid.AllowsItem("PG", "Books"));
        Assert.False(kid.AllowsItem("PG", "Comics"));

        Assert.True(kid.AllowsItem("R", "Music"));
        Assert.False(kid.AllowsItem("NC-17", "Music"));
    }

    [Fact]
    public void AnAudiobook_BelongsToListen_AsTheRepoAlreadyAssignsIt()
    {
        var kid = Kid(watch: "G", read: "G", listen: "PG-13");

        Assert.True(kid.AllowsItem("PG-13", "Audiobooks"));
        Assert.False(kid.AllowsItem("PG-13", "Books"));
    }

    [Fact]
    public void AdultContent_AlwaysCountsAsAdultRated_SoNoKidLevelLetsItThrough()
    {
        // An item marked Explicit is read as NC-17 (ContentRatingSql.WithExplicit); the ladder tops out at R, so it never passes.
        var kid = Kid("R", "R", "R");

        Assert.False(kid.AllowsItem("NC-17", "Movies"));
        Assert.False(kid.AllowsItem("NC-17", "Books"));
        Assert.False(kid.AllowsItem("NC-17", "Music"));
    }

    [Theory]
    [InlineData("R18+")]
    [InlineData("BBFC 18")]
    [InlineData("III")]
    public void ARatingTheLadderDoesNotKnow_FollowsTheShowUnratedSwitch_AsItAlwaysDid(string rating)
    {
        Assert.True(Kid(showUnrated: true).AllowsItem(rating, "Movies"));
        Assert.False(Kid(showUnrated: false).AllowsItem(rating, "Movies"));
    }

    [Fact]
    public void AMediaTypeInNoArea_GetsTheLowestOfTheThreeLevels()
    {
        var kid = Kid(watch: "R", read: "PG", listen: "PG-13");

        Assert.True(kid.AllowsItem("PG", "Mystery"));
        Assert.False(kid.AllowsItem("PG-13", "Mystery"));
        Assert.False(kid.AllowsItem("PG-13", mediaType: null));
    }

    [Fact]
    public void AProfileThatIsNotAKid_IsNotAffected()
    {
        Assert.True(ContentLimitFilter.Unrestricted.IsUnrestricted);
        Assert.True(ContentLimitFilter.Unrestricted.AllowsItem(null, "Movies"));
        Assert.True(ContentLimitFilter.Unrestricted.AllowsItem("NC-17", "Books"));
        var adult = new ContentLimitFilter(new ProfileContentLimit(false, HouseholdKidSettings.Strictest));
        Assert.True(adult.IsUnrestricted);
        Assert.True(adult.NeedsNoFiltering);
    }

    [Fact]
    public void AProfileWithNoUsableProfile_GetsTheStrictestLimit()
    {
        var strictest = new ContentLimitFilter(ProfileContentLimit.Strictest);

        Assert.False(strictest.IsUnrestricted);
        Assert.False(strictest.AllowsItem(null, "Movies"));
        Assert.False(strictest.AllowsItem("PG", "Movies"));
        Assert.True(strictest.AllowsItem("G", "Movies"));
    }

    [Fact]
    public void TheVault_StaysHiddenFromAKidsProfile_WithoutAnUnlockedSession()
    {
        var vault = new VaultVisibility(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { VaultLibrary }, unlocked: false);
        var kid = Kid().WithVault(vault);

        Assert.True(kid.HidesLibrary(VaultLibrary));
        Assert.True(kid.IsVaultLibrary(VaultLibrary));
        Assert.False(kid.NeedsNoFiltering);
    }
}
